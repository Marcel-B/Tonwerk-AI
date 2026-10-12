using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using YueUI.Api.Library;
using YueUI.Api.Video;
using YueUI.Api.Worker;

namespace YueUI.Api.Tests;

public sealed class VideoEndpointTests : IDisposable
{
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task A_video_is_rendered_from_the_layers_and_kept_to_download()
    {
        _app.AddSong("20260101-120000-neon", "song2");
        var client = _app.CreateClient();

        var response = await client.PostAsync(
            "/api/songs/20260101-120000-neon/song2/videos",
            Form(VideoFormats.Landscape, VideoEffects.Bars, VideoMotions.Particles, showTitle: true, color: "#A78BFA"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var queued = await response.Content.ReadFromJsonAsync<VideoState>(TestApp.Json);
        Assert.Equal(("20260101-120000-neon/song2", "Neon Night", "landscape", "bars", "#a78bfa", "particles", true, true),
            (queued!.SongId, queued.Title, queued.Format, queued.Effect, queued.Color, queued.Motion, queued.ShowCover, queued.ShowTitle));

        var done = await WaitForVideo(client, "20260101-120000-neon/song2", v => v.Finished);
        Assert.Equal(("done", (long?)FakeVideoRenderer.Mp4.Length), (done.Stage, done.Bytes));
        Assert.Equal(187.5, _app.Videos.Seconds);
        var layers = _app.Videos.Layers!;
        Assert.All(new[] { layers.Background, layers.Cover!, layers.Title!, Assert.Single(layers.Motion) }, path => Assert.True(File.Exists(path), path));

        var file = await client.GetAsync($"/api/videos/{done.Id}/file?download=true");
        Assert.Equal("video/mp4", file.Content.Headers.ContentType!.MediaType);
        Assert.Equal(FakeVideoRenderer.Mp4, await file.Content.ReadAsByteArrayAsync());
        Assert.Equal("Neon Night-song2-youtube.mp4", file.Content.Headers.ContentDisposition!.FileNameStar);
        var range = new HttpRequestMessage(HttpMethod.Get, $"/api/videos/{done.Id}/file");
        range.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 1);
        Assert.Equal(HttpStatusCode.PartialContent, (await client.SendAsync(range)).StatusCode);

        var status = (await client.GetFromJsonAsync<StatusSnapshot>("/api/status", TestApp.Json))!;
        Assert.Equal("done", Assert.Single(status.Videos!).Stage);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/videos/{done.Id}")).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<VideoState[]>("/api/songs/20260101-120000-neon/song2/videos", TestApp.Json))!);
        Assert.False(Directory.Exists(Path.Combine(_app.Root, "videos", done.Id)));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/videos/{done.Id}/file")).StatusCode);
    }

    [Fact]
    public async Task A_plasma_ball_without_cover_comes_as_frames_of_the_whole_picture()
    {
        _app.AddSong("20260101-120000-neon", "song1");
        var client = _app.CreateClient();
        var layout = VideoLayout.For(VideoFormats.Portrait);

        var response = await client.PostAsync(
            "/api/songs/20260101-120000-neon/song1/videos",
            Form(VideoFormats.Portrait, VideoEffects.Wave, VideoMotions.Plasma, showTitle: false, showCover: false));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var done = await WaitForVideo(client, "20260101-120000-neon/song1", v => v.Finished);
        Assert.Equal(("done", "plasma", false), (done.Stage, done.Motion, done.ShowCover));
        var layers = _app.Videos.Layers!;
        Assert.Null(layers.Cover);
        Assert.Null(layers.Title);
        Assert.Equal(
            Enumerable.Range(0, VideoLayout.PlasmaFrames).Select(i => Path.Combine(_app.Root, "videos", done.Id, $"plasma{i}.png")),
            layers.Motion);
        foreach (var frame in layers.Motion)
        {
            Assert.Equal((layout.Width, layout.Height), VideoEndpoints.PngSize(await File.ReadAllBytesAsync(frame)));
        }
    }

    [Fact]
    public async Task Layers_must_be_PNGs_of_the_frame_size_and_the_settings_known_ones()
    {
        _app.AddSong("20260101-120000-neon", "song1");
        var client = _app.CreateClient();

        var response = await client.PostAsync("/api/songs/20260101-120000-neon/song1/videos", Form("square", "lasers", "fireworks", false, color: "red;x"));
        var errors = (await response.Content.ReadFromJsonAsync<JsonObject>())!["errors"]!.AsObject();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(errors.ContainsKey("format"));
        Assert.True(errors.ContainsKey("effect"));
        Assert.True(errors.ContainsKey("motion"));
        Assert.True(errors.ContainsKey("color"));

        // A landscape background for a vertical video, a cover that is no PNG, no title although one was asked for and
        // a plasma ball of one frame, and that of the whole picture although it goes behind a cover.
        var wrong = new MultipartFormDataContent
        {
            { new StringContent("portrait"), "format" },
            { new StringContent("none"), "effect" },
            { new StringContent("plasma"), "motion" },
            { new StringContent("true"), "showTitle" },
            { new ByteArrayContent(Png(1920, 1080)), "background", "background.png" },
            { new ByteArrayContent("not a png"u8.ToArray()), "cover", "cover.png" },
            { new ByteArrayContent(Png(1080, 1920)), "plasma0", "plasma0.png" },
        };
        errors = (await (await client.PostAsync("/api/songs/20260101-120000-neon/song1/videos", wrong)).Content.ReadFromJsonAsync<JsonObject>())!["errors"]!.AsObject();
        Assert.Equal(
            ["background", "cover", .. Enumerable.Range(0, VideoLayout.PlasmaFrames).Select(i => $"plasma{i}"), "title"],
            errors.Select(e => e.Key).Order());
        Assert.Empty((await client.GetFromJsonAsync<VideoState[]>("/api/songs/20260101-120000-neon/song1/videos", TestApp.Json))!);
    }

    [Fact]
    public async Task An_unknown_song_is_not_found_and_without_ffmpeg_nothing_is_queued()
    {
        _app.AddSong("20260101-120000-neon", "song1");
        var client = _app.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/songs/20260101-120000-neon/song9/videos", Form(VideoFormats.Landscape, VideoEffects.Bars, VideoMotions.None, false))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/songs/../song1/videos")).StatusCode);

        _app.Videos.Available = false;
        var response = await client.PostAsync("/api/songs/20260101-120000-neon/song1/videos", Form(VideoFormats.Landscape, VideoEffects.Bars, VideoMotions.None, false));
        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
    }

    [Fact]
    public async Task A_failed_render_says_why_and_keeps_no_file()
    {
        _app.AddSong("20260101-120000-neon", "song1");
        _app.Videos.Failure = "Unknown encoder 'libx264'";
        var client = _app.CreateClient();

        await client.PostAsync("/api/songs/20260101-120000-neon/song1/videos", Form(VideoFormats.Portrait, VideoEffects.None, VideoMotions.None, false));
        var failed = await WaitForVideo(client, "20260101-120000-neon/song1", v => v.Finished);

        Assert.Equal(("failed", "Unknown encoder 'libx264'"), (failed.Stage, failed.Message));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/videos/{failed.Id}/file")).StatusCode);
    }

    [Fact]
    public async Task A_video_in_the_works_keeps_the_server_busy_until_it_is_deleted()
    {
        _app.AddSong("20260101-120000-neon", "song1");
        _app.Videos.Gate = new TaskCompletionSource();
        var client = _app.CreateClient();

        await client.PostAsync("/api/songs/20260101-120000-neon/song1/videos", Form(VideoFormats.Landscape, VideoEffects.Wave, VideoMotions.None, false));
        var rendering = await WaitForVideo(client, "20260101-120000-neon/song1", v => v.Stage == "rendering");
        var busy = (await client.GetFromJsonAsync<BusyInfo>("/api/busy", TestApp.Json))!;
        Assert.True(busy.Busy);
        Assert.Equal(["video"], busy.Reasons);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/videos/{rendering.Id}")).StatusCode);
        await _app.WaitForStatus(client, s => s.Videos is [{ Stage: "cancelled" }]);
        await TestApp.WaitUntil(() => !Directory.Exists(Path.Combine(_app.Root, "videos", rendering.Id)));
        Assert.False((await client.GetFromJsonAsync<BusyInfo>("/api/busy", TestApp.Json))!.Busy);
    }

    [Fact]
    public async Task A_new_video_replaces_the_older_one_in_its_format_and_the_library_shows_which_there_are()
    {
        _app.AddSong("20260101-120000-neon", "song1");
        var client = _app.CreateClient();
        const string song = "20260101-120000-neon/song1";

        await client.PostAsync($"/api/songs/{song}/videos", Form(VideoFormats.Landscape, VideoEffects.Bars, VideoMotions.None, false));
        var first = await WaitForVideo(client, song, v => v.Finished);
        await client.PostAsync($"/api/songs/{song}/videos", Form(VideoFormats.Portrait, VideoEffects.Bars, VideoMotions.None, false));
        var portrait = await WaitForVideo(client, song, v => v.Id != first.Id && v.Finished);
        Assert.Equal(["landscape", "portrait"], await FormatsInLibrary(client));

        await client.PostAsync($"/api/songs/{song}/videos", Form(VideoFormats.Landscape, VideoEffects.Wave, VideoMotions.None, false));
        var second = await WaitForVideo(client, song, v => v.Id != portrait.Id && v.Id != first.Id && v.Finished);

        var videos = await client.GetFromJsonAsync<VideoState[]>($"/api/songs/{song}/videos", TestApp.Json);
        Assert.Equal([second.Id, portrait.Id], videos!.Select(v => v.Id));
        Assert.False(Directory.Exists(Path.Combine(_app.Root, "videos", first.Id)));
        Assert.Equal(["landscape", "portrait"], await FormatsInLibrary(client));

        await client.DeleteAsync($"/api/videos/{second.Id}");
        await client.DeleteAsync($"/api/videos/{portrait.Id}");
        Assert.Empty(await FormatsInLibrary(client));
    }

    private static async Task<IReadOnlyList<string>> FormatsInLibrary(HttpClient client)
    {
        var runs = await client.GetFromJsonAsync<List<RunInfo>>("/api/library", TestApp.Json);
        return Assert.Single(Assert.Single(runs!).Songs).VideoFormats!;
    }

    [Fact]
    public async Task Deleting_the_song_deletes_its_videos()
    {
        _app.AddSong("20260101-120000-neon", "song1");
        _app.AddSong("20260101-120000-neon", "song2");
        var client = _app.CreateClient();

        await client.PostAsync("/api/songs/20260101-120000-neon/song1/videos", Form(VideoFormats.Landscape, VideoEffects.Bars, VideoMotions.None, false));
        var done = await WaitForVideo(client, "20260101-120000-neon/song1", v => v.Finished);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/songs/20260101-120000-neon/song1")).StatusCode);

        Assert.False(Directory.Exists(Path.Combine(_app.Root, "videos", done.Id)));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/videos/{done.Id}/file")).StatusCode);
    }

    [Fact]
    public async Task The_folders_under_the_media_root_are_listed_with_what_they_hold()
    {
        var dog = Directory.CreateDirectory(Path.Combine(_app.MediaRoot, "Hund")).FullName;
        File.WriteAllText(Path.Combine(dog, "a.JPG"), "");
        File.WriteAllText(Path.Combine(dog, "b.heic"), "");
        File.WriteAllText(Path.Combine(dog, "walk.mov"), "");
        File.WriteAllText(Path.Combine(dog, "notes.txt"), "");
        File.WriteAllText(Path.Combine(dog, ".DS_Store"), "");
        Directory.CreateDirectory(Path.Combine(dog, "Strand"));
        File.WriteAllText(Path.Combine(dog, "Strand", "c.jpeg"), "");
        Directory.CreateDirectory(Path.Combine(_app.MediaRoot, "Urlaub"));
        Directory.CreateDirectory(Path.Combine(_app.MediaRoot, ".hidden"));

        var info = await _app.CreateClient().GetFromJsonAsync<MediaInfo>("/api/videos/media", TestApp.Json);

        Assert.True(info!.Installed);
        Assert.Equal(_app.MediaRoot, info.Root);
        Assert.Equal([new MediaFolder("Hund", 3, 1), new MediaFolder("Urlaub", 0, 0)], info.Folders);
    }

    [Fact]
    public async Task A_photo_video_cuts_the_folder_to_the_song_and_keeps_the_cover_video_beside_it()
    {
        _app.AddSong("20260101-120000-neon", "song2");
        var folder = Directory.CreateDirectory(Path.Combine(_app.MediaRoot, "Hund")).FullName;
        var client = _app.CreateClient();
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync(
            "/api/songs/20260101-120000-neon/song2/videos",
            Form(VideoFormats.Landscape, VideoEffects.Bars, VideoMotions.None, showTitle: false))).StatusCode);
        var cover = await WaitForVideo(client, "20260101-120000-neon/song2", v => v.Finished);

        var form = Form(VideoFormats.Landscape, VideoEffects.Bars, VideoMotions.None, showTitle: true, showCover: false, media: "Hund");
        form.Add(new StringContent("  Hund am Strand "), "motif");
        var response = await client.PostAsync("/api/songs/20260101-120000-neon/song2/videos", form);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var queued = (await response.Content.ReadFromJsonAsync<VideoState>(TestApp.Json))!;
        Assert.Equal(("Hund", "Hund am Strand"), (queued.Media, queued.Motif));
        var done = await WaitForVideo(client, "20260101-120000-neon/song2", v => v.Id == queued.Id && v.Finished);
        Assert.Equal("done", done.Stage);
        Assert.Equal((folder, "Hund am Strand"), (_app.Montage.Built!.Value.Folder, _app.Montage.Built.Value.Motif));
        var layers = _app.Videos.Layers!;
        Assert.Equal(Path.Combine(_app.Root, "videos", done.Id, "montage.mp4"), layers.Background);
        Assert.Null(layers.Cover);
        Assert.True(File.Exists(layers.Title!));
        Assert.True(File.Exists(layers.Shade!));
        // The stills and the montage are big and made again on a retry; only the video stays.
        Assert.False(Directory.Exists(_app.Montage.Built.Value.Work));
        Assert.False(File.Exists(layers.Background));

        var videos = (await client.GetFromJsonAsync<VideoState[]>("/api/songs/20260101-120000-neon/song2/videos", TestApp.Json))!;
        Assert.Equal([done.Id, cover.Id], videos.Select(v => v.Id));
    }

    [Fact]
    public async Task A_failing_montage_fails_the_video_with_its_reason()
    {
        _app.AddSong("20260101-120000-neon", "song2");
        Directory.CreateDirectory(Path.Combine(_app.MediaRoot, "Hund"));
        _app.Montage.Failure = "Error: the folder has no photos or videos Tonwerk can read.";
        var client = _app.CreateClient();

        var response = await client.PostAsync(
            "/api/songs/20260101-120000-neon/song2/videos",
            Form(VideoFormats.Portrait, VideoEffects.None, VideoMotions.None, showTitle: false, showCover: false, media: "Hund"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var failed = await WaitForVideo(client, "20260101-120000-neon/song2", v => v.Finished);
        Assert.Equal(("failed", _app.Montage.Failure), (failed.Stage, failed.Message));
        Assert.False(Directory.Exists(_app.Montage.Built!.Value.Work));
    }

    [Theory]
    [InlineData("Urlaub", VideoMotions.None, "media")]
    [InlineData("../songs", VideoMotions.None, "media")]
    [InlineData(".", VideoMotions.None, "media")]
    [InlineData("Hund", VideoMotions.Particles, "motion")]
    public async Task A_photo_video_needs_one_of_the_folders_and_no_motion_layer(string media, string motion, string field)
    {
        _app.AddSong("20260101-120000-neon", "song2");
        Directory.CreateDirectory(Path.Combine(_app.MediaRoot, "Hund"));

        var response = await _app.CreateClient().PostAsync(
            "/api/songs/20260101-120000-neon/song2/videos",
            Form(VideoFormats.Landscape, VideoEffects.Bars, motion, showTitle: false, showCover: false, media: media));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"\"{field}\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Without_the_planner_a_photo_video_is_refused_and_a_cover_video_still_made()
    {
        _app.AddSong("20260101-120000-neon", "song2");
        Directory.CreateDirectory(Path.Combine(_app.MediaRoot, "Hund"));
        _app.Montage.Installed = false;
        var client = _app.CreateClient();

        var refused = await client.PostAsync(
            "/api/songs/20260101-120000-neon/song2/videos",
            Form(VideoFormats.Landscape, VideoEffects.Bars, VideoMotions.None, showTitle: false, showCover: false, media: "Hund"));
        var cover = await client.PostAsync(
            "/api/songs/20260101-120000-neon/song2/videos",
            Form(VideoFormats.Landscape, VideoEffects.Bars, VideoMotions.None, showTitle: false));

        Assert.Equal(HttpStatusCode.NotImplemented, refused.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, cover.StatusCode);
        Assert.False((await client.GetFromJsonAsync<MediaInfo>("/api/videos/media", TestApp.Json))!.Installed);
    }

    private static MultipartFormDataContent Form(string format, string effect, string motion, bool showTitle, bool showCover = true, string? color = null, string? media = null)
    {
        var layout = VideoLayout.For(format);
        var form = new MultipartFormDataContent
        {
            { new StringContent(format), "format" },
            { new StringContent(effect), "effect" },
            { new StringContent(color ?? ""), "color" },
            { new StringContent(motion), "motion" },
            { new StringContent(showCover ? "true" : "false"), "showCover" },
            { new StringContent(showTitle ? "true" : "false"), "showTitle" },
        };
        if (media is not null)
        {
            form.Add(new StringContent(media), "media");
        }
        // A photo video's background is its pictures, darkened by the shade.
        var names = media is null ? new List<string> { "background" } : ["shade"];
        if (showCover)
        {
            names.Add("cover");
        }
        if (showTitle)
        {
            names.Add("title");
        }
        if (motion == VideoMotions.Particles)
        {
            names.Add(motion);
        }
        foreach (var name in names)
        {
            form.Add(new ByteArrayContent(Png(layout.Width, layout.Height)), name, $"{name}.png");
        }
        if (motion == VideoMotions.Plasma)
        {
            var area = layout.PlasmaArea(showCover);
            for (var i = 0; i < VideoLayout.PlasmaFrames; i++)
            {
                form.Add(new ByteArrayContent(Png(area.Width, area.Height)), $"plasma{i}", $"plasma{i}.png");
            }
        }
        return form;
    }

    /// <summary>A PNG's signature and header chunk, which is all the server reads of it.</summary>
    private static byte[] Png(int width, int height)
    {
        var data = new byte[33];
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(data, 0);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(16), width);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(20), height);
        data[24] = 8;
        data[25] = 6;
        return data;
    }

    private static async Task<VideoState> WaitForVideo(HttpClient client, string songId, Func<VideoState, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            var videos = await client.GetFromJsonAsync<VideoState[]>($"/api/songs/{songId}/videos", TestApp.Json);
            if (videos?.FirstOrDefault() is { } video && condition(video))
            {
                return video;
            }
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"The video never got there: {videos?.FirstOrDefault()?.Stage}");
            }
            await Task.Delay(20);
        }
    }
}
