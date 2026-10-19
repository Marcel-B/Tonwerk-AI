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
        var settings = options.Value;
        if (!Installed)
        {
            throw new SingingException(
                $"SoulX-Singer is not installed ({settings.ResolvedRoot}); run deploy/install-singing.sh.", StatusCodes.Status501NotImplemented);
        }
        var request = new JsonObject
        {
            ["mode"] = "align",
            ["notes"] = Notes(notes),
            ["lyrics"] = lyrics,
        };
        using var process = Start(settings);
        // Only g2p_en loads, a second or two; a dictionary that never answers must not hold the editor.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(AlignTimeout);
        var tail = new Queue<string>();
        SingingAligned? result = null;
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
                if ((string?)data?["event"] == "assigned")
                {
                    var sung = (data["notes"] as JsonArray ?? [])
                        .Select(n => new AlignedNote((string?)n?["word"], (int?)n?["type"] ?? 1, (string?)n?["phonemes"] ?? ""))
                        .ToList();
                    result = new SingingAligned(sung, (int?)data["leftover"] ?? 0);
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
                throw new SingingException("The pronunciation dictionary did not answer.");
            }
            throw;
        }
        if (process.ExitCode != 0 || result is null || result.Notes.Count != notes.Count)
        {
            lock (tail)
            {
                logger.LogWarning("Aligning failed:\n{Output}", string.Join('\n', tail));
                throw new SingingException(MlxAudioEngine.FailureMessage(tail));
            }
        }
        return result;
    }

    private static readonly TimeSpan AlignTimeout = TimeSpan.FromSeconds(60);

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
