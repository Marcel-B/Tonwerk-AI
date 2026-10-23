using System.Globalization;
using System.Text.RegularExpressions;
using YueUI.Api.Data;

namespace YueUI.Api.Stats;

/// <summary>One finished synthesis as the worker reports it in its log.</summary>
/// <param name="Rate">Seconds of work per second of audio, the worker's own measure.</param>
public sealed record SynthesisRecord(DateTimeOffset At, string Song, string Engine, int Steps, double Seconds, double Rate);

/// <summary>Neural Engine programs that became ready for a song: compiled, or taken from the cache.</summary>
public sealed record AneProgramsRecord(DateTimeOffset At, string Song, double Seconds);

/// <summary>Synthesis times per engine and step count (32 is full quality, 8 a draft's default).</summary>
public sealed record EngineStats(string Engine, int Steps, int Count, double MeanRate, double BestRate, double MeanSeconds, DateTimeOffset LastAt);

/// <summary>How often Neural Engine programs were prepared, and how long it took.</summary>
public sealed record AneProgramStats(int Count, double MeanSeconds, double LongestSeconds, DateTimeOffset? LastAt);

public sealed record SynthesisOverview(
    IReadOnlyList<EngineStats> Engines,
    AneProgramStats AnePrograms,
    IReadOnlyList<SynthesisRecord> Recent);

/// <summary>
/// What YuE2's synthesis took, kept in <c>synth_stats</c> (schema version 33) so the queue page can compare GPU and
/// Neural Engine without Grafana. The worker says it only in log lines, so they are read from there (<see cref="Observe"/>);
/// a line in another wording after a YuE Studio update is simply not counted.
/// </summary>
public sealed partial class SynthesisStats(SqliteDatabase database, ILogger<SynthesisStats> logger)
{
    public const int RecentCount = 20;

    /// <summary>Seconds of work per second of audio: the Neural Engine at full quality is about 3, a draft on the CPU 30.</summary>
    public static readonly double[] RateBuckets = [0.5, 1, 2, 3, 4, 6, 8, 12, 16, 24, 32, 64];

    /// <summary>Preparing the Neural Engine's programs: seconds from the cache, minutes when compiled.</summary>
    public static readonly double[] AneBuckets = [5, 15, 30, 60, 120, 300, 600, 1200];

    private readonly Lock _gate = new();
    private readonly Dictionary<(string Engine, int Steps), Monitoring.Histogram> _rates = [];
    private readonly Monitoring.Histogram _anePrograms = new(AneBuckets);

    /// <summary>The rates seen since the server started, per engine and step count, for <c>/metrics</c>.</summary>
    public IReadOnlyDictionary<(string Engine, int Steps), Monitoring.Histogram> Rates()
    {
        lock (_gate)
        {
            return _rates.ToDictionary(pair => pair.Key, pair => pair.Value.Copy());
        }
    }

    /// <summary>How long the Neural Engine's programs took to get ready since the server started.</summary>
    public Monitoring.Histogram AnePrograms()
    {
        lock (_gate)
        {
            return _anePrograms.Copy();
        }
    }

    // "Synthesis of 20261023-004449-Chomas/song1 done in 769 s (3.5 s per second of audio, 32 steps, mlx)"
    [GeneratedRegex(@"^Synthesis of (?<song>\S+) done in (?<seconds>[\d.]+) s \((?<rate>[\d.]+) s per second of audio, (?<steps>\d+) steps, (?<engine>[\w+-]+)\)")]
    private static partial Regex SynthesisLine();

    // "Background: Neural Engine programs for 20261023-004449-Chomas/song1 ready in 465 s"
    [GeneratedRegex(@"^Background: Neural Engine programs for (?<song>\S+) ready in (?<seconds>[\d.]+) s")]
    private static partial Regex AneLine();

