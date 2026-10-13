using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using YueUI.Api.Data;
using YueUI.Api.Voices;

namespace YueUI.Api.Share;

/// <summary>Section <c>Loudness</c>: the loudness every file that leaves the app is brought to.</summary>
public sealed class LoudnessOptions
{
    public const string Section = "Loudness";

    /// <summary>Off leaves every file as loud as the model made it.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Integrated loudness in LUFS; -14 is what YouTube, Spotify and Apple Music turn louder songs down to.</summary>
    public double Target { get; set; } = -14;

    /// <summary>The highest true peak in dBTP, below 0 so the lossy encoders' overshoot does not clip.</summary>
    public double TruePeak { get; set; } = -1;
}

/// <summary>What brings a song's audio to <see cref="LoudnessOptions.Target"/>. Tests replace it.</summary>
public interface ILoudness
{
    /// <summary>Changes with the settings, so copies made before a change are made again; empty while off.</summary>
    string Key { get; }

    /// <summary>The ffmpeg audio filter for <paramref name="audio"/>, measured once per file.</summary>
    /// <returns>Null while off, without ffmpeg, or when the file could not be measured; it then goes out as it is.</returns>
    Task<string?> FilterAsync(string audio, CancellationToken cancellationToken);
}

/// <summary>
/// ffmpeg's <c>loudnorm</c> in two passes: the first measures the file, the second (the filter this returns, run by
/// whoever encodes) applies one gain for the whole song with the measured values, so the mix keeps its dynamics. Only
/// where that gain would push the peaks over <see cref="LoudnessOptions.TruePeak"/> does loudnorm fall back to its
/// limiter, which for YuE2's and ACE-Step's songs (mastered loud, around -9 LUFS) is rare: they are turned down.
/// </summary>
/// <remarks>
/// A measurement takes a few seconds of decoding, so it is kept in <c>loudness</c> by path, size and modification
/// time: a render writes the song's FLAC anew and is measured again. loudnorm works at 192 kHz inside, so the encoders
/// set 48 kHz behind it. The Logic page, the FLAC download and stems keep the original: those are for working on.
/// </remarks>
public sealed class Loudness(SqliteDatabase database, IOptions<LoudnessOptions> options, ILogger<Loudness> logger) : ILoudness
{
    /// <summary>Decoding a long song is quick; anything beyond this is hanging.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    /// <summary>The widest loudness range loudnorm takes; any narrower would push a dynamic song into its limiter.</summary>
    private const double Range = 50;

    public string Key => options.Value.Enabled
        ? string.Create(CultureInfo.InvariantCulture, $"I={options.Value.Target}:TP={options.Value.TruePeak}")
        : "";

    public async Task<string?> FilterAsync(string audio, CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled || AacEncoder.FindFfmpeg() is not { } ffmpeg || !File.Exists(audio))
        {
            return null;
        }
        var info = new FileInfo(audio);
        var measured = Read(audio, info) ?? await MeasureAsync(ffmpeg, audio, info, cancellationToken);
        return measured is null ? null : Filter(options.Value, measured);
    }

    /// <summary>The second pass, with the first pass's numbers.</summary>
    public static string Filter(LoudnessOptions options, Measurement measured) => string.Create(
        CultureInfo.InvariantCulture,
        $"loudnorm=I={options.Target}:TP={options.TruePeak}:LRA={Range}"
        + $":measured_I={measured.Integrated}:measured_TP={measured.TruePeak}:measured_LRA={measured.Range}"
        + $":measured_thresh={measured.Threshold}:offset={measured.Offset}:linear=true");

    /// <summary>The JSON loudnorm prints at the end of its first pass, among ffmpeg's other output.</summary>
    public static Measurement? Parse(string output)
    {
        var start = output.LastIndexOf("\"input_i\"", StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }
        start = output.LastIndexOf('{', start);
        var end = output.IndexOf('}', start);
        if (start < 0 || end < 0)
        {
            return null;
        }
        try
        {
            using var json = JsonDocument.Parse(output.AsMemory(start, end - start + 1));
            double Value(string name) => double.Parse(json.RootElement.GetProperty(name).GetString()!, CultureInfo.InvariantCulture);
            var measured = new Measurement(Value("input_i"), Value("input_tp"), Value("input_lra"), Value("input_thresh"), Value("target_offset"));
            // Silence measures as -inf, which loudnorm's second pass refuses; such a file goes out as it is.
            return double.IsFinite(measured.Integrated) && double.IsFinite(measured.TruePeak) && double.IsFinite(measured.Threshold) ? measured : null;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private async Task<Measurement?> MeasureAsync(string ffmpeg, string audio, FileInfo info, CancellationToken cancellationToken)
    {
        ToolResult result;
        try
        {
            // ffmpeg prints the measurement on stderr as its last lines, which ToolProcess keeps as the tail.
            result = await ToolProcess.RunAsync(
                ffmpeg,
                ["-nostdin", "-hide_banner", "-nostats", "-i", audio, "-vn",
                 "-af", string.Create(CultureInfo.InvariantCulture, $"loudnorm=I={options.Value.Target}:TP={options.Value.TruePeak}:LRA={Range}:print_format=json"),
                 "-f", "null", "-"],
                Timeout,
                cancellationToken);
        }
        catch (VoiceServiceException exception)
        {
            logger.LogWarning("Could not measure the loudness of {Audio}: {Reason}", audio, exception.Message);
            return null;
        }
        var measured = result.ExitCode == 0 ? Parse(string.Join("\n", result.Tail)) : null;
        if (measured is null)
        {
            logger.LogWarning("Could not measure the loudness of {Audio}: {Reason}", audio, string.Join(" | ", result.Tail.TakeLast(3)));
            return null;
        }
        Write(audio, info, measured);
        return measured;
    }

    private Measurement? Read(string audio, FileInfo info)
    {
        try
        {
            using var connection = database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT measured FROM loudness WHERE path = $path AND length = $length AND modified = $modified";
            command.Parameters.AddWithValue("$path", audio);
            command.Parameters.AddWithValue("$length", info.Length);
            command.Parameters.AddWithValue("$modified", info.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture));
            return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<Measurement>(json) : null;
        }
        catch (Exception exception) when (exception is SqliteException or JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>Only a cache: a file that cannot be written is measured again next time.</summary>
    private void Write(string audio, FileInfo info, Measurement measured)
    {
        try
        {
            using var connection = database.Open();
            SqliteDatabase.Execute(
                connection,
                null,
                """
                INSERT INTO loudness (path, length, modified, measured) VALUES ($path, $length, $modified, $measured)
                ON CONFLICT (path) DO UPDATE SET length = excluded.length, modified = excluded.modified, measured = excluded.measured
                """,
                ("$path", audio),
                ("$length", info.Length),
                ("$modified", info.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture)),
                ("$measured", JsonSerializer.Serialize(measured)));
        }
        catch (Exception exception) when (exception is SqliteException or IOException)
        {
            logger.LogWarning(exception, "Could not keep the loudness of {Audio}", audio);
        }
    }
}

/// <summary>loudnorm's first pass: integrated loudness (LUFS), true peak (dBTP), loudness range (LU), gate, offset.</summary>
public sealed record Measurement(double Integrated, double TruePeak, double Range, double Threshold, double Offset);
