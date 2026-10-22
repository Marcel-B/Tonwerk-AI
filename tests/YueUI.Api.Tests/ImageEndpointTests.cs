using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using YueUI.Api.Images;
using YueUI.Api.Memory;
using YueUI.Api.Speech;
using YueUI.Api.Worker;

namespace YueUI.Api.Tests;

public sealed class ImageEndpointTests : IDisposable
{
    private const string Run = "20260921-165850-Neon-Night";

    private readonly TestApp _app = new();

    private readonly HttpClient _client;

    public ImageEndpointTests() => _client = _app.CreateClient();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task Every_model_is_offered_with_what_it_costs_and_allows()
    {
        var info = await _client.GetFromJsonAsync<ImageInfo>("/api/images", TestApp.Json);

        Assert.True(info!.Installed);
        Assert.EndsWith(Path.Combine("images", "env", "bin", "python"), info.Python);
        Assert.EndsWith(Path.Combine("images", "flux3", "bin", "python"), info.Flux3Python);
        Assert.Equal(["klein-4b", "klein-9b", "z-image-turbo", "krea-2-turbo", "flux3-klein"], info.Models.Select(m => m.Id));
        Assert.Equal([true, false, true, true, true], info.Models.Select(m => m.Commercial));
        Assert.Equal([true, false, false, false, false], info.Models.Select(m => m.Downloaded));
        // 9B's and Krea's repositories are gated and no token is there.
        Assert.Equal([true, false, true, false, true], info.Models.Select(m => m.TokenFound));
        Assert.Equal([false, false, true, true, true], info.Models.Select(m => m.Lettering));
        Assert.All(info.Models, m => Assert.True(m.Available));
    }

    [Fact]
    public void The_new_models_need_a_Mac_with_32_GB_and_their_runner()
    {
        var root = Path.Combine(_app.Root, "runners");
        var settings = new ImageOptions { Root = root, SystemMemoryGb = 24 };
        var krea = ImageModel.Defaults.Single(m => m.Id == "krea-2-turbo");
        var flux3 = ImageModel.Defaults.Single(m => m.Id == "flux3-klein");
        var klein = ImageModel.Defaults[0];
        Directory.CreateDirectory(Path.Combine(root, "env", "bin"));
        File.WriteAllText(Path.Combine(root, "env", "bin", "python"), "");

        Assert.Null(MfluxEngine.UnavailableOn(klein, settings));
        Assert.Equal("Krea 2 Turbo needs a Mac with 32 GB; this one has 24.", MfluxEngine.UnavailableOn(krea, settings));

        settings.SystemMemoryGb = 32;
        Assert.Null(MfluxEngine.UnavailableOn(krea, settings));
        // FLUX 3 runs in flux3-mlx's own environment, which is not there.
        Assert.Contains("flux3-mlx is not installed", MfluxEngine.UnavailableOn(flux3, settings));
        Assert.True(krea.ContentFilter);
        Assert.True(flux3.ContentFilter);
        Assert.True(ImageModel.Defaults.Single(m => m.Id == "klein-9b").ContentFilter);
        Assert.Equal("4bit", flux3.Upsampler);
    }

    [Fact]
    public async Task A_model_no_Mac_here_can_paint_is_refused_with_why()
    {
        _app.AddSong(Run, "song1");
        _app.Images.Missing["krea-2-turbo"] = "Krea 2 Turbo needs a Mac with 32 GB; this one has 24.";

        var info = await _client.GetFromJsonAsync<ImageInfo>("/api/images", TestApp.Json);
        var response = await _client.PostAsJsonAsync($"/api/songs/{Run}/song1/images", new { prompt = "x", model = "krea-2-turbo" });

        var krea = info!.Models.Single(m => m.Id == "krea-2-turbo");
        Assert.Equal((false, "Krea 2 Turbo needs a Mac with 32 GB; this one has 24."), (krea.Available, krea.Unavailable));
        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        Assert.Contains("needs a Mac with 32 GB", await response.Content.ReadAsStringAsync());
        Assert.Empty(_app.Images.Jobs);
    }

    [Fact]
    public async Task What_the_upsampler_wrote_is_shown_as_the_painted_prompt()
    {
        _app.AddSong(Run, "song1");
        _app.Images.Upsampled = "A wide nocturnal cityscape drenched in neon, wet asphalt reflecting magenta signs.";

        await _client.PostAsJsonAsync($"/api/songs/{Run}/song1/images", new { prompt = "Neon city at night", model = "flux3-klein" });

        var done = await WaitForImages("song1", all => all.Single().Finished);
        Assert.Equal(("done", "FLUX 3 Klein"), (done[0].Stage, done[0].ModelLabel));
        Assert.Equal(_app.Images.Upsampled, done[0].PaintedPrompt);
        var job = Assert.Single(_app.Images.Jobs);
        Assert.Equal((ImageRunners.Flux3, "8bit", 16), (job.Model.Runner, job.Model.Model, job.Model.Steps));
    }

