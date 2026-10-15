using YueUI.Api.Library;
using YueUI.Api.Worker;

namespace YueUI.Api;

/// <summary>
/// A song's own cover: a photo chosen in the library or the export dialog, kept for the library, the player (and the
/// lock screen) and every later export. Songs without one show the cover the browser draws from title and id.
/// </summary>
public static class CoverEndpoints
{
    public static RouteGroupBuilder MapCoverEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/songs/{run}/{song}/cover", (string run, string song, SongLibrary library) =>
            library.CoverOf(run, song) is { } cover
                ? Results.File(cover.Path, cover.ContentType, lastModified: cover.UpdatedAt)
                : Results.NotFound())
            .WithName("GetSongCover")
            .ProducesFile("image/jpeg", "image/png")
            .Produces(StatusCodes.Status404NotFound);
        api.MapPut("/songs/{run}/{song}/cover", PutAsync).DisableAntiforgery()
            .WithName("SetSongCover")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesValidationProblem();
        api.MapDelete("/songs/{run}/{song}/cover", (string run, string song, SongLibrary library, Status.StatusHub hub) =>
        {
            if (!library.SetCover(run, song, null))
            {
                return Results.NotFound();
            }
            hub.LibraryChanged();
            return Results.NoContent();
        })
            .WithName("DeleteSongCover")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);
        return api;
    }

    /// <summary>Every open browser reloads its library, so the phone and the Mac show the same cover.</summary>
    private static async Task<IResult> PutAsync(
        string run, string song, IFormFile? cover, SongLibrary library, Status.StatusHub hub, CancellationToken cancellationToken)
    {
        if (library.SongDirectory(run, song) is null)
        {
            return Results.NotFound();
        }
        if (cover is null || await ExportEndpoints.ReadCoverAsync(cover, cancellationToken) is not { } image)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["cover"] = [$"A JPEG or PNG of at most {ExportEndpoints.MaxCoverBytes / 1024 / 1024} MB."],
            });
        }
        library.SetCover(run, song, image);
        hub.LibraryChanged();
        return Results.NoContent();
    }
}
