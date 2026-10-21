using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using YueUI.Api.Machines;
using YueUI.Api.Memory;
using YueUI.Api.Speech;
using YueUI.Api.Worker;

namespace YueUI.Api.Tests;

/// <summary>Two Tonwerks: this one, and a helper (another <see cref="TestApp"/> with <c>Serve</c> on) it hands jobs to.</summary>
public sealed class MachineTests : IDisposable
{
    private const string Key = "geheim";

    private readonly TestApp _helper = new()
    {
        Machines = options =>
        {
            options.Serve = true;
            options.Key = Key;
            options.Name = "Alter Mac";
        },
    };

    private readonly TestApp _app;

    public MachineTests()
    {
        _app = new TestApp
        {
            Machines = options =>
            {
                options.Name = "Neuer Mac";
                options.Key = Key;
                options.Helpers = [new HelperMachine { Id = "alt", Name = "Alter Mac", Url = "http://alt.test/" }];
            },
            HelperHandler = () => _helper.Server.CreateHandler(),
        };
    }

    public void Dispose()
    {
        _app.Dispose();
        _helper.Dispose();
    }

    [Fact]
    public async Task The_helper_routes_answer_only_when_serving_and_only_with_the_key()
    {
        var notServing = await _app.CreateClient().GetAsync("/api/helper/status");
        var noKey = await _helper.CreateClient().GetAsync("/api/helper/status");
        var client = _helper.CreateClient();
        client.DefaultRequestHeaders.Add(HelperClient.KeyHeader, Key);
        var status = await client.GetFromJsonAsync<HelperStatus>("/api/helper/status", TestApp.Json);

        Assert.Equal(HttpStatusCode.NotFound, notServing.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, noKey.StatusCode);
        Assert.Equal(("Alter Mac", null, false), (status!.Name, status.Holder, status.InUse));
        Assert.Contains(Engines.Speech, status.Engines);
        Assert.Contains(Engines.Lyrics, status.Engines);
    }

    [Fact]
    public async Task The_machines_list_this_Mac_and_the_helper_as_last_asked()
    {
        var client = _app.CreateClient();

        var machines = await WaitForMachines(client, all => all.Any(m => m.Id == "alt" && m.Reachable));

        Assert.Equal(["local", "alt"], machines.Select(m => m.Id));
        Assert.Equal(("Neuer Mac", true), (machines[0].Name, machines[0].Local));
        Assert.Equal(("Alter Mac", false), (machines[1].Name, machines[1].Busy));
        Assert.Contains(Engines.Images, machines[1].Engines);
    }

