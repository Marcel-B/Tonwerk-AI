using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace YueUI.Api.Tests;

/// <summary>The Logic page's sessions: its state for a song or an uploaded score, kept to go on with later.</summary>
public sealed class LogicSessionTests : IDisposable
{
    private const string Song = "20260101-120000-neon/song1";

    private readonly TestApp _app = new();
    private readonly HttpClient _client;

    public LogicSessionTests()
    {
        _client = _app.CreateClient();
        _app.AddSong("20260101-120000-neon", "song1");
    }

    public void Dispose()
    {
        _client.Dispose();
        _app.Dispose();
    }

    [Fact]
    public async Task A_song_session_is_created_saved_renamed_and_deleted()
    {
        var response = await _client.PostAsync("/api/logic/sessions", Form(("name", "  Neon Night  "), ("song", Song), ("settings", """{"form":{"countIn":2}}""")));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await Json(response);
        var id = (long)created["id"]!;
        Assert.Equal("Neon Night", (string?)created["name"]);
        Assert.Equal(Song, (string?)created["songId"]);
        Assert.Null(created["scoreFile"]);
        Assert.Equal(2, (int)created["settings"]!["form"]!["countIn"]!);

        var saved = await _client.PutAsJsonAsync($"/api/logic/sessions/{id}", new { settings = new { form = new { countIn = 4 }, muted = new[] { "Bass" } } });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        // Renaming keeps the settings.
        Assert.Equal(HttpStatusCode.OK, (await _client.PutAsJsonAsync($"/api/logic/sessions/{id}", new { name = "Zweiter Versuch" })).StatusCode);

        var session = Assert.Single((await Json(await _client.GetAsync("/api/logic/sessions"))).AsArray())!;
        Assert.Equal("Zweiter Versuch", (string?)session["name"]);
        Assert.Equal(4, (int)session["settings"]!["form"]!["countIn"]!);
        Assert.Equal("Bass", (string?)session["settings"]!["muted"]![0]);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/logic/sessions/{id}/file")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/logic/sessions/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/logic/sessions/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PutAsJsonAsync($"/api/logic/sessions/{id}", new { name = "Weg" })).StatusCode);
    }

    [Fact]
    public async Task An_uploaded_score_and_its_recording_are_kept_with_the_session()
    {
        var form = Form(("name", "Melodie"), ("settings", "{}"));
        form.Add(new ByteArrayContent([.. "MThd"u8, 0, 0, 0, 6]), "file", "melodie.midi");
        var created = await Json(await _client.PostAsync("/api/logic/sessions", form));
        var id = (long)created["id"]!;
        Assert.Equal("melodie.midi", (string?)created["scoreFile"]);
        Assert.Null(created["audioFile"]);

        var file = await _client.GetAsync($"/api/logic/sessions/{id}/file");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("audio/midi", file.Content.Headers.ContentType?.MediaType);
        Assert.Equal("MThd"u8.ToArray(), (await file.Content.ReadAsByteArrayAsync())[..4]);

        var audio = new MultipartFormDataContent { { new ByteArrayContent([.. "fLaC"u8, 1, 2, 3]), "audio", "aufnahme.flac" } };
        Assert.Equal(HttpStatusCode.NoContent, (await _client.PutAsync($"/api/logic/sessions/{id}/audio", audio)).StatusCode);
        var withAudio = await Json(await _client.GetAsync($"/api/logic/sessions/{id}"));
        Assert.Equal("aufnahme.flac", (string?)withAudio["audioFile"]);
        var recording = await _client.GetAsync($"/api/logic/sessions/{id}/audio");
        Assert.Equal(HttpStatusCode.OK, recording.StatusCode);
        Assert.Equal(7, (await recording.Content.ReadAsByteArrayAsync()).Length);

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/logic/sessions/{id}/audio")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/logic/sessions/{id}/audio")).StatusCode);

        var folder = Path.Combine(_app.Root, "logic-sessions", id.ToString());
        Assert.True(File.Exists(Path.Combine(folder, "score.mid")));
        await _client.DeleteAsync($"/api/logic/sessions/{id}");
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task A_score_without_midi_bytes_is_kept_as_abc()
    {
        var form = Form(("name", "Partitur"));
        form.Add(new StringContent("X:1\nK:C\nC D E F|\n"), "file", "score.abc");
        var created = await Json(await _client.PostAsync("/api/logic/sessions", form));
        var file = await _client.GetAsync($"/api/logic/sessions/{(long)created["id"]!}/file");
        Assert.Equal("text/plain", file.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("X:1", await file.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_session_needs_a_name_and_one_source()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsync("/api/logic/sessions", Form(("name", " "), ("song", Song)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsync("/api/logic/sessions", Form(("name", "A")))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsync("/api/logic/sessions", Form(("name", "A"), ("song", "20260101-120000-neon/song9")))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsync("/api/logic/sessions", Form(("name", "A"), ("song", Song), ("settings", "[1]")))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsync("/api/logic/sessions", Form(("name", "A"), ("song", Song), ("settings", "{kaputt")))).StatusCode);
        var both = Form(("name", "A"), ("song", Song));
        both.Add(new StringContent("X:1\n"), "file", "score.abc");
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsync("/api/logic/sessions", both)).StatusCode);

        var created = await Json(await _client.PostAsync("/api/logic/sessions", Form(("name", "A"), ("song", Song))));
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await _client.PutAsJsonAsync($"/api/logic/sessions/{(long)created["id"]!}", new { settings = "kein Objekt" })).StatusCode);
        // A library song's session has no recording of its own; the song's audio.flac is played.
        var audio = new MultipartFormDataContent { { new ByteArrayContent([.. "fLaC"u8]), "audio", "a.flac" } };
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PutAsync($"/api/logic/sessions/{(long)created["id"]!}/audio", audio)).StatusCode);
    }

    [Fact]
    public async Task The_newest_change_comes_first_and_a_deleted_song_takes_its_sessions()
    {
        var first = await Json(await _client.PostAsync("/api/logic/sessions", Form(("name", "Erste"), ("song", Song))));
        await Json(await _client.PostAsync("/api/logic/sessions", Form(("name", "Zweite"), ("song", Song))));
        var upload = Form(("name", "Datei"));
        upload.Add(new StringContent("X:1\n"), "file", "score.abc");
        await _client.PostAsync("/api/logic/sessions", upload);
        await _client.PutAsJsonAsync($"/api/logic/sessions/{(long)first["id"]!}", new { settings = new { form = new { } } });

        var names = (await Json(await _client.GetAsync("/api/logic/sessions"))).AsArray().Select(s => (string?)s!["name"]);
        Assert.Equal(["Erste", "Datei", "Zweite"], names);

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/songs/{Song}")).StatusCode);
        var left = Assert.Single((await Json(await _client.GetAsync("/api/logic/sessions"))).AsArray())!;
        Assert.Equal("Datei", (string?)left["name"]);
    }

    private static MultipartFormDataContent Form(params (string Name, string Value)[] fields)
    {
        var form = new MultipartFormDataContent();
        foreach (var (name, value) in fields)
        {
            form.Add(new StringContent(value), name);
        }
        return form;
    }

    private static async Task<JsonNode> Json(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
}
