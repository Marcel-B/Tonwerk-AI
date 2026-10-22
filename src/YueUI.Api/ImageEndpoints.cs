using Microsoft.Extensions.Options;
using YueUI.Api.Export;
using YueUI.Api.Images;
using YueUI.Api.Library;
using YueUI.Api.Worker;

namespace YueUI.Api;

/// <summary>
/// Covers painted by a local image model (<see cref="ImageMaker"/>): a prompt in, candidates out, which the cover
/// dialog lists; one taken becomes the song's cover like a photo would. Pictures are painted in the background; their
/// progress arrives as <c>image</c> events.
/// </summary>
public static class ImageEndpoints
{
    public static RouteGroupBuilder MapImageEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/images", (IOptions<ImageOptions> options, IImageEngine engine, IPromptTranslator translator, Machines.Machines machines) =>
            new ImageInfo(
                engine.Installed,
                options.Value.ResolvedPython,
                [.. options.Value.ResolvedModels.Select(m =>
                {
                    var unavailable = engine.Unavailable(m);
                    return new ImageModelInfo(
                        m.Id,
                        m.Label,
                        m.DownloadGb,
                        m.MemoryGb,
                        m.License,
                        m.Commercial,
                        m.Gated,
                        engine.Downloaded(m),
                        !m.Gated || engine.TokenFound,
                        machines.CanRun(Machines.Engines.ImageModel(m.Id), null, unavailable is null),
                        unavailable,
                        m.Lettering,
                        m.MinSystemGb,
                        m.TakesLoras,
                        m.MaxReferences,
                        m.TakesStartImage);
                })],
                translator.Configured,
                options.Value.ResolvedFlux3Python))
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
        ImageLoraLibrary loras,
        CoverStyleLibrary styles,
        Machines.Machines machines,
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
        machines.TryNormalize(request.Machine, out var machine, errors);
        var used = new List<ImageLoraUsed>();
        if (request.Loras is { Count: > 0 } chosen && model is not null)
        {
            if (!model.TakesLoras)
            {
                errors["loras"] = [$"{model.Label} takes no LoRAs."];
            }
            else if (chosen.Count > ImageLoraLibrary.MaxPerPicture)
            {
                errors["loras"] = [$"At most {ImageLoraLibrary.MaxPerPicture}."];
            }
            else
            {
                foreach (var choice in chosen.DistinctBy(c => c.Name))
                {
                    if (loras.Get(choice.Name) is not { } lora)
                    {
                        errors["loras"] = [$"There is no LoRA {choice.Name}."];
                    }
                    else if (lora.Model != model.Id)
                    {
                        errors["loras"] = [lora.Model is null
                            ? $"{lora.Label}: choose the model it was made for first."
                            : $"{lora.Label} was made for another model, not {model.Label}."];
                    }
                    else if (choice.Strength is < 0 or > ImageLoraLibrary.MaxStrength)
                    {
                        errors["loras"] = [$"A strength from 0 to {ImageLoraLibrary.MaxStrength}."];
                    }
                    else
                    {
                        used.Add(new ImageLoraUsed(lora.Name, lora.Label, choice.Strength ?? lora.Strength, lora.Trigger));
                    }
                }
            }
        }
        var references = model is null ? [] : References(request.References ?? [], model, run, song, library, images, styles, errors);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }
        if (machines.Lacks(Machines.Engines.ImageModel(model!.Id), machine))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["machine"] = [$"{machines.Find(machine)!.Label} cannot paint with {model.Label}; choose another Mac or Automatic."],
            });
        }
        if (!machines.CanRun(Machines.Engines.ImageModel(model.Id), machine, engine.Unavailable(model) is null))
        {
            return Results.Problem(
                engine.Unavailable(model) ?? $"No Mac here can paint with {model.Label}.",
                statusCode: StatusCodes.Status501NotImplemented);
        }
        // The runners seed MLX's generator, which takes 32 bits.
        var seed = request.Seed ?? Random.Shared.NextInt64(0, (long)uint.MaxValue + 1);
        var image = images.Enqueue($"{run}/{song}", library.TitleOf(run, directory), model!, prompt, seed, machine, used, references);
        return Results.Accepted($"/api/images/{image.Id}", image);
    }

    /// <summary>Each chosen reference's file, checked against what the model can take; problems go into <paramref name="errors"/>.</summary>
    private static List<(string Path, ImageReferenceUsed Used)> References(
        IReadOnlyList<ImageReferenceChoice> chosen,
        ImageModel model,
        string run,
        string song,
        SongLibrary library,
        ImageMaker images,
        CoverStyleLibrary styles,
        Dictionary<string, string[]> errors)
    {
        var resolved = new List<(string Path, ImageReferenceUsed Used)>();
        if (chosen.Count == 0)
        {
            return resolved;
        }
        var starts = chosen.Count(c => c.Role == ReferenceRoles.Start);
        var looked = chosen.Count - starts;
        if (starts > 0 && (!model.TakesStartImage || starts > 1 || looked > 0))
        {
            errors["references"] = [model.TakesStartImage
                ? "One start picture, and nothing else beside it."
                : $"{model.Label} paints over no start picture; give it pictures to look at."];
            return resolved;
        }
        if (looked > 0 && looked > model.MaxReferences)
        {
            errors["references"] = [model.MaxReferences == 0
                ? $"{model.Label} looks at no pictures; give it one start picture instead."
                : $"{model.Label} looks at most at {model.MaxReferences} pictures."];
            return resolved;
        }
        foreach (var choice in chosen)
        {
            var role = choice.Role ?? ReferenceRoles.Style;
            if (!ReferenceRoles.All.Contains(role))
            {
                errors["references"] = [$"A role is one of {string.Join(", ", ReferenceRoles.All)}."];
                return resolved;
            }
            if (role == ReferenceRoles.Start && choice.Strength is < 0.1 or > 0.9)
            {
                errors["references"] = ["The start picture's strength goes from 0.1 to 0.9."];
                return resolved;
            }
            (string? path, string label) = choice.Source switch
            {
                "style" when choice.Id?.Split('/') is [var style, var picture] && long.TryParse(style, out var styleId)
                    => (styles.ImagePath(styleId, picture), styles.Get(styleId)?.Name ?? "Stil"),
                "image" when choice.Id is { } id && images.Get(id) is { Stage: "done" } && File.Exists(images.ImagePath(id))
                    => (images.ImagePath(id), "Kandidat"),
                "cover" => (library.CoverOf(run, song)?.Path, "Cover"),
                "upload" when choice.Id is { } id => (images.UploadPath(id), "Hochgeladen"),
                _ => (null, ""),
            };
            if (path is null || !File.Exists(path))
            {
                errors["references"] = [$"The reference {choice.Source} {choice.Id} is not there."];
                return resolved;
            }
            resolved.Add((path, new ImageReferenceUsed(role, label, role == ReferenceRoles.Start ? choice.Strength ?? 0.5 : null)));
        }
        return resolved;
    }
}
