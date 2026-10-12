using Microsoft.Extensions.Options;
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

/// <summary>What the disk holds, by part, for the storage page.</summary>
/// <param name="Volumes">Every volume a part lies on, with its free space; more than one once a part was moved.</param>
/// <param name="Suggestions">What can most likely go, each with the entries to delete.</param>
public sealed record StorageReport(
    IReadOnlyList<StorageVolume> Volumes,
    IReadOnlyList<StorageGroup> Groups,
    IReadOnlyList<StorageSuggestion> Suggestions);

/// <param name="Name">Where the volume is mounted (<c>/</c>, <c>/Volumes/SSD</c>).</param>
public sealed record StorageVolume(string Name, long FreeBytes, long TotalBytes);

/// <summary>One part: the songs, the videos, a model cache, ….</summary>
/// <param name="Key">Names the part in <c>i18n.ts</c> (<see cref="StorageGroups"/>).</param>
/// <param name="Section"><c>data</c> for what was made here, <c>models</c> for what was installed.</param>
/// <param name="Count">How many entries the part has; null for a folder that is not made of entries.</param>
/// <param name="Volume">The <see cref="StorageVolume.Name"/> it lies on.</param>
/// <param name="Moved">Where the folder really lies, when <c>deploy/move-to-volume.sh</c> left a link in its place.</param>
/// <param name="Missing">The link points nowhere: the volume it moved to is not mounted.</param>
/// <param name="Largest">The biggest entries, biggest first, at most <see cref="StorageSurvey.Largest"/>.</param>
public sealed record StorageGroup(
    string Key,
    string Section,
    long Bytes,
    int? Count,
    string? Volume,
    string? Moved,
    bool Missing,
    IReadOnlyList<StorageItem> Largest);

/// <summary>Something the page can delete, through the endpoint its <see cref="Kind"/> names.</summary>
/// <param name="Kind">song, version, stems, video, cover, image, swap, take, transcription or lora.</param>
/// <param name="Id">What its delete endpoint takes: the song's <c>run/songN</c> for songs and covers.</param>
/// <param name="SongId">The song it belongs to, when it belongs to one.</param>
/// <param name="Detail">What tells it apart from its song's others: the voice, the format, the model.</param>
/// <param name="Bytes">What deleting it frees; for a song that includes everything deleted with it.</param>
/// <param name="Stage">Only for what failed or was cancelled.</param>
public sealed record StorageItem(
    string Kind,
    string Id,
    string? SongId,
    string Title,
    string? Detail,
    long Bytes,
    DateTimeOffset? CreatedAt,
    int? Rating = null,
    string? Stage = null);

/// <param name="Key"><c>paintedCovers</c>, <c>failed</c> or <c>oneStar</c>.</param>
public sealed record StorageSuggestion(string Key, long Bytes, IReadOnlyList<StorageItem> Items);

/// <summary>The parts, in the order the page lists them.</summary>
public static class StorageGroups
{
    public const string Data = "data";
    public const string Models = "models";
}

