using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using YueUI.Api.Speech;

namespace YueUI.Api.Singing;

/// <summary>Sings a melody with words. Tests replace it; the real one runs SoulX-Singer.</summary>
public interface ISingingEngine
{
    /// <summary>The Python environment and SoulX-Singer's checkout are there.</summary>
    bool Installed { get; }

    /// <summary>The weights are there.</summary>
    bool ModelDownloaded { get; }

    /// <param name="stage">Hears loading and singing as the script reaches them.</param>
    /// <param name="progress">How much of the melody is sung, 0 to 1.</param>
    /// <param name="aligned">How the words were laid on the notes, before the model loads.</param>
    Task<SingingResult> SingAsync(
        SingingJob job,
        Action<string> stage,
        Action<double> progress,
        Action<SingingAlignment> aligned,
        CancellationToken cancellationToken);

    /// <summary>
    /// What each note sings and how its word is spoken, for the app's note editor: the notes' own words, else the lyrics
    /// spread over them. Needs the environment, not the weights.
    /// </summary>
    Task<SingingAligned> AlignAsync(IReadOnlyList<SungNote> notes, string? lyrics, CancellationToken cancellationToken);

    /// <summary>
    /// A voice of one's own from a recording of singing (any format ffmpeg reads) and the words sung: the notes heard,
    /// the words laid on them, and the first phrase of a few seconds written as SoulX-Singer's prompt, its audio as a
    /// 24 kHz WAV and its metadata. Needs the environment, not the weights; the model is not loaded.
    /// </summary>
    Task<SingingVoiceMade> MakeVoiceAsync(string recording, string lyrics, string audio, string metadata, CancellationToken cancellationToken);
}

/// <summary>
/// Runs <c>Singing/yueui_sing.py</c> with the singing environment's Python, one process per take: the model (2.8 GB)
/// is loaded for the take and its memory freed with the process, since on 24 GB it must not stay beside YuE2.
/// </summary>
public sealed class SoulXSingerEngine(IOptions<SingingOptions> options, ILogger<SoulXSingerEngine> logger) : ISingingEngine
{
    private const string EventPrefix = "YUEUI ";

    private const int TailLines = 30;

    public static string Script => Path.Combine(AppContext.BaseDirectory, "Singing", "yueui_sing.py");

    public bool Installed => File.Exists(options.Value.ResolvedPython)
        && Directory.Exists(Path.Combine(options.Value.ResolvedSource, "soulxsinger"));

    public bool ModelDownloaded => File.Exists(options.Value.ResolvedModel);

    public async Task<SingingResult> SingAsync(
        SingingJob job,
        Action<string> stage,
        Action<double> progress,
        Action<SingingAlignment> aligned,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!Installed)
        {
            throw new SingingException(
                $"SoulX-Singer is not installed ({settings.ResolvedRoot}); run deploy/install-singing.sh.", StatusCodes.Status501NotImplemented);
        }
        if (!ModelDownloaded)
        {
            throw new SingingException(
                $"SoulX-Singer's weights are missing ({settings.ResolvedModel}); run deploy/install-singing.sh.", StatusCodes.Status501NotImplemented);
        }
        var request = new JsonObject
        {
            ["source"] = settings.ResolvedSource,
            ["model"] = settings.ResolvedModel,
            ["device"] = settings.Device,
            ["steps"] = settings.Steps,
            ["cfg"] = settings.Cfg,
            ["voice"] = new JsonObject
            {
                ["audio"] = settings.VoiceFile(job.Voice.Audio),
                ["metadata"] = settings.VoiceFile(job.Voice.Metadata),
            },
            ["notes"] = Notes(job.Notes),
            ["lyrics"] = job.Lyrics,
            ["pitchShift"] = job.PitchShift,
            ["autoShift"] = job.AutoShift,
            ["output"] = job.Output,
        };

