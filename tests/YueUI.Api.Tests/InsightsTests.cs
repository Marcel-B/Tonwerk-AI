using System.Net.Http.Json;
using YueUI.Api.Library;

namespace YueUI.Api.Tests;

public sealed class InsightsTests : IDisposable
{
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public void A_block_on_many_good_songs_outranks_one_on_a_single_perfect_song()
    {
        List<(int, List<(string, string)>)> songs =
        [
            (5, [("style", "lucky")]),
            .. Enumerable.Range(0, 10).Select(_ => (4, new List<(string, string)> { ("style", "half-time beat") })),
            (1, [("style", "half-time beat"), ("style", "lucky")]),
            (2, [("style", "polka")]),
            (2, [("style", "polka")]),
        ];

        var report = Insights.Report(songs);

        Assert.Equal(14, report.Rated);
        var style = Assert.Single(report.Groups);
        Assert.Equal(["half-time beat", "lucky", "polka"], style.Entries.Select(e => e.Value));
        Assert.Equal(11, style.Entries[0].Count);
        Assert.Equal(3.0, style.Entries[1].Average);
    }

    [Fact]
    public void A_style_counts_each_block_once_whatever_its_case()
    {
        Assert.Equal(["pop", "female vocals"], Insights.StyleBlocks("Pop, female vocals,, pop "));
    }

    [Fact]
    public async Task Only_rated_songs_count_with_their_style_and_settings()
    {
        const string run = "20260921-165850-Neon-Night";
        _app.AddSong(run, "song1");
        _app.AddSong(run, "song2");
        _app.AddSong(run, "song3");
        var client = _app.CreateClient();
        await client.PutAsJsonAsync($"/api/songs/{run}/song1/rating", new { rating = 5 });
        await client.PutAsJsonAsync($"/api/songs/{run}/song2/rating", new { rating = 3 });

        var report = (await client.GetFromJsonAsync<InsightReport>("/api/insights", TestApp.Json))!;

        Assert.Equal(2, report.Rated);
        Assert.Equal(4, report.Average);
        var style = report.Groups.Single(g => g.Key == "style");
        Assert.Equal(new InsightEntry("dark synthwave", 2, 4, 4), style.Entries.Single());
        Assert.Equal("yue2", report.Groups.Single(g => g.Key == "engine").Entries.Single().Value);
        Assert.Equal("3-4", report.Groups.Single(g => g.Key == "length").Entries.Single().Value);
    }
}
