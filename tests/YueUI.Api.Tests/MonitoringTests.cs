using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using YueUI.Api.Voices;

namespace YueUI.Api.Tests;

public sealed class MonitoringTests : IDisposable
{
    private const string Run = "20260922-101500-Neon-Night";

    private readonly TestApp _app = new();
    private HttpClient? _started;

    /// <summary>Started on first use, so a test can configure the app first.</summary>
    private HttpClient Client => _started ??= _app.CreateClient();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task Metrics_answer_in_prometheus_text_format()
    {
        var response = await Client.GetAsync("/metrics");
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/plain; version=0.0.4", response.Content.Headers.ContentType!.ToString());
        Assert.Contains("# TYPE tonwerk_build_info gauge\n", text);
        Assert.Contains("tonwerk_worker_state{state=\"stopped\"} 1\n", text);
        Assert.Contains("tonwerk_memory_holder{model=\"yue2\"} 0\n", text);
        Assert.Contains("tonwerk_memory_holder{model=\"ace\"} 0\n", text);
        Assert.Contains("tonwerk_queue_jobs{kind=\"lyrics\"} 0\n", text);
        Assert.Contains("tonwerk_jobs_finished_total{kind=\"song\",outcome=\"done\"} 0\n", text);
        Assert.Contains("tonwerk_disk_free_bytes{volume=\"songs\"} ", text);
        Assert.Contains("process_resident_memory_bytes ", text);
        // Every sample line is a name, optional labels and a number.
        Assert.All(text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(line => !line.StartsWith('#')),
            line => Assert.Matches(@"^[a-z_]+(\{[a-z_]+=""[^""]*""(,[a-z_]+=""[^""]*"")*\})? -?[0-9.E+\-]+$|^.+ \+Inf$", line));
    }

    [Fact]
    public async Task Finished_songs_are_counted_with_how_long_they_worked()
    {
        await Client.PostAsJsonAsync("/api/generate", new { style = "Pop", lyrics = "[verse]\nLa", batch = 2 });
        await _app.Worker.NextCommand();
        _app.Worker.Emit(new { @event = "ready" });
        _app.Worker.Emit(new
        {
            @event = "started",
            job = Run,
            title = "Neon Night",
            songs = new[]
            {
                new { index = 1, seed = 1, path = _app.AudioPath(Run, "song1"), priority = 1 },
                new { index = 2, seed = 2, path = _app.AudioPath(Run, "song2"), priority = 1 },
            },
        });
        _app.Worker.Emit(new { @event = "stage", path = _app.AudioPath(Run, "song1"), stage = "tokens", detail = "" });
        await TestApp.WaitUntil(() => Metrics().Result.Contains("tonwerk_jobs{kind=\"song\",state=\"running\"} 1\n"));
        Assert.Contains("tonwerk_jobs{kind=\"song\",state=\"waiting\"} 1\n", await Metrics());
        Assert.Contains("tonwerk_memory_holder{model=\"yue2\"} 1\n", await Metrics());

        _app.Worker.Emit(new { @event = "song", index = 1, path = _app.AudioPath(Run, "song1"), seconds = 120.0, quality = "draft", seed = 1 });
        _app.Worker.Emit(new { @event = "stage", path = _app.AudioPath(Run, "song1"), stage = "ready", detail = "" });
        // Repeated: still one song.
        _app.Worker.Emit(new { @event = "stage", path = _app.AudioPath(Run, "song1"), stage = "ready", detail = "" });
        _app.Worker.Emit(new { @event = "failed", path = _app.AudioPath(Run, "song2"), message = "out of memory" });
        await TestApp.WaitUntil(() => Metrics().Result.Contains("tonwerk_jobs_finished_total{kind=\"song\",outcome=\"failed\"} 1\n"));

        var text = await Metrics();
        Assert.Contains("tonwerk_jobs_finished_total{kind=\"song\",outcome=\"done\"} 1\n", text);
        Assert.Contains("tonwerk_job_duration_seconds_bucket{kind=\"song\",le=\"5\"} 1\n", text);
        Assert.Contains("tonwerk_job_duration_seconds_bucket{kind=\"song\",le=\"+Inf\"} 1\n", text);
        // A failed song counts, but its time does not go into the durations.
        Assert.Contains("tonwerk_job_duration_seconds_count{kind=\"song\"} 1\n", text);
        Assert.Contains("tonwerk_jobs{kind=\"song\",state=\"running\"} 0\n", text);
    }

