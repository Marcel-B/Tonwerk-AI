using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using YueUI.Api.Tonregal;

namespace YueUI.Api.Tests;

public sealed class TonregalEndpointTests : IDisposable
{
    private const string Run = "20260921-165850-Neon-Night";

    private readonly TestApp _app = new() { TonregalUrl = "http://tonregal.test:5100" };

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task A_song_goes_to_tonregal_as_tagged_flac_with_its_id_tempo_and_key_and_is_filed()
    {
        _app.AddSong(Run, "song1");
        _app.AddSong(Run, "song2", title: "Neon: Night?");
        _app.Services.GetRequiredService<Data.SqliteSongTraitStore>().SetSound($"{Run}/song2", 92, "A minor", "audio");

        var response = await Send("song2", artist: " Marcel ", genre: "Synthwave");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var transfer = (await response.Content.ReadFromJsonAsync<TonregalTransfer>())!;
        Assert.Equal(new TonregalTransfer(7, "imported", "Marcel/2026 - Neon Night/01 - Neon Night.flac", null, false), transfer);
        var (fileName, contentType, _, metadata, _) = Assert.Single(_app.Tonregal.Received);
        Assert.Equal("Neon Night-song2.flac", fileName);
        Assert.Equal("audio/flac", contentType);
        Assert.Equal($"{Run}/song2", metadata.GetProperty("externalId").GetString());
        Assert.Equal("Neon: Night?", metadata.GetProperty("title").GetString());
        Assert.Equal("Marcel", metadata.GetProperty("artist").GetString());
        Assert.Equal("Neon: Night?", metadata.GetProperty("album").GetString());
        Assert.Equal(2026, metadata.GetProperty("year").GetInt32());
        Assert.Equal(2, metadata.GetProperty("track").GetInt32());
        Assert.Equal("Synthwave", metadata.GetProperty("genre").GetString());
        Assert.Equal(92, metadata.GetProperty("bpm").GetDouble());
        Assert.Equal("A minor", metadata.GetProperty("key").GetString());
        // The same tags as an export, AI mark included.
        Assert.Equal(Share.AudioFormat.Flac, _app.Encoder.Format);
        Assert.Equal("Marcel", _app.Tagger.Tags!.Artist);
        Assert.Equal("YuE2", _app.Tagger.Tags.AiModel);
        Assert.False(File.Exists(_app.Tagger.Path));
    }

    [Fact]
    public async Task A_song_handed_over_again_says_tonregal_kept_the_first()
    {
        _app.AddSong(Run, "song1");
        _app.Tonregal.PollsUntilFiled = 0;

        await Send("song1");
        var transfer = (await (await Send("song1")).Content.ReadFromJsonAsync<TonregalTransfer>())!;

        Assert.True(transfer.Repeated);
        Assert.Equal("imported", transfer.State);
        Assert.Equal(0, _app.Tonregal.Polls);
    }

    [Fact]
    public async Task A_file_tonregal_has_already_comes_back_rejected_with_its_reason()
    {
        _app.AddSong(Run, "song1");
        _app.Tonregal.Outcome = "rejected";

        var transfer = (await (await Send("song1")).Content.ReadFromJsonAsync<TonregalTransfer>())!;

        Assert.Equal(("rejected", "Already in the library."), (transfer.State, transfer.Error));
    }

    [Fact]
    public async Task Tonregals_refusal_is_a_bad_gateway_with_its_reason()
    {
        _app.AddSong(Run, "song1");
        _app.Tonregal.Status = HttpStatusCode.Unauthorized;

        var response = await Send("song1");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Contains("401: Wrong API key.", problem.GetProperty("detail").GetString());
        Assert.Equal(401, problem.GetProperty("tonregalStatus").GetInt32());
    }

