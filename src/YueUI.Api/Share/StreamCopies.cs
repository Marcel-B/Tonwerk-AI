using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using YueUI.Api.Data;
using YueUI.Api.Export;
using YueUI.Api.Library;

namespace YueUI.Api.Share;

/// <summary>
/// The AAC copies the player streams instead of the FLAC. The Mac sits behind a home connection whose upload is a
/// fraction of its download: a FLAC runs at about 1 Mbit/s, which on the road stalled or took long to start, while
/// 192 kbit/s AAC is a sixth of that and sounds the same through a phone. The FLAC stays what downloads, exports and
/// the Logic page use.
/// </summary>
/// <remarks>
/// Copies live in <c>stream/</c> next to the database (a song's as <c>stream/&lt;run&gt;/songN.m4a</c>, a version's
/// beside its FLAC in <c>versions/</c>), never in YuE Studio's song folder. <see cref="StreamCopyMaker"/> makes them
/// when a song or version is finished; older ones are made on their first play. A render writes the song's FLAC
/// anew in place, so a copy older than its FLAC counts as missing. Without an encoder, or when encoding fails, the
/// caller serves the FLAC as before.
/// <para>
/// A copy carries the song's tags like an export (title, lyrics, style and seed, its own cover), so a file saved
/// from the player or opened elsewhere says what it is. Titles, covers and lyrics change without the audio, so the
/// tags' <see cref="StreamTags.Stamp"/> lies beside the copy (<c>.tags</c>) and a different one tags it again
/// without encoding. Every change goes into a temporary file that replaces the copy, since a player may be reading
/// it. A song without a cover of its own has none in the file: the drawn one exists only in the browser.
/// </para>
/// </remarks>
public sealed class StreamCopies(
    SqliteDatabase database,
    SqliteVersionStore versions,
    IAudioEncoder encoder,
    IAudioTagger tagger,
    ILogger<StreamCopies> logger)
{
    public const int BitRate = 192_000;

    /// <summary>One encoding per copy even when the player's range requests and <see cref="StreamCopyMaker"/> meet.</summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<bool>>> _making = new();

    /// <summary>The tags a song's copy carries, from what the library knows about it.</summary>
    public static StreamTags TagsFor(SongLibrary library, string run, string song, string directory, string? voice = null)
    {
        var cover = library.CoverOf(run, song);
        var tags = ExportEndpoints.Tags(library, run, song, directory, null, null, null);
        if (voice is not null)
        {
            // A version plays beside its song; the voice tells them apart in another player's list.
            tags = tags with { Title = $"{tags.Title} ({voice})" };
        }
        // The cover by its path, which changes with every new picture, rather than reading it on every request.
        var stamp = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join("\n", tags.Title, tags.Album, tags.Year, tags.Track, tags.TrackCount, tags.Lyrics, tags.Comment, cover?.Path))));
        return new StreamTags(stamp, () => cover is not null && File.Exists(cover.Path)
            ? tags with { Cover = new CoverImage(File.ReadAllBytes(cover.Path), cover.ContentType) }
            : tags);
    }

    private string Root => Path.Combine(database.Directory, "stream");

    /// <summary>Where the song's copy lies; <paramref name="run"/> and <paramref name="song"/> must be checked names.</summary>
    public string SongPath(string run, string song) => Path.Combine(Root, run, $"{song}.m4a");

    /// <summary>The song's copy, made first if needed.</summary>
    /// <returns>Its path, or null when there is none and the FLAC has to do.</returns>
    public Task<string?> SongAsync(string run, string song, string flac, StreamTags tags) => GetAsync(flac, SongPath(run, song), tags);

    /// <summary>The version's copy, made first if needed.</summary>
    /// <returns>Its path, or null when there is none and the FLAC has to do.</returns>
    public Task<string?> VersionAsync(string id, StreamTags tags) => GetAsync(versions.FilePath(id), versions.StreamPath(id), tags);

    /// <summary>Deletes the song's copy; the song is gone.</summary>
    public void ForgetSong(string run, string song) => Delete(() =>
    {
        File.Delete(SongPath(run, song));
        File.Delete(StampPath(SongPath(run, song)));
    });

    /// <summary>Where the stamp of a copy's tags lies.</summary>
    public static string StampPath(string copy) => $"{copy}.tags";

    /// <summary>Deletes the copies of the run's songs; the run is gone.</summary>
    public void ForgetRun(string run) => Delete(() =>
    {
        var directory = Path.Combine(Root, run);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    });

    /// <summary>
    /// Deletes every copy, of songs and versions; the player makes them again on the next play. Copies in the making
    /// (their temporary names start with a dot) are left alone.
    /// </summary>
    /// <returns>The bytes and files freed.</returns>
    public (long Bytes, int Files) ForgetAll()
    {
        long bytes = 0;
        var files = 0;
        foreach (var (folder, recurse) in new[] { (Root, true), (Path.Combine(database.Directory, "versions"), false) })
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }
            var options = new EnumerationOptions { RecurseSubdirectories = recurse, IgnoreInaccessible = true, AttributesToSkip = 0 };
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", options))
            {
                if (file.Name.StartsWith('.') || !(file.Name.EndsWith(".m4a", StringComparison.Ordinal) || file.Name.EndsWith(".m4a.tags", StringComparison.Ordinal)))
                {
                    continue;
                }
                var length = file.Length;
                Delete(() =>
                {
                    file.Delete();
                    bytes += length;
                    files++;
                });
            }
        }
        if (Directory.Exists(Root))
        {
            foreach (var run in new DirectoryInfo(Root).EnumerateDirectories())
            {
                // Empty now, unless a copy is being made in it this moment.
                Delete(() => run.Delete(recursive: false));
            }
        }
        return (bytes, files);
    }

    private async Task<string?> GetAsync(string source, string target, StreamTags tags)
    {
        if (!File.Exists(source))
        {
            return null;
        }
        if (IsFresh(source, target) && ReadStamp(target) == tags.Stamp)
        {
            return target;
        }
        var making = _making.GetOrAdd(target, _ => new Lazy<Task<bool>>(() => MakeAsync(source, target, tags)));
        try
        {
            return await making.Value ? target : null;
        }
        finally
        {
            _making.TryRemove(new KeyValuePair<string, Lazy<Task<bool>>>(target, making));
        }
    }

    private static bool IsFresh(string source, string target) =>
        File.Exists(target) && File.GetLastWriteTimeUtc(target) >= File.GetLastWriteTimeUtc(source);

    private static string? ReadStamp(string target)
    {
        try
        {
            return File.ReadAllText(StampPath(target));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Into a temporary name first, so a player never gets half a file. Not cancelled with the request: the player
    /// drops range requests all the time, and the copy is wanted anyway.
    /// </summary>
    private async Task<bool> MakeAsync(string source, string target, StreamTags tags)
    {
        var temp = Path.Combine(Path.GetDirectoryName(target)!, $".{Path.GetFileNameWithoutExtension(target)}-{Guid.NewGuid():N}.m4a");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            // Only the tags changed: a copy is quicker than encoding again (checked anew, a render may have come).
            if (IsFresh(source, target))
            {
                File.Copy(target, temp);
            }
            else if (!await encoder.EncodeAsync(source, temp, AudioFormat.M4a, BitRate, CancellationToken.None))
            {
                return false;
            }
            Tag(temp, tags);
            File.Move(temp, target, overwrite: true);
            // Also after a failed tagging: trying again on every range request would copy the file each time.
            await File.WriteAllTextAsync(StampPath(target), tags.Stamp);
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not make the streaming copy of {Source}", source);
            return false;
        }
        finally
        {
            Delete(() => File.Delete(temp));
        }
    }

    /// <summary>A copy without tags still plays; that matters more than the tags.</summary>
    private void Tag(string path, StreamTags tags)
    {
        try
        {
            tagger.Write(path, tags.Build());
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not tag the streaming copy {Path}", path);
        }
    }

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

/// <summary>The tags for a copy, built only when it has to be tagged, and a stamp that changes with them.</summary>
public sealed record StreamTags(string Stamp, Func<SongTags> Build);
