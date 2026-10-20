using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using YueUI.Api.Logs;
using YueUI.Api.Voices;
using YueUI.Api.Worker;

namespace YueUI.Api.Tests;

public sealed class LogEndpointTests : IDisposable
{
    private readonly TestApp _app = new();
    private HttpClient? _started;

    /// <summary>Started on first use, so a test can lay files out before the server opens its log.</summary>
    private HttpClient Client => _started ??= _app.CreateClient();

    public void Dispose() => _app.Dispose();

    private string LogDirectory => Path.Combine(_app.Root, "logs");

    [Fact]
    public async Task Server_messages_and_the_workers_output_land_in_one_log_with_their_source()
    {
        _app.Services.GetRequiredService<ILogger<VoiceConverter>>().LogWarning("Seed-VC failed:\n{Output}", "out of memory");
        await Client.PostAsJsonAsync("/api/generate", new { style = "Pop", lyrics = "[verse]\nLa" });
        await _app.Worker.NextCommand();
        _app.Worker.Stderr("Traceback (most recent call last):");
        await TestApp.WaitUntil(() => Read("source=worker").Result.Entries.Count > 0);
        _app.Services.GetRequiredService<WorkerHost>().LogError("The worker gave up");

        var page = await Read("source=voices,worker");

        Assert.Equal(
            [("worker", "error", "The worker gave up"), ("worker", "info", "Traceback (most recent call last):"), ("voices", "warning", "Seed-VC failed:\nout of memory")],
            page.Entries.Select(e => (e.Source, e.Level, e.Message)));
        Assert.False(page.More);
        Assert.Single(Directory.GetFiles(LogDirectory, "tonwerk-*.jsonl"));
    }

    [Fact]
    public async Task Lines_are_filtered_by_level_and_text_and_paged_from_the_newest()
    {
        var logger = _app.Services.GetRequiredService<ILogger<LogEndpointTests>>();
        for (var i = 1; i <= 5; i++)
        {
            logger.LogInformation("Line {Number}", i);
            await Task.Delay(2);
        }
        logger.LogError(new InvalidOperationException("disk full"), "Could not store the queue");

        // level=error: outside the published app ASP.NET Core warns that wwwroot is missing.
        var errors = await Read("level=error");
        var search = await Read("q=DISK");
        var first = await Read("q=line&limit=2");
        var next = await Read($"q=line&limit=2&before={Uri.EscapeDataString(first.Entries[^1].Time.ToString("O"))}");

        Assert.Equal("Could not store the queue", Assert.Single(errors.Entries).Message);
        Assert.Contains("disk full", Assert.Single(search.Entries).Exception);
        Assert.Equal(["Line 5", "Line 4"], first.Entries.Select(e => e.Message));
        Assert.True(first.More);
        Assert.Equal(["Line 3", "Line 2"], next.Entries.Select(e => e.Message));
        Assert.Equal("server", first.Entries[0].Source);
    }

    [Fact]
    public async Task The_updaters_log_is_read_along_with_its_continued_lines()
    {
        await File.WriteAllLinesAsync(Path.Combine(_app.Root, "tonwerk-update.log"),
        [
            "2026-10-02 09:00:00 Deploying abc1234 -> def5678:",
            "    def5678 Protokoll sammeln",
            "2026-10-02 09:03:00 Deploying def5678 failed; the next commit tries again, or deploy/setup-mac.sh --update by hand.",
            "2026-10-02 09:10:00 The checkout is on feature, not main; nothing done.",
        ]);

        var page = await Read("source=update");

        Assert.Equal(["warning", "error", "info"], page.Entries.Select(e => e.Level));
        Assert.Equal("Deploying abc1234 -> def5678:\n    def5678 Protokoll sammeln", page.Entries[2].Message);
        Assert.Equal(new DateTimeOffset(new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Local)), page.Entries[2].Time);
    }

    [Fact]
    public void Files_older_than_the_retention_are_deleted_when_a_day_starts()
    {
        Directory.CreateDirectory(LogDirectory);
        var old = Path.Combine(LogDirectory, "tonwerk-2000-01-01.jsonl");
        File.WriteAllText(old, """{"time":"2000-01-01T00:00:00+00:00","source":"server","level":"info","message":"old"}""" + "\n");

        _ = Client;

        Assert.False(File.Exists(old));
        Assert.Single(Directory.GetFiles(LogDirectory));
    }

    [Fact]
    public async Task The_report_names_the_build_and_engines_and_each_problem_with_what_led_to_it()
    {
        Directory.CreateDirectory(Path.Combine(_app.Root, "update"));
        await File.WriteAllTextAsync(Path.Combine(_app.Root, "update", "deployed"), "def5678abcdef\n");
        var voices = _app.Services.GetRequiredService<ILogger<VoiceConverter>>();
        _app.Services.GetRequiredService<ILogger<LogEndpointTests>>().LogInformation("Unrelated");
        voices.LogInformation("Separating song1");
        voices.LogWarning(new IOException("no space"), "The version v1 of song1 failed");

        var report = await Client.GetStringAsync("/api/logs/report");

        Assert.Contains("Updater: deployed def5678", report);
        Assert.Contains("Engines: separator service, Seed-VC service", report);
        Assert.Contains("Busy: no", report);
        Assert.Contains("Loki: off (no address set)", report);
        Assert.Matches(@"\n  \S+ \S+ INFO voices: Separating song1\n\S+ \S+ WARNING voices: The version v1 of song1 failed\n    System.IO.IOException: no space", report);
        Assert.DoesNotContain("Unrelated", report);
    }

    [Fact]
    public async Task An_unknown_source_is_refused()
    {
        var response = await Client.GetAsync("/api/logs?source=nonsense");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<LogPage> Read(string query) =>
        (await Client.GetFromJsonAsync<LogPage>("/api/logs?" + query, TestApp.Json))!;
}
