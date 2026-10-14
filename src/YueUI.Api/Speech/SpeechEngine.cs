using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using YueUI.Api.Share;

namespace YueUI.Api.Speech;

/// <param name="RefAudio">The recorded voice as WAV, or null for the model's own voice.</param>
/// <param name="RefText">What is said in it.</param>
/// <param name="Output">Where the take is written, as WAV.</param>
public sealed record SpeechJob(SpeechModel Model, string Text, string? RefAudio, string? RefText, string Output);

/// <param name="Input">The recording to clean, as 48 kHz mono WAV.</param>
/// <param name="Output">Where the cleaned recording is written, as WAV.</param>
/// <param name="Seconds">How long the recording is, which decides how long the model may take.</param>
public sealed record CleanJob(SpeechModel Model, string Input, string Output, double Seconds);

/// <summary>The script's measurements; null where it could not tell.</summary>
public sealed record SpeechResult(double? LoadSeconds, double? SpeakSeconds, double? PeakMemoryGb);

public sealed class SpeechException(string message, int status = StatusCodes.Status500InternalServerError) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>Speaks and prepares recordings. Tests replace it; the real one runs mlx-audio and ffmpeg.</summary>
public interface ISpeechEngine
{
    /// <summary>The Python environment with mlx-audio is there.</summary>
    bool Installed { get; }

    bool CanPrepareVoices { get; }

    /// <summary>Whether the model's repository is already in the cache.</summary>
    bool Downloaded(SpeechModel model);

    /// <summary>
    /// Turns a browser recording (WebM/Opus from Chrome, MP4/AAC from Safari, or any file) into the 24 kHz mono WAV
    /// the models read, without the silence before and after.
    /// </summary>
    Task PrepareVoiceAsync(string input, string output, CancellationToken cancellationToken);

    /// <param name="stage">Hears loading and speaking as the script reaches them.</param>
    Task<SpeechResult> SpeakAsync(SpeechJob job, Action<string> stage, CancellationToken cancellationToken);

    /// <summary>
    /// Turns an uploaded speech recording (any format ffmpeg reads, a video too) into the 48 kHz mono WAV the
    /// enhancement models read, at most <see cref="MlxAudioEngine.MaxRecordingSeconds"/> long.
    /// </summary>
    Task PrepareRecordingAsync(string input, string output, CancellationToken cancellationToken);

    /// <param name="stage">Hears loading and cleaning as the script reaches them.</param>
    /// <param name="progress">How much of the recording is cleaned, 0 to 1.</param>
    Task<SpeechResult> CleanAsync(CleanJob job, Action<string> stage, Action<double> progress, CancellationToken cancellationToken);
}

/// <summary>
/// Runs <c>Speech/yueui_speech.py</c> with the lab's Python, one process per take: the model is loaded for the take
/// and its memory freed with the process, since on 24 GB it must not stay beside YuE2. Loading costs seconds to a
/// minute each time, which a lab that compares models pays anyway.
/// </summary>
public sealed class MlxAudioEngine(IOptions<SpeechOptions> options, ILogger<MlxAudioEngine> logger) : ISpeechEngine
{
    /// <summary>Longer recordings do not clone better; the models are trained on 5 to 15 seconds.</summary>
    public const double MaxVoiceSeconds = 30;

    /// <summary>A long talk or lecture; the cleaned WAV of two hours is about 700 MB.</summary>
    public const double MaxRecordingSeconds = 2 * 60 * 60;

    /// <summary>What the script's own events start with; everything else on stdout is mlx-audio talking.</summary>
    private const string EventPrefix = "YUEUI ";

    /// <summary>The lines kept for the message when the script fails; its traceback ends there.</summary>
    private const int TailLines = 30;

    public static string Script => Path.Combine(AppContext.BaseDirectory, "Speech", "yueui_speech.py");

    public bool Installed => File.Exists(options.Value.ResolvedPython);

    public bool CanPrepareVoices => AacEncoder.FindFfmpeg() is not null;

    public bool Downloaded(SpeechModel model) =>
        Directory.Exists(Path.Combine(options.Value.ResolvedModelCache, "hub", $"models--{model.Repo.Replace("/", "--", StringComparison.Ordinal)}", "snapshots"));

