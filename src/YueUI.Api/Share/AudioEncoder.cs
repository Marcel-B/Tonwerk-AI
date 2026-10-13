using System.Diagnostics;

namespace YueUI.Api.Share;

/// <summary>A lossy format a song's FLAC is encoded into, for sharing or exporting.</summary>
public enum AudioFormat
{
    /// <summary>AAC in an MPEG-4 container (<c>.m4a</c>).</summary>
    M4a,
    Mp3,

    /// <summary>FLAC again, only worth it with <see cref="ILoudness"/> on: the export's copy brought to the target.</summary>
    Flac,
}

/// <summary>Makes the small AAC a song is shared as and the MP3 or M4A it is exported as. Tests replace it.</summary>
public interface IAudioEncoder
{
    /// <summary>Writes <paramref name="flac"/> in <paramref name="format"/> at <paramref name="bitRate"/> bit/s to <paramref name="target"/>.</summary>
    /// <returns>False when this machine has no encoder for the format.</returns>
    Task<bool> EncodeAsync(string flac, string target, AudioFormat format, int bitRate, CancellationToken cancellationToken);
}

/// <summary>
/// <c>afconvert</c> comes with macOS and reads FLAC, so the Mac needs nothing installed; <c>ffmpeg</c> is the
/// fallback for a development machine elsewhere. 128 kbit/s makes a three-minute song about 3 MB instead of the
/// FLAC's 30, small enough for any messenger, and AAC in <c>.m4a</c> plays everywhere, iOS included. MP3 needs
/// <c>ffmpeg</c> (Homebrew's comes with LAME); <c>afconvert</c> cannot write it. With <see cref="ILoudness"/> on, every
/// file goes through ffmpeg with its filter, since <c>afconvert</c> cannot change the level.
/// </summary>
public sealed class AacEncoder(ILoudness loudness, ILogger<AacEncoder> logger) : IAudioEncoder
{
    public const int BitRate = 128_000;

    private const string AfConvert = "/usr/bin/afconvert";

    /// <summary>A long song takes a few seconds; anything beyond this is hanging.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    public async Task<bool> EncodeAsync(string flac, string target, AudioFormat format, int bitRate, CancellationToken cancellationToken)
    {
        string tool;
        string[] arguments;
        var rate = bitRate.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var level = await loudness.FilterAsync(flac, cancellationToken);
        if (format == AudioFormat.Flac && level is null)
        {
            // Nothing to change: the caller copies the original.
            return false;
        }
        if (format == AudioFormat.M4a && level is null && File.Exists(AfConvert))
        {
            tool = AfConvert;
            arguments = ["-f", "m4af", "-d", "aac", "-b", rate, flac, target];
        }
        else if (FindFfmpeg() is { } ffmpeg)
        {
            tool = ffmpeg;
            // Tags are written afterwards (IAudioTagger); the FLAC's own would only be mixed into them.
            List<string> common = ["-nostdin", "-loglevel", "error", "-y", "-i", flac, "-vn", "-map_metadata", "-1"];
            if (level is not null)
            {
                // loudnorm resamples to 192 kHz inside; 48 kHz is what every player and the videos use.
                common.AddRange(["-af", level, "-ar", "48000"]);
            }
            arguments = format switch
            {
                AudioFormat.Mp3 => [.. common, "-c:a", "libmp3lame", "-b:a", rate, "-f", "mp3", target],
                // 24 bits, since the gain moved the samples off the original's 16-bit grid.
                AudioFormat.Flac => [.. common, "-sample_fmt", "s32", "-bits_per_raw_sample", "24", "-c:a", "flac", "-f", "flac", target],
                // faststart puts the index first, so a player can start before the whole file is there.
                _ => [.. common, "-c:a", "aac", "-b:a", rate, "-movflags", "+faststart", "-f", "mp4", target],
            };
        }
        else
        {
            return false;
        }

        var start = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {tool}.");
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0)
            {
                var message = $"{Path.GetFileName(tool)} exited with {process.ExitCode}: {((await error).Trim() is { Length: > 0 } e ? e : (await output).Trim())}";
                logger.LogWarning("{Message}", message);
                throw new InvalidOperationException(message);
            }
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        return true;
    }

    /// <summary>A LaunchAgent's PATH lacks Homebrew, so its folders are tried as well.</summary>
    public static string? FindFfmpeg()
    {
        var path = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        return path.Concat(["/opt/homebrew/bin", "/usr/local/bin"])
            .Where(directory => directory.Length > 0)
            .Select(directory => Path.Combine(directory, "ffmpeg"))
            .FirstOrDefault(File.Exists);
    }
}