    [Fact]
    public async Task Without_an_artist_nothing_is_sent()
    {
        _app.AddSong(Run, "song1");

        var response = await Send("song1", artist: " ");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_app.Tonregal.Received);
    }

    [Fact]
    public async Task An_unknown_song_is_not_found()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Send("song1")).StatusCode);
    }

    [Fact]
    public async Task The_status_says_whether_tonregal_is_configured()
    {
        Assert.True((await _app.CreateClient().GetFromJsonAsync<TonregalStatus>("/api/tonregal"))!.Configured);

        using var off = new TestApp();
        off.AddSong(Run, "song1");
        Assert.False((await off.CreateClient().GetFromJsonAsync<TonregalStatus>("/api/tonregal"))!.Configured);
        var form = new MultipartFormDataContent { { new StringContent("Marcel"), "artist" } };
        Assert.Equal(HttpStatusCode.NotImplemented, (await off.CreateClient().PostAsync($"/api/songs/{Run}/song1/tonregal", form)).StatusCode);
    }

    [Fact]
    public async Task The_settings_write_address_and_key_into_the_env_file_and_the_check_finds_them_right()
    {
        var envFile = Path.Combine(_app.Root, "config", "tonregal.env");
        Directory.CreateDirectory(Path.GetDirectoryName(envFile)!);
        File.WriteAllLines(envFile, ["# Tonregal", "OTHER=1", "TONREGAL_API_KEY=old"]);
        using var app = new TestApp { TonregalEnvFile = envFile };
        var client = app.CreateClient();

        var before = (await client.GetFromJsonAsync<TonregalStatus>("/api/tonregal"))!;
        Assert.Equal(new TonregalStatus(false, null, true, true), before);

        var response = await client.PutAsJsonAsync("/api/tonregal", new TonregalSettings(" http://tonregal.test:5100/ ", FakeTonregal.Key));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            new TonregalStatus(true, "http://tonregal.test:5100", true, true),
            await response.Content.ReadFromJsonAsync<TonregalStatus>());
        Assert.Equal(["# Tonregal", "OTHER=1", $"TONREGAL_API_KEY={FakeTonregal.Key}", "TONREGAL_URL=http://tonregal.test:5100"], File.ReadAllLines(envFile));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(envFile));
        }
        Assert.Equal(new TonregalCheck("ok", null), await Check(client));

        // An empty key keeps the one set; a wrong one is what the check names.
        await client.PutAsJsonAsync("/api/tonregal", new TonregalSettings("http://tonregal.test:5100", ""));
        Assert.Contains($"TONREGAL_API_KEY={FakeTonregal.Key}", File.ReadAllLines(envFile));
        await client.PutAsJsonAsync("/api/tonregal", new TonregalSettings("http://tonregal.test:5100", "wrong"));
        Assert.Equal("key", (await Check(client)).State);

        // An empty address turns the handover off.
        await client.PutAsJsonAsync("/api/tonregal", new TonregalSettings("", null));
        Assert.False((await client.GetFromJsonAsync<TonregalStatus>("/api/tonregal"))!.Configured);
        Assert.Equal("notConfigured", (await Check(client)).State);
    }

    [Fact]
    public async Task The_settings_refuse_a_bad_address_and_need_an_env_file()
    {
        using var app = new TestApp { TonregalEnvFile = Path.Combine(_app.Root, "tonregal.env") };
        var bad = await app.CreateClient().PutAsJsonAsync("/api/tonregal", new TonregalSettings("ftp://tonregal", "key"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var fixedByServer = await _app.CreateClient().PutAsJsonAsync("/api/tonregal", new TonregalSettings("http://x", "key"));
        Assert.Equal(HttpStatusCode.Conflict, fixedByServer.StatusCode);
        Assert.False((await _app.CreateClient().GetFromJsonAsync<TonregalStatus>("/api/tonregal"))!.Editable);
    }

    [Fact]
    public async Task The_check_says_when_tonregal_has_no_ingest_keys_or_cannot_be_reached()
    {
        _app.Tonregal.Status = HttpStatusCode.ServiceUnavailable;
        Assert.Equal("setup", (await Check(_app.CreateClient())).State);
        _app.Tonregal.Status = null;
        Assert.Equal("ok", (await Check(_app.CreateClient())).State);
    }

    private static async Task<TonregalCheck> Check(HttpClient client) =>
        (await (await client.PostAsync("/api/tonregal/check", null)).Content.ReadFromJsonAsync<TonregalCheck>())!;

    private Task<HttpResponseMessage> Send(string song, string artist = "Marcel", string? genre = null)
    {
        var form = new MultipartFormDataContent { { new StringContent(artist), "artist" } };
        if (genre is not null)
        {
            form.Add(new StringContent(genre), "genre");
        }
        return _app.CreateClient().PostAsync($"/api/songs/{Run}/{song}/tonregal", form);
    }
}
