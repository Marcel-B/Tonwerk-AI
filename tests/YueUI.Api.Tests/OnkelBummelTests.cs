using System.Net;
using System.Net.Http.Json;
using YueUI.Api.OnkelBummel;

namespace YueUI.Api.Tests;

public sealed class OnkelBummelTests : IDisposable
{
    private const string Run = "20260922-101500-Neon-Night";

    private readonly TestApp _app = new() { OnkelBummelUrl = "https://onkel-bummel.test" };
    private readonly HttpClient _client;

    public OnkelBummelTests() => _client = _app.CreateClient();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task A_batch_of_songs_is_one_message_once_its_last_song_is_done()
    {
        await StartSongs(3);

        _app.Worker.Emit(new { @event = "song", index = 1, path = _app.AudioPath(Run, "song1"), seconds = 187.5, quality = "full" });
        _app.Worker.Emit(new { @event = "stage", path = _app.AudioPath(Run, "song1"), stage = "ready", detail = "" });
        _app.Worker.Emit(new { @event = "failed", path = _app.AudioPath(Run, "song2"), message = "Out of memory" });
        _app.Worker.Emit(new { @event = "log", message = "two of three" });
        await _app.WaitForStatus(_client, s => s.Log.Any(entry => entry.Message == "two of three"));
        // Onkel Bummel waits for the batch.
        Assert.False(_app.OnkelBummel.TryNext(out _));

        _app.Worker.Emit(new { @event = "song", index = 3, path = _app.AudioPath(Run, "song3"), seconds = 61, quality = "full" });
        _app.Worker.Emit(new { @event = "stage", path = _app.AudioPath(Run, "song3"), stage = "ready", detail = "" });

        var (message, key) = await _app.OnkelBummel.Next();
        Assert.Equal("tonwerk", (string?)message["topic"]);
        Assert.Equal("2 von 3 Songs fertig", (string?)message["title"]);
        Assert.Equal(
            "✓ Neon Night · Song 1 · 3:08\n✗ Neon Night · Song 2: Out of memory\n✓ Neon Night · Song 3 · 1:01",
            (string?)message["message"]);
        Assert.Equal(4, (int?)message["priority"]);
        Assert.Equal(["warning", "tonwerk"], message["tags"]!.AsArray().Select(tag => (string?)tag));
        // Without Tonwerk's address there is nothing to open.
        Assert.Null(message["click"]);
        Assert.StartsWith("tonwerk-song-", key);

        _app.Worker.Emit(new { @event = "stage", path = _app.AudioPath(Run, "song3"), stage = "ready", detail = "" });
        _app.Worker.Emit(new { @event = "log", message = "again" });
        await _app.WaitForStatus(_client, s => s.Log.Any(entry => entry.Message == "again"));
        Assert.False(_app.OnkelBummel.TryNext(out _));
    }

    [Fact]
    public async Task A_single_song_says_what_its_push_says_and_a_cancelled_batch_stays_silent()
    {
        await StartSongs(2);
        _app.Worker.Emit(new { @event = "stage", path = _app.AudioPath(Run, "song1"), stage = "cancelled", detail = "" });
        _app.Worker.Emit(new { @event = "song", index = 2, path = _app.AudioPath(Run, "song2"), seconds = 187.5, quality = "full" });
        _app.Worker.Emit(new { @event = "stage", path = _app.AudioPath(Run, "song2"), stage = "ready", detail = "" });

        var (message, _) = await _app.OnkelBummel.Next();
        Assert.Equal("Song fertig", (string?)message["title"]);
        Assert.Equal("Neon Night · Song 2 · 3:08", (string?)message["message"]);
        Assert.Equal(3, (int?)message["priority"]);
        Assert.Equal(["white_check_mark", "tonwerk"], message["tags"]!.AsArray().Select(tag => (string?)tag));
    }

    [Fact]
    public async Task A_message_onkel_bummel_cannot_take_now_is_tried_again_under_the_same_key()
    {
        _app.OnkelBummel.Unavailable = 2;
        await StartSongs(1);
        _app.Worker.Emit(new { @event = "stage", path = _app.AudioPath(Run, "song1"), stage = "ready", detail = "" });

        var (message, key) = await _app.OnkelBummel.Next();
        Assert.Equal("Song fertig", (string?)message["title"]);
        Assert.NotNull(key);
        Assert.Equal(3, _app.OnkelBummel.Requests);
    }

    [Fact]
    public async Task Nothing_is_sent_while_onkel_bummel_is_not_configured()
    {
        using var off = new TestApp();
        var client = off.CreateClient();
        Assert.False((await client.GetFromJsonAsync<OnkelBummelStatus>("/api/onkel-bummel"))!.Configured);
        Assert.Equal(OnkelBummelCheck.NotConfigured, (await Test(client)).State);
        Assert.Equal(0, off.OnkelBummel.Requests);
    }