    public static SynthesisRecord? ParseSynthesis(string line, DateTimeOffset at)
    {
        var match = SynthesisLine().Match(line.Trim());
        if (!match.Success)
        {
            return null;
        }
        return new SynthesisRecord(
            at,
            match.Groups["song"].Value,
            match.Groups["engine"].Value,
            int.Parse(match.Groups["steps"].Value, CultureInfo.InvariantCulture),
            double.Parse(match.Groups["seconds"].Value, CultureInfo.InvariantCulture),
            double.Parse(match.Groups["rate"].Value, CultureInfo.InvariantCulture));
    }

    public static AneProgramsRecord? ParseAnePrograms(string line, DateTimeOffset at)
    {
        var match = AneLine().Match(line.Trim());
        return match.Success
            ? new AneProgramsRecord(at, match.Groups["song"].Value, double.Parse(match.Groups["seconds"].Value, CultureInfo.InvariantCulture))
            : null;
    }

    /// <summary>A line of the worker's log; anything but the two reports is ignored. Never throws at the worker.</summary>
    public void Observe(string line, DateTimeOffset at)
    {
        try
        {
            if (ParseSynthesis(line, at) is { } synthesis)
            {
                lock (_gate)
                {
                    if (!_rates.TryGetValue((synthesis.Engine, synthesis.Steps), out var histogram))
                    {
                        _rates[(synthesis.Engine, synthesis.Steps)] = histogram = new Monitoring.Histogram(RateBuckets);
                    }
                    histogram.Observe(synthesis.Rate);
                }
                Insert("synthesis", synthesis.Song, synthesis.Engine, synthesis.Steps, synthesis.Seconds, synthesis.Rate, at);
            }
            else if (ParseAnePrograms(line, at) is { } programs)
            {
                lock (_gate)
                {
                    _anePrograms.Observe(programs.Seconds);
                }
                Insert("ane-programs", programs.Song, "ane", null, programs.Seconds, null, at);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not keep a synthesis time");
        }
    }

    public SynthesisOverview Overview()
    {
        using var connection = database.Open();
        var engines = new List<EngineStats>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT engine, steps, COUNT(*), AVG(rate), MIN(rate), AVG(seconds), MAX(at)
                FROM synth_stats WHERE kind = 'synthesis'
                GROUP BY engine, steps ORDER BY steps DESC, AVG(rate)
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                engines.Add(new EngineStats(
                    reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetDouble(3), reader.GetDouble(4),
                    reader.GetDouble(5), DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture)));
            }
        }
        AneProgramStats programs;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*), AVG(seconds), MAX(seconds), MAX(at) FROM synth_stats WHERE kind = 'ane-programs'";
            using var reader = command.ExecuteReader();
            reader.Read();
            var count = reader.GetInt32(0);
            programs = count == 0
                ? new AneProgramStats(0, 0, 0, null)
                : new AneProgramStats(count, reader.GetDouble(1), reader.GetDouble(2), DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture));
        }
        var recent = new List<SynthesisRecord>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT at, song_id, engine, steps, seconds, rate FROM synth_stats WHERE kind = 'synthesis' ORDER BY at DESC, id DESC LIMIT $limit";
            command.Parameters.AddWithValue("$limit", RecentCount);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                recent.Add(new SynthesisRecord(
                    DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture), reader.GetString(1), reader.GetString(2),
                    reader.GetInt32(3), reader.GetDouble(4), reader.GetDouble(5)));
            }
        }
        return new SynthesisOverview(engines, programs, recent);
    }

    private void Insert(string kind, string song, string engine, int? steps, double seconds, double? rate, DateTimeOffset at)
    {
        using var connection = database.Open();
        SqliteDatabase.Execute(
            connection,
            null,
            "INSERT INTO synth_stats (kind, song_id, engine, steps, seconds, rate, at) VALUES ($kind, $song, $engine, $steps, $seconds, $rate, $at)",
            ("$kind", kind),
            ("$song", song),
            ("$engine", engine),
            ("$steps", steps is { } count ? count : DBNull.Value),
            ("$seconds", seconds),
            ("$rate", rate is { } value ? value : DBNull.Value),
            ("$at", at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)));
    }
}
