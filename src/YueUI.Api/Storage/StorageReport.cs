using Microsoft.Extensions.Options;
using YueUI.Api.Data;
using YueUI.Api.Images;
using YueUI.Api.Library;
using YueUI.Api.Logs;
using YueUI.Api.Speech;
using YueUI.Api.Video;
using YueUI.Api.Voices;
using YueUI.Api.Worker;

namespace YueUI.Api.Storage;

/// <param name="Path">Where the volume is mounted, e.g. <c>/</c> or <c>/Volumes/SSD</c>.</param>
public sealed record StorageVolume(string Path, long FreeBytes, long TotalBytes);

/// <param name="Id">What the page names it by, e.g. <c>songs</c>, <c>videos</c>, <c>yue2</c>.</param>
/// <param name="Group"><c>library</c> (what Tonwerk made, deletable piece by piece), <c>models</c> (models and Python
/// environments, which only the install scripts change) or <c>app</c> (database, log, streaming copies).</param>
/// <param name="Count">How many pieces the page can list (songs, videos, …); null where that means nothing.</param>
/// <param name="Path">Where it lies, after following a link left by <c>deploy/move-to-volume.sh</c>.</param>
/// <param name="Volume">The mount point of <paramref name="Path"/>, one of <see cref="StorageUsage.Volumes"/>.</param>
public sealed record StorageCategory(string Id, string Group, long Bytes, int? Count, string Path, string Volume);

/// <param name="Kind"><c>song</c>, <c>version</c>, <c>stems</c>, <c>video</c>, <c>swap</c>, <c>cover</c>, <c>image</c>
/// or <c>transcription</c>; each is deleted through its own endpoint.</param>
/// <param name="Id">The id that endpoint takes; a song's and a cover's is the song id.</param>
/// <param name="SongId"><c>run/songN</c> it belongs to, null for uploads and transcriptions.</param>
/// <param name="Detail">What tells it apart from its siblings: the voice, the stem model, the video's format, ….</param>
/// <param name="Bytes">What deleting it frees; for a song everything that goes with it (versions, stems, videos, …).</param>
public sealed record StorageItem(
    string Kind,
    string Id,
    string? SongId,
    string Title,
    string? Detail,
    long Bytes,
    DateTimeOffset? CreatedAt,
    int? Rating = null);

/// <summary>Files no row points to any more: left by a delete that was cut off, a crash or an older version.</summary>
public sealed record StorageLeftovers(int Count, long Bytes);

/// <param name="Items">The largest pieces of each kind, largest first.</param>
public sealed record StorageUsage(
    string DataFolder,
    IReadOnlyList<StorageVolume> Volumes,
    IReadOnlyList<StorageCategory> Categories,
    IReadOnlyList<StorageItem> Items,
    StorageLeftovers Leftovers);