        using var process = Start(settings);
        logger.LogInformation("Singing {Notes} notes with {Voice} (pid {Pid})", job.Notes.Count, job.Voice.Id, process.Id);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.Timeout);
        var tail = new Queue<string>();
        SingingResult? result = null;
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
                        stage((string?)data["stage"] ?? "singing");
                        break;
                    case "progress":
                        progress(Math.Clamp((double?)data["share"] ?? 0, 0, 1));
                        break;
                    case "aligned":
                        aligned(new SingingAlignment((int?)data["sung"] ?? 0, (int?)data["unsung"] ?? 0, (int?)data["leftover"] ?? 0));
                        break;
                    case "done":
                        result = new SingingResult((double?)data["loadSeconds"], (double?)data["singSeconds"], (double?)data["peakMemoryGb"]);
                        break;
                    case "note":
                        logger.LogInformation("Singing: {Note}", (string?)data["text"]);
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
                throw new SingingException($"SoulX-Singer took longer than {settings.Timeout.TotalMinutes:0} minutes.");
            }
            throw;
        }
        if (process.ExitCode != 0 || result is null || !File.Exists(job.Output))
        {
            lock (tail)
            {
                logger.LogWarning("Singing failed:\n{Output}", string.Join('\n', tail));
                throw new SingingException(MlxAudioEngine.FailureMessage(tail));
            }
        }
        return result;
    }

    public async Task<SingingAligned> AlignAsync(IReadOnlyList<SungNote> notes, string? lyrics, CancellationToken cancellationToken)
    {
        RequireInstalled();
        SingingAligned? result = null;
        // Only g2p_en loads, a second or two; a dictionary that never answers must not hold the editor.
        await RunScriptAsync(
            new JsonObject { ["mode"] = "align", ["notes"] = Notes(notes), ["lyrics"] = lyrics },
            AlignTimeout,
            "The pronunciation dictionary did not answer.",
            data =>
            {
                if ((string?)data["event"] == "assigned")
                {
                    var sung = (data["notes"] as JsonArray ?? [])
                        .Select(n => new AlignedNote((string?)n?["word"], (int?)n?["type"] ?? 1, (string?)n?["phonemes"] ?? ""))
                        .ToList();
                    result = new SingingAligned(sung, (int?)data["leftover"] ?? 0);
                }
            },
            () => result is not null && result.Notes.Count == notes.Count,
            "Aligning",
            StatusCodes.Status500InternalServerError,
            cancellationToken);
        return result!;
    }

    public async Task<SingingVoiceMade> MakeVoiceAsync(string recording, string lyrics, string audio, string metadata, CancellationToken cancellationToken)
    {
        RequireInstalled();
        var prepared = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(audio))!, $"recording-{Guid.NewGuid():N}.wav");
        try
        {
            try
            {
                await MlxAudioEngine.TrimmedVoiceAsync(recording, prepared, cancellationToken);
            }
            catch (SpeechException exception)
            {
                throw new SingingException(exception.Message, exception.Status);
            }
            SingingVoiceMade? result = null;
            await RunScriptAsync(
                new JsonObject
                {
                    ["mode"] = "voice",
                    ["audio"] = prepared,
                    ["lyrics"] = lyrics,
                    ["outputAudio"] = audio,
                    ["outputMetadata"] = metadata,
                },
                VoiceTimeout,
                "Listening to the recording took too long.",
                data =>
                {
                    if ((string?)data["event"] == "voice")
                    {
                        result = new SingingVoiceMade(
                            (double?)data["seconds"] ?? 0,
                            (double?)data["recordedSeconds"] ?? 0,
                            (int?)data["notes"] ?? 0,
                            (int?)data["held"] ?? 0,
                            (int?)data["unsung"] ?? 0,
                            (int?)data["leftover"] ?? 0,
                            (string?)data["text"] ?? "",
                            (double?)data["peak"] ?? 0,
                            (int?)data["low"] ?? 0,
                            (int?)data["high"] ?? 0);
                    }
                },
                () => result is not null && File.Exists(audio) && File.Exists(metadata),
                "Making a voice",
                // The script refuses a recording it hears no singing in, or words it cannot lay on it.
                StatusCodes.Status422UnprocessableEntity,
                cancellationToken);
            return result!;
        }
        finally
        {
            File.Delete(prepared);
        }
    }

    private void RequireInstalled()
    {
        if (!Installed)
        {
            throw new SingingException(
                $"SoulX-Singer is not installed ({options.Value.ResolvedRoot}); run deploy/install-singing.sh.", StatusCodes.Status501NotImplemented);
        }
    }

    /// <summary>Runs the script for an answer that needs no model: one request in, its events handed to <paramref name="read"/>.</summary>
    /// <param name="answered">Whether the script answered what was asked; otherwise its last lines become the error.</param>
    /// <param name="failure">The status of that error.</param>
    private async Task RunScriptAsync(
        JsonObject request,
        TimeSpan limit,
        string tooLong,
        Action<JsonNode> read,
        Func<bool> answered,
        string doing,
        int failure,
        CancellationToken cancellationToken)
    {
        using var process = Start(options.Value);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit);
        var tail = new Queue<string>();
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
                if (JsonNode.Parse(line[EventPrefix.Length..]) is { } data)
                {
                    read(data);
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
                throw new SingingException(tooLong);
            }
            throw;
        }
        if (process.ExitCode != 0 || !answered())
        {
            lock (tail)
            {
                logger.LogWarning("{Doing} failed:\n{Output}", doing, string.Join('\n', tail));
                throw new SingingException(MlxAudioEngine.FailureMessage(tail), failure);
            }
        }
    }

    private static readonly TimeSpan AlignTimeout = TimeSpan.FromSeconds(60);

    /// <summary>pYIN over 30 s of singing takes seconds on the Mac and half a minute in a slow container.</summary>
    private static readonly TimeSpan VoiceTimeout = TimeSpan.FromMinutes(3);

    private static JsonNode? Notes(IReadOnlyList<SungNote> notes) =>
        JsonSerializer.SerializeToNode(notes.Select(n => new { start = n.Start, end = n.End, pitch = n.Pitch, lyric = n.Lyric }));

    private static Process Start(SingingOptions settings)
    {
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
        start.Environment["PYTHONPATH"] = settings.ResolvedSource;
        start.Environment["NLTK_DATA"] = settings.ResolvedNltkData;
        // Operators Metal lacks run on the CPU instead of failing the take.
        start.Environment["PYTORCH_ENABLE_MPS_FALLBACK"] = "1";
        start.Environment["HF_HUB_OFFLINE"] = "1";
        start.Environment["PYTHONUNBUFFERED"] = "1";
        start.Environment["TQDM_DISABLE"] = "1";
        return Process.Start(start) ?? throw new SingingException($"Could not start {settings.ResolvedPython}.");
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