    [Fact]
    public async Task The_settings_write_the_env_file_and_the_test_message_finds_token_and_topic()
    {
        var envFile = Path.Combine(_app.Root, "config", "onkel-bummel.env");
        Directory.CreateDirectory(Path.GetDirectoryName(envFile)!);
        File.WriteAllLines(envFile, ["# Onkel Bummel", "OTHER=1"]);
        using var app = new TestApp { OnkelBummelEnvFile = envFile };
        var client = app.CreateClient();

        Assert.Equal(
            new OnkelBummelStatus(false, true, null, true, "tonwerk", null, true),
            await client.GetFromJsonAsync<OnkelBummelStatus>("/api/onkel-bummel"));

        var response = await client.PutAsJsonAsync(
            "/api/onkel-bummel",
            new OnkelBummelSettings(" https://onkel-bummel.test/ ", FakeOnkelBummel.Token, "", "https://mini.test:8443/"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            new OnkelBummelStatus(true, true, "https://onkel-bummel.test", true, "tonwerk", "https://mini.test:8443", true),
            await response.Content.ReadFromJsonAsync<OnkelBummelStatus>());
        Assert.Equal(
            ["# Onkel Bummel", "OTHER=1", "ONKELBUMMEL_URL=https://onkel-bummel.test", "ONKELBUMMEL_LINK=https://mini.test:8443",
                $"ONKELBUMMEL_TOKEN={FakeOnkelBummel.Token}"],
            File.ReadAllLines(envFile));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(envFile));
        }

        Assert.Equal(new OnkelBummelCheck("ok", null), await Test(client));
        var (message, _) = await app.OnkelBummel.Next();
        Assert.Equal("Nachrichten über Onkel Bummel funktionieren.", (string?)message["message"]);
        Assert.Equal("https://mini.test:8443/", (string?)message["click"]);

        // A topic the token may not send to, and a token Onkel Bummel does not know, are what the test names.
        await client.PutAsJsonAsync("/api/onkel-bummel", new OnkelBummelSettings("https://onkel-bummel.test", null, "garage", null));
        Assert.Equal(OnkelBummelCheck.Topic, (await Test(client)).State);
        await client.PutAsJsonAsync("/api/onkel-bummel", new OnkelBummelSettings("https://onkel-bummel.test", "ob_wrong", null, null));
        Assert.Equal(OnkelBummelCheck.Token, (await Test(client)).State);

        // Switched off, the address and token stay but no song is announced.
        await client.PutAsJsonAsync(
            "/api/onkel-bummel", new OnkelBummelSettings("https://onkel-bummel.test", FakeOnkelBummel.Token, null, null, Enabled: false));
        var status = (await client.GetFromJsonAsync<OnkelBummelStatus>("/api/onkel-bummel"))!;
        Assert.True(status.Configured);
        Assert.False(status.Enabled);
        Assert.Contains("ONKELBUMMEL_OFF=1", File.ReadAllLines(envFile));
    }

    [Fact]
    public async Task The_settings_refuse_bad_values_and_need_an_env_file()
    {
        using var app = new TestApp { OnkelBummelEnvFile = Path.Combine(_app.Root, "onkel-bummel.env") };
        var client = app.CreateClient();
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await client.PutAsJsonAsync("/api/onkel-bummel", new OnkelBummelSettings("ftp://x", "ob_x", null, null))).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await client.PutAsJsonAsync("/api/onkel-bummel", new OnkelBummelSettings("https://x", "ob_x", "a b", null))).StatusCode);
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await _client.PutAsJsonAsync("/api/onkel-bummel", new OnkelBummelSettings("https://x", "ob_x", null, null))).StatusCode);
    }

    private static async Task<OnkelBummelCheck> Test(HttpClient client)
    {
        var response = await client.PostAsync("/api/onkel-bummel/test", null);
        return (await response.Content.ReadFromJsonAsync<OnkelBummelCheck>())!;
    }

    private async Task StartSongs(int songs)
    {
        Assert.Equal(HttpStatusCode.Accepted, (await _client.PostAsJsonAsync("/api/generate", new { style = "Pop", lyrics = "[verse]\nLa", title = "Neon Night" })).StatusCode);
        await _app.Worker.NextCommand();
        _app.Worker.Emit(new { @event = "ready" });
        _app.Worker.Emit(new
        {
            @event = "started",
            job = Run,
            title = "Neon Night",
            songs = Enumerable.Range(1, songs).Select(i => new { index = i, seed = i, path = _app.AudioPath(Run, $"song{i}"), priority = 1 }).ToArray(),
        });
        await _app.WaitForStatus(_client, s => s.Songs.Count == songs);
    }
}
