using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace YueUI.Api.Logs;

/// <summary>
/// The server's log, the worker's output and the updater's log in one place, to be read from the browser rather than
/// over SSH. Each day is a file of JSON lines (<c>tonwerk-yyyy-MM-dd.jsonl</c>, UTC days) in
/// <see cref="LogOptions.ResolvedDirectory"/>; <c>deploy/update.sh</c> keeps writing its own file, which is read along.
/// </summary>
/// <remarks>
/// Lines are written as they come, under a lock, and flushed at once, so a query sees everything logged before it and a
/// crash loses nothing. Logging is a few lines a minute, a worker's stderr in bursts; a write is cheap next to either.
/// Writing must never fail what logs, so an unwritable folder only loses the lines.
/// </remarks>
public sealed partial class LogStore(IOptions<LogOptions> options, TimeProvider time, Monitoring.LogFeed feed) : IDisposable
{
    private const string Prefix = "tonwerk-";
    private const string Extension = ".jsonl";

    /// <summary>A message the updater continues over many lines (setup-mac.sh's output) is cut here.</summary>
    private const int MaxUpdateMessage = 20_000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Lock _gate = new();
    private StreamWriter? _writer;
    private string? _day;
    private long _size;
    private bool _disposed;

    private LogOptions Options => options.Value;

    public void Add(string source, string level, string message, string? exception = null)
    {
        var line = new LogLine(time.GetUtcNow(), source, level, message, exception);
        // Counted for /metrics and passed on to Loki even when the disk refuses the line.
        feed.Add(line);
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            try
            {
                Write(line);
            }
            catch (Exception caught) when (caught is IOException or UnauthorizedAccessException)
            {
                // Tried again with the next line.
                _writer?.Dispose();
                _writer = null;
                _day = null;
            }
        }
    }

    /// <summary>The newest lines that match, from the files and the updater's log.</summary>
    public LogPage Query(LogQuery query)
    {
        var limit = Math.Clamp(query.Limit, 1, 5000);
        List<LogLine> found = [];
        if (query.Wants(LogSources.Update))
        {
            found.AddRange(ReadUpdateLog().Where(query.Matches).TakeLast(limit + 1));
        }
        if (LogSources.All.Any(source => source != LogSources.Update && query.Wants(source)))
        {
            found.AddRange(ReadFiles(query, limit + 1));
        }
        var newest = found.OrderByDescending(line => line.Time).ToList();
        return new LogPage([.. newest.Take(limit)], newest.Count > limit);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void Write(LogLine line)
    {
        var day = line.Time.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (_writer is null || day != _day)
        {
            _writer?.Dispose();
            _writer = null;
            Directory.CreateDirectory(Options.ResolvedDirectory);
            Prune(line.Time);
            var stream = new FileStream(PathFor(day), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            _size = stream.Length;
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            _day = day;
        }
        if (_size > Options.MaxFileBytes && LogLevels.Rank(line.Level) < LogLevels.Rank(LogLevels.Warning))
        {
            return;
        }
        var json = JsonSerializer.Serialize(line, Json);
        _writer.WriteLine(json);
        _size += Encoding.UTF8.GetByteCount(json) + 1;
    }

    private void Prune(DateTimeOffset now)
    {
        var oldest = DateOnly.FromDateTime(now.UtcDateTime).AddDays(1 - Math.Max(1, Options.RetentionDays));
        foreach (var (date, path) in Files())
        {
            if (date < oldest)
            {
                File.Delete(path);
            }
        }
    }

    private string PathFor(string day) => Path.Combine(Options.ResolvedDirectory, Prefix + day + Extension);

    /// <summary>The daily files, newest first.</summary>
    private IEnumerable<(DateOnly Date, string Path)> Files()
    {
        if (!Directory.Exists(Options.ResolvedDirectory))
        {
            return [];
        }
        return Directory.EnumerateFiles(Options.ResolvedDirectory, Prefix + "*" + Extension)
            .Select(path => (Name: Path.GetFileNameWithoutExtension(path)[Prefix.Length..], Path: path))
            .Where(file => DateOnly.TryParseExact(file.Name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            .Select(file => (DateOnly.ParseExact(file.Name, "yyyy-MM-dd", CultureInfo.InvariantCulture), file.Path))
            .OrderByDescending(file => file.Item1)
            .ToList();
    }

    /// <summary>
    /// The newest <paramref name="count"/> lines that match, newest first. A file is read from its start, keeping only
    /// the last matches, since lines lie in order of time; older files are read only while lines are missing.
    /// </summary>
    private List<LogLine> ReadFiles(LogQuery query, int count)
    {
        List<LogLine> result = [];
        foreach (var (date, path) in Files())
        {
            if (query.Before is { } before && date > DateOnly.FromDateTime(before.UtcDateTime))
            {
                continue;
            }
            if (query.After is { } after && date < DateOnly.FromDateTime(after.UtcDateTime))
            {
                break;
            }
            var kept = new Queue<LogLine>();
            foreach (var line in ReadFile(path))
            {
                if (query.Matches(line))
                {
                    kept.Enqueue(line);
                    if (kept.Count > count - result.Count)
                    {
                        kept.Dequeue();
                    }
                }
            }
            result.AddRange(kept.Reverse());
            if (result.Count >= count)
            {
                break;
            }
        }
        return result;
    }

    private static IEnumerable<LogLine> ReadFile(string path)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception caught) when (caught is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } text)
        {
            LogLine? line = null;
            try
            {
                line = JsonSerializer.Deserialize<LogLine>(text, Json);
            }
            catch (JsonException)
            {
                // A line cut off by a crash; the next one starts clean.
            }
            if (line is { Message: not null, Source: not null, Level: not null })
            {
                yield return line;
            }
        }
    }

    /// <summary>
    /// <c>deploy/update.sh</c> writes <c>yyyy-MM-dd HH:mm:ss message</c> in local time; lines without a time
    /// (the commits it deploys, setup-mac.sh's output) belong to the line before. A failed deploy says "failed".
    /// </summary>
    public IReadOnlyList<LogLine> ReadUpdateLog()
    {
        var path = Options.ResolvedUpdateLog;
        if (!File.Exists(path))
        {
            return [];
        }
        List<LogLine> lines = [];
        StringBuilder? message = null;
        DateTimeOffset at = default;
        string level = LogLevels.Info;
        void Finish()
        {
            if (message is not null)
            {
                lines.Add(new LogLine(at, LogSources.Update, level, message.ToString()));
            }
        }
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (reader.ReadLine() is { } text)
            {
                var match = UpdateLine().Match(text);
                if (match.Success
                    && DateTime.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local))
                {
                    Finish();
                    at = new DateTimeOffset(local);
                    message = new StringBuilder(match.Groups[2].Value);
                    level = UpdateLevel(match.Groups[2].Value);
                }
                else if (message is not null && message.Length < MaxUpdateMessage)
                {
                    message.Append('\n').Append(text);
                    if (message.Length >= MaxUpdateMessage)
                    {
                        message.Append("\n…");
                    }
                }
            }
        }
        catch (Exception caught) when (caught is IOException or UnauthorizedAccessException)
        {
            return lines;
        }
        Finish();
        return lines;
    }

    private static string UpdateLevel(string message) =>
        message.Contains("failed", StringComparison.OrdinalIgnoreCase) ? LogLevels.Error
        : message.Contains("nothing done", StringComparison.OrdinalIgnoreCase) ? LogLevels.Warning
        : LogLevels.Info;

    [GeneratedRegex(@"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}) (.*)$")]
    private static partial Regex UpdateLine();
}
