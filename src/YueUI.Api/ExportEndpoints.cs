using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using YueUI.Api.Export;
using YueUI.Api.Library;
using YueUI.Api.Share;

namespace YueUI.Api;

/// <summary>
/// A song as a file for a music library or to share: MP3, M4A, FLAC or a small AAC with title, artist, album, genre, lyrics and cover in the
/// format's own tags, so a phone or Apple Music shows them. The browser picks the artist, genre and cover (it draws a
/// default cover itself); without one the song's own cover goes in, if it has one. Everything else comes from the song. Made anew each time into a temporary file, like the
/// share endpoint's AAC.
/// </summary>
public static class ExportEndpoints
{
    /// <summary>A phone photo scaled down in the browser is a few hundred kilobytes; this only guards against originals.</summary>
    public const long MaxCoverBytes = 5 * 1024 * 1024;

    /// <summary>Artist and genre show in one line; this only guards against pasting a whole text.</summary>
    public const int MaxFieldLength = 200;

    /// <summary>LAME's highest constant rate: an export is meant to be kept, not sent.</summary>
    public const int Mp3BitRate = 320_000;

    /// <summary>What Apple sells its music in.</summary>
    public const int M4aBitRate = 256_000;

    public static RouteGroupBuilder MapExportEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/songs/{run}/{song}/export", ExportAsync).DisableAntiforgery()
            .WithName("ExportSong")
            .ProducesFile("audio/mp4", "audio/mpeg", "audio/flac")
            .Produces(StatusCodes.Status404NotFound)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .ProducesProblem(StatusCodes.Status500InternalServerError);
        return api;
    }

    private static async Task<IResult> ExportAsync(
        string run,
        string song,
        [FromForm] string? format,
        [FromForm] string? artist,
        [FromForm] string? genre,
        IFormFile? cover,
        SongLibrary library,
        IAudioEncoder encoder,
        IAudioTagger tagger,
        CancellationToken cancellationToken)
    {
        if (library.SongDirectory(run, song) is not { } directory || !File.Exists(Path.Combine(directory, "audio.flac")))
        {
            return Results.NotFound();
        }
        var errors = new Dictionary<string, string[]>();
        if (format?.ToLowerInvariant() is not ("mp3" or "m4a" or "flac" or "small"))
        {
            errors["format"] = ["One of mp3, m4a, flac or small (AAC to share)."];
        }
        if (artist?.Length > MaxFieldLength)
        {
            errors["artist"] = [$"At most {MaxFieldLength} characters."];
        }
        if (genre?.Length > MaxFieldLength)
        {
            errors["genre"] = [$"At most {MaxFieldLength} characters."];
        }
        CoverImage? image = null;
        if (cover is not null)
        {
            image = await ReadCoverAsync(cover, cancellationToken);
            if (image is null)
            {
                errors["cover"] = [$"A JPEG or PNG of at most {MaxCoverBytes / 1024 / 1024} MB."];
            }
        }
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }
        if (cover is null && library.CoverOf(run, song) is { } own)
        {
            image = new CoverImage(await File.ReadAllBytesAsync(own.Path, cancellationToken), own.ContentType);
        }

        var kind = format!.ToLowerInvariant();
        var extension = kind == "small" ? "m4a" : kind;
        var temp = Path.Combine(Path.GetTempPath(), $"yueui-export-{Guid.NewGuid():N}.{extension}");
        try
        {
            var flac = Path.Combine(directory, "audio.flac");
            if (extension == "flac")
            {
                // Brought to the target loudness where that is on and ffmpeg is there, else the original.
                if (!await encoder.EncodeAsync(flac, temp, AudioFormat.Flac, 0, cancellationToken))
                {
                    File.Copy(flac, temp, overwrite: true);
                }
            }
            else if (!await encoder.EncodeAsync(
                flac,
                temp,
                extension == "mp3" ? AudioFormat.Mp3 : AudioFormat.M4a,
                kind switch { "mp3" => Mp3BitRate, "small" => AacEncoder.BitRate, _ => M4aBitRate },
                cancellationToken))
            {
                File.Delete(temp);
                return Results.Problem(
                    title: extension == "mp3" ? "MP3 needs ffmpeg, which is not installed." : "Neither afconvert nor ffmpeg is installed.",
                    statusCode: StatusCodes.Status501NotImplemented);
            }
            tagger.Write(temp, Tags(library, run, song, directory, artist, genre, image));
            var stream = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Delete, 1 << 16, FileOptions.DeleteOnClose);
            var contentType = extension switch
            {
                "mp3" => "audio/mpeg",
                "m4a" => "audio/mp4",
                _ => "audio/flac",
            };
            return Results.File(stream, contentType, $"{LibraryEndpoints.FileName(library.TitleOf(run, directory), run)}-{song}.{extension}");
        }
        catch (InvalidOperationException exception)
        {
            File.Delete(temp);
            return Results.Problem(title: "The song could not be exported.", detail: exception.Message, statusCode: StatusCodes.Status500InternalServerError);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }

    /// <summary>What the song says about itself: its title, lyrics and style, and its place among the run's songs.</summary>
    internal static SongTags Tags(
        SongLibrary library, string run, string song, string directory, string? artist, string? genre, CoverImage? cover)
    {
        var title = library.TitleOf(run, directory);
        var request = library.ReadRequest(run, song);
        var model = File.Exists(Path.Combine(directory, SongLibrary.AceFile)) ? "ACE-Step" : "YuE2";
        var comment = string.Join(" · ", new[]
        {
            AiMark.Note(model),
            request?.Seed is { } seed ? $"Seed {seed.ToString(CultureInfo.InvariantCulture)}" : null,
            request?.Style is { Length: > 0 } style ? style : null,
        }.OfType<string>());
        return new SongTags(
            title,
            artist,
            title,
            SongLibrary.CreatedAt(run)?.Year,
            int.TryParse(song.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out var track) ? track : null,
            library.SongDirectories(run)?.Count,
            genre,
            request?.Instrumental == true ? null : request?.Lyrics,
            comment,
            cover,
            model);
    }

    /// <summary>Read by its first bytes rather than the declared type, which a browser guesses from the file name.</summary>
    internal static async Task<CoverImage?> ReadCoverAsync(IFormFile cover, CancellationToken cancellationToken)
    {
        if (cover.Length is 0 or > MaxCoverBytes)
        {
            return null;
        }
        using var buffer = new MemoryStream((int)cover.Length);
        await cover.CopyToAsync(buffer, cancellationToken);
        var data = buffer.ToArray();
        return data switch
        {
            [0xFF, 0xD8, 0xFF, ..] => new CoverImage(data, "image/jpeg"),
            [0x89, (byte)'P', (byte)'N', (byte)'G', ..] => new CoverImage(data, "image/png"),
            _ => null,
        };
    }
}
