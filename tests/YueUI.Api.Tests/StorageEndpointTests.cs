using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using YueUI.Api.Data;
using YueUI.Api.Storage;
using YueUI.Api.Voices;

namespace YueUI.Api.Tests;

public sealed class StorageEndpointTests : IDisposable
{
    private const string Run = "20260921-165850-Neon-Night";

    private readonly TestApp _app = new();

    private readonly HttpClient _client;

    public StorageEndpointTests() => _client = _app.CreateClient();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task The_overview_measures_each_part_and_lists_its_largest_entries()
    {
        _app.AddSong(Run, "song1");
        var big = _app.AddSong(Run, "song2");
        File.WriteAllBytes(Path.Combine(big, "tokens.npy"), new byte[5000]);
        _app.AddTranscription("My-Song-20260923-201500");
        var stems = AddStems($"{Run}/song2");
        var cover = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3]);
        cover.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        await _client.PutAsync($"/api/songs/{Run}/song1/cover", new MultipartFormDataContent { { cover, "cover", "cover.jpg" } });

        var overview = (await _client.GetFromJsonAsync<StorageOverview>("/api/storage/overview", TestApp.Json))!;

        var songs = Category(overview, "songs");
        Assert.Equal(("work", 2), (songs.Group, songs.Count));
        Assert.Equal(new[] { $"{Run}/song2", $"{Run}/song1" }, songs.Largest.Select(i => i.Id));
        Assert.Equal("Neon Night · 2", songs.Largest[0].Title);
        Assert.True(songs.Bytes >= 5000 + songs.Largest[1].Bytes, "song folders add up");
        // Transcriptions lie under the songs folder but count on their own.
        var transcriptions = Category(overview, "transcriptions");
        Assert.Equal("transcription", Assert.Single(transcriptions.Largest).Kind);
        Assert.True(songs.Bytes < songs.Largest.Sum(s => s.Bytes) + transcriptions.Bytes);
        var stemSet = Assert.Single(Category(overview, "stems").Largest);
        Assert.Equal(("stems", stems, $"{Run}/song2", "Neon Night · 2", (long)FakeMixer.Flac.Length * 2),
            (stemSet.Kind, stemSet.Id, stemSet.SongId, stemSet.Title, stemSet.Bytes));
        Assert.Equal(7, Assert.Single(Category(overview, "covers").Largest).Bytes);
        Assert.Contains(overview.Categories, c => c.Key == "yue2" && c.Group == "models");
        Assert.NotNull(songs.Volume);
        Assert.Contains(overview.Volumes, v => v.Mount == songs.Volume && v.Total > 0);
    }

    [Fact]
    public async Task Sung_takes_and_own_voices_count_as_work_and_soulx_singer_as_models_and_programs()
    {
        var store = _app.Services.GetRequiredService<SqliteSingingStore>();
        var now = DateTimeOffset.UtcNow;
        store.Add(new Api.Singing.SingTake { Id = "take1", Title = "Twinkle", VoiceLabel = "Englisch", Stage = "done", CreatedAt = now }, []);
        Directory.CreateDirectory(Path.GetDirectoryName(store.AudioPath("take1"))!);
        File.WriteAllBytes(store.AudioPath("take1"), new byte[400]);
        store.AddVoice(new Api.Singing.SingingOwnVoice("voice1", "Meine Stimme", "", "twinkle", 5, 7, 0, 60, 67, now));
        Directory.CreateDirectory(Path.GetDirectoryName(store.VoiceAudioPath("voice1"))!);
        File.WriteAllBytes(store.VoiceAudioPath("voice1"), new byte[100]);
        File.WriteAllText(store.VoiceMetadataPath("voice1"), "{}");
        // By default SoulX-Singer's environment and weights lie in the same folder as the takes.
        var singing = Path.Combine(_app.Root, "singing");
        Directory.CreateDirectory(Path.Combine(singing, "env", "bin"));
        File.WriteAllBytes(Path.Combine(singing, "env", "bin", "python"), new byte[1000]);
        Directory.CreateDirectory(Path.Combine(singing, "models", "SoulX-Singer"));
        File.WriteAllBytes(Path.Combine(singing, "models", "SoulX-Singer", "model.pt"), new byte[3000]);

        var overview = (await _client.GetFromJsonAsync<StorageOverview>("/api/storage/overview", TestApp.Json))!;

        var work = Category(overview, "singing");
        Assert.Equal(("work", 2, 502L), (work.Group, work.Count, work.Bytes));
        Assert.Equal(new[] { ("singtake", "take1", 400L), ("singvoice", "voice1", 102L) },
            work.Largest.Select(i => (i.Kind, i.Id, i.Bytes)));
        Assert.Equal(3000, Category(overview, "singingModels").Bytes);
        Assert.True(Category(overview, "programs").Bytes >= 1000);

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync("/api/singing/voices/voice1")).StatusCode);
        overview = (await _client.GetFromJsonAsync<StorageOverview>("/api/storage/overview", TestApp.Json))!;
        Assert.Equal((1, 400L), (Category(overview, "singing").Count, Category(overview, "singing").Bytes));
    }

    [Fact]
    public async Task Cleaning_up_removes_old_files_without_entry_and_streaming_copies()
    {
        _app.AddSong(Run, "song1");
        var kept = AddStems($"{Run}/song1");
        var store = _app.Services.GetRequiredService<SqliteStemStore>();
        var orphan = Directory.CreateDirectory(store.Folder("leftover"));
        File.WriteAllBytes(Path.Combine(orphan.FullName, "vocals.flac"), new byte[300]);
        var fresh = Directory.CreateDirectory(store.Folder("being-made"));
        File.WriteAllBytes(Path.Combine(fresh.FullName, "vocals.flac"), new byte[300]);
        Directory.SetLastWriteTimeUtc(orphan.FullName, DateTime.UtcNow.AddDays(-2));
        var versions = Path.Combine(_app.Root, "versions");
        Directory.CreateDirectory(versions);
        File.WriteAllBytes(Path.Combine(versions, "gone.flac"), new byte[200]);
        File.SetLastWriteTimeUtc(Path.Combine(versions, "gone.flac"), DateTime.UtcNow.AddDays(-2));
        File.WriteAllBytes(Path.Combine(versions, "gone.m4a"), new byte[50]);
        var copy = Path.Combine(_app.Root, "stream", Run, "song1.m4a");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.WriteAllBytes(copy, new byte[100]);
        File.WriteAllText(copy + ".tags", "stamp");

        var overview = (await _client.GetFromJsonAsync<StorageOverview>("/api/storage/overview", TestApp.Json))!;
        Assert.Equal(300, Category(overview, "stems").Orphaned);
        Assert.Equal(200, Category(overview, "versions").Orphaned);
        Assert.Equal(155, Category(overview, "stream").Bytes);

        var orphans = await (await _client.PostAsync("/api/storage/cleanup/orphans", null)).Content.ReadFromJsonAsync<StorageCleanup>(TestApp.Json);
        Assert.Equal(new StorageCleanup(500, 2), orphans);
        Assert.False(orphan.Exists && Directory.Exists(orphan.FullName));
        Assert.True(Directory.Exists(fresh.FullName), "a folder younger than a day may be one being made");
        Assert.True(Directory.Exists(store.Folder(kept)));

        var stream = await (await _client.PostAsync("/api/storage/cleanup/stream", null)).Content.ReadFromJsonAsync<StorageCleanup>(TestApp.Json);
        Assert.Equal(new StorageCleanup(155, 3), stream);
        Assert.False(File.Exists(copy));
        Assert.False(Directory.Exists(Path.GetDirectoryName(copy)));

        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync("/api/storage/cleanup/songs", null)).StatusCode);
    }

    [Fact]
    public async Task Failed_work_that_left_files_is_cleaned_up()
    {
        _app.AddSong(Run, "song1");
        var failed = AddStems($"{Run}/song1", "failed");
        var done = AddStems($"{Run}/song1");

        var overview = (await _client.GetFromJsonAsync<StorageOverview>("/api/storage/overview", TestApp.Json))!;
        Assert.Equal(FakeMixer.Flac.Length * 2, overview.Failed);

        var cleaned = await (await _client.PostAsync("/api/storage/cleanup/failed", null)).Content.ReadFromJsonAsync<StorageCleanup>(TestApp.Json);
        Assert.Equal(new StorageCleanup(FakeMixer.Flac.Length * 2, 1), cleaned);
        var store = _app.Services.GetRequiredService<SqliteStemStore>();
        Assert.Null(store.Get(failed));
        Assert.False(Directory.Exists(store.Folder(failed)));
        Assert.NotNull(store.Get(done));
    }

    [Fact]
    public void A_moved_part_is_measured_through_its_link_but_links_inside_count_once()
    {
        var root = Directory.CreateTempSubdirectory("yueui-storage-").FullName;
        try
        {
            var target = Directory.CreateDirectory(Path.Combine(root, "ssd", "models")).FullName;
            Directory.CreateDirectory(Path.Combine(target, "blobs"));
            File.WriteAllBytes(Path.Combine(target, "blobs", "abc"), new byte[1000]);
            Directory.CreateDirectory(Path.Combine(target, "snapshots"));
            File.CreateSymbolicLink(Path.Combine(target, "snapshots", "model.safetensors"), Path.Combine(target, "blobs", "abc"));
            var link = Path.Combine(root, "models");
            Directory.CreateSymbolicLink(link, target);

            Assert.Equal((1000L, 1), StorageScanner.Measure(link));
            Assert.Equal(target, StorageScanner.RealPath(link));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static StorageCategory Category(StorageOverview overview, string key) => Assert.Single(overview.Categories, c => c.Key == key);

    private string AddStems(string songId, string stage = "done")
    {
        var store = _app.Services.GetRequiredService<SqliteStemStore>();
        var set = new StemSetState
        {
            Id = Guid.NewGuid().ToString("N"),
            SongId = songId,
            Model = "htdemucs",
            Stage = stage,
            Stems = [new StemFile("vocals", "vocals.flac", 1, null), new StemFile("drums", "drums.flac", 1, null)],
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        Directory.CreateDirectory(store.Folder(set.Id));
        File.WriteAllBytes(Path.Combine(store.Folder(set.Id), "vocals.flac"), FakeMixer.Flac);
        File.WriteAllBytes(Path.Combine(store.Folder(set.Id), "drums.flac"), FakeMixer.Flac);
        store.Add(set);
        return set.Id;
    }
}
