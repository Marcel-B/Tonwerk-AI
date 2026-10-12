using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using YueUI.Api.Data;
using YueUI.Api.Export;
using YueUI.Api.Storage;
using YueUI.Api.Video;
using YueUI.Api.Voices;

namespace YueUI.Api.Tests;

public sealed class StorageEndpointTests : IDisposable
{
    private const string Run = "20260921-165850-Neon-Night";
    private const string SongId = $"{Run}/song1";

    private readonly TestApp _app = new();
    private readonly HttpClient _client;

    public StorageEndpointTests() => _client = _app.CreateClient();

    [Fact]
    public async Task Usage_measures_each_kind_and_counts_what_goes_with_a_song_into_the_song()
    {
        _app.AddSong(Run, "song1");
        _app.AddSong(Run, "song2");
        _app.AddTranscription("My-Song-20260923-201500");
        var version = AddVersion(SongId, 1000);
        var stems = AddStems(SongId, 2000);
        var video = AddVideo(SongId, 5000);
        Service<SqliteCoverStore>().Set(SongId, new CoverImage(new byte[300], "image/jpeg"));

        var usage = await Usage();

        var songs = Category(usage, "songs");
        Assert.Equal("library", songs.Group);
        Assert.Equal(2, songs.Count);
        Assert.Equal(1000, Category(usage, "versions").Bytes);
        Assert.Equal(2000, Category(usage, "stems").Bytes);
        Assert.Equal(5000, Category(usage, "videos").Bytes);
        Assert.Equal(300, Category(usage, "covers").Bytes);
        Assert.Equal(1, Category(usage, "transcriptions").Count);
        Assert.Contains(usage.Volumes, v => v.Path == songs.Volume && v.TotalBytes > 0);

        var song = Assert.Single(usage.Items, i => i.Kind == "song" && i.Id == SongId);
        var bare = Assert.Single(usage.Items, i => i.Kind == "song" && i.Id == $"{Run}/song2");
        Assert.Equal("Neon Night", song.Title);
        // Deleting the song deletes its version, stems, video and cover with it.
        Assert.Equal(bare.Bytes + 1000 + 2000 + 5000 + 300, song.Bytes);
        Assert.Equal(song, usage.Items.First(i => i.Kind == "song"));
        Assert.Equal(5000, Assert.Single(usage.Items, i => i.Kind == "video" && i.Id == video).Bytes);
        Assert.Equal("htdemucs", Assert.Single(usage.Items, i => i.Kind == "stems" && i.Id == stems).Detail);
        Assert.Equal("Anna", Assert.Single(usage.Items, i => i.Kind == "version" && i.Id == version).Detail);
        Assert.Equal(0, usage.Leftovers.Count);
    }

    [Fact]
    public async Task Usage_without_anything_made_yet_answers_an_empty_library()
    {
        var usage = await Usage();

        Assert.DoesNotContain(usage.Categories, c => c.Group == "library");
        Assert.Empty(usage.Items);
    }

    [Fact]
    public async Task Leftovers_are_files_no_row_points_to_and_only_old_ones_are_deleted()
    {
        _app.AddSong(Run, "song1");
        var kept = AddStems(SongId, 2000);
        var data = _app.Root;
        var orphanStems = Folder(Path.Combine(data, "stems", "gone"), 700);
        var orphanVideo = Folder(Path.Combine(data, "videos", "gone"), 800);
        var orphanVersion = File(Path.Combine(data, "versions", "gone.flac"), 900);
        // The streaming copy of a run deleted while the copy was being made.
        var orphanStream = Folder(Path.Combine(data, "stream", "20260101-000000-Deleted"), 100);
        var liveStream = File(Path.Combine(data, "stream", Run, "song1.m4a"), 50);
        // A video whose row is not written yet looks the same, only younger.
        var young = Path.Combine(data, "videos", "young");
        Directory.CreateDirectory(young);
        System.IO.File.WriteAllBytes(Path.Combine(young, "video.mp4"), new byte[10]);
        foreach (var path in new[] { orphanStems, orphanVideo, orphanVersion, orphanStream, liveStream, Service<SqliteStemStore>().Folder(kept) })
        {
            Age(path);
        }

        var usage = await Usage();
        Assert.Equal(new StorageLeftovers(4, 700 + 800 + 900 + 100), usage.Leftovers);

        var response = await _client.PostAsync("/api/storage/leftovers/delete", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new StorageLeftovers(4, 2500), await response.Content.ReadFromJsonAsync<StorageLeftovers>(TestApp.Json));
        Assert.False(Directory.Exists(orphanStems));
        Assert.False(Directory.Exists(orphanVideo));
        Assert.False(System.IO.File.Exists(orphanVersion));
        Assert.False(Directory.Exists(orphanStream));
        Assert.True(System.IO.File.Exists(liveStream));
        Assert.True(Directory.Exists(young));
        Assert.True(Directory.Exists(Service<SqliteStemStore>().Folder(kept)));
        Assert.Equal(0, (await Usage()).Leftovers.Count);
    }

    private async Task<StorageUsage> Usage() =>
        (await _client.GetFromJsonAsync<StorageUsage>("/api/storage/usage", TestApp.Json))!;

    private static StorageCategory Category(StorageUsage usage, string id) => Assert.Single(usage.Categories, c => c.Id == id);

    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();

    private string AddVersion(string songId, int bytes)
    {
        var store = Service<SqliteVersionStore>();
        var version = new VersionState
        {
            Id = Guid.NewGuid().ToString("N"),
            SongId = songId,
            VoiceId = "anna",
            VoiceLabel = "Anna",
            Stage = "done",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        File(store.FilePath(version.Id), bytes);
        store.Add(version);
        return version.Id;
    }

    private string AddStems(string songId, int bytes)
    {
        var store = Service<SqliteStemStore>();
        var set = new StemSetState
        {
            Id = Guid.NewGuid().ToString("N"),
            SongId = songId,
            Model = "htdemucs",
            Stage = "done",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        File(Path.Combine(store.Folder(set.Id), "vocals.flac"), bytes);
        store.Add(set);
        return set.Id;
    }

    private string AddVideo(string songId, int bytes)
    {
        var store = Service<SqliteVideoStore>();
        var video = new VideoState
        {
            Id = Guid.NewGuid().ToString("N"),
            SongId = songId,
            Title = "Neon Night",
            Format = VideoFormats.Landscape,
            Effect = VideoEffects.Bars,
            Stage = "done",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        File(store.VideoPath(video.Id), bytes);
        store.Add(video);
        return video.Id;
    }

    private static string File(string path, int bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private static string Folder(string path, int bytes)
    {
        File(Path.Combine(path, "file.bin"), bytes);
        return path;
    }

    /// <summary>Two hours old, everything in it too, past <see cref="StorageReport.LeftoverAge"/>.</summary>
    private static void Age(string path)
    {
        var then = DateTime.UtcNow.AddHours(-2);
        if (Directory.Exists(path))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
            {
                if (Directory.Exists(entry))
                {
                    Directory.SetLastWriteTimeUtc(entry, then);
                }
                else
                {
                    System.IO.File.SetLastWriteTimeUtc(entry, then);
                }
            }
            Directory.SetLastWriteTimeUtc(path, then);
            return;
        }
        System.IO.File.SetLastWriteTimeUtc(path, then);
    }

    public void Dispose()
    {
        _client.Dispose();
        _app.Dispose();
    }
}