    [Fact]
    public async Task Log_lines_are_counted_by_source_and_level()
    {
        await Client.GetAsync("/api/health");
        var logger = _app.Services.GetRequiredService<ILogger<VoiceConverter>>();
        logger.LogWarning("Seed-VC failed");
        logger.LogWarning("Seed-VC failed again");

        Assert.Contains("tonwerk_log_lines_total{source=\"voices\",level=\"warning\"} 2\n", await Metrics());
    }

    [Fact]
    public async Task Metrics_can_be_switched_off()
    {
        using var app = _app.WithWebHostBuilder(builder =>
            builder.UseSetting("Monitoring:Metrics", "false"));

        var response = await app.CreateClient().GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_log_goes_to_loki_with_its_labels_once_configured()
    {
        _app.LokiUrl = "http://loki.test:3100";
        await Client.GetAsync("/api/health");
        var logger = _app.Services.GetRequiredService<ILogger<VoiceConverter>>();
        logger.LogDebug("Too chatty for Loki");
        logger.LogError(new InvalidOperationException("disk full"), "Could not store the version");

        await TestApp.WaitUntil(() => _app.Loki.Lines().Any(line => line.Line.StartsWith("Could not store", StringComparison.Ordinal)));

        var (labels, text) = _app.Loki.Lines().Single(line => line.Line.StartsWith("Could not store", StringComparison.Ordinal));
        Assert.Equal("/loki/api/v1/push", _app.Loki.Pushes()[0].Path);
        Assert.Equal("tonwerk", labels["app"]!.GetValue<string>());
        Assert.Equal("mini", labels["host"]!.GetValue<string>());
        Assert.Equal("voices", labels["source"]!.GetValue<string>());
        Assert.Equal("error", labels["level"]!.GetValue<string>());
        Assert.Contains("disk full", text);
        Assert.DoesNotContain(_app.Loki.Lines(), line => line.Line.Contains("Too chatty", StringComparison.Ordinal));
        Assert.Contains(_app.Loki.Lines(), line => line.Line == "Sending the log to Loki as host mini");
        var report = await Client.GetStringAsync("/api/logs/report");
        Assert.Contains("Loki: on, last sent ", report);
        Assert.DoesNotContain("loki.test", report);
    }

    [Fact]
    public async Task A_batch_loki_refuses_for_the_moment_is_sent_again()
    {
        _app.LokiUrl = "http://loki.test:3100";
        _app.Loki.Refusals.Enqueue(HttpStatusCode.ServiceUnavailable);
        await Client.GetAsync("/api/health");

        _app.Services.GetRequiredService<ILogger<VoiceConverter>>().LogWarning("Seed-VC failed");

        await TestApp.WaitUntil(() => _app.Loki.Lines().Any(line => line.Line == "Seed-VC failed"));
        Assert.Empty(_app.Loki.Refusals);
    }

    [Fact]
    public async Task Without_a_loki_address_nothing_is_sent()
    {
        await Client.GetAsync("/api/health");
        _app.Services.GetRequiredService<ILogger<VoiceConverter>>().LogError("Seed-VC failed");
        await Task.Delay(100);

        Assert.Empty(_app.Loki.Pushes());
        var logged = await Client.GetStringAsync("/api/logs?q=Loki");
        Assert.Contains("Loki is off: Monitoring:Loki:Url is not set", logged);
    }

    private Task<string> Metrics() => Client.GetStringAsync("/metrics");
}