    [Fact]
    public async Task A_take_asked_for_the_helper_is_spoken_there_with_the_voice_sent_along()
    {
        var client = _app.CreateClient();
        var voice = await (await client.PostAsync("/api/speech/voices", Recording())).Content.ReadFromJsonAsync<SpeechVoice>(TestApp.Json);

        var response = await client.PostAsJsonAsync("/api/speech/takes", new { text = "Hallo vom alten Mac", voiceId = voice!.Id, models = new[] { "chatterbox" }, machine = "alt" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var take = (await WaitForTakes(client, all => all.Single().Finished)).Single();
        Assert.Equal(("done", "alt", "Alter Mac", 2.5), (take.Stage, take.Machine, take.RanOn, take.Seconds));
        Assert.Empty(_app.Speech.Jobs);
        var job = Assert.Single(_helper.Speech.Jobs);
        Assert.Equal(("Hallo vom alten Mac", "Hallo, das ist meine Stimme."), (job.Text, job.RefText));
        Assert.NotNull(job.RefAudio);
        var audio = await client.GetAsync($"/api/speech/takes/{take.Id}/audio");
        Assert.Equal(HttpStatusCode.OK, audio.StatusCode);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(await audio.Content.ReadAsByteArrayAsync(), 0, 4));
    }

    [Fact]
    public async Task A_take_with_a_voice_of_ones_own_is_sung_on_the_helper_with_the_voice_sent_along()
    {
        var client = _app.CreateClient();
        var form = new MultipartFormDataContent
        {
            { new StringContent("Marcel"), "label" },
            { new StringContent("Amazing grace"), "lyrics" },
            { new ByteArrayContent([1, 2, 3]), "file", "aufnahme.m4a" },
        };
        var voice = (await (await client.PostAsync("/api/singing/voices", form)).Content.ReadFromJsonAsync<Singing.SingingVoiceCreated>(TestApp.Json))!.Voice;

        var sing = new MultipartFormDataContent
        {
            { new StringContent("""[{"start":0,"end":1,"pitch":62,"lyric":"sun"}]"""), "notes" },
            { new StringContent(voice.Id), "voice" },
            { new StringContent("alt"), "machine" },
        };
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync("/api/singing", sing)).StatusCode);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!(await client.GetFromJsonAsync<Singing.SingingInfo>("/api/singing", TestApp.Json))!.Takes.Single().Finished)
        {
            Assert.True(DateTime.UtcNow < deadline, "The take never finished.");
            await Task.Delay(20);
        }
        var take = (await client.GetFromJsonAsync<Singing.SingingInfo>("/api/singing", TestApp.Json))!.Takes.Single();
        Assert.Equal(("done", "Alter Mac"), (take.Stage, take.RanOn));
        Assert.Empty(_app.Singing.Jobs);
        var sung = Assert.Single(_helper.Singing.Jobs).Voice;
        Assert.True(sung.Own);
        Assert.Equal(voice.Id, sung.Id);
        // The helper's copies, read from its run's inputs (deleted with the run).
        Assert.Equal(("voiceAudio.wav", "voiceMetadata.json"), (Path.GetFileName(sung.Audio), Path.GetFileName(sung.Metadata)));
    }

    [Fact]
    public async Task Whichever_is_free_is_the_helper_while_this_Macs_memory_is_taken()
    {
        var client = _app.CreateClient();
        await WaitForMachines(client, all => all.Any(m => m.Id == "alt" && m.Reachable));
        var memory = _app.Services.GetRequiredService<ModelMemory>();
        Assert.True(memory.TryClaim(LargeModel.Images));

        await client.PostAsJsonAsync("/api/speech/takes", new { text = "Hallo", models = new[] { "chatterbox" } });

        var take = (await WaitForTakes(client, all => all.Single().Finished)).Single();
        Assert.Equal(("done", null, "Alter Mac"), (take.Stage, take.Machine, take.RanOn));
        Assert.Single(_helper.Speech.Jobs);
        memory.Release(LargeModel.Images);
    }

    [Fact]
    public async Task Whichever_is_free_is_this_Mac_while_its_memory_is_free()
    {
        var client = _app.CreateClient();
        await WaitForMachines(client, all => all.Any(m => m.Id == "alt" && m.Reachable));

        await client.PostAsJsonAsync("/api/speech/takes", new { text = "Hallo", models = new[] { "chatterbox" } });

        var take = (await WaitForTakes(client, all => all.Single().Finished)).Single();
        Assert.Equal("Neuer Mac", take.RanOn);
        Assert.Single(_app.Speech.Jobs);
        Assert.Empty(_helper.Speech.Jobs);
    }

    [Fact]
    public async Task A_helper_failure_fails_the_job_with_its_words()
    {
        _helper.Speech.Failure = "Chatterbox stolperte.";
        var client = _app.CreateClient();

        await client.PostAsJsonAsync("/api/speech/takes", new { text = "Hallo", models = new[] { "chatterbox" }, machine = "alt" });

        var take = (await WaitForTakes(client, all => all.Single().Finished)).Single();
        Assert.Equal("failed", take.Stage);
        Assert.Contains("Chatterbox stolperte.", take.Message);
    }

    [Fact]
    public async Task A_cover_asked_for_the_helper_is_painted_there()
    {
        var client = _app.CreateClient();
        _app.AddSong("20260922-101500-Neon-Night", "song1");

        var response = await client.PostAsJsonAsync("/api/songs/20260922-101500-Neon-Night/song1/images", new { prompt = "a neon city", seed = 7, machine = "alt" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var image = await response.Content.ReadFromJsonAsync<Images.ImageState>(TestApp.Json);
        await TestApp.WaitUntil(() => File.Exists(Path.Combine(_app.Root, "images", $"{image!.Id}.jpg")));
        Assert.Empty(_app.Images.Jobs);
        Assert.Equal(("a neon city", 7L), (Assert.Single(_helper.Images.Jobs).Prompt, _helper.Images.Jobs[0].Seed));
        var picture = await client.GetAsync($"/api/images/{image!.Id}");
        Assert.Equal(FakeImages.Jpeg, await picture.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_model_too_large_for_the_helper_waits_for_this_Mac_instead()
    {
        _helper.Images.Missing["krea-2-turbo"] = "Krea 2 Turbo needs a Mac with 32 GB; this one has 24.";
        var client = _app.CreateClient();
        _app.AddSong("20260922-101500-Neon-Night", "song1");
        var machines = await WaitForMachines(client, all => all.Any(m => m.Id == "alt" && m.Reachable));
        Assert.Contains(Engines.ImageModel("klein-4b"), machines[1].Engines);
        Assert.DoesNotContain(Engines.ImageModel("krea-2-turbo"), machines[1].Engines);
        var memory = _app.Services.GetRequiredService<ModelMemory>();
        Assert.True(memory.TryClaim(LargeModel.Speech));

        await client.PostAsJsonAsync("/api/songs/20260922-101500-Neon-Night/song1/images", new { prompt = "a neon city", model = "krea-2-turbo" });
        await Task.Delay(300);

        // The helper is free but cannot paint it; it waits for this Mac.
        Assert.Empty(_helper.Images.Jobs);
        Assert.Empty(_app.Images.Jobs);
        memory.Release(LargeModel.Speech);
        await TestApp.WaitUntil(() => _app.Images.Jobs.Count == 1);
        Assert.Empty(_helper.Images.Jobs);
    }

    [Fact]
    public async Task Lyrics_asked_for_the_helper_are_written_by_its_LM_Studio_beside_a_busy_Mac()
    {
        _helper.LmStudio.Answer = "[Verse]\nVom alten Mac";
        var client = _app.CreateClient();
        // A take holding this Mac's memory: a draft here would have to wait.
        Assert.True(_app.Services.GetRequiredService<ModelMemory>().TryClaim(LargeModel.Speech));

        var response = await client.PostAsJsonAsync("/api/lyrics", new { keywords = "Nachtzug", machine = "alt" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<LyricsState>(TestApp.Json))!.Id;
        var draft = (await _app.WaitForStatus(client, s => s.Lyrics is { Finished: true } lyrics && lyrics.Id == id)).Lyrics!;
        Assert.Equal(("done", "[Verse]\nVom alten Mac", "Alter Mac"), (draft.Stage, draft.Lyrics, draft.RanOn));
        Assert.Empty(_app.LmStudio.Requests);
        Assert.Contains(_helper.LmStudio.Requests, r => r.Path == "/v1/chat/completions");
    }

    [Fact]
    public async Task The_lyrics_picker_lists_the_helpers_models_for_it()
    {
        var models = await _app.CreateClient().GetFromJsonAsync<Lyrics.LyricsModels>("/api/lyrics/models?machine=alt", TestApp.Json);

        Assert.Equal("google/gemma-4-e4b", models!.Default);
        Assert.Contains(models.Models, m => m.Id == "qwen/qwen3-8b");
        Assert.Empty(_app.LmStudio.Requests);
    }

    [Fact]
    public async Task An_unknown_Mac_is_refused()
    {
        var client = _app.CreateClient();

        var speech = await client.PostAsJsonAsync("/api/speech/takes", new { text = "Hallo", models = new[] { "chatterbox" }, machine = "nirgends" });
        var lyrics = await client.PostAsJsonAsync("/api/lyrics", new { keywords = "Nachtzug", machine = "nirgends" });

        Assert.Equal(HttpStatusCode.BadRequest, speech.StatusCode);
        Assert.Contains("\"machine\"", await speech.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, lyrics.StatusCode);
    }

    private static MultipartFormDataContent Recording() => new()
    {
        { new StringContent("Marcel"), "label" },
        { new StringContent("Hallo, das ist meine Stimme."), "transcript" },
        { new ByteArrayContent([0x1A, 0x45, 0xDF, 0xA3, 1, 2, 3]), "file", "recording.webm" },
    };

    private static async Task<IReadOnlyList<MachineInfo>> WaitForMachines(HttpClient client, Func<IReadOnlyList<MachineInfo>, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            var machines = (await client.GetFromJsonAsync<List<MachineInfo>>("/api/machines", TestApp.Json))!;
            if (condition(machines))
            {
                return machines;
            }
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"The machines never matched: {string.Join(", ", machines.Select(m => $"{m.Id} {m.Reachable} {m.Message}"))}");
            }
            await Task.Delay(20);
        }
    }

    private static async Task<IReadOnlyList<SpeechTake>> WaitForTakes(HttpClient client, Func<IReadOnlyList<SpeechTake>, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            var takes = (await client.GetFromJsonAsync<SpeechInfo>("/api/speech", TestApp.Json))!.Takes;
            if (takes.Count > 0 && condition(takes))
            {
                return takes;
            }
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"The takes never matched: {string.Join(", ", takes.Select(t => $"{t.ModelId} {t.Stage} {t.Message}"))}");
            }
            await Task.Delay(20);
        }
    }
}
