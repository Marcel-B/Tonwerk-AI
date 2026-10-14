using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using YueUI.Api.Analysis;
using YueUI.Api.Library;
using YueUI.Api.Timing;

namespace YueUI.Api.Tests;

public sealed class SongAnalysisTests : IDisposable
{
    private const string Run = "20260921-165850-Neon-Night";

    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    [Theory]
    [InlineData("C", "C")]
    [InlineData("Am", "Am")]
    [InlineData("A min", "Am")]
    [InlineData("F# minor", "F#m")]
    [InlineData("Db minor", "C#m")]
    [InlineData("Gb major", "F#")]
    [InlineData("bb", "Bb")]
    [InlineData("A♭ Dur", "Ab")]
    [InlineData("D dor", null)]
    [InlineData("none", null)]
    public void Keys_are_spelled_as_the_key_picker_spells_them(string text, string? expected) =>
        Assert.Equal(expected, SongAnalyzer.KeyName(text));

    [Fact]
    public void A_song_was_asked_for_the_tempo_and_key_of_its_score_or_its_ace_request()
    {
        var yue = _app.AddSong(Run, "song1");
        File.WriteAllText(Path.Combine(yue, "score.abc"), "X:1\nT:\nM:4/4\nL:1/16\nQ:1/4=88\nV: Vocal\nK:Am % minor\n");
        var ace = _app.AddSong(Run, "song2");
        File.WriteAllText(Path.Combine(ace, "score.abc"), "");
        File.WriteAllText(Path.Combine(ace, SongLibrary.AceFile), """{"bpm": 124, "keyscale": "E minor"}""");

        Assert.Equal((88.0, "Am"), SongAnalyzer.Asked(yue));
        Assert.Equal((124.0, "Em"), SongAnalyzer.Asked(ace));
        Assert.Equal((null, null), SongAnalyzer.Asked(_app.AddSong(Run, "song3")));
    }

    [Fact]
    public async Task Songs_are_heard_newest_first_and_the_library_shows_tempo_and_key()
    {
        var first = _app.AddSong(Run, "song1");
        File.WriteAllText(Path.Combine(first, "score.abc"), "X:1\nQ:1/4=60\nK:C\n");
        _app.AddSong(Run, "song2");
        _app.AddSong("20260922-100000-Later", "song1");
        _app.SongListener.Installed = true;
        _app.SongListener.Songs[$"{Run}/song1"] = (60.4, "Em");
        _app.SongListener.Songs["20260922-100000-Later/song1"] = (128, "F#m");
        _app.AnalyseSongs = true;
        var client = _app.CreateClient();

        var runs = await WaitForTraits(client, 2);

        var batch = Assert.Single(_app.SongListener.Batches);
        Assert.Equal(["20260922-100000-Later/song1", $"{Run}/song1", $"{Run}/song2"], batch.Select(s => s.Id));
        // The score's tempo goes along, so the octave is the one asked for.
        Assert.Equal(60, batch[1].TempoHint);
        var heard = runs.Single(r => r.Id == Run).Songs[0].Traits!;
        Assert.Equal((60.4, "Em", "audio"), (heard.Tempo, heard.Key, heard.From));
        Assert.Equal((128, "F#m"), (runs[0].Songs[0].Traits!.Tempo, runs[0].Songs[0].Traits!.Key));
        // Unreadable: kept back until a restart, so the next round does not hear it again.
        Assert.Null(runs.Single(r => r.Id == Run).Songs[1].Traits);
        await _app.Services.GetRequiredService<SongAnalyzer>().RoundAsync(CancellationToken.None);
        Assert.Single(_app.SongListener.Batches);
    }

    [Fact]
    public async Task Without_librosa_a_song_shows_what_its_score_asked_for()
    {
        var song = _app.AddSong(Run, "song1");
        File.WriteAllText(Path.Combine(song, "score.abc"), "X:1\nQ:1/4=96\nK:Bb\n");
        _app.AnalyseSongs = true;
        var client = _app.CreateClient();

        var runs = await WaitForTraits(client, 1);

        var traits = runs[0].Songs[0].Traits!;
        Assert.Equal((96, "Bb", "score"), (traits.Tempo, traits.Key, traits.From));
        Assert.Empty(_app.SongListener.Batches);
    }

    [Fact]
    public async Task A_timed_song_shows_how_much_of_its_lyrics_was_understood()
    {
        _app.AddSong(Run, "song1");
        _app.Listener.Installed = true;
        _app.Listener.Words = [new HeardWord("La", 5, 5.4)];
        var client = _app.CreateClient();

        await client.PostAsync($"/api/songs/{Run}/song1/timing", null);
        var runs = await WaitForTraits(client, 1);

        Assert.Equal((0.5, "mix"), (runs[0].Songs[0].Traits!.Clarity, runs[0].Songs[0].Traits!.ClarityFrom));

        // Deleting the song deletes its row.
        await client.DeleteAsync($"/api/songs/{Run}/song1");
        Assert.Empty(_app.Services.GetRequiredService<Data.SqliteSongTraitStore>().All());
    }

    [Fact]
    public async Task Songs_timed_before_clarity_was_kept_get_it_from_their_timing()
    {
        _app.AddSong(Run, "song1");
        var client = _app.CreateClient();
        await _app.Services.GetRequiredService<TimingFiles>().SaveAsync(
            Run, "song1", new LyricsTiming([new TimedLine("La la", null, 5, 6, 2)], "vocals", 0.9, DateTimeOffset.UnixEpoch), CancellationToken.None);

        await _app.Services.GetRequiredService<SongAnalyzer>().RoundAsync(CancellationToken.None);

        var runs = (await client.GetFromJsonAsync<List<RunInfo>>("/api/library", TestApp.Json))!;
        Assert.Equal((0.9, "vocals"), (runs[0].Songs[0].Traits!.Clarity, runs[0].Songs[0].Traits!.ClarityFrom));
    }

    private static async Task<List<RunInfo>> WaitForTraits(HttpClient client, int songs)
    {
        List<RunInfo> runs = [];
        for (var tries = 0; tries < 200; tries++)
        {
            runs = (await client.GetFromJsonAsync<List<RunInfo>>("/api/library", TestApp.Json))!;
            if (runs.SelectMany(r => r.Songs).Count(s => s.Traits is not null) >= songs)
            {
                return runs;
            }
            await Task.Delay(25);
        }
        return runs;
    }
}
