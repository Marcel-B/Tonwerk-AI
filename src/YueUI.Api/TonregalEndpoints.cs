using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using YueUI.Api.Data;
using YueUI.Api.Export;
using YueUI.Api.Library;
using YueUI.Api.Share;
using YueUI.Api.Tonregal;

namespace YueUI.Api;

/// <summary>Whether songs can be handed to Tonregal; the export dialog shows its button only then.</summary>
public sealed record TonregalStatus(bool Configured);

/// <summary>
/// A song handed to Tonregal. <see cref="State"/> is Tonregal's: <c>imported</c> (filed under <see cref="LibraryPath"/>),
/// <c>matched</c> (accepted, filed shortly), <c>rejected</c> (the same file is there already, see <see cref="Error"/>).
/// </summary>
/// <param name="Repeated">Tonregal knew the song from an earlier handover and kept that one.</param>
public sealed record TonregalTransfer(int JobId, string State, string? LibraryPath, string? Error, bool Repeated);

/// <summary>
/// Hands a song to Tonregal: the export's FLAC with the same tags (title, album, lyrics, cover, the AI mark) plus artist
/// and genre from the dialog, tempo and key as heard, under the song's id as Tonregal's <c>externalId</c>.
/// </summary>
public static class TonregalEndpoints
{
    public static RouteGroupBuilder MapTonregalEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/tonregal", (IOptions<TonregalOptions> options) => new TonregalStatus(options.Value.Configured))
            .WithName("GetTonregalStatus")
            .Produces<TonregalStatus>(StatusCodes.Status200OK);

        api.MapPost("/songs/{run}/{song}/tonregal", SendAsync).DisableAntiforgery()
            .WithName("SendSongToTonregal")
            .Produces<TonregalTransfer>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .ProducesProblem(StatusCodes.Status502BadGateway);
        return api;
    }

    private static async Task<IResult> SendAsync(
        string run,
        string song,
        [FromForm] string? artist,
        [FromForm] string? genre,
        IFormFile? cover,
        SongLibrary library,
        SqliteSongTraitStore traits,
        IAudioEncoder encoder,
        IAudioTagger tagger,
        TonregalClient tonregal,
        IOptions<TonregalOptions> options,
        CancellationToken cancellationToken)
    {
        if (library.SongDirectory(run, song) is not { } directory || !File.Exists(Path.Combine(directory, "audio.flac")))
        {
            return Results.NotFound();
        }
        if (!options.Value.Configured)
        {
            return Results.Problem(
                title: "Tonregal is not configured (TONREGAL_URL and TONREGAL_API_KEY in ~/.config/tonwerk/tonregal.env).",
                statusCode: StatusCodes.Status501NotImplemented);
        }
        var errors = new Dictionary<string, string[]>();
        // Tonregal files by artist, so it needs one.
        if (string.IsNullOrWhiteSpace(artist))
        {
            errors["artist"] = ["Tonregal files songs by artist."];
        }
        else if (artist.Length > ExportEndpoints.MaxFieldLength)
        {
            errors["artist"] = [$"At most {ExportEndpoints.MaxFieldLength} characters."];
        }
        if (genre?.Length > ExportEndpoints.MaxFieldLength)
        {
            errors["genre"] = [$"At most {ExportEndpoints.MaxFieldLength} characters."];
        }
        CoverImage? image = null;
        if (cover is not null && (image = await ExportEndpoints.ReadCoverAsync(cover, cancellationToken)) is null)
        {
            errors["cover"] = [$"A JPEG or PNG of at most {ExportEndpoints.MaxCoverBytes / 1024 / 1024} MB."];
        }
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }
        if (cover is null && library.CoverOf(run, song) is { } own)
        {
            image = new CoverImage(await File.ReadAllBytesAsync(own.Path, cancellationToken), own.ContentType);
        }

        var temp = Path.Combine(Path.GetTempPath(), $"yueui-tonregal-{Guid.NewGuid():N}.flac");
        try
        {
            // The FLAC as an export makes it, so the library holds what a download would.
            var flac = Path.Combine(directory, "audio.flac");
            if (!await encoder.EncodeAsync(flac, temp, AudioFormat.Flac, 0, cancellationToken))
            {
                File.Copy(flac, temp, overwrite: true);
            }
            var tags = ExportEndpoints.Tags(library, run, song, directory, artist!.Trim(), genre?.Trim() is { Length: > 0 } g ? g : null, image);
            tagger.Write(temp, tags);
            var heard = traits.All().GetValueOrDefault($"{run}/{song}");
            var metadata = new TonregalMetadata(
                $"{run}/{song}", tags.Title, tags.Artist!, tags.Album, tags.Year, tags.Track, tags.Genre, heard?.Tempo, heard?.Key);
            var fileName = $"{LibraryEndpoints.FileName(tags.Title, run)}-{song}.flac";
            var (job, repeated) = await tonregal.SendAsync(temp, "audio/flac", fileName, metadata, image, cancellationToken);

            // Tonregal files it in the background, usually within a second or two; wait a little to say where.
            var deadline = DateTime.UtcNow + options.Value.Wait;
            while (job.State == "matched" && job.Error is null && DateTime.UtcNow < deadline)
            {
                await Task.Delay(options.Value.PollInterval, cancellationToken);
                job = await tonregal.GetAsync(job.Id, cancellationToken);
            }
            return Results.Ok(new TonregalTransfer(job.Id, job.State, job.LibraryPath, job.Error, repeated));
        }
        catch (TonregalException exception)
        {
            return Results.Problem(title: "Tonregal did not take the song.", detail: exception.Message, statusCode: StatusCodes.Status502BadGateway);
        }
        catch (InvalidOperationException exception)
        {
            return Results.Problem(title: "The song could not be prepared.", detail: exception.Message, statusCode: StatusCodes.Status500InternalServerError);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
