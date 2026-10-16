using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using YueUI.Api.Data;
using YueUI.Api.Library;
using YueUI.Api.Memory;
using YueUI.Api.Timing;
using YueUI.Api.Voices;
using YueUI.Api.Worker;

namespace YueUI.Api.Analysis;

/// <summary>
/// Section <c>Analysis</c>: tempo and key heard in every song, with librosa. It brings no environment of its own: the
/// photo videos' (<c>deploy/install-montage.sh</c>) and audio to MIDI's (<c>deploy/install-midi.sh</c>) both have
/// librosa, and the first one installed is taken.
/// </summary>
public sealed class AnalysisOptions
{
    public const string Section = "Analysis";

    /// <summary>A Python with librosa; default the photo videos', else audio to MIDI's.</summary>
    public string? Python { get; set; }

    /// <summary>Every song is heard once, new ones when they are ready, older ones while nothing else runs.</summary>
    public bool Auto { get; set; } = true;

    /// <summary>Songs per process: loading librosa takes longer than hearing one song.</summary>
    public int BatchSize { get; set; } = 10;

    /// <summary>A song takes some seconds; a batch gets this much in all.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>The first look through the library waits so long after the start, which has enough else to do.</summary>
    public TimeSpan StartDelay { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How often the library is looked through again for songs nobody announced (YuE Studio's own).</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>How often a waiting batch looks whether the models are done.</summary>
    public TimeSpan IdlePoll { get; set; } = TimeSpan.FromSeconds(15);

    public string? ResolvedPython(Video.MontageOptions montage, AudioMidi.AudioMidiOptions audioMidi)
    {
        if (!string.IsNullOrWhiteSpace(Python))
        {
            return Path.GetFullPath(Python.StartsWith("~/", StringComparison.Ordinal)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Python[2..])
                : Python);
        }
        return ((string[])[montage.ResolvedPython, audioMidi.ResolvedPython]).FirstOrDefault(File.Exists);
    }
}

/// <param name="TempoHint">The tempo the song was asked for, which settles whether it is heard at double or half speed.</param>
public sealed record SongToHear(string Id, string Audio, double? TempoHint);

/// <param name="Error">Why the song could not be heard; tempo and key are null then.</param>
public sealed record HeardSong(string Id, double? Tempo, string? Key, string? Error);

/// <summary>Hears tempo and key of songs. Tests replace it; the real one runs librosa.</summary>
public interface ISongListener
{
    bool Installed { get; }

