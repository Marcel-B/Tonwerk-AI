using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using YueUI.Api.Voices;

namespace YueUI.Api.AudioMidi;

/// <param name="Audio">A WAV or FLAC.</param>
/// <param name="Output">Where the MIDI file is written.</param>
/// <param name="TrackName">The track's name in the file: <c>Vocal</c> makes the Logic page and the backing vocals read it as the melody.</param>
/// <param name="Kind">What the track is: a voice or a bass keeps to its range, drums are found by band instead of pitch.</param>
public sealed record AudioMidiJob(string Audio, string Output, string TrackName, AudioMidiSettings Settings, double Tempo, AudioMidiKind Kind);

public enum AudioMidiKind
{
    /// <summary>An instrument of no known range: Basic Pitch hears the whole spectrum.</summary>
    Other,

    /// <summary>A singing voice: notes outside its range (hiss, rumble) are left out.</summary>
    Voice,

    /// <summary>A bass: one note at a time, and only in a bass's range, since its overtones are heard as notes too.</summary>
    Bass,

    /// <summary>A drum kit: kick, snare and hi-hat on General MIDI's drum notes, found by their bands.</summary>
    Drums,
}

public sealed record AudioMidiResult(int Notes, double Seconds);

public sealed class AudioMidiException(string message, int status = StatusCodes.Status500InternalServerError) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>Turns a track into MIDI. Tests replace it; the real one runs Basic Pitch.</summary>
public interface IAudioMidiEngine
{
    /// <summary>The Python environment with Basic Pitch is there.</summary>
    bool Installed { get; }

    Task<AudioMidiResult> ConvertAsync(AudioMidiJob job, CancellationToken cancellationToken);
}

/// <summary>
/// Runs <c>AudioMidi/yueui_basicpitch.py</c> with Basic Pitch's own Python, one process per track. The model is a few
/// MB and runs on the CPU in seconds, so it waits for none of the big models; tracks are converted one at a time only
/// so two phones at once do not share the CPU with YuE2 twice over.
/// </summary>
public sealed class BasicPitchEngine(IOptions<AudioMidiOptions> options, ILogger<BasicPitchEngine> logger) : IAudioMidiEngine
{
    /// <summary>What the script's own events start with; everything else is Basic Pitch talking.</summary>
    private const string EventPrefix = "YUEUI ";

    /// <summary>C2 to F6: below a bass's lowest sung note and above a soprano's highest, not much more.</summary>
    private const double VoiceMinHz = 65;

    private const double VoiceMaxHz = 1400;

    /// <summary>B0 (a five-string bass's low string) to G4, the highest fundamental a bass line plays.</summary>
    private const double BassMinHz = 30;

    private const double BassMaxHz = 400;

    private readonly SemaphoreSlim _one = new(1, 1);

    public static string Script => Path.Combine(AppContext.BaseDirectory, "AudioMidi", "yueui_basicpitch.py");

    public bool Installed => File.Exists(options.Value.ResolvedPython);

    public async Task<AudioMidiResult> ConvertAsync(AudioMidiJob job, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!Installed)
        {
            throw new AudioMidiException($"Basic Pitch is not installed ({settings.ResolvedPython}); run deploy/install-midi.sh.", StatusCodes.Status501NotImplemented);
        }
        var request = new JsonObject
        {
            ["audio"] = job.Audio,
            ["output"] = job.Output,
            ["name"] = job.TrackName,
            ["mono"] = job.Settings.Mono || job.Kind == AudioMidiKind.Bass,
            ["quantize"] = job.Settings.Quantize,
            ["bends"] = job.Settings.Bends,
            ["tempo"] = job.Tempo,
            ["drums"] = job.Kind == AudioMidiKind.Drums,
            ["minHz"] = job.Kind switch { AudioMidiKind.Voice => VoiceMinHz, AudioMidiKind.Bass => BassMinHz, _ => null },
            ["maxHz"] = job.Kind switch { AudioMidiKind.Voice => VoiceMaxHz, AudioMidiKind.Bass => BassMaxHz, _ => null },
        };
        AudioMidiResult? result = null;
        ToolResult run;
        await _one.WaitAsync(cancellationToken);
        try
        {
            run = await ToolProcess.RunAsync(
                settings.ResolvedPython,
                ["-u", Script, request.ToJsonString()],
                settings.Timeout,
                cancellationToken,
                new Dictionary<string, string> { ["PYTHONUNBUFFERED"] = "1", ["PYTHONWARNINGS"] = "ignore" },
                line: line =>
                {
                    if (line.StartsWith(EventPrefix, StringComparison.Ordinal)
                        && JsonNode.Parse(line[EventPrefix.Length..]) is { } data
                        && (string?)data["event"] == "done")
                    {
                        result = new AudioMidiResult((int?)data["notes"] ?? 0, (double?)data["seconds"] ?? 0);
                    }
                });
        }
        catch (VoiceServiceException exception)
        {
            throw new AudioMidiException(exception.Message, (int)exception.Status);
        }
        finally
        {
            _one.Release();
        }
        // The script says done only after the file is written; a crash while Python shuts down after that (onnxruntime
        // aborted on the Mac at exit) costs nothing, so the file counts.
        if (result is not null && File.Exists(job.Output) && run.ExitCode != 0)
        {
            logger.LogWarning("Basic Pitch exited with {Code} after writing {Output}:\n{Tail}", run.ExitCode, Path.GetFileName(job.Output), string.Join('\n', run.Tail.TakeLast(3)));
        }
        else if (run.ExitCode != 0 || result is null || !File.Exists(job.Output))
        {
            logger.LogWarning("Basic Pitch failed on {Audio}:\n{Output}", Path.GetFileName(job.Audio), string.Join('\n', run.Tail));
            throw new AudioMidiException(ToolProcess.Reason(run, "Basic Pitch wrote no MIDI file."));
        }
        logger.LogInformation(
            "Basic Pitch found {Notes} notes in {Audio} ({Seconds} s)",
            result.Notes, Path.GetFileName(job.Audio), result.Seconds.ToString("0.0", CultureInfo.InvariantCulture));
        return result;
    }
}
