using System.Net;
using System.Net.Http.Json;
using YueUI.Api.Timing;

namespace YueUI.Api.Tests;

public sealed class LyricsTimingTests : IDisposable
{
    private const string Lyrics = """
        [Verse]
        Rain on the roof tonight
        Nobody knows my name

        [Chorus]
        Hold on, hold on
        Hold on, hold on
        """;

    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    /// <summary>Each word heard half a second apart from <paramref name="start"/>.</summary>
    private static List<HeardWord> Heard(double start, params string[] words) =>
        [.. words.Select((word, i) => new HeardWord(word, start + i * 0.5, start + i * 0.5 + 0.4))];

    [Fact]
    public void Lines_take_the_time_of_their_heard_words_even_misheard_ones()
    {
        var heard = Heard(10, "Rain", "on", "the", "roof", "tonigt,").Concat(Heard(14, "nobody", "knows", "my", "name")).ToList();

        var timing = LyricsAligner.Align(Lyrics, heard, 60, "mix", DateTimeOffset.UnixEpoch);

        Assert.Equal(["Rain on the roof tonight", "Nobody knows my name", "Hold on, hold on", "Hold on, hold on"], timing.Lines.Select(l => l.Text));
        Assert.Equal((10, 12.4, 5), (timing.Lines[0].Start, timing.Lines[0].End, timing.Lines[0].Heard));
        Assert.Equal((14, 15.9), (timing.Lines[1].Start, timing.Lines[1].End));
        Assert.Equal("Verse", timing.Lines[1].Section);
        Assert.Equal("Chorus", timing.Lines[2].Section);
        // Unheard: the rest of the song shared between them.
        Assert.Equal((15.9, 37.95, 0), (timing.Lines[2].Start, timing.Lines[2].End, timing.Lines[2].Heard));
        Assert.Equal(60, timing.Lines[3].End);
        Assert.Equal(0.53, timing.Coverage);
    }

    [Fact]
    public void A_repeated_line_takes_its_own_repetition_not_the_first()
    {
        var heard = Heard(30, "hold", "on", "hold", "on").Concat(Heard(40, "hold", "on", "hold", "on")).ToList();

        var timing = LyricsAligner.Align(Lyrics, heard, 60, "vocals", DateTimeOffset.UnixEpoch);

        Assert.Equal(30, timing.Lines[2].Start);
        Assert.Equal(40, timing.Lines[3].Start);
        // The verse, unheard, fills the time before the chorus.
        Assert.Equal((0, 15), (timing.Lines[0].Start, timing.Lines[0].End));
    }

    [Fact]
    public void Subtitles_are_numbered_with_comma_milliseconds()
    {
        var timing = new LyricsTiming([new TimedLine("Rain", null, 1.5, 62.25, 1)], "mix", 1, DateTimeOffset.UnixEpoch);

        Assert.Equal("1\n00:00:01,500 --> 00:01:02,250\nRain\n\n", LyricsAligner.Srt(timing));
    }

    [Theory]
    [InlineData("German, synthwave", "", "de")]
    [InlineData("pop, English", "", "en")]
    [InlineData("pop", "Grüße", "de")]
    [InlineData("pop", "hello", null)]
    public void The_language_comes_from_the_style_or_the_umlauts(string style, string lyrics, string? language) =>
        Assert.Equal(language, LyricsTimer.Language(style, lyrics));

    [Fact]
    public async Task The_player_asks_for_a_songs_timing_and_gets_it_once_whisper_heard_it()
    {
        const string run = "20260921-165850-Neon-Night";
        _app.AddSong(run, "song1");
        _app.Listener.Installed = true;
        _app.Listener.Words = Heard(5, "La", "la");
        var client = _app.CreateClient();

        var before = (await client.GetFromJsonAsync<TimingAnswer>($"/api/songs/{run}/song1/timing", TestApp.Json))!;
        Assert.Null(before.Timing);
        Assert.Equal("none", before.Info.State);

        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/api/songs/{run}/song1/timing", null)).StatusCode);
        TimingAnswer? after = null;
        for (var tries = 0; tries < 100 && after?.Timing is null; tries++)
        {
            await Task.Delay(50);
            after = await client.GetFromJsonAsync<TimingAnswer>($"/api/songs/{run}/song1/timing", TestApp.Json);
        }

        var line = Assert.Single(after!.Timing!.Lines);
        Assert.Equal(("La la", 5.0), (line.Text, line.Start));
        Assert.Equal("mix", after.Timing.Source);
        var call = Assert.Single(_app.Listener.Calls);
        Assert.EndsWith("audio.flac", call.Audio);
        Assert.Equal("La la", call.Prompt);
        var srt = await client.GetStringAsync($"/api/songs/{run}/song1/timing.srt");
        Assert.Contains("00:00:05,000 --> ", srt);

        // Deleting the song deletes its timing.
        await client.DeleteAsync($"/api/songs/{run}/song1");
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_app.Root, "lyrics-timing"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Without_whisper_asking_says_how_to_install_it()
    {
        _app.AddSong("20260921-165850-Neon-Night", "song1");

        var response = await _app.CreateClient().PostAsync("/api/songs/20260921-165850-Neon-Night/song1/timing", null);

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
    }
}
