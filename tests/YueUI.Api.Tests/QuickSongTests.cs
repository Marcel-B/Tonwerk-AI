using System.Net;
using System.Net.Http.Json;
using YueUI.Api.Lyrics;

namespace YueUI.Api.Tests;

/// <summary>The shortcut's one call: a draft, then the song with it.</summary>
public sealed class QuickSongTests : IDisposable
{
    private readonly TestApp _app = new();
    private readonly HttpClient _client;

    public QuickSongTests() => _client = _app.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _app.Dispose();
    }

    [Fact]
    public async Task The_keywords_become_lyrics_and_then_a_song_in_the_newest_songs_style()
    {
        _app.AddSong("20260921-165850-Neon-Night", "song1");

        var response = await _client.PostAsJsonAsync("/api/quick", new { keywords = "  regen auf dem dach, nachts  ", batch = 2 });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var started = (await response.Content.ReadFromJsonAsync<QuickSongStarted>(TestApp.Json))!;
        Assert.Equal("Regen auf dem dach, nachts", started.Title);
        Assert.Equal("Dark synthwave", started.Style);

        var command = await (await _app.StartedWorker()).NextCommand();
        Assert.Equal("generate", (string?)command["cmd"]);
        Assert.Equal("Dark synthwave", (string?)command["style"]);
        Assert.Equal("[Verse]\nLine one\n\n[Chorus]\nLine two", (string?)command["lyrics"]);
        Assert.Equal("Regen auf dem dach, nachts", (string?)command["title"]);
        Assert.Equal(2, (int?)command["batch"]);
        var system = (string)Assert.Single(_app.LmStudio.Requests, r => r.Path == "/v1/chat/completions").Body!["messages"]![0]!["content"]!;
        Assert.Contains("Write in German", system, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_song_to_take_it_from_the_style_has_to_be_given()
    {
        var response = await _client.PostAsJsonAsync("/api/quick", new { keywords = "regen", language = "fr" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"style\"", body, StringComparison.Ordinal);
        Assert.Contains("\"language\"", body, StringComparison.Ordinal);
        Assert.Empty(_app.LmStudio.Requests);
    }

    [Theory]
    [InlineData("kurz", "Kurz")]
    [InlineData("ein sehr langer satz über den regen, der nachts auf das dach fällt und nicht aufhört zu fallen",
        "Ein sehr langer satz über den regen, der nachts auf das dach")]
    public void A_title_is_the_keywords_beginning(string keywords, string title) =>
        Assert.Equal(title, QuickSongs.TitleFrom(keywords));
}
