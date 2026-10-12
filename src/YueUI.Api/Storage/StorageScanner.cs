using Microsoft.Extensions.Options;
using YueUI.Api.AudioMidi;
using YueUI.Api.Data;
using YueUI.Api.Images;
using YueUI.Api.Library;
using YueUI.Api.Loras;
using YueUI.Api.Logs;
using YueUI.Api.Share;
using YueUI.Api.Speech;
using YueUI.Api.Video;
using YueUI.Api.Voices;
using YueUI.Api.Worker;

namespace YueUI.Api.Storage;

/// <summary>Where the disk space goes: what this app made, what it keeps for itself, and the models it runs.</summary>
/// <param name="Volumes">The volumes the categories lie on (an external SSD after <c>deploy/move-to-volume.sh</c>).</param>
/// <param name="Failed">What failed or cancelled versions, stems, swaps and videos still take; cleanup <c>failed</c>.</param>
public sealed record StorageOverview(IReadOnlyList<StorageVolume> Volumes, IReadOnlyList<StorageCategory> Categories, long Failed);

/// <param name="Mount">Where the volume is mounted: <c>/</c> for the Mac's own disk, <c>/Volumes/…</c> for another.</param>
public sealed record StorageVolume(string Mount, long Free, long Total);

/// <param name="Key">What it is (<see cref="StorageScanner"/> lists them), named by the frontend.</param>
/// <param name="Group"><c>work</c> (songs and what was made from them), <c>app</c> (kept by the app) or <c>models</c> (models and programs).</param>
/// <param name="Count">How many entries it has, where it has entries.</param>
/// <param name="Volume">The <see cref="StorageVolume.Mount"/> it lies on; null where nothing of it exists.</param>
/// <param name="Orphaned">Files without an entry in the database (left by a crash or an older version), older than a day.</param>
/// <param name="Largest">Its largest entries, each deletable through the endpoint its <see cref="StorageItem.Kind"/> names.</param>
public sealed record StorageCategory(
    string Key,
    string Group,
    long Bytes,
    int Count,
    string? Volume,
    long Orphaned,
    IReadOnlyList<StorageItem> Largest);

/// <param name="Kind">song, version, stems, swap, video, cover, image, transcription or take.</param>
/// <param name="Id">The entry's own id (for a song and a cover the song id <c>run/songN</c>).</param>
/// <param name="SongId">The song it belongs to, where it belongs to one.</param>
/// <param name="Detail">A word about it that is not translated: a voice, a format, a model, a file name.</param>
public sealed record StorageItem(
    string Kind,
    string Id,
    string? SongId,
    string Title,
    string? Detail,
    long Bytes,
    DateTimeOffset? CreatedAt);

/// <summary>What a cleanup removed.</summary>
public sealed record StorageCleanup(long Bytes, int Removed);

