using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using YueUI.Api.Data;
using YueUI.Api.Library;
using YueUI.Api.Memory;
using YueUI.Api.Voices;
using YueUI.Api.Worker;

namespace YueUI.Api.Timing;

/// <summary>
/// Section <c>Timing</c>: when each lyrics line is sung, heard by Whisper (mlx-whisper), for the full-screen player
/// and the music videos. It needs its own Python environment, which <c>deploy/install-whisper.sh</c> makes.
/// </summary>
public sealed class TimingOptions
{
    public const string Section = "Timing";

    /// <summary>Default: <c>whisper</c> in the app's data folder, where the install script puts it.</summary>
    public string? Root { get; set; }

    /// <summary>The Python with mlx-whisper; default <c>env/bin/python</c> under <see cref="Root"/>.</summary>
    public string? Python { get; set; }

    /// <summary>
    /// The Whisper model: large-v3 turbo, about 1.6 GB, nearly large-v3's accuracy at several times its speed; its
    /// German is good enough to find words it is told to expect.
    /// </summary>
    public string Model { get; set; } = "mlx-community/whisper-large-v3-turbo";

    /// <summary>Where the model is downloaded on first use (<c>HF_HOME</c>); default <c>models</c> under <see cref="Root"/>.</summary>
    public string? ModelCache { get; set; }

    /// <summary>Every new song with lyrics is timed once it is ready; off leaves it to the player asking.</summary>
    public bool Auto { get; set; } = true;

    /// <summary>A song takes well under a minute; a first run also downloads the model.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(20);

    public string ResolvedRoot => string.IsNullOrWhiteSpace(Root)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YuE UI", "whisper")
        : Expand(Root);

    public string ResolvedPython => string.IsNullOrWhiteSpace(Python) ? Path.Combine(ResolvedRoot, "env", "bin", "python") : Expand(Python);

    public string ResolvedModelCache => string.IsNullOrWhiteSpace(ModelCache) ? Path.Combine(ResolvedRoot, "models") : Expand(ModelCache);

    private static string Expand(string path) => Path.GetFullPath(path.StartsWith("~/", StringComparison.Ordinal)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
        : path);
}

public sealed class TimingException(string message) : Exception(message);

/// <summary>Hears when the words of a recording are sung. Tests replace it; the real one runs Whisper.</summary>
public interface IWordListener
{
    bool Installed { get; }

    /// <param name="language"><c>de</c>, <c>en</c> or null to let Whisper guess.</param>
    /// <param name="prompt">The lyrics' beginning, so Whisper expects their words.</param>
    Task<IReadOnlyList<HeardWord>> ListenAsync(string audio, string? language, string prompt, CancellationToken cancellationToken);
}

/// <summary>Runs <c>Timing/yueui_whisper.py</c> with mlx-whisper's own Python, one process per song, so the model leaves the memory with it.</summary>
public sealed class WhisperListener(IOptions<TimingOptions> options, ILogger<WhisperListener> logger) : IWordListener
{
    private const string EventPrefix = "YUEUI ";

    public static string Script => Path.Combine(AppContext.BaseDirectory, "Timing", "yueui_whisper.py");

    public bool Installed => File.Exists(options.Value.ResolvedPython);

    public async Task<IReadOnlyList<HeardWord>> ListenAsync(string audio, string? language, string prompt, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var request = new JsonObject { ["audio"] = audio, ["model"] = settings.Model, ["language"] = language, ["prompt"] = prompt };
        List<HeardWord>? words = null;
        string? error = null;
        ToolResult run;
        try
        {
            run = await ToolProcess.RunAsync(
                settings.ResolvedPython,
                ["-u", Script, request.ToJsonString()],
                settings.Timeout,
                cancellationToken,
                new Dictionary<string, string>
                {
                    ["PYTHONUNBUFFERED"] = "1",
                    ["PYTHONWARNINGS"] = "ignore",
                    ["HF_HOME"] = settings.ResolvedModelCache,
                    // mlx-whisper decodes with ffmpeg, which a LaunchAgent's PATH does not reach.
                    ["PATH"] = $"/opt/homebrew/bin:/usr/local/bin:{Environment.GetEnvironmentVariable("PATH")}",
                },
                line: line =>
                {
                    if (!line.StartsWith(EventPrefix, StringComparison.Ordinal) || JsonNode.Parse(line[EventPrefix.Length..]) is not { } data)
                    {
                        return;
                    }
                    switch ((string?)data["event"])
                    {
                        case "done":
                            words = [.. (data["words"]?.AsArray() ?? []).OfType<JsonNode>()
                                .Select(w => new HeardWord((string?)w["word"] ?? "", (double?)w["start"] ?? 0, (double?)w["end"] ?? 0))];
                            logger.LogInformation("Whisper heard {Count} words in {Audio} ({Seconds} s)", words.Count, Path.GetFileName(audio), (double?)data["seconds"]);
                            break;
                        case "error":
                            error = (string?)data["message"];
                            break;
                    }
                });
        }
        catch (VoiceServiceException exception)
        {
            throw new TimingException(exception.Message);
        }
        if (words is null)
        {
            logger.LogWarning("Whisper failed on {Audio}:\n{Output}", audio, string.Join('\n', run.Tail));
            throw new TimingException(error ?? ToolProcess.Reason(run, "Whisper heard nothing."));
        }
        return words;
    }
}

