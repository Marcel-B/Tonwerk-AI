using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using YueUI.Api.Ace;
using YueUI.Api.Library;
using YueUI.Api.Memory;

namespace YueUI.Api.Tests;

public sealed class AceEndpointTests : IDisposable
{
    private const string Run = "20260921-165850-Neon-Night";

    private readonly TestApp _app = new();

    private readonly HttpClient _client;

    public AceEndpointTests() => _client = _app.CreateClient();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task The_page_learns_which_models_are_there()
    {
        var info = await _client.GetFromJsonAsync<AceInfo>("/api/ace", TestApp.Json);

        Assert.True(info!.Ready);
        Assert.Equal(["acestep-5Hz-lm-4B-Q8_0.gguf", "acestep-5Hz-lm-1.7B-Q8_0.gguf"], info.LmModels);
        Assert.Equal(["acestep-v15-turbo-Q8_0.gguf", "acestep-v15-sft-Q8_0.gguf"], info.DitModels);
        Assert.Equal(["singer.safetensors"], info.Adapters);
        Assert.EndsWith(Path.Combine("ace", "acestep.cpp", "build"), info.Binaries);
        Assert.Empty(info.Jobs);
    }

    [Fact]
    public async Task A_song_goes_into_the_library_with_what_the_lm_wrote()
    {
        var response = await _client.PostAsJsonAsync("/api/ace", new
        {
            title = "Wüste Nacht",
            caption = " dark synthwave, female vocals ",
            language = "de",
            duration = 90,
            seed = 7,
            batch = 2,
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var queued = await response.Content.ReadFromJsonAsync<AceJob>(TestApp.Json);
        Assert.Equal(("queued", "Wüste Nacht", 7L), (queued!.Stage, queued.Title, queued.Seed));
        var done = await WaitForJob(queued.Id, j => j.Finished);
        Assert.Equal("done", done.Stage);
        Assert.Matches(@"^\d{8}-\d{6}-Wueste-Nacht$", done.Run);

        var run = Assert.Single(_app.Ace.Runs);
        Assert.True(run.UsesLm);
        Assert.Null(run.SourceAudio);
        Assert.Equal(
            """{"caption":"dark synthwave, female vocals","task_type":"text2music","seed":7,"synth_model":"acestep-v15-turbo-Q8_0.gguf","output_format":"wav32","lm_model":"acestep-5Hz-lm-4B-Q8_0.gguf","vocal_language":"de","duration":90,"lm_batch_size":2}""",
            run.Request.ToJsonString());
        // The work folder goes once the run is in place.
        Assert.False(Directory.Exists(run.WorkDirectory));

        var library = await _client.GetFromJsonAsync<List<RunInfo>>("/api/library", TestApp.Json);
        var listed = Assert.Single(library!);
        Assert.Equal((done.Run, "Wüste Nacht", "dark synthwave, female vocals"), (listed.Id, listed.Title, listed.Style));
        Assert.Equal("[Verse]\nWritten by the LM 0", listed.Lyrics);
        Assert.Equal([1, 2], listed.Songs.Select(s => s.Index));
        Assert.All(listed.Songs, s => Assert.Equal(("ace", 2.5, true, false, false), (s.Engine, s.Seconds, s.HasAudio, s.HasScore, s.CanRender)));
        Assert.Equal([7L, 8L], listed.Songs.Select(s => s.Seed));
        Assert.Equal(FakeMixer.Flac, await _client.GetByteArrayAsync($"/api/songs/{done.Run}/song2/audio"));

        // Taken over on the page, the second song comes back with its own seed and the lyrics the LM wrote for it.
        var settings = await _client.GetFromJsonAsync<AceSongRequest>($"/api/songs/{done.Run}/song2/ace", TestApp.Json);
        Assert.Equal(("[Verse]\nWritten by the LM 1", 8L, (int?)null, "de"), (settings!.Lyrics, settings.Seed, settings.Batch, settings.Language));
        var written = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_app.Root, "ace-songs", done.Run!, "song2", SongLibrary.AceFile)))!;
        Assert.Equal(("A minor", 120), ((string?)written["keyscale"], (int?)written["bpm"]));
        // The codes are of no use once the song is there and take kilobytes.
        Assert.Null(written["audio_codes"]);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/songs/{done.Run}/song2/score")).StatusCode);
    }

    [Fact]
    public async Task An_ace_song_is_deleted_like_any_other()
    {
        var job = await Post(new { caption = "lo-fi", instrumental = true });
        var done = await WaitForJob(job.Id, j => j.Finished);

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/runs/{done.Run}")).StatusCode);

        Assert.False(Directory.Exists(Path.Combine(_app.Root, "ace-songs", done.Run!)));
        Assert.Empty((await _client.GetFromJsonAsync<List<RunInfo>>("/api/library", TestApp.Json))!);
    }

    [Fact]
    public async Task An_instrumental_has_no_lyrics_and_no_language()
    {
        var job = await Post(new { caption = "ambient drone", lyrics = "ignored", instrumental = true, language = "de" });
        await WaitForJob(job.Id, j => j.Finished);

        var request = Assert.Single(_app.Ace.Runs).Request;
        Assert.Equal("[Instrumental]", (string?)request["lyrics"]);
        Assert.Null(request["vocal_language"]);
        var listed = Assert.Single((await _client.GetFromJsonAsync<List<RunInfo>>("/api/library", TestApp.Json))!);
        Assert.Equal(("ambient drone", ""), (listed.Title, listed.Lyrics));
    }

    [Fact]
    public async Task A_cover_starts_from_a_library_song_without_the_lm()
    {
        var song = _app.AddSong(Run, "song1");

        var job = await Post(new
        {
            caption = "jazz piano cover",
            task = "cover",
            source = $"{Run}/song1",
            coverStrength = 0.4,
            batch = 2,
            lmModel = "acestep-5Hz-lm-1.7B-Q8_0.gguf",
            ditModel = "acestep-v15-sft-Q8_0.gguf",
            steps = 50,
            guidance = 7,
            shift = 1,
            solver = "dpm3m",
        });
        var done = await WaitForJob(job.Id, j => j.Finished);

        Assert.Equal("done", done.Stage);
        var run = Assert.Single(_app.Ace.Runs);
        Assert.False(run.UsesLm);
        // The FLAC decoded to WAV for acestep.cpp (the fake decoder copies it).
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(song, "audio.flac")), _app.Ace.Source);
        Assert.Equal(
            """{"caption":"jazz piano cover","task_type":"cover","seed":""" + done.Seed
            + ""","synth_model":"acestep-v15-sft-Q8_0.gguf","output_format":"wav32","lyrics":"[Instrumental]","synth_batch_size":2,"inference_steps":50,"guidance_scale":7,"shift":1,"solver":"dpm3m","audio_cover_strength":0.4}""",
            run.Request.ToJsonString());
        var listed = (await _client.GetFromJsonAsync<List<RunInfo>>("/api/library", TestApp.Json))!;
        Assert.Equal([done.Run, Run], listed.Select(r => r.Id));
    }

    [Fact]
    public async Task What_acestep_cannot_do_is_refused_before_it_runs()
    {
        var response = await _client.PostAsJsonAsync("/api/ace", new
        {
            caption = "",
            language = "xx",
            duration = 5,
            timeSignature = 5,
            task = "repaint",
            source = $"{Run}/song9",
            repaintStart = 20,
            repaintEnd = 10,
            ditModel = "missing.gguf",
            adapter = "missing.safetensors",
            batch = 9,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(
            ["adapter", "batch", "caption", "ditModel", "duration", "language", "repaintEnd", "source", "timeSignature"],
            problem!["errors"]!.AsObject().Select(e => e.Key).Order());
        Assert.Empty(_app.Ace.Runs);
    }

    [Fact]
    public async Task Without_acestep_the_page_is_told_how_to_build_it()
    {
        _app.Ace.Installed = false;

        var response = await _client.PostAsJsonAsync("/api/ace", new { caption = "pop" });

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        Assert.Contains("deploy/install-ace.sh", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_song_waits_for_the_memory_and_holds_it_while_it_runs()
    {
        var memory = _app.Services.GetRequiredService<ModelMemory>();
        Assert.True(memory.TryClaim(LargeModel.Images));
        _app.Ace.Gate = new TaskCompletionSource();

        var job = await Post(new { caption = "pop" });
        await Task.Delay(100);
        Assert.Empty(_app.Ace.Runs);
        Assert.Equal("queued", _app.Snapshot().Ace!.Single().Stage);

        memory.Release(LargeModel.Images);
        await TestApp.WaitUntil(() => _app.Ace.Runs.Count == 1);
        await _app.WaitForStatus(_client, s => s.Ace is [{ Stage: "composing" }]);
        Assert.True(memory.Holds(LargeModel.Ace));
        var busy = await _client.GetFromJsonAsync<Worker.BusyInfo>("/api/busy", TestApp.Json);
        Assert.Equal(["ace"], busy!.Reasons);

        _app.Ace.Gate.SetResult();
        await WaitForJob(job.Id, j => j.Finished);
        await TestApp.WaitUntil(() => !memory.Holds(LargeModel.Ace));
    }

    [Fact]
    public async Task A_failure_says_what_acestep_said_and_a_stop_cancels()
    {
        _app.Ace.Failure = "[Ace-LM] FATAL: lm_model 'x' not found in registry";
        var failed = await WaitForJob((await Post(new { caption = "pop" })).Id, j => j.Finished);
        Assert.Equal(("failed", "[Ace-LM] FATAL: lm_model 'x' not found in registry"), (failed.Stage, failed.Message));

        _app.Ace.Failure = null;
        _app.Ace.Gate = new TaskCompletionSource();
        var running = await Post(new { caption = "pop" });
        await TestApp.WaitUntil(() => _app.Ace.Runs.Count == 2);

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/ace/{running.Id}")).StatusCode);

        await _app.WaitForStatus(_client, s => s.Ace is [{ Stage: "cancelled" }]);
        await TestApp.WaitUntil(() => !_app.Services.GetRequiredService<ModelMemory>().Holds(LargeModel.Ace));
        var info = await _client.GetFromJsonAsync<AceInfo>("/api/ace", TestApp.Json);
        Assert.Equal([failed.Id], info!.Jobs.Select(j => j.Id));
        Assert.Empty((await _client.GetFromJsonAsync<List<RunInfo>>("/api/library", TestApp.Json))!);
    }

    [Fact]
    public async Task A_brightened_song_keeps_the_switch_for_taking_it_over()
    {
        var done = await WaitForJob((await Post(new { caption = "pop", brighten = true })).Id, j => j.Finished);

        Assert.Equal("done", done.Stage);
        var request = await _client.GetFromJsonAsync<AceSongRequest>($"/api/songs/{done.Run}/song1/ace", TestApp.Json);
        Assert.True(request!.Brighten);
        // ace-synth's float WAV goes through the mastering before the FLAC.
        Assert.Equal(["mastered0.wav"], _app.Mixer.Encoded);
    }

    [Fact]
    public async Task A_recording_is_heard_and_answered_for_the_form()
    {
        using var form = new MultipartFormDataContent
        {
            { new ByteArrayContent([1, 2, 3, 4]), "file", "idee.m4a" },
            { new StringContent("acestep-5Hz-lm-1.7B-Q8_0.gguf"), "lmModel" },
        };

        var response = await _client.PostAsync("/api/ace/understand", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var heard = await response.Content.ReadFromJsonAsync<AceHeard>(TestApp.Json);
        Assert.Equal(_app.Ace.Heard, heard);
        var hearing = Assert.Single(_app.Ace.Hearings);
        // Through ffmpeg (here a copy) into a WAV first, since ace-understand reads no M4A.
        Assert.Equal([1, 2, 3, 4], hearing.Audio);
        Assert.Equal(("acestep-5Hz-lm-1.7B-Q8_0.gguf", "acestep-v15-turbo-Q8_0.gguf"), (hearing.Lm, hearing.Dit));
        Assert.Contains("audio.wav", _app.Mixer.Decoded);
        Assert.False(_app.Services.GetRequiredService<ModelMemory>().Holds(LargeModel.Ace));
    }

    [Fact]
    public async Task A_library_song_is_heard_from_its_flac()
    {
        var directory = _app.AddSong(Run, "song1");
        using var form = new MultipartFormDataContent { { new StringContent($"{Run}/song1"), "song" } };

        var response = await _client.PostAsync("/api/ace/understand", form);

        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var hearing = Assert.Single(_app.Ace.Hearings);
        Assert.Equal("acestep-5Hz-lm-4B-Q8_0.gguf", hearing.Lm);
        Assert.Equal(File.ReadAllBytes(Path.Combine(directory, "audio.flac")), hearing.Audio);
    }

    [Fact]
    public async Task Hearing_is_refused_while_the_memory_is_taken_or_without_ace_understand()
    {
        var memory = _app.Services.GetRequiredService<ModelMemory>();
        Assert.True(memory.TryClaim(LargeModel.Images));
        using (var form = new MultipartFormDataContent { { new ByteArrayContent([1]), "file", "idee.wav" } })
        {
            Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsync("/api/ace/understand", form)).StatusCode);
        }
        memory.Release(LargeModel.Images);

        _app.Ace.Understands = false;
        using (var form = new MultipartFormDataContent { { new ByteArrayContent([1]), "file", "idee.wav" } })
        {
            var response = await _client.PostAsync("/api/ace/understand", form);
            Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
            Assert.Contains("deploy/install-ace.sh", await response.Content.ReadAsStringAsync());
        }

        using (var form = new MultipartFormDataContent
        {
            { new StringContent($"{Run}/song9"), "song" },
            { new StringContent("missing.gguf"), "lmModel" },
        })
        {
            var response = await _client.PostAsync("/api/ace/understand", form);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var problem = await response.Content.ReadFromJsonAsync<JsonObject>();
            Assert.Equal(["lmModel", "song"], problem!["errors"]!.AsObject().Select(e => e.Key).Order());
        }
        Assert.Empty(_app.Ace.Hearings);
    }

    [Fact]
    public async Task What_acestep_heard_fails_with_its_words_and_gives_the_memory_back()
    {
        _app.Ace.Failure = "[Ace-Understand] FATAL: cannot read audio";
        using var form = new MultipartFormDataContent { { new ByteArrayContent([1]), "file", "idee.wav" } };

        var response = await _client.PostAsync("/api/ace/understand", form);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("cannot read audio", await response.Content.ReadAsStringAsync());
        Assert.False(_app.Services.GetRequiredService<ModelMemory>().Holds(LargeModel.Ace));
    }

    private async Task<AceJob> Post(object request)
    {
        var response = await _client.PostAsJsonAsync("/api/ace", request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AceJob>(TestApp.Json))!;
    }

    private async Task<AceJob> WaitForJob(string id, Func<AceJob, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            var info = await _client.GetFromJsonAsync<AceInfo>("/api/ace", TestApp.Json);
            if (info!.Jobs.FirstOrDefault(j => j.Id == id) is { } job && condition(job))
            {
                return job;
            }
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The job never got there.");
            }
            await Task.Delay(20);
        }
    }
}
