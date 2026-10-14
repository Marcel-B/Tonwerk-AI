using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using YueUI.Api.Data;
using YueUI.Api.Export;
using YueUI.Api.Share;
using YueUI.Api.Voices;
using YueUI.Api.Worker;

namespace YueUI.Api.Library;

/// <param name="Id">The run folder's name, e.g. <c>20260921-165850-Neon-Night-Struggle</c>.</param>
/// <param name="Title">The title given in this app if there is one, else the worker's.</param>
/// <param name="OriginalTitle">The worker's title, which the folder is named after; what an emptied title returns to.</param>
/// <param name="Style">The style prompt, from the first song's <c>request.json</c>; all songs of a run share it.</param>
/// <param name="Bytes">What the whole run folder takes on disk.</param>
public sealed record RunInfo(
    string Id,
    string Title,
    string OriginalTitle,
    DateTimeOffset? CreatedAt,
    string Style,
    string Lyrics,
    IReadOnlyList<SongInfo> Songs,
    long Bytes);

/// <param name="Id"><c>run/songN</c>, the key for the song endpoints and the worker's song states.</param>
/// <param name="Quality">"draft" or "full" once synthesized.</param>
/// <param name="CanRender">Its tokens are saved, so it can be synthesized again (a draft at full quality).</param>
/// <param name="Bytes">What the song folder takes on disk: audio, tokens and the worker's intermediate files.</param>
/// <param name="Rating">One to five stars given in this app, null while not rated.</param>
/// <param name="Versions">The song sung with other voices, oldest first; cancelled ones are left out.</param>
/// <param name="CoverUpdatedAt">When the song's cover was chosen, null while it has the drawn one.</param>
/// <param name="Note">A note written to the song in this app, null while there is none.</param>
/// <param name="VideoFormats">The formats the song has a finished music video in; empty while it has none.</param>
/// <param name="Engine"><c>ace</c> for a song ACE-Step made (Ace/AceComposer.cs), null for YuE2's.</param>
/// <param name="Traits">Tempo, key and how clearly the lyrics are sung, as far as heard yet (Analysis/SongAnalyzer.cs).</param>
public sealed record SongInfo(
    string Id,
    int Index,
    long? Seed,
    string? Quality,
    double? Seconds,
    bool HasAudio,
    bool HasScore,
    bool CanRender,
    long Bytes,
    int? Rating,
    IReadOnlyList<VersionState> Versions,
    DateTimeOffset? CoverUpdatedAt = null,
    string? Note = null,
    IReadOnlyList<string>? VideoFormats = null,
    string? Engine = null,
    SongTraits? Traits = null);

/// <summary>
/// What a song was generated with, from its <c>request.json</c>, in the terms of <see cref="GenerateRequest"/>, so the
/// web form can take it over as a new song. Null where the file does not say (YuE Studio's own songs, older workers);
/// the form keeps its default there.
/// </summary>
/// <param name="Title">The request's title, else the run's.</param>
/// <param name="Seed">This song's seed; the songs of a batch have consecutive ones.</param>
public sealed record SongRequest(
    string Title,
    string Style,
    string Lyrics,
    bool? Instrumental,
    string? Quality,
    string? Cot,
    long? Seed,
    string? Engines,
    int? DraftSteps,
    int? MaxTokens,
    string? Abc,
    int? FullSteps,
    SamplingOverrides? AbcSampling,
    SamplingOverrides? SemanticSampling,
    string? Lora = null,
    double? LoraStrength = null);

