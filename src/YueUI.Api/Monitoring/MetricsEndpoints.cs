using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Options;
using YueUI.Api.Data;
using YueUI.Api.Worker;

namespace YueUI.Api.Monitoring;

/// <summary>
/// <c>GET /metrics</c> in Prometheus' text format, written by hand: a few dozen series do not need a client library,
/// and the names stay what the dashboard in <c>deploy/monitoring/</c> expects.
/// </summary>
public static class MetricsEndpoints
{
    public const string ContentType = "text/plain; version=0.0.4; charset=utf-8";

    public static WebApplication MapMetrics(this WebApplication app)
    {
        app.MapGet("/metrics", (HttpContext context, IOptions<MonitoringOptions> options) =>
            options.Value.Metrics
                ? Results.Text(Write(context.RequestServices), ContentType)
                : Results.NotFound())
            .ExcludeFromDescription();
        return app;
    }

    public static string Write(IServiceProvider services)
    {
        var worker = services.GetRequiredService<WorkerHost>();
        var memory = services.GetRequiredService<Memory.ModelMemory>();
        var jobs = services.GetRequiredService<JobMetrics>();
        var feed = services.GetRequiredService<LogFeed>();
        var paths = services.GetRequiredService<YuePaths>();
        var data = services.GetRequiredService<IOptions<DataOptions>>().Value;
        var hub = services.GetRequiredService<Status.StatusHub>();
        var videos = services.GetRequiredService<Video.VideoMaker>();
        var machines = services.GetRequiredService<Machines.Machines>();
        var idle = services.GetRequiredService<Status.IdleClock>();
        var cloud = services.GetRequiredService<CloudUsage>();
        var backup = services.GetRequiredService<Backup.CloudBackup>();
        var synthesis = services.GetRequiredService<Stats.SynthesisStats>();

        var text = new PrometheusText();
        var version = typeof(MetricsEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        text.Gauge("tonwerk_build_info", "The running build; the value is always 1.", [([("version", version)], 1)]);

        // The worker's part only, not a full snapshot: that would scan the process table for YuE Studio on every scrape.
        var part = worker.Part();
        text.Gauge("tonwerk_worker_state", "Whether YuE Studio's worker process is stopped, starting or ready.",
            Enum.GetValues<WorkerStatus>().Select(status =>
                (Labels(("state", status.ToString().ToLowerInvariant())), part.Worker.Status == status ? 1.0 : 0)));
        text.Gauge("tonwerk_studio_running", "The YuE Studio app is open with its own model (as last checked).", part.Worker.StudioRunning ? 1 : 0);

        var holder = memory.Holder;
        text.Gauge("tonwerk_memory_holder", "Which large model holds the memory; yue2 while songs are in the worker.",
            [
                (Labels(("model", "yue2")), worker.IsBusy ? 1 : 0),
                .. Enum.GetValues<Memory.LargeModel>().Select(model =>
                    (Labels(("model", model.ToString().ToLowerInvariant())), holder == model ? 1.0 : 0)),
            ]);
        var busy = WorkerEndpoints.Busy(worker, memory, videos);
        text.Gauge("tonwerk_busy", "A restart would cut work off (what GET /api/busy says).", busy.Busy ? 1 : 0);
        var quiet = idle.Read(busy, jobs);
        text.Gauge("tonwerk_idle", "Nothing runs or waits in any model's queue (what GET /api/idle and yue-guard read).", quiet.Idle ? 1 : 0);
        text.Gauge("tonwerk_idle_seconds", "Seconds since the last activity; 0 while something runs or waits.", quiet.IdleSeconds);

        // This Mac is always up; a helper is up when it answered its last poll.
        var macs = machines.List([]);
        text.Gauge("tonwerk_machine_up", "The Mac answered its last poll (this one always).",
            macs.Select(mac => (Labels(("machine", mac.Name)), mac.Reachable ? 1.0 : 0)));
        text.Gauge("tonwerk_machine_busy", "The Mac's YuE worker generates, its memory is held, or a job of this server runs there.",
            macs.Select(mac => (Labels(("machine", mac.Name)), mac.Busy || mac.Holder is not null ? 1.0 : 0)));

        var queue = hub.Queue;
        text.Gauge("tonwerk_queue_jobs", "Songs, renders, lyrics drafts and transcriptions waiting in the queue for the memory.",
            Enum.GetValues<Queue.JobKind>().Select(kind =>
                (Labels(("kind", kind.ToString().ToLowerInvariant())), (double)queue.Count(job => job.Kind == kind))));

        var live = jobs.Live();
        text.Gauge("tonwerk_jobs", "Jobs that wait in their own model's queue or the worker's, or run.",
            JobMetrics.Kinds.SelectMany(kind => new[] { "waiting", "running" }.Select(state =>
                (Labels(("kind", kind), ("state", state)), (double)live.GetValueOrDefault((kind, state))))));

        // Every kind and outcome on this Mac from the start, so a rate has a zero to begin with; other Macs once they ran one.
        var finished = jobs.Finished();
        var local = jobs.LocalMachine;
        var series = JobMetrics.Kinds.SelectMany(kind => JobMetrics.Outcomes.Select(outcome => (kind, outcome, local)))
            .Union(finished.Keys)
            .OrderBy(key => key.Item1, StringComparer.Ordinal).ThenBy(key => key.Item3, StringComparer.Ordinal).ThenBy(key => key.Item2, StringComparer.Ordinal);
        text.Counter("tonwerk_jobs_finished_total", "Jobs finished since the server started, by outcome and the Mac they ran on (claude for the cloud).",
            series.Select(key =>
                (Labels(("kind", key.Item1), ("outcome", key.Item2), ("machine", key.Item3)), (double)finished.GetValueOrDefault(key))));

        text.Histograms("tonwerk_job_duration_seconds", "How long finished jobs worked, waiting for the memory left out.",
            jobs.Durations()
                .OrderBy(pair => pair.Key.Kind, StringComparer.Ordinal).ThenBy(pair => pair.Key.Machine, StringComparer.Ordinal)
                .Select(pair => (Labels(("kind", pair.Key.Kind), ("machine", pair.Key.Machine)), pair.Value)));

        text.Histograms("tonwerk_synthesis_rate", "YuE2's seconds of work per second of audio, as the worker reports each synthesis.",
            synthesis.Rates()
                .OrderBy(pair => pair.Key.Engine, StringComparer.Ordinal).ThenBy(pair => pair.Key.Steps)
                .Select(pair => (Labels(("engine", pair.Key.Engine), ("steps", pair.Key.Steps.ToString(CultureInfo.InvariantCulture))), pair.Value)));
        text.Histograms("tonwerk_ane_programs_seconds", "How long the Neural Engine's programs took to get ready for a song.",
            [(Labels(), synthesis.AnePrograms())]);

        var usage = cloud.Totals();
        text.Counter("tonwerk_claude_requests_total", "Calls to Claude since the server started, by use (lyrics, sketch, translate), model and outcome.",
            usage.SelectMany(entry => new[] { ("answered", entry.Succeeded), ("failed", entry.Failed) }.Select(outcome =>
                (Labels(("use", entry.Use), ("model", entry.Model), ("outcome", outcome.Item1)), (double)outcome.Item2))));
        text.Counter("tonwerk_claude_tokens_total", "Tokens Claude read and wrote since the server started, thinking included in output.",
            usage.SelectMany(entry => new[] { ("input", entry.Input), ("output", entry.Output) }.Select(direction =>
                (Labels(("use", entry.Use), ("model", entry.Model), ("direction", direction.Item1)), (double)direction.Item2))));

        text.Counter("tonwerk_log_lines_total", "Lines of the collected log since the server started, by source and level.",
            feed.Counts.OrderBy(pair => pair.Key.Source, StringComparer.Ordinal).ThenBy(pair => pair.Key.Level, StringComparer.Ordinal)
                .Select(pair => (Labels(("source", pair.Key.Source), ("level", pair.Key.Level)), (double)pair.Value)));

        var settings = services.GetRequiredService<IOptions<Backup.BackupOptions>>().Value;
        var (lastRun, succeeded) = backup.Outcome;
        // Without a value the families still say they exist (no samples), so the dashboard's names stay checkable.
        text.Gauge("tonwerk_backup_configured", "A Nextcloud for the backup is set.", settings.Configured ? 1 : 0);
        text.Gauge("tonwerk_backup_last_run_success", "Whether the last backup succeeded.",
            lastRun is null ? [] : [(Labels(), lastRun.Success ? 1 : 0)]);
        text.Gauge("tonwerk_backup_last_run_timestamp_seconds", "When the last backup finished, in Unix seconds.",
            lastRun is null ? [] : [(Labels(), Seconds(lastRun.FinishedAt))]);
        text.Gauge("tonwerk_backup_last_success_timestamp_seconds", "When a backup last succeeded, in Unix seconds.",
            succeeded is { } at ? [(Labels(), Seconds(at))] : []);

        // Prometheus pulls, so these show a Loki that stopped taking the log, or a settings file that lost its address.
        text.Gauge("tonwerk_loki_shipping", "Shipping the log to Loki is configured.", feed.Shipping ? 1 : 0);
        text.Gauge("tonwerk_loki_failing", "Loki refuses the log or does not answer.", feed.FailingSince is null ? 0 : 1);
        text.Gauge("tonwerk_loki_last_sent_timestamp_seconds", "When Loki last took a batch, in Unix seconds.",
            feed.LastSent is { } sent ? [(Labels(), Seconds(sent))] : []);
        text.Counter("tonwerk_loki_lines_sent_total", "Lines Loki took since the server started.", feed.SentLines);
        text.Counter("tonwerk_loki_lines_dropped_total", "Lines given up on after Loki refused them or did not answer.", feed.DroppedLines);

        List<(IReadOnlyList<(string, string)>, double)> free = [];
        List<(IReadOnlyList<(string, string)>, double)> size = [];
        foreach (var (name, path) in new[] { ("songs", paths.OutputDir), ("data", Path.GetDirectoryName(data.ResolvedPath) ?? data.ResolvedPath) })
        {
            if (Drive(path) is { } drive)
            {
                free.Add((Labels(("volume", name)), drive.AvailableFreeSpace));
                size.Add((Labels(("volume", name)), drive.TotalSize));
            }
        }
        text.Gauge("tonwerk_disk_free_bytes", "Free space on the volume of the songs and of the data folder.", free);
        text.Gauge("tonwerk_disk_size_bytes", "Size of the volume of the songs and of the data folder.", size);

        using var process = Process.GetCurrentProcess();
        text.Gauge("process_resident_memory_bytes", "Resident memory of the server process (not the models' processes).", process.WorkingSet64);
        text.Counter("process_cpu_seconds_total", "CPU time of the server process.", process.TotalProcessorTime.TotalSeconds);
        text.Gauge("process_start_time_seconds", "When the server process started, in Unix seconds.",
            new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds() / 1000.0);
        text.Gauge("dotnet_gc_heap_bytes", "Managed heap of the server process.", GC.GetTotalMemory(false));
        return text.ToString();
    }

    private static IReadOnlyList<(string, string)> Labels(params (string, string)[] labels) => labels;

    private static double Seconds(DateTimeOffset time) => time.ToUnixTimeMilliseconds() / 1000.0;

    /// <summary>The volume a path lies on, following it up to a folder that exists; null where the system will not say.</summary>
    private static DriveInfo? Drive(string path)
    {
        try
        {
            var existing = new DirectoryInfo(path);
            while (!existing.Exists && existing.Parent is { } parent)
            {
                existing = parent;
            }
            var drive = new DriveInfo(existing.FullName);
            return drive.IsReady ? drive : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>Prometheus' text exposition format, version 0.0.4.</summary>
public sealed class PrometheusText
{
    private readonly StringBuilder _text = new();

    public void Gauge(string name, string help, double value) => Gauge(name, help, [([], value)]);

    public void Gauge(string name, string help, IEnumerable<(IReadOnlyList<(string, string)> Labels, double Value)> series) =>
        Family(name, help, "gauge", series);

    public void Counter(string name, string help, double value) => Counter(name, help, [([], value)]);

    public void Counter(string name, string help, IEnumerable<(IReadOnlyList<(string, string)> Labels, double Value)> series) =>
        Family(name, help, "counter", series);

    public void Histograms(string name, string help, IEnumerable<(IReadOnlyList<(string, string)> Labels, Histogram Histogram)> series)
    {
        Header(name, help, "histogram");
        foreach (var (labels, histogram) in series)
        {
            for (var i = 0; i < histogram.Bounds.Count; i++)
            {
                Sample(name + "_bucket", [.. labels, ("le", Number(histogram.Bounds[i]))], histogram.Counts[i]);
            }
            Sample(name + "_bucket", [.. labels, ("le", "+Inf")], histogram.Count);
            Sample(name + "_sum", labels, histogram.Sum);
            Sample(name + "_count", labels, histogram.Count);
        }
    }

    public override string ToString() => _text.ToString();

    private void Family(string name, string help, string type, IEnumerable<(IReadOnlyList<(string, string)> Labels, double Value)> series)
    {
        Header(name, help, type);
        foreach (var (labels, value) in series)
        {
            Sample(name, labels, value);
        }
    }

    private void Header(string name, string help, string type)
    {
        _text.Append("# HELP ").Append(name).Append(' ').Append(help.Replace("\\", "\\\\").Replace("\n", "\\n")).Append('\n');
        _text.Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');
    }

    private void Sample(string name, IReadOnlyList<(string Name, string Value)> labels, double value)
    {
        _text.Append(name);
        if (labels.Count > 0)
        {
            _text.Append('{');
            for (var i = 0; i < labels.Count; i++)
            {
                if (i > 0)
                {
                    _text.Append(',');
                }
                _text.Append(labels[i].Name).Append("=\"").Append(Escape(labels[i].Value)).Append('"');
            }
            _text.Append('}');
        }
        _text.Append(' ').Append(Number(value)).Append('\n');
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");

    private static string Number(double value) =>
        double.IsPositiveInfinity(value) ? "+Inf" : double.IsNaN(value) ? "NaN" : value.ToString("R", CultureInfo.InvariantCulture);
}
