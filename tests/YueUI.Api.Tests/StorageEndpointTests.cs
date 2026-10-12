using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using YueUI.Api.Data;
using YueUI.Api.Images;
using YueUI.Api.Storage;
using YueUI.Api.Video;
using YueUI.Api.Voices;

namespace YueUI.Api.Tests;

public sealed class StorageEndpointTests : IDisposable
{
    private const string Run = "20260921-165850-Neon-Night";

    private readonly TestApp _app = new();

    private readonly HttpClient _client;

    public StorageEndpointTests() => _client = _app.CreateClient();

    public void Dispose() => _app.Dispose();

    /// <summary>Next to the database, where versions, stems and videos lie.</summary>
    private string Data => _app.Root;

    [Fact]
    public async Task Each_part_says_what_it_takes_and_a_song_counts_what_goes_with_it()
    {
        Fill(_app.AddSong(Run, "song1"), 1000);
        Fill(_app.AddSong(Run, "song2"), 5000);
        var versions = _app.Services.GetRequiredService<SqliteVersionStore>();
        versions.Add(new VersionState { Id = "v1", SongId = $"{Run}/song1", VoiceId = "anna", VoiceLabel = "Anna", Stage = "done" });
        Write(versions.FilePath("v1"), 3000);
        var videos = _app.Services.GetRequiredService<SqliteVideoStore>();
        videos.Add(new VideoState { Id = "film", SongId = $"{Run}/song1", Title = "Neon Night", Format = "portrait", Effect = "bars", Stage = "done" });
        Write(videos.VideoPath("film"), 20_000);
        _app.AddTranscription("My Song-20260921-170000");

        var report = await Report();

        var songs = Group(report, "songs");
        Assert.Equal((2, "data"), (songs.Count, songs.Section));
        // The part counts the song folders; the second song is bigger by its own files…
        Assert.Equal([$"{Run}/song2", $"{Run}/song1"], songs.Largest.Select(i => i.Id));
        Assert.True(songs.Bytes >= 6000);
        // …but deleting the first frees its version and video too.
        Assert.True(songs.Largest[1].Bytes >= 1000 + 3000 + 20_000);
        Assert.Equal(("Neon Night", "song"), (songs.Largest[1].Title, songs.Largest[1].Kind));

        var video = Assert.Single(Group(report, "videos").Largest);
        Assert.Equal(("video", "film", 20_000L, "portrait"), (video.Kind, video.Id, video.Bytes, video.Detail));
        var version = Assert.Single(Group(report, "versions").Largest);
        Assert.Equal(("version", "Anna", 3000L), (version.Kind, version.Detail, version.Bytes));
        Assert.Equal("transcription", Assert.Single(Group(report, "transcriptions").Largest).Kind);
        Assert.Contains(report.Groups, g => g.Key == "database");
        // Nothing of these was made, so the page does not list them.
        Assert.DoesNotContain(report.Groups, g => g.Key is "stems" or "covers" or "swaps" or "yue2");
        Assert.NotEmpty(report.Volumes);
        Assert.All(report.Groups, g => Assert.Contains(report.Volumes, v => v.Name == g.Volume));
    }

    [Fact]
    public async Task Model_caches_count_each_file_once()
    {
        // Hugging Face's layout: the snapshot links to the blob.
        var cache = Path.Combine(_app.Root, "install", "models", "hub", "models--m-a-p--YuE2");
        Write(Path.Combine(cache, "blobs", "abc"), 7000);
        Directory.CreateDirectory(Path.Combine(cache, "snapshots", "main"));
        File.CreateSymbolicLink(Path.Combine(cache, "snapshots", "main", "model.safetensors"), Path.Combine(cache, "blobs", "abc"));

        var yue2 = Group(await Report(), "yue2");

        Assert.Equal(("models", 7000L, (int?)null), (yue2.Section, yue2.Bytes, yue2.Count));
    }

