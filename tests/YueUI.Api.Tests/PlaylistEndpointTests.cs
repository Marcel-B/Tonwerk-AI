using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;

namespace YueUI.Api.Tests;

public sealed class PlaylistEndpointTests : IDisposable
{
    private const string Run = "20260921-165850-Neon-Night";

    private readonly TestApp _app = new();
    private readonly HttpClient _client;

    public PlaylistEndpointTests() => _client = _app.CreateClient();

    [Fact]
    public async Task There_is_one_empty_playlist_to_begin_with()
    {
        var playlist = Assert.Single(await Playlists());

        Assert.Equal(1, playlist.Id);
        Assert.Equal("Playlist", playlist.Name);
        Assert.Empty(playlist.SongIds);
    }

    [Fact]
    public async Task A_playlist_keeps_its_order_once_per_song_in_the_database()
    {
        _app.AddSong(Run, "song1");
        _app.AddSong(Run, "song2");

        var response = await _client.PutAsJsonAsync("/api/playlists/1/songs", new { songIds = new[] { $"{Run}/song2", $"{Run}/song1", $"{Run}/song2" } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([$"{Run}/song2", $"{Run}/song1"], (await response.Content.ReadFromJsonAsync<PlaylistInfo>())!.SongIds);

        // A second server over the same database, as after a restart.
        using var restarted = new TestApp();
        restarted.AddSong(Run, "song1");
        restarted.AddSong(Run, "song2");
        File.Copy(Path.Combine(_app.Root, "yueui.db"), Path.Combine(restarted.Root, "yueui.db"));
        var playlists = await restarted.CreateClient().GetFromJsonAsync<List<PlaylistInfo>>("/api/playlists");
        Assert.Equal([$"{Run}/song2", $"{Run}/song1"], Assert.Single(playlists!).SongIds);
    }

    [Fact]
    public async Task Playlists_are_created_renamed_and_keep_their_songs_apart()
    {
        _app.AddSong(Run, "song1");
        _app.AddSong(Run, "song2");

        var response = await _client.PostAsJsonAsync("/api/playlists", new { name = "  Zum Joggen  " });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<PlaylistInfo>())!;
        Assert.Equal("Zum Joggen", created.Name);
        Assert.Empty(created.SongIds);

        await _client.PutAsJsonAsync("/api/playlists/1/songs", new { songIds = new[] { $"{Run}/song1" } });
        await _client.PutAsJsonAsync($"/api/playlists/{created.Id}/songs", new { songIds = new[] { $"{Run}/song2", $"{Run}/song1" } });
        var renamed = await _client.PutAsJsonAsync($"/api/playlists/{created.Id}/name", new { name = "Abends" });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);

        var playlists = await Playlists();
        Assert.Equal(["Playlist", "Abends"], playlists.Select(p => p.Name));
        Assert.Equal([$"{Run}/song1"], playlists[0].SongIds);
        Assert.Equal([$"{Run}/song2", $"{Run}/song1"], playlists[1].SongIds);
    }

    [Fact]
    public async Task A_playlist_is_created_while_another_connection_reads()
    {
        Assert.Single(await Playlists());
        using var reader = new SqliteConnection($"Data Source={Path.Combine(_app.Root, "yueui.db")};Pooling=False");
        reader.Open();
        using var transaction = reader.BeginTransaction(deferred: true);
        using (var select = reader.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT count(*) FROM playlists";
            select.ExecuteScalar();
        }

        // The reader keeps the file from being written for a moment. INSERT ... RETURNING failed when it let go
        // ("database is locked", a 500 seen in CI), since its row was already handed out and could not be retried.
        var create = _client.PostAsJsonAsync("/api/playlists", new { name = "Später" });
        await Task.Delay(300);
        transaction.Commit();

        Assert.Equal(HttpStatusCode.Created, (await create).StatusCode);
        Assert.Equal(["Playlist", "Später"], (await Playlists()).Select(p => p.Name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task A_playlist_needs_a_name(string? name)
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/playlists", new { name })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PutAsJsonAsync("/api/playlists/1/name", new { name })).StatusCode);
        Assert.Equal("Playlist", Assert.Single(await Playlists()).Name);
    }

    [Fact]
    public async Task A_deleted_playlist_takes_its_songs_along_but_the_last_one_stays()
    {
        _app.AddSong(Run, "song1");
        var created = (await (await _client.PostAsJsonAsync("/api/playlists", new { name = "Zweite" })).Content.ReadFromJsonAsync<PlaylistInfo>())!;
        await _client.PutAsJsonAsync($"/api/playlists/{created.Id}/songs", new { songIds = new[] { $"{Run}/song1" } });

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync("/api/playlists/1")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.DeleteAsync($"/api/playlists/{created.Id}")).StatusCode);

        var left = Assert.Single(await Playlists());
        Assert.Equal(created.Id, left.Id);
        Assert.Equal([$"{Run}/song1"], left.SongIds);
    }

    [Fact]
    public async Task An_unknown_playlist_is_not_found()
    {
        _app.AddSong(Run, "song1");

        Assert.Equal(HttpStatusCode.NotFound, (await _client.PutAsJsonAsync("/api/playlists/9/songs", new { songIds = new[] { $"{Run}/song1" } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PutAsJsonAsync("/api/playlists/9/name", new { name = "X" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync("/api/playlists/9")).StatusCode);
    }

    [Fact]
    public async Task A_deleted_song_drops_out_of_the_playlist()
    {
        _app.AddSong(Run, "song1");
        _app.AddSong(Run, "song2");
        await _client.PutAsJsonAsync("/api/playlists/1/songs", new { songIds = new[] { $"{Run}/song1", $"{Run}/song2" } });

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/songs/{Run}/song1")).StatusCode);

        Assert.Equal([$"{Run}/song2"], Assert.Single(await Playlists()).SongIds);
    }

    [Theory]
    [InlineData("20260921-165850-Neon-Night/song9")]
    [InlineData("../../etc/passwd")]
    [InlineData("20260921-165850-Neon-Night")]
    public async Task Only_songs_of_the_library_are_accepted(string songId)
    {
        _app.AddSong(Run, "song1");

        var response = await _client.PutAsJsonAsync("/api/playlists/1/songs", new { songIds = new[] { $"{Run}/song1", songId } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(Assert.Single(await Playlists()).SongIds);
    }

    private async Task<List<PlaylistInfo>> Playlists() =>
        (await _client.GetFromJsonAsync<List<PlaylistInfo>>("/api/playlists"))!;

    public void Dispose()
    {
        _client.Dispose();
        _app.Dispose();
    }
}
