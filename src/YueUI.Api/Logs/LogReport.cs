using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Options;
using YueUI.Api.Data;
using YueUI.Api.Lyrics;
using YueUI.Api.Speech;
using YueUI.Api.Voices;
using YueUI.Api.Worker;

namespace YueUI.Api.Logs;

/// <summary>
/// What someone looking into a problem from afar asks for first (<c>GET /api/logs/report</c>): which build runs, which
/// engines are there, what is busy, and the recent warnings and errors with the lines that led to each. Plain text,
/// copied from the log page into a chat, so it says nothing a chat should not get: no settings, keys or addresses.
/// </summary>
public sealed class LogReport(
    LogStore store,
    WorkerHost worker,
    Memory.ModelMemory memory,
    Video.VideoMaker videos,
    YuePaths paths,
    IOptions<VoiceOptions> voiceOptions,
    IOptions<SpeechOptions> speechOptions,
    IOptions<Images.ImageOptions> imageOptions,
    IOptions<DataOptions> dataOptions,
    Monitoring.LogFeed feed,
    TimeProvider time)
{
    /// <summary>Warnings and errors listed; the newest are kept.</summary>
    public const int MaxProblems = 30;

    /// <summary>Lines of the same source before a problem, within <see cref="ContextWindow"/>.</summary>
    public const int ContextLines = 5;

    private const int MaxText = 3000;

    private static readonly TimeSpan ContextWindow = TimeSpan.FromMinutes(5);

    /// <summary>Whether the log reaches Loki; the address stays out, as everything else of the settings.</summary>
    private string Loki() => feed switch
    {
        { Shipping: false } => "off (no address set)",
        { FailingSince: { } since } => $"failing since {Stamp(since)}",
        { LastSent: { } sent } => $"on, last sent {Stamp(sent)}",
        _ => "on, nothing sent yet",
    };

    public string Build(TimeSpan span)
    {
        var now = time.GetUtcNow();
        var text = new StringBuilder();
        text.AppendLine("# Tonwerk log report");
        text.AppendLine();
        text.AppendLine($"Created: {Stamp(now)} (server time {now.ToLocalTime():zzz})");
        text.AppendLine($"Server: {ServerVersion()}, .NET {Environment.Version}, {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        var deployed = ReadState("deployed");
        var failed = ReadState("failed");
        var waiting = ReadState("waiting");
        text.AppendLine($"Updater: deployed {Short(deployed) ?? "unknown"}"
            + (failed is null ? "" : $", failed {Short(failed)}")
            + (waiting is null ? "" : $", waiting with {Short(waiting)}"));
        var info = worker.Info();
        text.AppendLine($"Worker: {info.Status.ToString().ToLowerInvariant()}, extensions {Flag(info.Extensions)}, YuE Studio open {Flag(info.StudioRunning)}"
            + (info.LastError is null ? "" : $", last error: {info.LastError}"));
        var voice = voiceOptions.Value;
        text.AppendLine($"Engines: separator {Engine(voice.LocalStems, voice.StemServiceConfigured)}, Seed-VC {Engine(voice.LocalVoices, voice.VoiceServiceConfigured)}, "
            + $"speech lab {(File.Exists(speechOptions.Value.ResolvedPython) ? "installed" : "missing")}, "
            + $"images {(File.Exists(imageOptions.Value.ResolvedPython) ? "installed" : "missing")}, "
            + $"SheetSage2 {(paths.SheetSageInstalled ? "installed" : "missing")}, YuE2 worker {(File.Exists(paths.WorkerScript) ? "installed" : "missing")}");
        var busy = WorkerEndpoints.Busy(worker, memory, videos);
        text.AppendLine($"Busy: {(busy.Busy ? string.Join(", ", busy.Reasons) : "no")}");
        text.AppendLine($"Loki: {Loki()}");
        text.AppendLine();

        var hours = span.TotalHours.ToString("0.#", CultureInfo.InvariantCulture);
        var lines = store.Query(new LogQuery(After: now - span, Limit: 5000)).Entries.Reverse().ToList();
        var problems = Enumerable.Range(0, lines.Count)
            .Where(i => LogLevels.Rank(lines[i].Level) >= LogLevels.Rank(LogLevels.Warning))
            .TakeLast(MaxProblems)
            .ToList();
        if (problems.Count == 0)
        {
            text.AppendLine($"No warnings or errors in the last {hours} h.");
            return text.ToString();
        }
        text.AppendLine($"## Warnings and errors, last {hours} h (oldest first, at most {MaxProblems})");
        var shown = new HashSet<int>();
        foreach (var index in problems)
        {
            var problem = lines[index];
            text.AppendLine();
            var context = Enumerable.Range(0, index)
                .Reverse()
                .Where(i => lines[i].Source == problem.Source && problem.Time - lines[i].Time <= ContextWindow)
                .Take(ContextLines)
                .Where(i => !shown.Contains(i))
                .Reverse();
            foreach (var before in context)
            {
                Append(text, lines[before], "  ");
                shown.Add(before);
            }
            Append(text, problem, "");
            shown.Add(index);
        }
        return text.ToString();
    }

    private static void Append(StringBuilder text, LogLine line, string indent)
    {
        var body = Cut(line.Message) + (line.Exception is null ? "" : "\n" + Cut(line.Exception));
        text.Append(indent).Append(Stamp(line.Time)).Append(' ').Append(line.Level.ToUpperInvariant()).Append(' ')
            .Append(line.Source).Append(": ")
            .AppendLine(body.Replace("\n", "\n" + indent + "    ", StringComparison.Ordinal));
    }

    private static string Cut(string value) => value.Length <= MaxText ? value : value[..MaxText] + " …";

    private static string Stamp(DateTimeOffset value) => value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Flag(bool? value) => value switch { true => "yes", false => "no", null => "unknown" };

    private static string Engine(bool local, bool service) => local ? "local" : service ? "service" : "missing";

    private static string? Short(string? commit) => commit is null ? null : commit[..Math.Min(7, commit.Length)];

    /// <summary>The informational version carries the commit the build was made from (<c>1.0.0+sha</c>).</summary>
    private static string ServerVersion()
    {
        var version = typeof(LogReport).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 && version.Length > plus + 8 ? version[..(plus + 8)] : version;
    }

    /// <summary><c>deploy/update.sh</c>'s state files in the data folder (<c>update/deployed</c>, …).</summary>
    private string? ReadState(string name)
    {
        var folder = Path.GetDirectoryName(dataOptions.Value.ResolvedPath);
        try
        {
            return folder is null ? null : File.ReadAllText(Path.Combine(folder, "update", name)).Trim() is { Length: > 0 } value ? value : null;
        }
        catch (Exception caught) when (caught is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