    public Task PrepareVoiceAsync(string input, string output, CancellationToken cancellationToken)
    {
        // Silence is cut at both ends (the reversed pass does the end): a recording starts with the tap and ends with
        // the next one, and a model copies the pauses it hears into every sentence.
        const string trim = "silenceremove=start_periods=1:start_threshold=-45dB:start_silence=0.15,"
            + "areverse,silenceremove=start_periods=1:start_threshold=-45dB:start_silence=0.25,areverse";
        return DecodeAsync(input, output, ["-af", trim, "-ar", "24000"], MaxVoiceSeconds, TimeSpan.FromMinutes(1), cancellationToken);
    }

    // Nothing cut and no filter: the model is to hear the recording as it was, and the page compares before and after.
    public Task PrepareRecordingAsync(string input, string output, CancellationToken cancellationToken) =>
        DecodeAsync(input, output, ["-ar", "48000"], MaxRecordingSeconds, TimeSpan.FromMinutes(10), cancellationToken);

    private static async Task DecodeAsync(
        string input, string output, string[] filters, double maxSeconds, TimeSpan limit, CancellationToken cancellationToken)
    {
        var ffmpeg = AacEncoder.FindFfmpeg() ?? throw new SpeechException("ffmpeg is not installed.", StatusCodes.Status501NotImplemented);
        var start = new ProcessStartInfo(ffmpeg) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var argument in (string[])["-nostdin", "-loglevel", "error", "-y", "-i", input, "-vn", .. filters, "-ac", "1",
                     "-sample_fmt", "s16", "-t", maxSeconds.ToString(CultureInfo.InvariantCulture), output])
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start) ?? throw new SpeechException("Could not start ffmpeg.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        _ = process.StandardOutput.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        if (process.ExitCode != 0 || !File.Exists(output))
        {
            throw new SpeechException($"The recording could not be read: {(await error).Trim()}", StatusCodes.Status422UnprocessableEntity);
        }
    }

    public Task<SpeechResult> SpeakAsync(SpeechJob job, Action<string> stage, CancellationToken cancellationToken)
    {
        var request = new JsonObject
        {
            ["model"] = job.Model.Repo,
            ["text"] = job.Text,
            ["output"] = job.Output,
            ["refAudio"] = job.RefAudio,
            ["refText"] = job.RefText,
            ["langCode"] = job.Model.LangCode,
            ["options"] = job.Model.Options is { } extra ? JsonSerializer.SerializeToNode(extra) : null,
            ["chunkCharacters"] = job.Model.ChunkCharacters,
            ["contextPieces"] = job.Model.ContextPieces,
        };
        return RunAsync(job.Model, "Speaking", request, job.Output, stage, _ => { }, options.Value.Timeout, cancellationToken);
    }

    public Task<SpeechResult> CleanAsync(CleanJob job, Action<string> stage, Action<double> progress, CancellationToken cancellationToken)
    {
        var request = new JsonObject
        {
            ["model"] = job.Model.Repo,
            ["subfolder"] = job.Model.Subfolder,
            ["clean"] = true,
            ["input"] = job.Input,
            ["output"] = job.Output,
        };
        // An hour of talk is not cleaned in the minutes a take is given; allow the recording's length on top.
        var limit = options.Value.Timeout + TimeSpan.FromSeconds(job.Seconds);
        return RunAsync(job.Model, "Cleaning", request, job.Output, stage, progress, limit, cancellationToken);
    }

    private async Task<SpeechResult> RunAsync(
        SpeechModel model,
        string doing,
        JsonObject request,
        string output,
        Action<string> stage,
        Action<double> progress,
        TimeSpan limit,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!Installed)
        {
            throw new SpeechException($"mlx-audio is not installed ({settings.ResolvedPython}); run deploy/install-speech.sh.", StatusCodes.Status501NotImplemented);
        }
        var start = new ProcessStartInfo(settings.ResolvedPython)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("-u");
        start.ArgumentList.Add(Script);
        start.Environment["HF_HOME"] = settings.ResolvedModelCache;
        start.Environment["HF_HUB_DISABLE_TELEMETRY"] = "1";
        start.Environment["PYTHONUNBUFFERED"] = "1";
        start.Environment["TQDM_DISABLE"] = "1";

        using var process = Process.Start(start) ?? throw new SpeechException($"Could not start {settings.ResolvedPython}.");
        logger.LogInformation("{Doing} with {Model} (pid {Pid})", doing, model.Repo, process.Id);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit);
        var tail = new Queue<string>();
        SpeechResult? result = null;
        try
        {
            await process.StandardInput.WriteAsync(request.ToJsonString());
            process.StandardInput.Close();
            var errors = PumpAsync(process.StandardError, line => Keep(tail, line), timeout.Token);
            await PumpAsync(process.StandardOutput, line =>
            {
                if (!line.StartsWith(EventPrefix, StringComparison.Ordinal))
                {
                    Keep(tail, line);
                    return;
                }
                var data = JsonNode.Parse(line[EventPrefix.Length..]);
                switch ((string?)data?["event"])
                {
                    case "stage":
                        stage((string?)data["stage"] ?? "speaking");
                        break;
                    case "progress":
                        progress(Math.Clamp((double?)data["share"] ?? 0, 0, 1));
                        break;
                    case "done":
                        result = new SpeechResult((double?)data["loadSeconds"], (double?)data["speakSeconds"], (double?)data["peakMemoryGb"]);
                        break;
                    case "note":
                        logger.LogInformation("{Doing} with {Model}: {Note}", doing, model.Repo, (string?)data["text"]);
                        break;
                }
            }, timeout.Token);
            await errors;
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            if (!cancellationToken.IsCancellationRequested)
            {
                throw new SpeechException($"The model took longer than {limit.TotalMinutes:0} minutes.");
            }
            throw;
        }
        if (process.ExitCode != 0 || result is null || !File.Exists(output))
        {
            lock (tail)
            {
                logger.LogWarning("{Doing} with {Model} failed:\n{Output}", doing, model.Repo, string.Join('\n', tail));
                throw new SpeechException(FailureMessage(tail));
            }
        }
        return result;
    }

    /// <summary>
    /// mlx-audio catches its own errors and prints them ("Error loading model: …") before the traceback, so its line
    /// says more than the last one does.
    /// </summary>
    public static string FailureMessage(IEnumerable<string> output)
    {
        var lines = output.Where(l => l.Trim().Length > 0).ToList();
        var line = lines.LastOrDefault(l => l.StartsWith("Error", StringComparison.Ordinal) || l.Contains("Error:", StringComparison.Ordinal))
            ?? lines.LastOrDefault();
        return line is null ? "The model wrote no audio." : line.Trim();
    }

    private static void Keep(Queue<string> tail, string line)
    {
        lock (tail)
        {
            tail.Enqueue(line);
            while (tail.Count > TailLines)
            {
                tail.Dequeue();
            }
        }
    }

    private static async Task PumpAsync(StreamReader reader, Action<string> line, CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } read)
        {
            line(read);
        }
    }
}