/// <summary>
/// Where the disk went: the songs, everything made from them (versions, stems, videos, covers), the models and the
/// app's own files, each with its size, and the largest pieces to delete. Measured on every call; a few thousand files,
/// most of them in the song folders, take well under a second.
/// </summary>
/// <remarks>
/// Links are not followed below a category's own folder: Hugging Face caches link every snapshot file to its blob,
/// which would count each model twice. The folder itself may be a link, which <c>deploy/move-to-volume.sh</c> leaves
/// when it moves a folder to an external volume; that one is followed, and its volume reported.
/// </remarks>
public sealed class StorageReport(
    YuePaths paths,
    SqliteDatabase database,
    SongLibrary library,
    TranscriptionLibrary transcriptions,
    SqliteVersionStore versions,
    SqliteStemStore stems,
    SqliteSwapStore swaps,
    SqliteVideoStore videos,
    SqliteCoverStore covers,
    ImageMaker images,
    IOptions<VoiceOptions> voiceOptions,
    IOptions<SpeechOptions> speechOptions,
    IOptions<ImageOptions> imageOptions,
    IOptions<MontageOptions> montageOptions,
    IOptions<AudioMidi.AudioMidiOptions> midiOptions,
    IOptions<LogOptions> logOptions,
    TimeProvider time)
{
    /// <summary>How many pieces of each kind the page lists; the library is there for the rest.</summary>
    public const int ItemsPerKind = 30;

    /// <summary>
    /// A file younger than this may belong to something whose row is not written yet (a video, a cover being set), so it
    /// never counts as left over.
    /// </summary>
    public static readonly TimeSpan LeftoverAge = TimeSpan.FromHours(1);

    private static readonly EnumerationOptions Below = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        // Dot files count too (Hugging Face's .locks, .git of an engine's checkout); links do not, see the remarks.
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    private string Data => database.Directory;

    public StorageUsage Measure()
    {
        var runs = library.ListRuns();
        var allVersions = Read(versions.All) ?? [];
        var allStems = Read(stems.All) ?? [];
        var allSwaps = Read(swaps.All) ?? [];
        var allCovers = Read(() => covers.All().Keys.Select(covers.Get).OfType<SongCover>().ToList()) ?? [];
        var allImages = ImageFiles().Select(id => images.Get(id)).OfType<ImageState>().ToList();
        var allVideos = Folders(Path.Combine(Data, "videos")).Select(d => Read(() => videos.Get(d.Name))).OfType<VideoState>().ToList();
        var allTranscriptions = transcriptions.List().Items;

        var categories = new List<StorageCategory>();
        var claimed = new List<string>();
        void Measured(string id, string group, int? count, long bytes, string[] places)
        {
            claimed.AddRange(places.Select(Resolve));
            if (places.Length > 0 && (bytes > 0 || count > 0))
            {
                var path = Resolve(places[0]);
                categories.Add(new StorageCategory(id, group, bytes, count, path, MountOf(path)));
            }
        }
        void Add(string id, string group, int? count, params string[] places) =>
            Measured(id, group, count, places.Sum(Size), places);

        // The output folder also holds the transcriptions, a category of their own; the library has measured the runs.
        Measured("songs", "library", runs.Sum(r => r.Songs.Count), runs.Sum(r => r.Bytes),
            [.. runs.Select(r => Path.Combine(paths.OutputDir, r.Id))]);
        Add("versions", "library", allVersions.Count, VersionFiles(".flac"));
        Add("stems", "library", allStems.Count, Path.Combine(Data, "stems"));
        Add("videos", "library", allVideos.Count, Path.Combine(Data, "videos"));
        Add("covers", "library", allCovers.Count, covers.Folder);
        Add("images", "library", allImages.Count, [.. ImageFiles().SelectMany(id => new[] { images.ImagePath(id), Path.Combine(images.Folder, $"{id}.json") })]);
        Add("swaps", "library", allSwaps.Count, Path.Combine(Data, "swaps"));
        Add("transcriptions", "library", allTranscriptions.Count, paths.TranscriptionsDir);
        Add("speech", "library", null, Path.Combine(Data, "speech", "takes"), Path.Combine(Data, "speech", "voices"));
        Add("voices", "library", null, Path.Combine(Data, "voices"));

        Add("stream", "app", null, [Path.Combine(Data, "stream"), .. VersionFiles(".m4a"), .. VersionFiles(".m4a.tags")]);
        Add("database", "app", null, [.. Files(Data, "yueui.db*"), Path.Combine(Data, "push.json"), Path.Combine(Data, "backup.json")]);
        Add("logs", "app", null, logOptions.Value.ResolvedDirectory);

        var voice = voiceOptions.Value;
        var speech = speechOptions.Value;
        var image = imageOptions.Value;
        var montage = montageOptions.Value;
        Add("yue2", "models", null, paths.ModelCache);
        Add("aneCache", "models", null, Path.Combine(paths.InstallRoot, "ane-cache"));
        Add("yueEngine", "models", null, Path.Combine(paths.InstallRoot, "env"), paths.SourceRoot, Path.Combine(paths.InstallRoot, "sheetsage-env"));
        Add("loras", "models", null, Path.Combine(Data, "loras"));
        Add("lmstudio", "models", null, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lmstudio", "models"));
        Add("separator", "models", null, Path.Combine(voice.ResolvedEngineRoot, "separator"));
        Add("seedvc", "models", null, Path.Combine(voice.ResolvedEngineRoot, "seed-vc"));
        Add("speechModels", "models", null, speech.ResolvedModelCache, Path.Combine(speech.ResolvedRoot, "env"));
        Add("imageModels", "models", null, image.ResolvedModelCache, Path.Combine(image.ResolvedRoot, "env"));
        Add("montage", "models", null, montage.ResolvedModelCache, Path.Combine(montage.ResolvedRoot, "env"));
        Add("midi", "models", null, Path.Combine(midiOptions.Value.ResolvedRoot, "env"));

        var items = new List<StorageItem>();
        items.AddRange(SongItems(runs, allVersions, allStems, allVideos, allCovers, allImages));
        items.AddRange(Largest(allVersions.Select(v => new StorageItem(
            "version", v.Id, v.SongId, TitleOf(runs, v.SongId, v.Title), v.VoiceLabel,
            Size(versions.FilePath(v.Id)) + Size(versions.StreamPath(v.Id)), v.CreatedAt))));
        items.AddRange(Largest(allStems.Select(s => new StorageItem(
            "stems", s.Id, s.SongId.Length > 0 ? s.SongId : null, s.SongId.Length > 0 ? TitleOf(runs, s.SongId, s.Title) : s.Title, s.Model,
            Size(stems.Folder(s.Id)), s.CreatedAt))));
        items.AddRange(Largest(allVideos.Select(v => new StorageItem(
            "video", v.Id, v.SongId, TitleOf(runs, v.SongId, v.Title), v.Format,
            Size(videos.Folder(v.Id)), v.CreatedAt))));
        items.AddRange(Largest(allSwaps.Select(s => new StorageItem(
            "swap", s.Id, null, s.FileName, s.VoiceLabel, Size(swaps.Folder(s.Id)), s.CreatedAt))));
        items.AddRange(Largest(allCovers.Select(c => new StorageItem(
            "cover", c.SongId, c.SongId, TitleOf(runs, c.SongId, ""), null, Size(c.Path), c.UpdatedAt))));
        items.AddRange(Largest(allImages.Select(i => new StorageItem(
            "image", i.Id, i.SongId, TitleOf(runs, i.SongId, i.Title ?? ""), i.ModelLabel,
            Size(images.ImagePath(i.Id)) + Size(Path.Combine(images.Folder, $"{i.Id}.json")), i.CreatedAt))));
        items.AddRange(Largest(allTranscriptions.Select(t => new StorageItem(
            "transcription", t.Id, null, t.SourceName, null, t.Bytes, t.CreatedAt))));

        // Whatever else lies in the data folder: what an older version left, a tool's own files, …. A folder holding a
        // category (engines/, speech/, the songs in a test) is that category's, the rest of it is too small to matter.
        var other = Directory.Exists(Data)
            ? new DirectoryInfo(Data).EnumerateFileSystemInfos()
                .Where(entry => !Holds(claimed, Resolve(entry.FullName)) && !entry.Name.StartsWith("yueui.db", StringComparison.Ordinal))
                .Sum(entry => Size(entry.FullName))
            : 0;
        if (other > 0)
        {
            categories.Add(new StorageCategory("other", "app", other, null, Data, MountOf(Data)));
        }

        var leftovers = Leftovers(allVersions, allStems, allSwaps, allCovers);
        var volumes = categories.Select(c => c.Volume).Append(MountOf(Resolve(Data))).Distinct(StringComparer.Ordinal)
            .Select(Volume).OfType<StorageVolume>().ToList();
        return new StorageUsage(
            Data,
            volumes,
            [.. categories.OrderByDescending(c => c.Bytes)],
            items,
            new StorageLeftovers(leftovers.Count, leftovers.Sum(l => l.Bytes)));
    }

    /// <summary>Deletes what <see cref="Measure"/> counts as left over.</summary>
    public StorageLeftovers RemoveLeftovers()
    {
        var leftovers = Leftovers(
            Read(versions.All) ?? [],
            Read(stems.All) ?? [],
            Read(swaps.All) ?? [],
            Read(() => covers.All().Keys.Select(covers.Get).OfType<SongCover>().ToList()) ?? []);
        var removed = 0;
        long bytes = 0;
        foreach (var leftover in leftovers)
        {
            try
            {
                if (Directory.Exists(leftover.Path))
                {
                    Directory.Delete(leftover.Path, recursive: true);
                }
                else
                {
                    File.Delete(leftover.Path);
                }
                removed++;
                bytes += leftover.Bytes;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
        return new StorageLeftovers(removed, bytes);
    }

    private sealed record Leftover(string Path, long Bytes);

    /// <summary>
    /// Folders and files in Tonwerk's own folders that no row names: a version's FLAC, a stem set, a video, a swap, a
    /// cover file, the streaming copy of a song that is gone. Only the database decides, and only once they are older
    /// than <see cref="LeftoverAge"/>.
    /// </summary>
    private List<Leftover> Leftovers(
        IReadOnlyList<VersionState> allVersions,
        IReadOnlyList<StemSetState> allStems,
        IReadOnlyList<SwapState> allSwaps,
        IReadOnlyList<SongCover> allCovers)
    {
        var found = new List<Leftover>();
        // Unreadable database: every file would look left over, so none is.
        if (!Readable())
        {
            return found;
        }
        var before = time.GetUtcNow() - LeftoverAge;
        void Check(FileSystemInfo entry, bool known)
        {
            if (!known && Newest(entry) < before)
            {
                found.Add(new Leftover(entry.FullName, Size(entry.FullName)));
            }
        }

        var versionIds = allVersions.Select(v => v.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var file in FilesIn(Path.Combine(Data, "versions")))
        {
            Check(file, versionIds.Contains(file.Name.Split('.')[0]));
        }
        var stemIds = allStems.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var folder in Folders(Path.Combine(Data, "stems")))
        {
            Check(folder, stemIds.Contains(folder.Name));
        }
        var swapIds = allSwaps.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var folder in Folders(Path.Combine(Data, "swaps")))
        {
            Check(folder, swapIds.Contains(folder.Name));
        }
        foreach (var folder in Folders(Path.Combine(Data, "videos")))
        {
            Check(folder, Read(() => videos.Get(folder.Name)) is not null);
        }
        var coverFiles = allCovers.Select(c => Path.GetFileName(c.Path)).ToHashSet(StringComparer.Ordinal);
        foreach (var file in FilesIn(covers.Folder))
        {
            Check(file, coverFiles.Contains(file.Name));
        }
        foreach (var run in Folders(Path.Combine(Data, "stream")))
        {
            if (!Directory.Exists(Path.Combine(paths.OutputDir, run.Name)))
            {
                Check(run, known: false);
                continue;
            }
            foreach (var copy in FilesIn(run.FullName))
            {
                Check(copy, Directory.Exists(Path.Combine(paths.OutputDir, run.Name, copy.Name.Split('.')[0])));
            }
        }
        return found;
    }

    /// <summary>Every song with what goes with it, since deleting the song deletes all of it.</summary>
    private IEnumerable<StorageItem> SongItems(
        IReadOnlyList<RunInfo> runs,
        IReadOnlyList<VersionState> allVersions,
        IReadOnlyList<StemSetState> allStems,
        IReadOnlyList<VideoState> allVideos,
        IReadOnlyList<SongCover> allCovers,
        IReadOnlyList<ImageState> allImages)
    {
        var attached = new Dictionary<string, long>(StringComparer.Ordinal);
        void Attach(string songId, long bytes) => attached[songId] = attached.GetValueOrDefault(songId) + bytes;
        foreach (var version in allVersions)
        {
            Attach(version.SongId, Size(versions.FilePath(version.Id)) + Size(versions.StreamPath(version.Id)));
        }
        foreach (var set in allStems.Where(s => s.SongId.Length > 0))
        {
            Attach(set.SongId, Size(stems.Folder(set.Id)));
        }
        foreach (var video in allVideos)
        {
            Attach(video.SongId, Size(videos.Folder(video.Id)));
        }
        foreach (var cover in allCovers)
        {
            Attach(cover.SongId, Size(cover.Path));
        }
        foreach (var image in allImages)
        {
            Attach(image.SongId, Size(images.ImagePath(image.Id)));
        }
        return Largest(runs.SelectMany(run => run.Songs.Select(song =>
        {
            var stream = Path.Combine(Data, "stream", run.Id, $"song{song.Index}.m4a");
            return new StorageItem(
                "song", song.Id, song.Id, run.Title, $"song{song.Index}",
                song.Bytes + attached.GetValueOrDefault(song.Id) + Size(stream),
                run.CreatedAt,
                song.Rating);
        })));
    }

    private static IEnumerable<StorageItem> Largest(IEnumerable<StorageItem> items) =>
        items.OrderByDescending(i => i.Bytes).Take(ItemsPerKind);

    /// <summary>The run's title as the library shows it, else what the item remembered.</summary>
    private static string TitleOf(IReadOnlyList<RunInfo> runs, string songId, string fallback)
    {
        var run = songId.Split('/')[0];
        return runs.FirstOrDefault(r => r.Id == run)?.Title is { Length: > 0 } title ? title : fallback;
    }

    /// <summary>The entry is one of the places measured, or holds one.</summary>
    private static bool Holds(IEnumerable<string> places, string entry) =>
        places.Any(place => place == entry || place.StartsWith(entry + Path.DirectorySeparatorChar, StringComparison.Ordinal));

    private IEnumerable<string> ImageFiles() =>
        FilesIn(images.Folder).Where(f => f.Extension == ".json").Select(f => Path.GetFileNameWithoutExtension(f.Name));

    private string[] VersionFiles(string ending) =>
        [.. FilesIn(Path.Combine(Data, "versions")).Where(f => f.Name.EndsWith(ending, StringComparison.Ordinal)
            && (ending != ".m4a" || !f.Name.EndsWith(".m4a.tags", StringComparison.Ordinal))).Select(f => f.FullName)];

    private static string[] Files(string folder, string pattern) =>
        Directory.Exists(folder) ? Directory.GetFiles(folder, pattern) : [];

    private static IEnumerable<FileInfo> FilesIn(string folder) =>
        Directory.Exists(folder) ? new DirectoryInfo(folder).EnumerateFiles() : [];

    private static IEnumerable<DirectoryInfo> Folders(string folder) =>
        Directory.Exists(folder) ? new DirectoryInfo(folder).EnumerateDirectories() : [];

    /// <summary>A file's size, or everything below a folder; nothing for what does not exist.</summary>
    internal static long Size(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return new FileInfo(path).Length;
            }
            if (!Directory.Exists(path))
            {
                return 0;
            }
            long bytes = 0;
            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", Below))
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>When anything in it was last written; a folder something still writes into is new.</summary>
    private static DateTimeOffset Newest(FileSystemInfo entry)
    {
        var newest = entry.LastWriteTimeUtc;
        if (entry is DirectoryInfo directory)
        {
            foreach (var file in directory.EnumerateFileSystemInfos("*", Below))
            {
                if (file.LastWriteTimeUtc > newest)
                {
                    newest = file.LastWriteTimeUtc;
                }
            }
        }
        return new DateTimeOffset(newest, TimeSpan.Zero);
    }

    /// <summary>Where a folder moved to an external volume really lies.</summary>
    private static string Resolve(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Path.GetFullPath(path);
        }
    }

    /// <summary>The longest mount point the path lies below; <c>/</c> when none is readable.</summary>
    private static string MountOf(string path)
    {
        try
        {
            return DriveInfo.GetDrives()
                .Select(d => d.RootDirectory.FullName)
                .Where(root => path.StartsWith(root.EndsWith('/') ? root : root + "/", StringComparison.Ordinal) || path == root)
                .OrderByDescending(root => root.Length)
                .FirstOrDefault() ?? Path.GetPathRoot(path) ?? "/";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Path.GetPathRoot(path) ?? "/";
        }
    }

    private static StorageVolume? Volume(string mount)
    {
        try
        {
            var drive = new DriveInfo(mount);
            return new StorageVolume(mount, drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private bool Readable() => Read(() => versions.All()) is not null;

    /// <summary>The lists that come from the database are empty, never an error, when it cannot be read.</summary>
    private static T? Read<T>(Func<T?> read) where T : class
    {
        try
        {
            return read();
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            return null;
        }
    }
}