/// <summary>
/// Reads the songs the worker (this server's or YuE Studio's) wrote to <see cref="YuePaths.OutputDir"/>:
/// one folder per run, one <c>songN</c> folder per song with <c>audio.flac</c>, <c>score.abc</c>,
/// <c>request.json</c> (the prompt) and <c>result.json</c> (title, quality, length). ACE-Step's songs
/// (Ace/AceComposer.cs) are run folders of the same shape in a folder of their own, <see cref="AceDirectory"/>, with an
/// <c>ace.json</c> in each song; a run name is looked for in both, YuE's first.
/// </summary>
/// <remarks>Only names matching the worker's own patterns are accepted, which also keeps request paths inside the library.</remarks>
public sealed partial class SongLibrary(
    YuePaths paths,
    SqliteRunTitleStore titles,
    SqliteSongRatingStore ratings,
    SqliteVersionStore versions,
    SqliteStemStore stems,
    SqliteCoverStore covers,
    SqliteSongNoteStore notes,
    SqliteVideoStore videos,
    StreamCopies streams,
    SqliteDatabase database,
    Microsoft.Extensions.Options.IOptions<Ace.AceOptions> ace,
    Timing.TimingFiles timings,
    SqliteSongTraitStore traits)
{
    public string OutputDir => paths.OutputDir;

    /// <summary>Where ACE-Step's run folders are, apart from YuE Studio's, whose app would not understand them.</summary>
    public string AceDirectory => ace.Value.ResolvedSongs(database.Directory);

    /// <summary>What marks a song folder as ACE-Step's: the request as acestep.cpp synthesized it.</summary>
    public const string AceFile = "ace.json";

    /// <summary>The run's folder in either library, or null for a name the worker would not write or a run that does not exist.</summary>
    public string? RunDirectory(string run)
    {
        if (!RunName().IsMatch(run))
        {
            return null;
        }
        foreach (var root in (string[])[paths.OutputDir, AceDirectory])
        {
            var directory = Path.Combine(root, run);
            if (Directory.Exists(directory))
            {
                return directory;
            }
        }
        return null;
    }

    /// <summary>Every run folder's name in both libraries, for the backup.</summary>
    public IEnumerable<string> RunNames() =>
        Roots().SelectMany(root => root.EnumerateDirectories()).Select(d => d.Name).Where(name => RunName().IsMatch(name));

    /// <summary>Whether <paramref name="name"/> would make a valid run folder's name.</summary>
    public static bool IsRunName(string name) => RunName().IsMatch(name);

    private IEnumerable<DirectoryInfo> Roots() =>
        ((string[])[paths.OutputDir, AceDirectory]).Select(root => new DirectoryInfo(root)).Where(root => root.Exists);

    public IReadOnlyList<RunInfo> ListRuns()
    {
        var roots = Roots().ToList();
        if (roots.Count == 0)
        {
            return [];
        }
        var renamed = Read(titles.All) ?? new Dictionary<string, string>();
        var rated = Read(ratings.All) ?? new Dictionary<string, int>();
        var covered = Read(covers.All) ?? new Dictionary<string, DateTimeOffset>();
        var noted = Read(notes.All) ?? new Dictionary<string, string>();
        var filmed = Read(videos.FinishedFormats) ?? new Dictionary<string, IReadOnlyList<string>>();
        var heard = Read(traits.All) ?? new Dictionary<string, SongTraits>();
        var sung = (Read(versions.All) ?? [])
            .Where(v => v.Stage != "cancelled")
            .GroupBy(v => v.SongId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<VersionState>)[.. g]);
        return
        [
            .. roots.SelectMany(root => root.EnumerateDirectories())
                .Where(d => RunName().IsMatch(d.Name))
                // The same name in both (two runs in the same second) shows YuE's, as RunDirectory finds it.
                .DistinctBy(d => d.Name)
                .OrderByDescending(d => d.Name, StringComparer.Ordinal)
                .Select(d => ReadRun(d, renamed.GetValueOrDefault(d.Name), rated, covered, noted, sung, filmed, heard))
                // A run still tokenizing has no song folders yet; the queue shows it.
                .Where(r => r.Songs.Count > 0),
        ];
    }

    /// <summary>The song's folder, or null for a name the worker would not write or a song that does not exist.</summary>
    public string? SongDirectory(string run, string song)
    {
        if (RunDirectory(run) is not { } runDirectory || !SongName().IsMatch(song))
        {
            return null;
        }
        var directory = Path.Combine(runDirectory, song);
        return Directory.Exists(directory) ? directory : null;
    }

    /// <summary>The run's song folders in order, or null for a name the worker would not write or a run that does not exist.</summary>
    public IReadOnlyList<string>? SongDirectories(string run)
    {
        if (RunDirectory(run) is not { } path)
        {
            return null;
        }
        return [.. SongFolders(new DirectoryInfo(path)).Select(d => d.FullName)];
    }

    /// <summary>Deletes the song's folder, and the run's folder with it once no song is left.</summary>
    /// <returns>False for a name the worker would not write or a song that does not exist.</returns>
    public bool DeleteSong(string run, string song)
    {
        if (SongDirectory(run, song) is not { } directory)
        {
            return false;
        }
        Directory.Delete(directory, recursive: true);
        Forget(() =>
        {
            ratings.Remove($"{run}/{song}");
            versions.RemoveSong($"{run}/{song}");
            stems.RemoveSong($"{run}/{song}");
            covers.Remove($"{run}/{song}");
            notes.Remove($"{run}/{song}");
            videos.RemoveSong($"{run}/{song}");
            traits.Remove($"{run}/{song}");
        });
        streams.ForgetSong(run, song);
        timings.ForgetSong(run, song);
        var runDirectory = new DirectoryInfo(Path.GetDirectoryName(directory)!);
        if (!SongFolders(runDirectory).Any())
        {
            runDirectory.Delete(recursive: true);
            ForgetRun(run);
        }
        return true;
    }

    /// <summary>Deletes the run's folder with all its songs.</summary>
    /// <returns>False for a name the worker would not write or a run that does not exist.</returns>
    public bool DeleteRun(string run)
    {
        if (RunDirectory(run) is not { } directory)
        {
            return false;
        }
        Directory.Delete(directory, recursive: true);
        ForgetRun(run);
        return true;
    }

    /// <summary>Gives the run a title of its own, or with an empty one returns it to the worker's.</summary>
    /// <returns>False for a name the worker would not write or a run that does not exist.</returns>
    public bool Rename(string run, string title)
    {
        if (SongDirectories(run) is null)
        {
            return false;
        }
        if (title.Length == 0)
        {
            titles.Remove(run);
        }
        else
        {
            titles.Set(run, title);
        }
        return true;
    }

    /// <summary>Gives the song one to five stars, or with null takes its rating away.</summary>
    /// <returns>False for a name the worker would not write or a song that does not exist.</returns>
    public bool Rate(string run, string song, int? rating)
    {
        if (SongDirectory(run, song) is null)
        {
            return false;
        }
        if (rating is { } stars)
        {
            ratings.Set($"{run}/{song}", stars);
        }
        else
        {
            ratings.Remove($"{run}/{song}");
        }
        return true;
    }

    /// <summary>Writes the song's note, or with an empty one takes it away.</summary>
    /// <returns>False for a name the worker would not write or a song that does not exist.</returns>
    public bool SetNote(string run, string song, string note)
    {
        if (SongDirectory(run, song) is null)
        {
            return false;
        }
        if (note.Length == 0)
        {
            notes.Remove($"{run}/{song}");
        }
        else
        {
            notes.Set($"{run}/{song}", note);
        }
        return true;
    }

    /// <summary>Gives the song its own cover, or with null returns it to the drawn one.</summary>
    /// <returns>False for a name the worker would not write or a song that does not exist.</returns>
    public bool SetCover(string run, string song, CoverImage? image)
    {
        if (SongDirectory(run, song) is null)
        {
            return false;
        }
        if (image is null)
        {
            covers.Remove($"{run}/{song}");
        }
        else
        {
            covers.Set($"{run}/{song}", image);
        }
        return true;
    }

    /// <summary>The song's own cover, or null (also when the database cannot be read).</summary>
    public SongCover? CoverOf(string run, string song) =>
        SongDirectory(run, song) is null ? null : Read(() => covers.Get($"{run}/{song}"));

    /// <summary>The title given to the run in this app, or null (also when the database cannot be read).</summary>
    public string? RenamedTitle(string run) => RunName().IsMatch(run) ? Read(() => titles.Get(run)) : null;

    /// <summary>
    /// Maps a path the worker reports (<c>…/run/songN/audio.flac</c> or the song folder) to <c>run/songN</c>.
    /// </summary>
    public string IdFor(string path)
    {
        var directory = Path.GetFileName(path) == "audio.flac" ? Path.GetDirectoryName(path) ?? "" : path;
        directory = directory.TrimEnd(Path.DirectorySeparatorChar);
        return $"{Path.GetFileName(Path.GetDirectoryName(directory))}/{Path.GetFileName(directory)}";
    }

    /// <summary>The run's title for file names: from its songs, else from the folder name's slug.</summary>
    public string TitleOf(string run, string songDirectory) =>
        RenamedTitle(run) is { } renamed
            ? renamed
            : ReadJson(Path.Combine(songDirectory, "result.json")) is { } result && Text(result["title"]) is { Length: > 0 } title
            ? title
            : TitleFromName(run);

    /// <summary>The generate command a song was made with, beside YuE Studio's own files.</summary>
    public const string AppRequestFile = "yueui-request.json";

    /// <summary>
    /// Keeps the generate command a song was made with in its folder, without the parts that only concern the whole
    /// command (batch, seed choice) or this machine (the LoRA's path; its name is in <c>yueui.json</c>).
    /// </summary>
    public void SaveAppRequest(string songDirectory, JsonObject command)
    {
        if (!Directory.Exists(songDirectory))
        {
            return;
        }
        var saved = command.DeepClone().AsObject();
        foreach (var key in (string[])["cmd", "batch", "seed", "random_seed", "lora", "lora_strength"])
        {
            saved.Remove(key);
        }
        File.WriteAllText(Path.Combine(songDirectory, AppRequestFile), saved.ToJsonString());
    }

    /// <summary>The song's <c>request.json</c>, or null for an unknown song or one without (readable) request.</summary>
    public SongRequest? ReadRequest(string run, string song)
    {
        if (SongDirectory(run, song) is not { } directory || ReadJson(Path.Combine(directory, "request.json")) is not { } worker)
        {
            return null;
        }
        // YuE Studio's request.json keeps only style, lyrics, planning, seed and score; what the form sent is beside it
        // for songs made since this app saves it, and wins (the worker turns an instrumental's planning to "full").
        // The seed is the song's own from request.json, the command only names the first of its batch.
        var request = ReadJson(Path.Combine(directory, AppRequestFile)) ?? new JsonObject();
        request.Remove("seed");
        foreach (var (key, value) in worker)
        {
            if (!request.ContainsKey(key))
            {
                request[key] = value?.DeepClone();
            }
        }
        // Older songs: quality and its steps at least are in the worker's result.
        if (Text(request["quality"]) is null
            && (ReadJson(Path.Combine(directory, "result.json")) ?? ReadJson(Path.Combine(directory, "tokens.json"))) is { } result
            && Text(result["quality"]) is { } quality)
        {
            request["quality"] = quality;
            if (Number(result["ode_steps"] ?? result["steps"]) is { } steps)
            {
                request[quality == "full" ? "full_steps" : "draft_steps"] = steps;
            }
        }
        // The worker extension notes a song's LoRA beside it when its synthesis starts.
        var extension = ReadJson(Path.Combine(directory, "yueui.json"));
        var lora = Text(extension?["lora_name"]) is { Length: > 0 } name ? name : null;
        // The worker's own names; the extension's fields (full_steps, the sampling) are there when the app sent them.
        return new SongRequest(
            // A title given in this app wins over the one the song was made with.
            RenamedTitle(run) ?? (Text(request["title"]) is { Length: > 0 } title ? title : TitleOf(run, directory)),
            Text(request["style"]) ?? "",
            Text(request["lyrics"]) ?? "",
            Flag(request["instrumental"]),
            Text(request["quality"]),
            Text(request["cot"]),
            (long?)Number(request["seed"]),
            Text(request["engines"]),
            (int?)Number(request["draft_steps"]),
            (int?)Number(request["max_tokens"]),
            Text(request["abc"]) is { Length: > 0 } abc ? abc : null,
            (int?)Number(request["full_steps"]),
            Sampling(request["abc_sampling"]),
            Sampling(request["semantic_sampling"]),
            lora,
            lora is null ? null : Number(extension!["lora_strength"]));
    }

    private static SamplingOverrides? Sampling(JsonNode? node) =>
        node is JsonObject values
            ? new SamplingOverrides(
                Number(values["temperature"]),
                Number(values["top_p"]),
                (int?)Number(values["top_k"]),
                Number(values["repetition_penalty"]),
                (int?)Number(values["penalty_window"]))
            : null;

    /// <summary>
    /// The songs are YuE Studio's and stay listed without this app's database (a data folder that is not writable,
    /// a locked file); they only lose the titles given here.
    /// </summary>
    private static T? Read<T>(Func<T?> read) where T : class
    {
        try
        {
            return read();
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A later run could not reuse the name (it carries the second it started), but the rows are of no use now.</summary>
    private void ForgetRun(string run) => Forget(() =>
    {
        titles.Remove(run);
        ratings.RemoveRun(run);
        versions.RemoveRun(run);
        stems.RemoveRun(run);
        covers.RemoveRun(run);
        notes.RemoveRun(run);
        videos.RemoveRun(run);
        traits.RemoveRun(run);
        streams.ForgetRun(run);
        timings.ForgetRun(run);
    });

    /// <summary>The files are gone already; a database that cannot be written only keeps a row nobody sees.</summary>
    private static void Forget(Action remove)
    {
        try
        {
            remove();
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
        }
    }

    private RunInfo ReadRun(
        DirectoryInfo run,
        string? renamed,
        IReadOnlyDictionary<string, int> rated,
        IReadOnlyDictionary<string, DateTimeOffset> covered,
        IReadOnlyDictionary<string, string> noted,
        IReadOnlyDictionary<string, IReadOnlyList<VersionState>> sung,
        IReadOnlyDictionary<string, IReadOnlyList<string>> filmed,
        IReadOnlyDictionary<string, SongTraits> heard)
    {
        var songs = new List<SongInfo>();
        string title = "", style = "", lyrics = "";
        foreach (var folder in SongFolders(run))
        {
            // result.json once synthesized, tokens.json while only the tokens exist; both carry title and quality.
            var result = ReadJson(Path.Combine(folder.FullName, "result.json")) ?? ReadJson(Path.Combine(folder.FullName, "tokens.json"));
            var request = ReadJson(Path.Combine(folder.FullName, "request.json"));
            if (title.Length == 0 && result is not null)
            {
                title = Text(result["title"]) ?? "";
            }
            if (style.Length == 0 && request is not null)
            {
                style = Text(request["style"]) ?? "";
                lyrics = Text(request["lyrics"]) ?? "";
            }

            var audio = File.Exists(Path.Combine(folder.FullName, "audio.flac"));
            var id = $"{run.Name}/{folder.Name}";
            songs.Add(new SongInfo(
                id,
                int.Parse(folder.Name.AsSpan(4), CultureInfo.InvariantCulture),
                request is null ? null : (long?)Number(request["seed"]),
                audio && result is not null ? Text(result["quality"]) : null,
                audio && result is not null ? Number(result["audio_seconds"]) : null,
                audio,
                File.Exists(Path.Combine(folder.FullName, "score.abc")),
                File.Exists(Path.Combine(folder.FullName, "semantic.npy")),
                Size(folder),
                rated.TryGetValue(id, out var rating) ? rating : null,
                sung.GetValueOrDefault(id) ?? [],
                covered.TryGetValue(id, out var cover) ? cover : null,
                noted.GetValueOrDefault(id),
                filmed.GetValueOrDefault(id) ?? [],
                File.Exists(Path.Combine(folder.FullName, AceFile)) ? "ace" : null,
                heard.GetValueOrDefault(id)));
        }

        var original = title.Length > 0 ? title : TitleFromName(run.Name);
        return new RunInfo(run.Name, renamed ?? original, original, CreatedAt(run.Name), style, lyrics, songs, Size(run));
    }

    /// <summary>The folder's files, all levels down; a file the worker deletes meanwhile counts as nothing.</summary>
    internal static long Size(DirectoryInfo directory)
    {
        long bytes = 0;
        foreach (var file in directory.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
        {
            try
            {
                bytes += file.Length;
            }
            catch (IOException)
            {
            }
        }
        return bytes;
    }

    private static IEnumerable<DirectoryInfo> SongFolders(DirectoryInfo run) =>
        run.EnumerateDirectories()
            .Where(d => SongName().IsMatch(d.Name))
            .OrderBy(d => int.Parse(d.Name.AsSpan(4), CultureInfo.InvariantCulture));

    /// <summary>"20260921-165850-Neon-Night-Struggle" → "Neon Night Struggle" (the slug has lost umlauts and punctuation).</summary>
    private static string TitleFromName(string run) =>
        run.Length > 16 ? run[16..].Replace('-', ' ').Trim() : "";

    /// <summary>The worker names runs by its local time.</summary>
    internal static DateTimeOffset? CreatedAt(string run) =>
        DateTime.TryParseExact(run.AsSpan(0, 15), "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var created)
            ? new DateTimeOffset(created)
            : null;

    /// <summary>Null when missing or half written (the worker may be writing it right now).</summary>
    /// <summary>The song's length as its <c>result.json</c> says, null while it has none.</summary>
    public static double? SecondsOf(string songDirectory) => Number(ReadJson(Path.Combine(songDirectory, "result.json"))?["audio_seconds"]);

    private static JsonObject? ReadJson(string path)
    {
        try
        {
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            return null;
        }
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool? Flag(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

    private static double? Number(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;

    /// <summary>What <c>yue2_worker.py</c> names run folders: a timestamp, the title's slug, and "b"s against collisions.</summary>
    [GeneratedRegex(@"^\d{8}-\d{6}(-[A-Za-z0-9-]+)?b*$")]
    private static partial Regex RunName();

    [GeneratedRegex(@"^song\d{1,4}$")]
    private static partial Regex SongName();
}
