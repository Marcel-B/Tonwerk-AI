namespace YueUI.Api.Logs;

/// <summary>One line of the collected log.</summary>
/// <param name="Source">Which part wrote it, one of <see cref="LogSources.All"/>.</param>
/// <param name="Level">debug, info, warning or error.</param>
/// <param name="Exception">The exception with its stack trace, when one was logged.</param>
public sealed record LogLine(DateTimeOffset Time, string Source, string Level, string Message, string? Exception = null);

/// <summary>Lines of the log, newest first (<c>GET /api/logs</c>).</summary>
/// <param name="More">Older lines match too; ask again with <c>before</c> set to the last line's time.</param>
public sealed record LogPage(IReadOnlyList<LogLine> Entries, bool More);

/// <param name="Sources">Only these sources; null or empty for all.</param>
/// <param name="MinLevel">Only this level and above; null for all.</param>
/// <param name="Text">Only lines whose message or exception contains it, ignoring case.</param>
public sealed record LogQuery(
    IReadOnlySet<string>? Sources = null,
    string? MinLevel = null,
    string? Text = null,
    DateTimeOffset? Before = null,
    DateTimeOffset? After = null,
    int Limit = 200)
{
    public bool Wants(string source) => Sources is not { Count: > 0 } sources || sources.Contains(source);

    public bool Matches(LogLine line) =>
        Wants(line.Source)
        && (MinLevel is null || LogLevels.Rank(line.Level) >= LogLevels.Rank(MinLevel))
        && (Before is not { } before || line.Time < before)
        && (After is not { } after || line.Time >= after)
        && (string.IsNullOrWhiteSpace(Text)
            || line.Message.Contains(Text, StringComparison.OrdinalIgnoreCase)
            || (line.Exception?.Contains(Text, StringComparison.OrdinalIgnoreCase) ?? false));
}

public static class LogLevels
{
    public const string Debug = "debug";
    public const string Info = "info";
    public const string Warning = "warning";
    public const string Error = "error";

    public static readonly IReadOnlyList<string> All = [Debug, Info, Warning, Error];

    public static int Rank(string level) => level switch
    {
        Debug => 0,
        Warning => 2,
        Error => 3,
        _ => 1,
    };

    public static string From(LogLevel level) => level switch
    {
        LogLevel.Trace or LogLevel.Debug => Debug,
        LogLevel.Warning => Warning,
        LogLevel.Error or LogLevel.Critical => Error,
        _ => Info,
    };
}

/// <summary>The parts a line can come from; the page offers them as a filter.</summary>
public static class LogSources
{
    public const string Server = "server";
    public const string Worker = "worker";
    public const string Update = "update";

    public static readonly IReadOnlyList<string> All =
        [Server, Worker, "queue", "lyrics", "voices", "speech", "images", "ace", "video", "export", "logic", "backup", "push", Update];

    private static readonly (string Prefix, string Source)[] Categories =
    [
        ("YueUI.Api.Worker.", Worker),
        ("YueUI.Api.Queue.", "queue"),
        ("YueUI.Api.Lyrics.", "lyrics"),
        ("YueUI.Api.Voices.", "voices"),
        ("YueUI.Api.Speech.", "speech"),
        ("YueUI.Api.Images.", "images"),
        ("YueUI.Api.Ace.", "ace"),
        ("YueUI.Api.Video.", "video"),
        ("YueUI.Api.Share.", "export"),
        ("YueUI.Api.Export.", "export"),
        ("YueUI.Api.Logic.", "logic"),
        // MIDI from a track ends up on the Logic page or in Logic; one source fewer to filter by.
        ("YueUI.Api.AudioMidi.", "logic"),
        ("YueToLogic.", "logic"),
        ("YueUI.Api.Backup.", "backup"),
        ("YueUI.Api.Push.", "push"),
    ];

    /// <summary>
    /// A logger's category is its class's full name; the namespace says the part. ASP.NET Core, the database, the
    /// library and whatever else is left are the server.
    /// </summary>
    public static string ForCategory(string category)
    {
        foreach (var (prefix, source) in Categories)
        {
            if (category.StartsWith(prefix, StringComparison.Ordinal))
            {
                return source;
            }
        }
        return Server;
    }
}