/// <summary>
/// Measures the folders this app and the engines it runs write to, and cleans up what can be made again.
/// </summary>
/// <remarks>
/// Measured like <c>du</c>: a part moved to another volume (a symbolic link at the old place) is followed, links
/// inside are not, so Hugging Face caches, whose snapshots link to their blobs, count once. Each call walks the
/// disk anew; with a few thousand song files and models of few large files that takes well under a second.
/// </remarks>
public sealed class StorageScanner(
    YuePaths paths,
    SqliteDatabase database,
    SongLibrary library,
    TranscriptionLibrary transcriptions,
    SqliteVersionStore versions,
    SqliteStemStore stems,
    SqliteSwapStore swaps,
    SqliteVideoStore videos,
    SqliteCoverStore covers,
    SqliteSpeechStore speech,
    ImageMaker images,
    LoraLibrary loras,
    StreamCopies streams,
    VoiceConverter converter,
    VideoMaker videoMaker,
    Status.StatusHub hub,
    IOptions<SpeechOptions> speechOptions,
    IOptions<ImageOptions> imageOptions,
    IOptions<VoiceOptions> voiceOptions,
    IOptions<MontageOptions> montageOptions,
    IOptions<AudioMidiOptions> midiOptions,
    IOptions<LogOptions> logOptions,
    TimeProvider time)
{
    /// <summary>How many entries a category lists; the page shows the first few and the rest on request.</summary>
    public const int ListedItems = 30;

    /// <summary>A file without its database row is left alone this long: it may be one being made right now.</summary>
    public static readonly TimeSpan OrphanAge = TimeSpan.FromDays(1);

    /// <summary>What <see cref="Cleanup"/> takes.</summary>
    public static readonly IReadOnlyList<string> CleanupTargets = ["stream", "images", "videos", "orphans", "failed"];

    private string Data => database.Directory;

    public StorageOverview Scan()
    {
        var runs = Safe(library.ListRuns) ?? [];
        var titles = runs.SelectMany(r => r.Songs.Select(s => (s.Id, Title: $"{r.Title} · {s.Index}"))).ToDictionary(p => p.Id, p => p.Title);
        string TitleOf(string songId) => titles.GetValueOrDefault(songId) ?? songId;

        var categories = new List<StorageCategory>();
        var mounts = Mounts();
        string? VolumeOf(params string[] roots) =>
            roots.Select(RealPath).FirstOrDefault(p => p is not null) is { } real ? MountOf(mounts, real) : null;
        void Add(string key, string group, long bytes, int count, string?[] roots, long orphaned = 0, IEnumerable<StorageItem>? items = null) =>
            categories.Add(new StorageCategory(key, group, bytes, count, VolumeOf([.. roots.OfType<string>()]), orphaned,
                [.. (items ?? []).OrderByDescending(i => i.Bytes).Take(ListedItems)]));

        // ---- What was made ----
        var songs = runs.SelectMany(r => r.Songs.Select(s => new StorageItem("song", s.Id, s.Id, $"{r.Title} · {s.Index}", null, s.Bytes, r.CreatedAt))).ToList();
        Add("songs", "work", Measure(paths.OutputDir, skip: [paths.TranscriptionsDir]).Bytes, songs.Count, [paths.OutputDir], items: songs);

        var transcribed = (Safe(() => transcriptions.List().Items) ?? [])
            .Select(t => new StorageItem("transcription", t.Id, null, t.SourceName, t.Task, t.Bytes, t.CreatedAt)).ToList();
        Add("transcriptions", "work", Measure(paths.TranscriptionsDir).Bytes, transcribed.Count, [paths.TranscriptionsDir], items: transcribed);

        var versionFolder = Path.Combine(Data, "versions");
        var sung = new List<StorageItem>();
        long versionBytes = 0, versionOrphans = 0;
        foreach (var file in Files(versionFolder, "*.flac"))
        {
            var id = Path.GetFileNameWithoutExtension(file.Name);
            versionBytes += file.Length;
            if (Safe(() => versions.Get(id)) is { } version)
            {
                sung.Add(new StorageItem("version", id, version.SongId, TitleOf(version.SongId), version.VoiceLabel, file.Length, version.CreatedAt));
            }
            else if (Old(file))
            {
                versionOrphans += file.Length;
            }
        }
        Add("versions", "work", versionBytes, sung.Count, [versionFolder], versionOrphans, sung);

        var (stemBytes, stemOrphans, separated) = Folders(Path.Combine(Data, "stems"), id => Safe(() => stems.Get(id)) is { } set
            ? new StorageItem("stems", id, set.SongId is { Length: > 0 } ? set.SongId : null,
                set.SongId is { Length: > 0 } ? TitleOf(set.SongId) : set.Title, set.Model, 0, set.CreatedAt)
            : null);
        Add("stems", "work", stemBytes, separated.Count, [Path.Combine(Data, "stems")], stemOrphans, separated);

        var (swapBytes, swapOrphans, swapped) = Folders(Path.Combine(Data, "swaps"), id => Safe(() => swaps.Get(id)) is { } swap
            ? new StorageItem("swap", id, null, swap.FileName, swap.VoiceLabel, 0, swap.CreatedAt)
            : null);
        Add("swaps", "work", swapBytes, swapped.Count, [Path.Combine(Data, "swaps")], swapOrphans, swapped);

        var (videoBytes, videoOrphans, filmed) = Folders(Path.Combine(Data, "videos"), id => Safe(() => videos.Get(id)) is { } video
            ? new StorageItem("video", id, video.SongId, video.Title, video.Format, 0, video.CreatedAt)
            : null);
        Add("videos", "work", videoBytes, filmed.Count, [Path.Combine(Data, "videos")], videoOrphans, filmed);

        var coverFiles = Safe(() => covers.All().Keys.Select(covers.Get).OfType<SongCover>().ToList()) ?? [];
        var coverPaths = coverFiles.Select(c => Path.GetFullPath(c.Path)).ToHashSet(StringComparer.Ordinal);
        long coverBytes = 0, coverOrphans = 0;
        foreach (var file in Files(covers.Folder, "*"))
        {
            coverBytes += file.Length;
            if (!coverPaths.Contains(file.FullName) && Old(file))
            {
                coverOrphans += file.Length;
            }
        }
        var covered = coverFiles.Select(c => new StorageItem("cover", c.SongId, c.SongId, TitleOf(c.SongId), null, Length(c.Path), c.UpdatedAt)).ToList();
        Add("covers", "work", coverBytes, covered.Count, [covers.Folder], coverOrphans, covered);

        // Candidates lie loose in images/, beside the painter's own environment and models when those are not moved.
        var painted = images.All()
            .Select(i => new StorageItem("image", i.Id, i.SongId, i.Title ?? TitleOf(i.SongId), i.ModelLabel, Length(images.ImagePath(i.Id)) + Length(Path.Combine(images.Folder, $"{i.Id}.json")), i.CreatedAt))
            .ToList();
        Add("images", "work", Files(images.Folder, "*").Sum(f => f.Length), painted.Count, [images.Folder], items: painted);

        var takes = (Safe(() => speech.Takes(int.MaxValue)) ?? [])
            .Select(t => new StorageItem("take", t.Id, null, t.Text.Length > 60 ? t.Text[..60] + "…" : t.Text, t.ModelLabel, Length(speech.TakePath(t.Id)), t.CreatedAt))
            .ToList();
        var speechData = Path.Combine(Data, "speech");
        Add("speech", "work", Measure(Path.Combine(speechData, "takes")).Bytes + Measure(Path.Combine(speechData, "voices")).Bytes, takes.Count,
            [Path.Combine(speechData, "takes")], items: takes);

        var voices = Measure(Path.Combine(Data, "voices"));
        Add("voices", "work", voices.Bytes, voices.Files, [Path.Combine(Data, "voices")]);
        var lora = Measure(loras.Folder);
        Add("loras", "work", lora.Bytes, Files(loras.Folder, "*" + LoraLibrary.Extension).Count(), [loras.Folder]);

        // ---- Kept by the app ----
        var streamFolder = Path.Combine(Data, "stream");
        var copies = Measure(streamFolder);
        var versionCopies = Files(versionFolder, "*").Where(f => !f.Name.EndsWith(".flac", StringComparison.Ordinal)).ToList();
        Add("stream", "app", copies.Bytes + versionCopies.Sum(f => f.Length), copies.Files + versionCopies.Count, [streamFolder]);
        var logs = Measure(logOptions.Value.ResolvedDirectory);
        Add("logs", "app", logs.Bytes, logs.Files, [logOptions.Value.ResolvedDirectory]);
        var own = Files(Data, "*").ToList();
        Add("database", "app", own.Sum(f => f.Length), 0, [Data]);

        // ---- Models and programs ----
        var speechRoot = speechOptions.Value;
        var imageRoot = imageOptions.Value;
        var montage = montageOptions.Value;
        var voice = voiceOptions.Value;
        var lmStudio = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lmstudio", "models");
        AddModel("yue2", paths.ModelCache);
        AddModel("ane", Path.Combine(paths.InstallRoot, "ane-cache"));
        AddModel("lmstudio", lmStudio);
        AddModel("speechModels", speechRoot.ResolvedModelCache);
        AddModel("imageModels", imageRoot.ResolvedModelCache);
        AddModel("separatorModels", voice.ResolvedSeparatorModels);
        AddModel("seedvcModels", voice.ResolvedSeedVcModels);
        AddModel("montageModels", montage.ResolvedModelCache);

        // The Python environments and sources: YuE Studio's installation without its models, the voice engines
        // without theirs, and each tool's env.
        var modelPaths = new[]
        {
            paths.ModelCache, Path.Combine(paths.InstallRoot, "ane-cache"), voice.ResolvedSeparatorModels, voice.ResolvedSeedVcModels,
        };
        var programs = new[]
        {
            Measure(paths.InstallRoot, skip: modelPaths),
            Measure(voice.ResolvedEngineRoot, skip: modelPaths),
            Measure(Path.Combine(speechRoot.ResolvedRoot, "env")),
            Measure(Path.Combine(imageRoot.ResolvedRoot, "env")),
            Measure(Path.Combine(montage.ResolvedRoot, "env")),
            Measure(midiOptions.Value.ResolvedRoot),
        };
        Add("programs", "models", programs.Sum(p => p.Bytes), 0, [paths.InstallRoot]);

        void AddModel(string key, string path)
        {
            var size = Measure(path);
            Add(key, "models", size.Bytes, 0, [path]);
        }

        var used = categories.Select(c => c.Volume).OfType<string>().Append(MountOf(mounts, RealPath(Data) ?? Data)).ToHashSet();
        return new StorageOverview(
            [.. mounts.Where(m => used.Contains(m.Mount)).OrderBy(m => m.Mount, StringComparer.Ordinal)],
            [.. categories],
            FailedWork().Sum(f => f.Bytes));
    }

    /// <summary>Removes what can be made again or what nothing refers to any more.</summary>
    /// <param name="target">One of <see cref="CleanupTargets"/>.</param>
    public StorageCleanup Cleanup(string target)
    {
        switch (target)
        {
            case "stream":
            {
                var (bytes, files) = streams.ForgetAll();
                return new StorageCleanup(bytes, files);
            }
            case "images":
            {
                var done = images.All().Where(i => i.Finished).ToList();
                var bytes = done.Sum(i => Length(images.ImagePath(i.Id)) + Length(Path.Combine(images.Folder, $"{i.Id}.json")));
                foreach (var image in done)
                {
                    images.Delete(image);
                }
                return new StorageCleanup(bytes, done.Count);
            }
            case "videos":
            {
                long bytes = 0;
                var removed = 0;
                foreach (var folder in Directories(Path.Combine(Data, "videos")))
                {
                    if (videos.Get(folder.Name) is { Finished: true } video)
                    {
                        bytes += Measure(folder.FullName).Bytes;
                        videoMaker.Delete(video);
                        removed++;
                    }
                }
                return new StorageCleanup(bytes, removed);
            }
            case "orphans":
                return RemoveOrphans();
            case "failed":
            {
                var failed = FailedWork();
                foreach (var work in failed)
                {
                    work.Delete();
                }
                return new StorageCleanup(failed.Sum(f => f.Bytes), failed.Count);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(target), target, null);
        }
    }

    /// <summary>Versions, stems, swaps and videos that failed or were cancelled but still have files.</summary>
    private List<(long Bytes, Action Delete)> FailedWork()
    {
        static bool Failed(string stage) => stage is "failed" or "cancelled";
        var work = new List<(long Bytes, Action Delete)>();
        foreach (var version in (Safe(versions.All) ?? []).Where(v => Failed(v.Stage)))
        {
            if (Length(versions.FilePath(version.Id)) is > 0 and var bytes)
            {
                work.Add((bytes, () => converter.Delete(version)));
            }
        }
        foreach (var set in (Safe(stems.All) ?? []).Where(s => Failed(s.Stage)))
        {
            if (Measure(stems.Folder(set.Id)).Bytes is > 0 and var bytes)
            {
                work.Add((bytes, () => converter.DeleteStems(set)));
            }
        }
        foreach (var swap in (Safe(swaps.All) ?? []).Where(s => Failed(s.Stage)))
        {
            if (Measure(swaps.Folder(swap.Id)).Bytes is > 0 and var bytes)
            {
                work.Add((bytes, () => converter.DeleteSwap(swap)));
            }
        }
        foreach (var folder in Directories(Path.Combine(Data, "videos")))
        {
            if (Safe(() => videos.Get(folder.Name)) is { } video && Failed(video.Stage) && Measure(folder.FullName).Bytes is > 0 and var bytes)
            {
                work.Add((bytes, () => videoMaker.Delete(video)));
            }
        }
        return work;
    }

    private StorageCleanup RemoveOrphans()
    {
        long bytes = 0;
        var removed = 0;
        void Remove(FileSystemInfo entry, long size)
        {
            try
            {
                if (entry is DirectoryInfo directory)
                {
                    directory.Delete(recursive: true);
                }
                else
                {
                    entry.Delete();
                }
                bytes += size;
                removed++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        foreach (var (folder, known) in new (string, Func<string, bool>)[]
        {
            ("stems", id => stems.Get(id) is not null),
            ("swaps", id => swaps.Get(id) is not null),
            ("videos", id => videos.Get(id) is not null),
        })
        {
            foreach (var directory in Directories(Path.Combine(Data, folder)).Where(d => !known(d.Name) && Old(d)))
            {
                Remove(directory, Measure(directory.FullName).Bytes);
            }
        }
        foreach (var file in Files(Path.Combine(Data, "versions"), "*.flac").Where(f => versions.Get(Path.GetFileNameWithoutExtension(f.Name)) is null && Old(f)))
        {
            Remove(file, file.Length);
        }
        var coverPaths = covers.All().Keys.Select(covers.Get).OfType<SongCover>().Select(c => Path.GetFullPath(c.Path)).ToHashSet(StringComparer.Ordinal);
        foreach (var file in Files(covers.Folder, "*").Where(f => !coverPaths.Contains(f.FullName) && Old(f)))
        {
            Remove(file, file.Length);
        }
        if (removed > 0)
        {
            hub.LibraryChanged();
        }
        return new StorageCleanup(bytes, removed);
    }

    /// <summary>Folders named by their entry's id: the size of each, and of those without an entry.</summary>
    private (long Bytes, long Orphaned, List<StorageItem> Items) Folders(string root, Func<string, StorageItem?> entry)
    {
        long bytes = 0, orphaned = 0;
        var items = new List<StorageItem>();
        foreach (var directory in Directories(root))
        {
            var size = Measure(directory.FullName).Bytes;
            bytes += size;
            if (entry(directory.Name) is { } item)
            {
                items.Add(item with { Bytes = size });
            }
            else if (Old(directory))
            {
                orphaned += size;
            }
        }
        return (bytes, orphaned, items);
    }

    private bool Old(FileSystemInfo entry) => time.GetUtcNow() - entry.LastWriteTimeUtc > OrphanAge;

    private static long Length(string path)
    {
        try
        {
            return new FileInfo(path) is { Exists: true } file ? file.Length : 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static IEnumerable<FileInfo> Files(string folder, string pattern) =>
        Directory.Exists(folder)
            ? new DirectoryInfo(folder).EnumerateFiles(pattern, new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }).Where(f => f.LinkTarget is null)
            : [];

    private static IEnumerable<DirectoryInfo> Directories(string folder) =>
        Directory.Exists(folder)
            ? new DirectoryInfo(folder).EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }).Where(d => d.LinkTarget is null)
            : [];

    /// <summary>A folder's size in bytes and files, following the folder itself if it is a link but no link inside.</summary>
    /// <param name="skip">Folders inside that count elsewhere.</param>
    public static (long Bytes, int Files) Measure(string path, IReadOnlyCollection<string>? skip = null)
    {
        var skipped = (skip ?? []).Select(s => Path.TrimEndingDirectorySeparator(Path.GetFullPath(s))).ToHashSet(StringComparer.Ordinal);
        if (!Directory.Exists(path))
        {
            return (0, 0);
        }
        long bytes = 0;
        var files = 0;
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
        var pending = new Stack<DirectoryInfo>([new DirectoryInfo(path)]);
        while (pending.TryPop(out var directory))
        {
            try
            {
                foreach (var entry in directory.EnumerateFileSystemInfos("*", options))
                {
                    if (entry.LinkTarget is not null)
                    {
                        continue;
                    }
                    if (entry is DirectoryInfo inner)
                    {
                        if (!skipped.Contains(Path.TrimEndingDirectorySeparator(inner.FullName)))
                        {
                            pending.Push(inner);
                        }
                    }
                    else if (entry is FileInfo file)
                    {
                        bytes += file.Length;
                        files++;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
        return (bytes, files);
    }

    /// <summary>The path with every link along it resolved, like <c>realpath</c>; null when it does not exist.</summary>
    public static string? RealPath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var current = Path.GetPathRoot(full) ?? "/";
            foreach (var part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                var next = Path.Combine(current, part);
                var info = new DirectoryInfo(next);
                if (!info.Exists && !File.Exists(next))
                {
                    return null;
                }
                current = info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target
                    ? RealPath(target.FullName) ?? target.FullName
                    : next;
            }
            return current;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The mounted volumes with a size; pseudo file systems have none.</summary>
    private static List<StorageVolume> Mounts()
    {
        var volumes = new List<StorageVolume>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.IsReady && drive.TotalSize > 0)
                    {
                        volumes.Add(new StorageVolume(drive.Name, drive.AvailableFreeSpace, drive.TotalSize));
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
        return volumes;
    }

    /// <summary>The volume whose mount point is the longest prefix of the path.</summary>
    private static string? MountOf(List<StorageVolume> mounts, string path) =>
        mounts
            .Where(m => m.Mount == "/" || path == m.Mount || path.StartsWith(Path.TrimEndingDirectorySeparator(m.Mount) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .MaxBy(m => m.Mount.Length)?.Mount;

    /// <summary>The database may be unreadable; the sizes are still worth showing.</summary>
    private static T? Safe<T>(Func<T?> read) where T : class
    {
        try
        {
            return read();
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }
}
