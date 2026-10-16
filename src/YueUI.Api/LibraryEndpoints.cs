using System.IO.Compression;
using System.Text.RegularExpressions;
using YueUI.Api.Export;
using YueUI.Api.Library;
using YueUI.Api.Share;
using YueUI.Api.Worker;

namespace YueUI.Api;

/// <param name="Title">The new title; empty returns the run to the worker's.</param>
public sealed record RenameRequest(string? Title);

/// <param name="Rating">One to five stars; null (or 0) takes the rating away.</param>
public sealed record RatingRequest(int? Rating);

/// <param name="Note">The song's note; empty (or null) takes it away.</param>
public sealed record NoteRequest(string? Note);

/// <param name="FreeBytes">What the current user can still write.</param>
public sealed record StorageInfo(long FreeBytes, long TotalBytes);

/// <summary>
/// The finished songs on disk: the list, each song's audio and score, deleting songs and runs, and how much space
/// is left.
/// </summary>
public static partial class LibraryEndpoints
{
    /// <summary>Titles show in one line on a phone; this is only a guard against pasting a whole text.</summary>
    public const int MaxTitleLength = 200;

    /// <summary>Room for a page of thoughts; only a guard against pasting a whole file.</summary>
    public const int MaxNoteLength = 10_000;

    public static RouteGroupBuilder MapLibraryEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/library", (SongLibrary library) => library.ListRuns())
            .WithName("ListLibrary");
        // Which style blocks and settings the rated songs were made with, against their stars.
        api.MapGet("/insights", (SongLibrary library) => Insights.Build(library))
            .WithName("GetInsights");
        // Range requests let the browser's player seek and start before a five-minute FLAC is loaded.
        // A download is a tagged copy (cover, lyrics); the player, the Logic page and range requests get the file itself.
        api.MapGet("/songs/{run}/{song}/audio", (string run, string song, bool? download, SongLibrary library, TaggedFiles tagged) =>
            download == true
                ? AudioDownload(library, tagged, run, song)
                : SongFile(library, run, song, "audio.flac", "audio/flac", "flac", download: false))
            .WithName("GetSongAudio")
            .ProducesFile("audio/flac")
            .Produces(StatusCodes.Status404NotFound);
        // What the player plays: the AAC copy, since the Mac's home upload is too slow for the FLAC on the road.
        api.MapGet("/songs/{run}/{song}/stream", StreamAsync)
            .WithName("GetSongStream")
            .ProducesFile("audio/mp4", "audio/flac")
            .Produces(StatusCodes.Status404NotFound);
        api.MapGet("/songs/{run}/{song}/score", (string run, string song, SongLibrary library) =>
            SongFile(library, run, song, "score.abc", "text/vnd.abc; charset=utf-8", "abc", download: true))
            .WithName("GetSongScore")
            .ProducesFile("text/vnd.abc")
            .Produces(StatusCodes.Status404NotFound);
        // What the song was made with, for "as a new song" in the web form.
        api.MapGet("/songs/{run}/{song}/request", (string run, string song, SongLibrary library) =>
            library.ReadRequest(run, song) is { } request ? Results.Ok(request) : Results.NotFound())
            .WithName("GetSongRequest")
            .Produces<SongRequest>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);
        // A version in the works reads the song's audio; it is cancelled with its song by deleting the version first.
        // Cover candidates go with the song; they are no use to anything else.
        api.MapDelete("/songs/{run}/{song}", (string run, string song, SongLibrary library, WorkerHost host, Status.StatusHub hub, Voices.VoiceConverter voices, Images.ImageMaker images) =>
            Delete(hub, host.IsWorkingOn(run, song) || voices.IsWorkingOn(run, song), () => library.DeleteSong(run, song) && images.DeleteOf(run, song)))
            .WithName("DeleteSong")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        api.MapDelete("/runs/{run}", (string run, SongLibrary library, WorkerHost host, Status.StatusHub hub, Voices.VoiceConverter voices, Images.ImageMaker images) =>
            Delete(hub, host.IsWorkingOn(run) || voices.IsWorkingOn(run), () => library.DeleteRun(run) && images.DeleteOf(run)))
            .WithName("DeleteRun")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        api.MapPut("/runs/{run}/title", (string run, RenameRequest request, SongLibrary library, WorkerHost host) =>
            Rename(library, host, run, request.Title?.Trim() ?? ""))
            .WithName("RenameRun")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesValidationProblem();
        api.MapPut("/songs/{run}/{song}/rating", (string run, string song, RatingRequest request, SongLibrary library, Status.StatusHub hub) =>
            Rate(library, hub, run, song, request.Rating is 0 ? null : request.Rating))
            .WithName("RateSong")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesValidationProblem();
        api.MapPut("/songs/{run}/{song}/note", (string run, string song, NoteRequest request, SongLibrary library, Status.StatusHub hub) =>
            SetNote(library, hub, run, song, request.Note ?? ""))
            .WithName("SetSongNote")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesValidationProblem();
        api.MapGet("/storage", (YuePaths paths) => Storage(paths.OutputDir))
            .WithName("GetFreeSpace");
        return api;
    }

    private static async Task<IResult> StreamAsync(string run, string song, SongLibrary library, StreamCopies streams, HttpContext context)
    {
        if (library.SongDirectory(run, song) is not { } directory || !File.Exists(Path.Combine(directory, "audio.flac")))
        {
            return Results.NotFound();
        }
        var tags = StreamCopies.TagsFor(library, run, song, directory);
        return await streams.SongAsync(run, song, Path.Combine(directory, "audio.flac"), tags) is { } copy
            ? StreamFile(context, copy, "audio/mp4")
            : StreamFile(context, Path.Combine(directory, "audio.flac"), "audio/flac");
    }

    /// <summary>
    /// With range requests for seeking, and a validator the browser must check each time: a render writes the
    /// song anew under the same address, and the check costs a 304 without body.
    /// </summary>
    internal static IResult StreamFile(HttpContext context, string path, string contentType)
    {
        var info = new FileInfo(path);
        context.Response.Headers.CacheControl = "no-cache";
        var tag = new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{info.LastWriteTimeUtc.Ticks:x}-{info.Length:x}\"");
        return Results.File(path, contentType, lastModified: info.LastWriteTimeUtc, entityTag: tag, enableRangeProcessing: true);
    }

    /// <summary>
    /// Deleting what the worker still works on would pull the folder from under it; it would fail the song, or
    /// write it again half. Only this server's worker is known here, not the one in YuE Studio.
    /// </summary>
    private static IResult Delete(Status.StatusHub hub, bool working, Func<bool> delete)
    {
        if (working)
        {
            return Results.Problem(title: "The worker is still working on it; cancel it first.", statusCode: StatusCodes.Status409Conflict);
        }
        if (!delete())
        {
            return Results.NotFound();
        }
        hub.LibraryChanged();
        return Results.NoContent();
    }

    /// <summary>
    /// Only the title shown and used for file names changes; the folder keeps its name, so the song ids in the
    /// playlist, in links and in the worker's states stay valid (see <see cref="Data.SqliteRunTitleStore"/>).
    /// </summary>
    private static IResult Rename(SongLibrary library, WorkerHost host, string run, string title)
    {
        if (title.Length > MaxTitleLength)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["title"] = [$"At most {MaxTitleLength} characters."],
            });
        }
        if (!library.Rename(run, title))
        {
            return Results.NotFound();
        }
        var directories = library.SongDirectories(run);
        host.RunRenamed(run, directories is { Count: > 0 } ? library.TitleOf(run, directories[0]) : title);
        return Results.NoContent();
    }

    /// <summary>Every open browser reloads its library, so the stars match on the phone and the Mac.</summary>
    private static IResult Rate(SongLibrary library, Status.StatusHub hub, string run, string song, int? rating)
    {
        if (rating is < 1 or > Data.SqliteSongRatingStore.MaxRating)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["rating"] = [$"1 to {Data.SqliteSongRatingStore.MaxRating} stars, or null to remove the rating."],
            });
        }
        if (!library.Rate(run, song, rating))
        {
            return Results.NotFound();
        }
        hub.LibraryChanged();
        return Results.NoContent();
    }

    /// <summary>
    /// Whitespace around the text is dropped, so a note of only blank lines counts as none and the button by the
    /// song is not marked for it. Every open browser reloads its library, like for the stars.
    /// </summary>
    private static IResult SetNote(SongLibrary library, Status.StatusHub hub, string run, string song, string note)
    {
        note = note.Trim();
        if (note.Length > MaxNoteLength)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["note"] = [$"At most {MaxNoteLength} characters."],
            });
        }
        if (!library.SetNote(run, song, note))
        {
            return Results.NotFound();
        }
        hub.LibraryChanged();
        return Results.NoContent();
    }

    /// <summary>Free and total space of the volume the songs are written to.</summary>
    private static StorageInfo Storage(string outputDir)
    {
        // On macOS and Linux DriveInfo takes any path on the volume, but it has to exist.
        var existing = new DirectoryInfo(outputDir);
        while (!existing.Exists && existing.Parent is { } parent)
        {
            existing = parent;
        }
        var drive = new DriveInfo(existing.FullName);
        return new StorageInfo(drive.AvailableFreeSpace, drive.TotalSize);
    }

    /// <summary>The song's FLAC as a tagged copy, named after its title.</summary>
    private static IResult AudioDownload(SongLibrary library, TaggedFiles tagged, string run, string song)
    {
        if (library.SongDirectory(run, song) is not { } directory || !File.Exists(Path.Combine(directory, "audio.flac")))
        {
            return Results.NotFound();
        }
        return tagged.Download(
            Path.Combine(directory, "audio.flac"),
            StreamCopies.TagsFor(library, run, song, directory).Build(),
            "audio/flac",
            $"{FileName(library.TitleOf(run, directory), run)}-{song}.flac");
    }

    /// <summary>
    /// Builds the archive in a temporary file that deletes itself once the response is sent: a run of four full
    /// songs is well over 100 MB, too much to hold in memory, and ZipArchive writes synchronously, which Kestrel's
    /// response stream refuses. FLAC is compressed already, so it is only stored.
    /// </summary>
    internal static IResult Zip(string fileName, IEnumerable<(string Path, string Name)> entries)
    {
        var existing = entries.Where(e => File.Exists(e.Path)).ToList();
        if (existing.Count == 0)
        {
            return Results.NotFound();
        }

        var temp = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose);
        try
        {
            using (var archive = new ZipArchive(temp, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (path, name) in existing)
                {
                    archive.CreateEntryFromFile(path, name, name.EndsWith(".flac", StringComparison.Ordinal) ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                }
            }
            temp.Position = 0;
            return Results.File(temp, "application/zip", fileName);
        }
        catch
        {
            temp.Dispose();
            throw;
        }
    }

    private static IResult SongFile(SongLibrary library, string run, string song, string name, string contentType, string extension, bool download)
    {
        if (library.SongDirectory(run, song) is not { } directory || !File.Exists(Path.Combine(directory, name)))
        {
            return Results.NotFound();
        }
        var fileName = download ? $"{FileName(library.TitleOf(run, directory), run)}-{song}.{extension}" : null;
        return Results.File(Path.Combine(directory, name), contentType, fileName, enableRangeProcessing: true);
    }

    /// <summary>The title without characters a file system or a Content-Disposition header dislikes.</summary>
    internal static string FileName(string title, string run)
    {
        var name = Unsafe().Replace(title, "").Trim();
        return name.Length > 0 ? name : run;
    }

    [GeneratedRegex(@"[\\/:*?""<>|\p{C}]")]
    private static partial Regex Unsafe();
}