    [Fact]
    public async Task A_part_moved_to_another_volume_is_measured_where_it_lies()
    {
        _app.AddSong(Run, "song1");
        var elsewhere = Path.Combine(_app.Root, "ssd", "Tonwerk", "stems");
        Write(Path.Combine(elsewhere, "set1", "vocals.flac"), 4000);
        _app.Services.GetRequiredService<SqliteStemStore>().Add(new StemSetState { Id = "set1", SongId = $"{Run}/song1", Model = "htdemucs", Stage = "done" });
        Directory.CreateSymbolicLink(Path.Combine(Data, "stems"), elsewhere);

        var stems = Group(await Report(), "stems");

        Assert.Equal((4000L, elsewhere, false), (stems.Bytes, stems.Moved, stems.Missing));
    }

    [Fact]
    public async Task A_part_on_a_volume_that_is_not_mounted_says_so()
    {
        Directory.CreateSymbolicLink(Path.Combine(Data, "videos"), Path.Combine(_app.Root, "unplugged", "videos"));

        var videos = Group(await Report(), "videos");

        Assert.Equal((0L, true, (string?)null), (videos.Bytes, videos.Missing, videos.Volume));
    }

    [Fact]
    public async Task Candidates_of_a_covered_song_and_one_star_songs_are_suggested()
    {
        _app.AddSong(Run, "song1");
        _app.AddSong(Run, "song2");
        var painted = await _client.PostAsJsonAsync($"/api/songs/{Run}/song1/images", new { prompt = "Neon city" });
        var image = (await painted.Content.ReadFromJsonAsync<ImageState>(TestApp.Json))!;
        var maker = _app.Services.GetRequiredService<ImageMaker>();
        await TestApp.WaitUntil(() => maker.Get(image.Id)?.Stage == "done");
        await _client.PutAsync($"/api/images/{image.Id}/cover", null);
        await _client.PutAsJsonAsync($"/api/songs/{Run}/song2/rating", new { rating = 1 });

        var report = await Report();

        var covers = Assert.Single(report.Suggestions, s => s.Key == "paintedCovers");
        Assert.Equal(("image", image.Id), (covers.Items[0].Kind, covers.Items[0].Id));
        var oneStar = Assert.Single(report.Suggestions, s => s.Key == "oneStar");
        Assert.Equal(($"{Run}/song2", 1), (oneStar.Items[0].Id, oneStar.Items[0].Rating));
        Assert.Equal("cover", Assert.Single(Group(report, "covers").Largest).Kind);
        Assert.DoesNotContain(report.Suggestions, s => s.Key == "failed");
    }

    [Fact]
    public async Task What_failed_but_left_files_is_suggested()
    {
        _app.AddSong(Run, "song1");
        var videos = _app.Services.GetRequiredService<SqliteVideoStore>();
        videos.Add(new VideoState { Id = "broken", SongId = $"{Run}/song1", Title = "Neon Night", Format = "landscape", Effect = "bars", Stage = "failed" });
        Write(Path.Combine(videos.Folder("broken"), "cover.png"), 900);

        var failed = Assert.Single((await Report()).Suggestions, s => s.Key == "failed");

        Assert.Equal(("broken", "failed", 900L), (failed.Items[0].Id, failed.Items[0].Stage, failed.Bytes));
    }

    [Fact]
    public void Links_to_folders_are_followed_once()
    {
        var root = Path.Combine(_app.Root, "walk");
        Write(Path.Combine(root, "real", "a.bin"), 100);
        Directory.CreateSymbolicLink(Path.Combine(root, "linked"), Path.Combine(root, "real"));
        // A link back up would loop forever.
        Directory.CreateSymbolicLink(Path.Combine(root, "real", "up"), root);

        Assert.Equal(100, FolderSize.Of(Path.Combine(root, "linked")));
        Assert.Equal(100, FolderSize.Of(root));
        Assert.Equal(0, FolderSize.Of(Path.Combine(root, "nothing")));
    }

    private async Task<StorageReport> Report() =>
        (await _client.GetFromJsonAsync<StorageReport>("/api/storage/usage", TestApp.Json))!;

    private static StorageGroup Group(StorageReport report, string key) => Assert.Single(report.Groups, g => g.Key == key);

    private static void Fill(string directory, int bytes) => Write(Path.Combine(directory, "tokens.npy"), bytes);

    private static void Write(string path, int bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }
}