    [Fact]
    public async Task A_painted_picture_becomes_the_songs_cover_once_taken()
    {
        _app.AddSong(Run, "song1");

        var response = await _client.PostAsJsonAsync($"/api/songs/{Run}/song1/images", new { prompt = " Neon city at night, no text ", seed = 7 });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var queued = await response.Content.ReadFromJsonAsync<ImageState>(TestApp.Json);
        Assert.Equal(("queued", "klein-4b", "Neon Night", 7L), (queued!.Stage, queued.ModelId, queued.Title, queued.Seed));
        var done = await WaitForImages("song1", all => all.Single().Finished);
        Assert.Equal(("done", 20.5, 31.25, 8.9), (done[0].Stage, done[0].LoadSeconds, done[0].PaintSeconds, done[0].PeakMemoryGb));
        var job = Assert.Single(_app.Images.Jobs);
        Assert.Equal(("flux2-klein-4b", 8, "Neon city at night, no text", 7L, 1024, 1024), (job.Model.Model, job.Model.Quantize, job.Prompt, job.Seed, job.Width, job.Height));

        var picture = await _client.GetAsync($"/api/images/{queued.Id}");
        Assert.Equal("image/jpeg", picture.Content.Headers.ContentType!.MediaType);
        Assert.Equal(FakeImages.Jpeg, await picture.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/songs/{Run}/song1/cover")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await _client.PutAsync($"/api/images/{queued.Id}/cover", null)).StatusCode);

        var cover = await _client.GetAsync($"/api/songs/{Run}/song1/cover");
        Assert.Equal(FakeImages.Jpeg, await cover.Content.ReadAsByteArrayAsync());
        // The candidate stays, to be taken again after trying another.
        Assert.Single(await Images("song1"));
    }

    [Fact]
    public async Task A_description_is_painted_in_the_english_claude_made_of_it()
    {
        _app.AddSong(Run, "song1");
        _app.Translator.Configured = true;
        _app.Translator.Answer = "Someone stands in the forest, the morning sun shines through the leaves.";

        Assert.True((await _client.GetFromJsonAsync<ImageInfo>("/api/images", TestApp.Json))!.Translates);
        var response = await _client.PostAsJsonAsync($"/api/songs/{Run}/song1/images", new { prompt = "jemand steht im Wald, die Morgensonne strahlt durch die Blätter." });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var done = await WaitForImages("song1", all => all.Single().Finished);
        Assert.Equal("jemand steht im Wald, die Morgensonne strahlt durch die Blätter.", done[0].Prompt);
        Assert.Equal(_app.Translator.Answer, done[0].PaintedPrompt);
        Assert.Equal(_app.Translator.Answer, Assert.Single(_app.Images.Jobs).Prompt);
    }

    [Fact]
    public async Task Without_a_translation_the_description_is_painted_as_written()
    {
        _app.AddSong(Run, "song1");
        _app.Translator.Configured = true;

        await _client.PostAsJsonAsync($"/api/songs/{Run}/song1/images", new { prompt = "Neon city at night" });

        var done = await WaitForImages("song1", all => all.Single().Finished);
        Assert.Null(done[0].PaintedPrompt);
        Assert.Equal("Neon city at night", Assert.Single(_app.Images.Jobs).Prompt);
        Assert.Equal(["Neon city at night"], _app.Translator.Calls);
    }

    [Fact]
    public async Task Requests_it_cannot_paint_are_refused()
    {
        _app.AddSong(Run, "song1");

        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync($"/api/songs/{Run}/song1/images", new { prompt = "  " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync($"/api/songs/{Run}/song1/images", new { prompt = "x", model = "flux-dev" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync($"/api/songs/{Run}/song1/images", new { prompt = "x", seed = -1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsJsonAsync($"/api/songs/{Run}/song9/images", new { prompt = "x" })).StatusCode);
        _app.Images.Installed = false;
        var missing = await _client.PostAsJsonAsync($"/api/songs/{Run}/song1/images", new { prompt = "x" });
        Assert.Equal(HttpStatusCode.NotImplemented, missing.StatusCode);
        Assert.Contains("install-images.sh", await missing.Content.ReadAsStringAsync());
        Assert.Empty(_app.Images.Jobs);
    }

    [Fact]
    public async Task A_picture_waits_for_a_speech_take_and_holds_the_memory_while_it_paints()
    {
        _app.AddSong(Run, "song1");
        _app.Speech.Gate = new TaskCompletionSource();
        _app.Images.Gate = new TaskCompletionSource();
        await _client.PostAsJsonAsync("/api/speech/takes", new { text = "Hallo", models = new[] { "chatterbox" } });
        await TestApp.WaitUntil(() => _app.Speech.Jobs.Count == 1);

        await _client.PostAsJsonAsync($"/api/songs/{Run}/song1/images", new { prompt = "Neon", model = "klein-9b" });
        await Task.Delay(100);
        Assert.Empty(_app.Images.Jobs);
        Assert.Equal("queued", _app.Snapshot().Images!.Single().Stage);

        _app.Speech.Gate.SetResult();
        await TestApp.WaitUntil(() => _app.Images.Jobs.Count == 1);
        Assert.Equal(("flux2-klein-9b", 4), (_app.Images.Jobs[0].Model.Model, _app.Images.Jobs[0].Model.Quantize));
        await _app.WaitForStatus(_client, s => s.Images is [{ Stage: "painting" }]);
        var busy = await _client.GetFromJsonAsync<BusyInfo>("/api/busy", TestApp.Json);
        Assert.Equal(["images"], busy!.Reasons);

        // A song waits in the queue meanwhile.
        var song = await _client.PostAsJsonAsync("/api/generate", new { style = "pop", lyrics = "[verse]\nLa", title = "X" });
        Assert.Equal(HttpStatusCode.Accepted, song.StatusCode);
        Assert.Null(_app.Launcher.Current);

        _app.Images.Gate.SetResult();
        await WaitForImages("song1", all => all.Single().Stage == "done");
        await _app.StartedWorker();
        Assert.False(_app.Services.GetRequiredService<ModelMemory>().Holds(LargeModel.Images));
        Assert.False(_app.Services.GetRequiredService<ModelMemory>().Holds(LargeModel.Speech));
    }

    [Fact]
    public async Task Deleting_a_picture_in_the_works_stops_it_and_a_failed_one_says_why()
    {
        _app.AddSong(Run, "song1");
        _app.Images.Gate = new TaskCompletionSource();
        var image = await (await _client.PostAsJsonAsync($"/api/songs/{Run}/song1/images", new { prompt = "Neon" })).Content.ReadFromJsonAsync<ImageState>(TestApp.Json);
        await TestApp.WaitUntil(() => _app.Images.Jobs.Count == 1);

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/images/{image!.Id}")).StatusCode);
        await WaitForImages("song1", all => all.Count == 0);
        await TestApp.WaitUntil(() => !_app.Services.GetRequiredService<ModelMemory>().Holds(LargeModel.Images));

        _app.Images.Gate = new TaskCompletionSource();
        _app.Images.Gate.SetResult();
        _app.Images.Failure = "Error: Hugging Face refused the download.";
        await _client.PostAsJsonAsync($"/api/songs/{Run}/song1/images", new { prompt = "Neon" });
        var failed = (await WaitForImages("song1", all => all.SingleOrDefault()?.Finished == true)).Single();
        Assert.Equal(("failed", "Error: Hugging Face refused the download."), (failed.Stage, failed.Message));
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/images/{failed.Id}")).StatusCode);
    }

    [Fact]
    public async Task Candidates_go_with_their_song()
    {
        _app.AddSong(Run, "song1");
        _app.AddSong(Run, "song2");
        await _client.PostAsJsonAsync($"/api/songs/{Run}/song1/images", new { prompt = "One" });
        await _client.PostAsJsonAsync($"/api/songs/{Run}/song2/images", new { prompt = "Two" });
        await WaitForImages("song2", all => all.SingleOrDefault()?.Finished == true);
        var folder = Path.Combine(_app.Root, "images");
        Assert.Equal(4, Directory.GetFiles(folder).Length);

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/songs/{Run}/song1")).StatusCode);

        Assert.Single(await Images("song2"));
        Assert.Equal(2, Directory.GetFiles(folder).Length);
    }

    [Fact]
    public void The_message_of_a_failed_picture_is_the_scripts_own_error_line()
    {
        const string refused = "Error: Hugging Face refused the download. Accept the model's licence.";
        Assert.Equal(refused, MfluxEngine.FailureMessage(["YUEUI {\"event\": \"stage\"}", refused, "Traceback (most recent call last):", "  File \"x.py\""]));
        Assert.Equal("RuntimeError: out of memory", MfluxEngine.FailureMessage(["Traceback", "RuntimeError: out of memory", ""]));
        Assert.Equal("The model painted no picture.", MfluxEngine.FailureMessage([]));
    }

    private async Task<IReadOnlyList<ImageState>> Images(string song) =>
        (await _client.GetFromJsonAsync<ImageState[]>($"/api/songs/{Run}/{song}/images", TestApp.Json))!;

    private async Task<IReadOnlyList<ImageState>> WaitForImages(string song, Func<IReadOnlyList<ImageState>, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            var images = await Images(song);
            if (condition(images))
            {
                return images;
            }
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"The pictures stayed {string.Join(", ", images.Select(i => i.Stage))}.");
            }
            await Task.Delay(20);
        }
    }
}