/// <summary>The length of a WAV from its header, PCM or float alike.</summary>
public static class WavInfo
{
    /// <returns>Null when the file is no WAV this can read.</returns>
    public static double? Seconds(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 12 || new string(reader.ReadChars(4)) != "RIFF")
            {
                return null;
            }
            reader.ReadUInt32();
            if (new string(reader.ReadChars(4)) != "WAVE")
            {
                return null;
            }
            uint? byteRate = null;
            while (stream.Position + 8 <= stream.Length)
            {
                var id = new string(reader.ReadChars(4));
                var size = reader.ReadUInt32();
                if (id == "fmt ")
                {
                    reader.ReadUInt16();
                    reader.ReadUInt16();
                    reader.ReadUInt32();
                    byteRate = reader.ReadUInt32();
                    stream.Seek(size - 12, SeekOrigin.Current);
                }
                else if (id == "data")
                {
                    // A writer that streamed may leave the size at its maximum; the file's end is the truth then.
                    var bytes = Math.Min(size, stream.Length - stream.Position);
                    return byteRate is > 0 ? Math.Round(bytes / (double)byteRate, 2) : null;
                }
                else
                {
                    stream.Seek(size + (size & 1), SeekOrigin.Current);
                }
            }
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            return null;
        }
    }
}
