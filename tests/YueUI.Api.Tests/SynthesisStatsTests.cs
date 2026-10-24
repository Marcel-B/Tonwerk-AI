using System.Net.Http.Json;
using YueUI.Api.Stats;

namespace YueUI.Api.Tests;

public sealed class SynthesisStatsTests : IDisposable
{
    private readonly TestApp _app = new();
    private readonly HttpClient _client;

    public SynthesisStatsTests() => _client = _app.CreateClient();

    [Fact]
    public void The_workers_report_is_read_as_it_writes_it()
    {
        var at = DateTimeOffset.UnixEpoch;
        var record = SynthesisStats.ParseSynthesis(
            "Synthesis of 20261023-004449-Chomas/song1 done in 769 s (3.5 s per second of audio, 32 steps, mlx)", at);

        Assert.Equal(new SynthesisRecord(at, "20261023-004449-Chomas/song1", "mlx", 32, 769, 3.5), record);
        Assert.Equal(465, SynthesisStats.ParseAnePrograms(
            "Background: Neural Engine programs for 20261023-004449-Chomas/song1 ready in 465 s", at)?.Seconds);
        Assert.Null(SynthesisStats.ParseSynthesis("Synthesizing 20261023-004449-Chomas/song2 on the GPU", at));
    }

    [Fact]
    public async Task Worker_log_lines_become_statistics_per_engine()
    {
        await _client.PostAsJsonAsync("/api/generate", new { style = "pop", lyrics = "[verse]\nla" });
        await _app.Worker.NextCommand();
        _app.Worker.Emit(new { @event = "log", message = "Synthesis of run/song1 done in 769 s (3.5 s per second of audio, 32 steps, mlx)" });
        _app.Worker.Emit(new { @event = "log", message = "Synthesis of run/song2 done in 611 s (2.5 s per second of audio, 32 steps, mlx)" });
        _app.Worker.Emit(new { @event = "log", message = "Synthesis of run/song3 done in 2041 s (8.3 s per second of audio, 32 steps, ane)" });
        _app.Worker.Emit(new { @event = "log", message = "Background: Neural Engine programs for run/song3 ready in 465 s" });
        _app.Worker.Emit(new { @event = "log", message = "Synthesizing run/song4 on the Neural Engine: about 222 s of audio, full quality (32 steps)" });
        _app.Worker.Emit(new { @event = "log", message = "Neural Engine program 1/1 ready for run/song4" });
        _app.Worker.Emit(new { @event = "log", message = "Saved /x/audio.flac (221.9 s, full)" });

        SynthesisOverview? overview = null;
        await TestApp.WaitUntil(() =>
        {
            overview = _client.GetFromJsonAsync<SynthesisOverview>("/api/stats/synthesis", TestApp.Json).GetAwaiter().GetResult();
            return overview!.Recent.Count == 3 && overview.AnePrograms.Count == 2;
        });

        var mlx = Assert.Single(overview!.Engines, e => e.Engine == "mlx");
        Assert.Equal(2, mlx.Count);
        Assert.Equal(3.0, mlx.MeanRate, 3);
        Assert.Equal(2.5, mlx.BestRate);
        Assert.Equal(8.3, Assert.Single(overview.Engines, e => e.Engine == "ane").MeanRate, 3);
        Assert.Equal(465, overview.AnePrograms.LongestSeconds);
        Assert.Equal(["run/song4", "run/song3"], overview.AneRecent.Select(r => r.Song));
        Assert.True(overview.AneRecent[0].Waited);
        Assert.False(overview.AneRecent[1].Waited);

        var metrics = await _client.GetStringAsync("/metrics");
        Assert.Contains("tonwerk_synthesis_rate_count{engine=\"mlx\",steps=\"32\"} 2\n", metrics);
        Assert.Contains("tonwerk_synthesis_rate_bucket{engine=\"mlx\",steps=\"32\",le=\"3\"} 1\n", metrics);
        Assert.Contains("tonwerk_synthesis_rate_sum{engine=\"ane\",steps=\"32\"} 8.3\n", metrics);
        Assert.Contains("tonwerk_ane_programs_seconds_count 2\n", metrics);
    }

    public void Dispose()
    {
        _client.Dispose();
        _app.Dispose();
    }
}