/// <summary>
/// Measures the parts <c>deploy/move-to-volume.sh</c> knows and what else this app keeps next to the database, and
/// lists each part's biggest entries with what deleting them frees. Nothing here deletes: the page goes through the
/// endpoints each part already has, so a song still cannot be deleted while the worker sings it.
/// </summary>
/// <remarks>
/// Walking the songs and the model caches takes a moment on a full disk (a few thousand files), so it runs on request
/// only, off the request thread. Sizes are the files' lengths, not the blocks they take.
/// </remarks>
public sealed class StorageSurvey(
    YuePaths paths,
    SongLibrary library,
    TranscriptionLibrary transcriptions,
    SqliteDatabase database,
    SqliteVersionStore versions,
    SqliteStemStore stems,
    SqliteVideoStore videos,
    SqliteCoverStore covers,
    SqliteSwapStore swaps,
    SqliteSpeechStore speech,
    ImageMaker images,
    LoraLibrary loras,
    StreamCopies streams,
    IOptions<SpeechOptions> speechOptions,
    IOptions<ImageOptions> imageOptions,
    IOptions<VoiceOptions> voiceOptions,
    IOptions<MontageOptions> montageOptions,
    IOptions<LogOptions> logOptions)
{
    /// <summary>Enough to find what is worth deleting on a phone's screen; the library lists the rest.</summary>
    public const int Largest = 8;

    /// <summary>A song this many stars is the first candidate to go.</summary>
    private const int LowRating = 1;

    public StorageReport Survey()
    {
        var data = database.Directory;
        var runs = library.ListRuns();

        var versionItems = versions.All()
            .Select(v => new StorageItem("version", v.Id, v.SongId, v.Title, v.VoiceLabel,
                FolderSize.LengthOf(versions.FilePath(v.Id)) + FolderSize.LengthOf(versions.StreamPath(v.Id)), v.CreatedAt, Stage: Unfinished(v.Stage)))
            .ToList();
        var stemItems = stems.All()
            .Select(s => new StorageItem("stems", s.Id, s.SongId.Length > 0 ? s.SongId : null, s.Title, s.Model,
                FolderSize.Of(stems.Folder(s.Id)), s.CreatedAt, Stage: Unfinished(s.Stage)))
            .ToList();
        // Videos go with their song, so asking per song finds them all.
        var videoItems = runs.SelectMany(r => r.Songs).SelectMany(s => videos.ForSong(s.Id))
            .Select(v => new StorageItem("video", v.Id, v.SongId, v.Title, v.Media is null ? v.Format : $"{v.Format} · {v.Media}",
                FolderSize.Of(videos.Folder(v.Id)), v.CreatedAt, Stage: Unfinished(v.Stage)))
            .ToList();
        var titles = runs.SelectMany(r => r.Songs.Select(s => (s.Id, r.Title))).ToDictionary(p => p.Id, p => p.Title);
        var coverItems = covers.All().Keys
            .Select(id => covers.Get(id) is { } cover
                ? new StorageItem("cover", id, id, titles.GetValueOrDefault(id, id), null, FolderSize.LengthOf(cover.Path), cover.UpdatedAt)
                : null)
            .OfType<StorageItem>()
            .ToList();
        var imageItems = images.All()
            .Select(i => new StorageItem("image", i.Id, i.SongId, i.Title ?? titles.GetValueOrDefault(i.SongId, i.SongId), i.ModelLabel,
                FolderSize.LengthOf(images.ImagePath(i.Id)) + FolderSize.LengthOf(Path.ChangeExtension(images.ImagePath(i.Id), ".json")),
                i.CreatedAt, Stage: Unfinished(i.Stage)))
            .ToList();

        // A song takes everything made from it along when it is deleted (SongLibrary.DeleteSong, ImageMaker.DeleteOf).
        var related = versionItems.Concat(stemItems).Concat(videoItems).Concat(coverItems).Concat(imageItems)
            .Where(i => i.SongId is not null)
            .GroupBy(i => i.SongId!)
            .ToDictionary(g => g.Key, g => g.Sum(i => i.Bytes));
        var songItems = runs
            .SelectMany(r => r.Songs.Select(s =>
            {
                var song = s.Id.Split('/')[1];
                return new StorageItem("song", s.Id, s.Id, r.Title, null,
                    s.Bytes + related.GetValueOrDefault(s.Id) + FolderSize.LengthOf(streams.SongPath(r.Id, song)), r.CreatedAt, s.Rating);
            }))
            .ToList();

        var transcriptionItems = transcriptions.List().Items
            .Select(t => new StorageItem("transcription", t.Id, null, t.SourceName, null, t.Bytes, t.CreatedAt))
            .ToList();
        var swapItems = swaps.All()
            .Select(s => new StorageItem("swap", s.Id, null, s.FileName, s.VoiceLabel, FolderSize.Of(swaps.Folder(s.Id)), s.CreatedAt, Stage: Unfinished(s.Stage)))
            .ToList();
        var takeItems = speech.Takes(int.MaxValue)
            .Select(t => new StorageItem("take", t.Id, null, Shorten(t.Text), t.ModelLabel, FolderSize.LengthOf(speech.TakePath(t.Id)), t.CreatedAt, Stage: Unfinished(t.Stage)))
            .ToList();
        var loraItems = loras.List()
            .Select(l => new StorageItem("lora", l.Name, null, l.Name, l.TriggerWord, l.SizeBytes, l.ModifiedAt))
            .ToList();

        var volumes = new Volumes();
        var groups = new List<StorageGroup?>
        {
            // Song folders only: the transcriptions in the same folder are a part of their own.
            Entries("songs", Path.Combine(paths.OutputDir), runs.Sum(r => r.Bytes), songItems, volumes, byOwnSize: runs.SelectMany(r => r.Songs).ToDictionary(s => s.Id, s => s.Bytes)),
            Entries("versions", Path.Combine(data, "versions"), versionItems, volumes),
            Entries("stems", Path.Combine(data, "stems"), stemItems, volumes),
            Entries("videos", Path.Combine(data, "videos"), videoItems, volumes),
            Entries("covers", covers.Folder, coverItems, volumes),
            Entries("images", images.Folder, imageItems, volumes),
            Entries("swaps", Path.Combine(data, "swaps"), swapItems, volumes),
            Entries("takes", Path.Combine(data, "speech", "takes"), takeItems, volumes),
            Entries("transcriptions", paths.TranscriptionsDir, transcriptionItems, volumes),
            Entries("loras", loras.Folder, loraItems, volumes),
            // Made again on the next play; deleting them is not worth a button.
            Folder("streams", StorageGroups.Data, Path.Combine(data, "stream"), volumes,
                extra: versionItems.Sum(v => FolderSize.LengthOf(versions.StreamPath(v.Id)))),
            Files("database", data, volumes, Path.GetFileName(database.Path), $"{Path.GetFileName(database.Path)}-wal", $"{Path.GetFileName(database.Path)}-shm"),
            Folder("logs", StorageGroups.Data, logOptions.Value.ResolvedDirectory, volumes),
            Folder("yue2", StorageGroups.Models, paths.ModelCache, volumes),
            Folder("ane", StorageGroups.Models, Path.Combine(paths.InstallRoot, "ane-cache"), volumes),
            Folder("lmstudio", StorageGroups.Models,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lmstudio", "models"), volumes),
            Folder("speechModels", StorageGroups.Models, speechOptions.Value.ResolvedModelCache, volumes),
            Folder("imageModels", StorageGroups.Models, imageOptions.Value.ResolvedModelCache, volumes),
            Folder("separator", StorageGroups.Models, voiceOptions.Value.ResolvedSeparatorModels, volumes),
            Folder("seedvc", StorageGroups.Models, voiceOptions.Value.ResolvedSeedVcModels, volumes),
            Folder("montageModels", StorageGroups.Models, montageOptions.Value.ResolvedModelCache, volumes),
        };

        var failed = versionItems.Concat(stemItems).Concat(videoItems).Concat(swapItems).Concat(imageItems).Concat(takeItems)
            .Where(i => i.Stage is "failed" or "cancelled" && i.Bytes > 0)
            .ToList();
        // A candidate is copied when it becomes the cover, so once a song has one its candidates only take room.
        var covered = coverItems.Select(c => c.Id).ToHashSet();
        var painted = imageItems.Where(i => i.Stage is null && covered.Contains(i.SongId!)).ToList();
        var oneStar = songItems.Where(s => s.Rating == LowRating).ToList();

        return new StorageReport(
            volumes.All,
            [.. groups.OfType<StorageGroup>()],
            [
                .. new[] { Suggest("paintedCovers", painted), Suggest("failed", failed), Suggest("oneStar", oneStar) }
                    .OfType<StorageSuggestion>(),
            ]);
    }

    private static string? Unfinished(string stage) => stage is "failed" or "cancelled" ? stage : null;

    private static string Shorten(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= 80 ? line : $"{line[..79]}…";
    }

    private static StorageSuggestion? Suggest(string key, List<StorageItem> items) =>
        items.Count == 0 ? null : new StorageSuggestion(key, items.Sum(i => i.Bytes), [.. items.OrderByDescending(i => i.Bytes)]);

    private static StorageGroup? Entries(string key, string folder, List<StorageItem> items, Volumes volumes) =>
        Entries(key, folder, items.Sum(i => i.Bytes), items, volumes);

    /// <param name="bytes">The part's own share; a song's item counts what goes with it, the part only its folder.</param>
    /// <param name="byOwnSize">Ranks the entries by their own size rather than what deleting them frees.</param>
    private static StorageGroup? Entries(
        string key, string folder, long bytes, List<StorageItem> items, Volumes volumes, IReadOnlyDictionary<string, long>? byOwnSize = null)
    {
        var place = Place(folder, volumes);
        if (items.Count == 0 && !place.Missing)
        {
            return null;
        }
        var largest = items.OrderByDescending(i => byOwnSize?.GetValueOrDefault(i.Id) ?? i.Bytes).Take(Largest);
        return new StorageGroup(key, StorageGroups.Data, bytes, items.Count, place.Volume, place.Moved, place.Missing, [.. largest]);
    }

    private static StorageGroup? Folder(string key, string section, string folder, Volumes volumes, long extra = 0)
    {
        var place = Place(folder, volumes);
        var bytes = FolderSize.Of(folder) + extra;
        return bytes == 0 && !place.Missing ? null : new StorageGroup(key, section, bytes, null, place.Volume, place.Moved, place.Missing, []);
    }

    private static StorageGroup? Files(string key, string folder, Volumes volumes, params string[] names)
    {
        var bytes = names.Sum(n => FolderSize.LengthOf(Path.Combine(folder, n)));
        return bytes == 0 ? null : new StorageGroup(key, StorageGroups.Data, bytes, null, Place(folder, volumes).Volume, null, false, []);
    }

    /// <summary>Where the folder lies, following the link <c>move-to-volume.sh</c> left in its place.</summary>
    private static (string? Volume, string? Moved, bool Missing) Place(string folder, Volumes volumes)
    {
        var directory = new DirectoryInfo(folder);
        if (directory.LinkTarget is null)
        {
            return (directory.Exists ? volumes.Of(directory.FullName) : null, null, false);
        }
        return FolderSize.Target(directory) is { } target
            ? (volumes.Of(target.FullName), target.FullName, false)
            : (null, directory.LinkTarget, true);
    }

    /// <summary>The mounted volumes, matched to a path by the longest mount point it starts with.</summary>
    private sealed class Volumes
    {
        private readonly List<DriveInfo> _drives = Mounted();
        private readonly Dictionary<string, StorageVolume> _used = new(StringComparer.Ordinal);

        public IReadOnlyList<StorageVolume> All => [.. _used.Values.OrderBy(v => v.Name.Length)];

        public string? Of(string path)
        {
            var drive = _drives
                .Where(d => Under(path, d.Name))
                .MaxBy(d => d.Name.Length);
            if (drive is null)
            {
                return null;
            }
            if (!_used.ContainsKey(drive.Name))
            {
                try
                {
                    _used[drive.Name] = new StorageVolume(drive.Name, drive.AvailableFreeSpace, drive.TotalSize);
                }
                catch (IOException)
                {
                    return null;
                }
            }
            return drive.Name;
        }

        private static bool Under(string path, string mount) =>
            mount == "/" || path == mount || path.StartsWith(mount.TrimEnd('/') + "/", StringComparison.Ordinal);

        /// <summary>
        /// Every mount, virtual ones included (<c>/proc</c>, <c>/dev</c>): no folder of this app lies under them, and the
        /// types .NET reports for APFS or a container's overlay are not reliable enough to filter by.
        /// </summary>
        private static List<DriveInfo> Mounted()
        {
            try
            {
                return [.. DriveInfo.GetDrives().Where(d => d.IsReady)];
            }
            catch (IOException)
            {
                return [];
            }
        }
    }
}