    Task<IReadOnlyList<HeardSong>> HearAsync(IReadOnlyList<SongToHear> songs, CancellationToken cancellationToken);
}

/// <summary>Runs <c>Analysis/yueui_analyze.py</c>, one process per batch of songs.</summary>
public sealed class LibrosaListener(
    IOptions<AnalysisOptions> options,
    IOptions<Video.MontageOptions> montage,
    IOptions<AudioMidi.AudioMidiOptions> audioMidi,
    ILogger<LibrosaListener> logger) : ISongListener
{
    private const string EventPrefix = "YUEUI ";

    public static string Script => Path.Combine(AppContext.BaseDirectory, "Analysis", "yueui_analyze.py");

    private string? Python => options.Value.ResolvedPython(montage.Value, audioMidi.Value);

    public bool Installed => Python is { } python && File.Exists(python);

    public async Task<IReadOnlyList<HeardSong>> HearAsync(IReadOnlyList<SongToHear> songs, CancellationToken cancellationToken)
    {
        var python = Python ?? throw new InvalidOperationException("No Python with librosa is installed.");
        var request = new JsonObject
        {
            ["songs"] = new JsonArray([.. songs.Select(s => (JsonNode)new JsonObject { ["id"] = s.Id, ["audio"] = s.Audio, ["tempoHint"] = s.TempoHint })]),
        };
        List<HeardSong> heard = [];
        ToolResult run;
        try
        {
            run = await ToolProcess.RunAsync(
                python,
                ["-u", Script, request.ToJsonString()],
                options.Value.Timeout,
                cancellationToken,
                new Dictionary<string, string>
                {
                    ["PYTHONUNBUFFERED"] = "1",
                    ["PYTHONWARNINGS"] = "ignore",
                    // librosa reads FLAC itself; anything else goes through audioread, which wants ffmpeg.
                    ["PATH"] = $"/opt/homebrew/bin:/usr/local/bin:{Environment.GetEnvironmentVariable("PATH")}",
                },
                line: line =>
                {
                    if (!line.StartsWith(EventPrefix, StringComparison.Ordinal) || JsonNode.Parse(line[EventPrefix.Length..]) is not { } data)
                    {
                        return;
                    }
                    var id = (string?)data["id"] ?? "";
                    heard.Add((string?)data["event"] == "song"
                        ? new HeardSong(id, (double?)data["tempo"], (string?)data["key"], null)
                        : new HeardSong(id, null, null, (string?)data["message"] ?? "failed"));
                });
        }
        catch (VoiceServiceException exception)
        {
            // A batch cut off by its timeout: the songs heard so far count, the rest is tried another time.
            logger.LogWarning("Hearing songs stopped: {Reason}", exception.Message);
            return heard;
        }
        if (heard.Count < songs.Count && run.ExitCode != 0)
        {
            logger.LogWarning("Hearing songs failed:\n{Output}", string.Join('\n', run.Tail));
        }
        return heard;
    }
}

/// <summary>
/// Hears tempo and key of every song with audio, one batch at a time while YuE2 and the other large models rest:
/// librosa needs only the CPU and some hundred MB, but a song generating should not share them. Without librosa, a
/// song takes what its score (<c>Q:</c>, <c>K:</c>) or ACE-Step's request asked for, marked as such, and is heard once
/// librosa comes. The lyrics' clarity is <see cref="LyricsTimer"/>'s; songs timed before this existed get it here.
/// </summary>
public sealed partial class SongAnalyzer(
    Status.StatusHub hub,
    SongLibrary library,
    SqliteSongTraitStore traits,
    TimingFiles timings,
    WorkerHost worker,
    ModelMemory memory,
    ISongListener listener,
    IOptions<AnalysisOptions> options,
    TimeProvider time,
    ILogger<SongAnalyzer> logger) : BackgroundService
{
    private readonly SemaphoreSlim _wake = new(0);

    /// <summary>Songs librosa could not read; not tried again until a restart, so a broken file is not heard every round.</summary>
    private readonly HashSet<string> _failed = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Auto)
        {
            return;
        }
        await Task.Yield();
        var listening = ListenToSongsAsync(stoppingToken);
        try
        {
            await Task.Delay(options.Value.StartDelay, time, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RoundAsync(stoppingToken);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
                {
                    logger.LogWarning("Looking through the songs failed: {Reason}", exception.Message);
                }
                await _wake.WaitAsync(options.Value.Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        await listening;
    }

    /// <summary>A song ready (not a render, which plays the same notes) starts a round, like <see cref="LyricsTimer"/> hears it.</summary>
    private async Task ListenToSongsAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var subscription = hub.Subscribe(checkStudio: false);
            try
            {
                await foreach (var item in subscription.Reader.ReadAllAsync(stoppingToken))
                {
                    if (item.Data is SongState { Stage: "ready", Render: false } || item.Data is Ace.AceJob { Stage: "done" })
                    {
                        _wake.Release();
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>One look through the library: clarity copied from timings, then the songs not heard yet, in batches.</summary>
    public async Task RoundAsync(CancellationToken stoppingToken)
    {
        var known = traits.All();
        List<(SongToHear Song, (double? Tempo, string? Key) Asked)> toHear = [];
        foreach (var run in library.RunNames().OrderDescending(StringComparer.Ordinal))
        {
            foreach (var directory in library.SongDirectories(run) ?? [])
            {
                var song = Path.GetFileName(directory);
                var id = $"{run}/{song}";
                var audio = Path.Combine(directory, "audio.flac");
                if (!File.Exists(audio) || worker.IsWorkingOn(run, song))
                {
                    continue;
                }
                known.TryGetValue(id, out var have);
                if (have?.Clarity is null && timings.Get(run, song) is { Lines.Count: > 0 } timing)
                {
                    traits.SetClarity(id, timing.Coverage, timing.Source);
                }
                if (have?.From == "audio" || _failed.Contains(id))
                {
                    continue;
                }
                var asked = Asked(directory);
                if (listener.Installed)
                {
                    toHear.Add((new SongToHear(id, audio, asked.Tempo), asked));
                }
                else if (have?.From is null && (asked.Tempo is not null || asked.Key is not null))
                {
                    traits.SetSound(id, asked.Tempo, asked.Key, "score");
                }
            }
        }
        // Newest first, so the songs just made are heard before a backlog of old ones.
        foreach (var batch in toHear.Chunk(Math.Max(options.Value.BatchSize, 1)))
        {
            await WaitForRestAsync(stoppingToken);
            var heard = (await listener.HearAsync([.. batch.Select(b => b.Song)], stoppingToken)).ToDictionary(h => h.Id);
            foreach (var (song, asked) in batch)
            {
                if (!heard.TryGetValue(song.Id, out var result))
                {
                    continue;
                }
                // Deleted while it was heard: no row for a song that is gone.
                if (song.Id.Split('/') is not [var run, var name] || library.SongDirectory(run, name) is null)
                {
                    continue;
                }
                if (result.Error is null)
                {
                    traits.SetSound(song.Id, result.Tempo, result.Key ?? asked.Key, "audio");
                }
                else
                {
                    logger.LogWarning("Could not hear {Song}: {Reason}", song.Id, result.Error);
                    _failed.Add(song.Id);
                    if (!known.ContainsKey(song.Id) && (asked.Tempo is not null || asked.Key is not null))
                    {
                        traits.SetSound(song.Id, asked.Tempo, asked.Key, "score");
                    }
                }
            }
            hub.LibraryChanged();
        }
    }

    private async Task WaitForRestAsync(CancellationToken stoppingToken)
    {
        while (worker.IsBusy || memory.Holder is not null)
        {
            await Task.Delay(options.Value.IdlePoll, time, stoppingToken);
        }
    }

    /// <summary>What the song was asked to be: the score's tempo and key, or ACE-Step's bpm and key scale.</summary>
    public static (double? Tempo, string? Key) Asked(string songDirectory)
    {
        double? tempo = null;
        string? key = null;
        try
        {
            var score = Path.Combine(songDirectory, "score.abc");
            if (File.Exists(score))
            {
                var abc = File.ReadAllText(score);
                if (TempoField().Match(abc) is { Success: true } q)
                {
                    tempo = double.Parse(q.Groups["bpm"].Value, CultureInfo.InvariantCulture);
                }
                if (KeyField().Match(abc) is { Success: true } k)
                {
                    key = KeyName(k.Groups["key"].Value);
                }
            }
            var ace = Path.Combine(songDirectory, SongLibrary.AceFile);
            if (File.Exists(ace) && JsonNode.Parse(File.ReadAllText(ace)) is JsonObject request)
            {
                tempo ??= request["bpm"] is JsonValue bpm && bpm.TryGetValue<double>(out var b) && b > 0 ? b : null;
                key ??= request["keyscale"] is JsonValue scale && scale.TryGetValue<string>(out var s) ? KeyName(s) : null;
            }
        }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException or FormatException)
        {
        }
        return (tempo is > 20 and < 400 ? tempo : null, key);
    }

    private static readonly string[] MajorNames = ["C", "Db", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B"];
    private static readonly string[] MinorNames = ["Cm", "C#m", "Dm", "Ebm", "Em", "Fm", "F#m", "Gm", "G#m", "Am", "Bbm", "Bm"];

    /// <summary>
    /// "Am", "A min", "A minor", "F#", "Gb major" as the key picker spells it ("F#m", "F#"); null for a church mode or
    /// anything else, which the picker does not know.
    /// </summary>
    public static string? KeyName(string text)
    {
        if (KeyText().Match(text.Trim()) is not { Success: true } match)
        {
            return null;
        }
        var root = "C D EF G A B".IndexOf(char.ToUpperInvariant(match.Groups["root"].Value[0]), StringComparison.Ordinal);
        root += match.Groups["accidental"].Value switch
        {
            "#" or "♯" => 1,
            "b" or "♭" => -1,
            _ => 0,
        };
        var mode = match.Groups["mode"].Value.ToLowerInvariant();
        var minor = mode is "m" or "min" or "minor" or "moll";
        if (!minor && mode is not ("" or "maj" or "major" or "dur"))
        {
            return null;
        }
        return (minor ? MinorNames : MajorNames)[(root + 12) % 12];
    }

    [GeneratedRegex(@"^Q:\s*(?:\d+/\d+\s*=\s*)?(?<bpm>\d+(?:\.\d+)?)\s*$", RegexOptions.Multiline)]
    private static partial Regex TempoField();

    [GeneratedRegex(@"^K:\s*(?<key>[^\r\n%]*)", RegexOptions.Multiline)]
    private static partial Regex KeyField();

    [GeneratedRegex(@"^(?<root>[A-Ga-g])(?<accidental>[#♯b♭]?)[\s-]*(?<mode>[A-Za-z]*)$")]
    private static partial Regex KeyText();
}