/// <param name="Installed">mlx-whisper's environment is there.</param>
/// <param name="State"><c>none</c>, <c>queued</c>, <c>listening</c> or <c>failed</c> for a song without timing yet.</param>
public sealed record TimingInfo(bool Installed, string State, string? Message);

/// <summary>
/// Times songs' lyrics, one song at a time: new ones once they are ready (<see cref="TimingOptions.Auto"/>), older ones
/// when the player asks. The result lies in <c>lyrics-timing/&lt;run&gt;/songN.json</c> next to the database and goes
/// with the song.
/// </summary>
/// <remarks>
/// Whisper hears the vocal stem when the song has been split (much clearer), otherwise the mix. Its model (about
/// 1.6 GB) is small like SheetSage2's and runs beside YuE2, but it waits while another large model holds the memory
/// (<see cref="ModelMemory.Holder"/>), which may need all of it. A song without lyrics is never timed.
/// </remarks>
public sealed class LyricsTimer(
    Status.StatusHub hub,
    SongLibrary library,
    TimingFiles files,
    SqliteStemStore stems,
    SqliteSongTraitStore traits,
    ModelMemory memory,
    IWordListener listener,
    IOptions<TimingOptions> options,
    TimeProvider time,
    ILogger<LyricsTimer> logger) : BackgroundService
{
    /// <summary>How often a waiting song looks whether the memory came free.</summary>
    private static readonly TimeSpan MemoryPoll = TimeSpan.FromSeconds(10);

    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();

    /// <summary>Songs queued or being heard (and why the last try failed), so a song is asked for once.</summary>
    private readonly ConcurrentDictionary<string, TimingInfo> _states = new();

    public bool Installed => listener.Installed;

    /// <summary>The song's timing, if there is one.</summary>
    public LyricsTiming? Get(string run, string song) => files.Get(run, song);

    public TimingInfo State(string songId) =>
        _states.TryGetValue(songId, out var state) ? state : new TimingInfo(Installed, "none", null);

    /// <summary>Queues the song unless it waits or is heard already; a failed one is tried again.</summary>
    /// <returns>False when Whisper is not installed.</returns>
    public bool Enqueue(string songId)
    {
        if (!Installed)
        {
            return false;
        }
        if (_states.TryGetValue(songId, out var state) && state.State is "queued" or "listening")
        {
            return true;
        }
        _states[songId] = new TimingInfo(true, "queued", null);
        _queue.Writer.TryWrite(songId);
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        var listening = ListenToSongsAsync(stoppingToken);
        try
        {
            await foreach (var songId in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                while (memory.Holder is not null)
                {
                    await Task.Delay(MemoryPoll, time, stoppingToken);
                }
                await TimeAsync(songId, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        await listening;
    }

    /// <summary>New songs, from the status like <see cref="Share.StreamCopyMaker"/>; a render's FLAC sings the same words.</summary>
    private async Task ListenToSongsAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Auto)
        {
            return;
        }
        HashSet<string> seen = [];
        while (!stoppingToken.IsCancellationRequested)
        {
            using var subscription = hub.Subscribe(checkStudio: false);
            try
            {
                foreach (var song in subscription.Snapshot.Songs)
                {
                    Consider(song, seen);
                }
                await foreach (var item in subscription.Reader.ReadAllAsync(stoppingToken))
                {
                    if (item.Data is SongState song)
                    {
                        Consider(song, seen);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private void Consider(SongState song, HashSet<string> seen)
    {
        if (song.Stage == "ready" && !song.Render && seen.Add(song.Id) && song.Id.Split('/') is [var run, var name] && Get(run, name) is null)
        {
            Enqueue(song.Id);
        }
    }

    private async Task TimeAsync(string songId, CancellationToken stoppingToken)
    {
        if (songId.Split('/') is not [var run, var song] || library.SongDirectory(run, song) is not { } directory)
        {
            _states.TryRemove(songId, out _);
            return;
        }
        var request = library.ReadRequest(run, song);
        var lyrics = request?.Lyrics ?? "";
        if (request?.Instrumental == true || LyricsAligner.SungLines(lyrics).Count == 0)
        {
            _states[songId] = new TimingInfo(true, "failed", "The song has no lyrics.");
            return;
        }
        _states[songId] = new TimingInfo(true, "listening", null);
        var (audio, source) = Vocals(songId) is { } vocals ? (vocals, "vocals") : (Path.Combine(directory, "audio.flac"), "mix");
        try
        {
            var heard = await listener.ListenAsync(audio, Language(request!.Style, lyrics), Prompt(lyrics), stoppingToken);
            var timing = LyricsAligner.Align(lyrics, heard, SongLibrary.SecondsOf(directory), source, time.GetUtcNow());
            await files.SaveAsync(run, song, timing, stoppingToken);
            Clarity(songId, timing);
            _states.TryRemove(songId, out _);
            logger.LogInformation(
                "Lyrics of {Song} timed from the {Source}, {Coverage} of the words heard",
                songId, source, timing.Coverage.ToString("P0", CultureInfo.InvariantCulture));
        }
        catch (Exception exception) when (exception is TimingException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not time the lyrics of {Song}: {Reason}", songId, exception.Message);
            _states[songId] = new TimingInfo(true, "failed", exception.Message);
        }
    }

    /// <summary>How much of the lyrics was understood, for the library; the timing itself is in its file already.</summary>
    private void Clarity(string songId, LyricsTiming timing)
    {
        try
        {
            traits.SetClarity(songId, timing.Coverage, timing.Source);
            hub.LibraryChanged();
        }
        catch (Microsoft.Data.Sqlite.SqliteException exception)
        {
            logger.LogWarning("Could not keep the clarity of {Song}: {Reason}", songId, exception.Message);
        }
    }

    /// <summary>The newest finished stem set's dry (else plain) vocals, if the song has been split.</summary>
    private string? Vocals(string songId)
    {
        try
        {
            foreach (var set in stems.All().Where(s => s.SongId == songId && s.Stage == "done"))
            {
                if ((stems.FilePath(set, "vocals_dry") ?? stems.FilePath(set, "vocals")) is { } path && File.Exists(path))
                {
                    return path;
                }
            }
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
        }
        return null;
    }

    /// <summary>The style names the language as YuE2's examples do ("German, …"); else the lyrics' umlauts tell, else Whisper guesses.</summary>
    public static string? Language(string? style, string lyrics)
    {
        var lower = style?.ToLowerInvariant() ?? "";
        if (lower.Contains("german", StringComparison.Ordinal) || lower.Contains("deutsch", StringComparison.Ordinal))
        {
            return "de";
        }
        if (lower.Contains("english", StringComparison.Ordinal))
        {
            return "en";
        }
        return lyrics.IndexOfAny(['ä', 'ö', 'ü', 'ß', 'Ä', 'Ö', 'Ü']) >= 0 ? "de" : null;
    }

    /// <summary>Whisper's prompt holds some 220 tokens; the lyrics' first lines fit, which sets their words and spelling.</summary>
    public static string Prompt(string lyrics)
    {
        var text = string.Join(' ', LyricsAligner.SungLines(lyrics).Select(l => l.Text));
        return text.Length > 600 ? text[..text.LastIndexOf(' ', 600)] : text;
    }

}

/// <summary>The timings' files, in <c>lyrics-timing/&lt;run&gt;/songN.json</c> next to the database.</summary>
public sealed class TimingFiles(SqliteDatabase database)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private string Root => Path.Combine(database.Directory, "lyrics-timing");

    /// <summary>Where the song's timing lies; <paramref name="run"/> and <paramref name="song"/> must be checked names.</summary>
    public string PathOf(string run, string song) => Path.Combine(Root, run, $"{song}.json");

    public LyricsTiming? Get(string run, string song)
    {
        var path = PathOf(run, song);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<LyricsTiming>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Into a temporary file first, so a reader never gets half of it.</summary>
    public async Task SaveAsync(string run, string song, LyricsTiming timing, CancellationToken cancellationToken)
    {
        var path = PathOf(run, song);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".part";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(timing, Json), cancellationToken);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>The song is gone; so is its timing.</summary>
    public void ForgetSong(string run, string song) => Delete(() => File.Delete(PathOf(run, song)));

    public void ForgetRun(string run) => Delete(() =>
    {
        var directory = Path.Combine(Root, run);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    });

    private static void Delete(Action delete)
    {
        try
        {
            delete();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
