using Microsoft.Extensions.Options;
using YueUI.Api.Export;
using YueUI.Api.Images;
using YueUI.Api.Library;
using YueUI.Api.Worker;

namespace YueUI.Api;

/// <summary>
/// Covers painted by a local FLUX.2 Klein (<see cref="ImageMaker"/>): a prompt in, candidates out, which the cover
/// dialog lists; one taken becomes the song's cover like a photo would. Pictures are painted in the background; their
/// progress arrives as <c>image</c> events.
/// </summary>
public static class ImageEndpoints
{
    public static RouteGroupBuilder MapImageEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/images", (IOptions<ImageOptions> options, IImageEngine engine) =>
            new ImageInfo(
                engine.Installed,
                options.Value.ResolvedPython,
                [.. options.Value.ResolvedModels.Select(m => new ImageModelInfo(
                    m.Id, m.Label, m.DownloadGb, m.MemoryGb, m.License, m.Commercial, m.Gated, engine.Downloaded(m), !m.Gated || engine.TokenFound))]))
            .WithName("GetImageInfo")
            .Produces<ImageInfo>(StatusCodes.Status200OK);

        api.MapGet("/songs/{run}/{song}/images", (string run, string song, SongLibrary library, ImageMaker images) =>
            library.SongDirectory(run, song) is null ? Results.NotFound() : Results.Ok(images.Of($"{run}/{song}")))
            .WithName("ListSongImages")
            .Produces<IReadOnlyList<ImageState>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);
        api.MapPost("/songs/{run}/{song}/images", Paint)
            .WithName("CreateSongImage")
            .Produces<ImageState>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status501NotImplemented);

        api.MapGet("/images/{id}", (string id, ImageMaker images) =>
            images.Get(id) is { Stage: "done" } && File.Exists(images.ImagePath(id))
                ? Results.File(images.ImagePath(id), "image/jpeg")
                : Results.NotFound())
            .WithName("GetImage")
            .ProducesFile("image/jpeg")
            .Produces(StatusCodes.Status404NotFound);
        api.MapPut("/images/{id}/cover", async (string id, ImageMaker images, SongLibrary library, Status.StatusHub hub, CancellationToken cancellationToken) =>
        {
            if (images.Get(id) is not { Stage: "done" } image
                || !File.Exists(images.ImagePath(id))
                || image.SongId.Split('/') is not [var run, var song])
            {
                return Results.NotFound();
            }
            // A copy: the candidate stays in the dialog, so another one can be tried and this one taken back.
            if (!library.SetCover(run, song, new CoverImage(await File.ReadAllBytesAsync(images.ImagePath(id), cancellationToken), "image/jpeg")))
            {
                return Results.NotFound();
            }
            hub.LibraryChanged();
            return Results.NoContent();
        })
            .WithName("SetImageAsCover")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);
        api.MapDelete("/images/{id}", (string id, ImageMaker images) =>
        {
            if (images.Get(id) is not { } image)
            {
                return Results.NotFound();
            }
            images.Delete(image);
            return Results.NoContent();
        })
            .WithName("DeleteImage")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);
        return api;
    }

    private static IResult Paint(
        string run,
        string song,
        ImageRequest request,
        SongLibrary library,
        ImageMaker images,
        IImageEngine engine,
        IOptions<ImageOptions> options)
    {
        if (library.SongDirectory(run, song) is not { } directory)
        {
            return Results.NotFound();
        }
        var offered = options.Value.ResolvedModels;
        var errors = new Dictionary<string, string[]>();
        var prompt = request.Prompt?.Trim() ?? "";
        if (prompt.Length is 0 or > ImageRequest.MaxPromptLength)
        {
            errors["prompt"] = [$"What to paint, up to {ImageRequest.MaxPromptLength} characters."];
        }
        var model = request.Model is null ? offered.FirstOrDefault() : offered.FirstOrDefault(m => m.Id == request.Model);
        if (model is null)
        {
            errors["model"] = [$"One of {string.Join(", ", offered.Select(m => m.Id))}."];
        }
        if (request.Seed is < 0 or > uint.MaxValue)
        {
            errors["seed"] = [$"From 0 to {uint.MaxValue}."];
        }
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }
        if (!engine.Installed)
        {
            return Results.Problem(
                $"mflux is not installed ({options.Value.ResolvedPython}); run deploy/install-images.sh.",
                statusCode: StatusCodes.Status501NotImplemented);
        }
        // mflux seeds MLX's generator, which takes 32 bits.
        var seed = request.Seed ?? Random.Shared.NextInt64(0, (long)uint.MaxValue + 1);
        var image = images.Enqueue($"{run}/{song}", library.TitleOf(run, directory), model!, prompt, seed);
        return Results.Accepted($"/api/images/{image.Id}", image);
    }
}
