using Microsoft.AspNetCore.Mvc;
using YueUI.Api.Images;
using YueUI.Api.Loras;

namespace YueUI.Api;

/// <summary>
/// The image models' LoRAs (<see cref="ImageLoraLibrary"/>): listing, uploading, fetching one of Krea's from Hugging
/// Face, setting the model it fits and its trigger words, deleting.
/// </summary>
public static class ImageLoraEndpoints
{
    public static RouteGroupBuilder MapImageLoraEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/image-loras", (ImageLoraLibrary loras) => Results.Ok(loras.List()))
            .WithName("ListImageLoras")
            .Produces<ImageLoraList>(StatusCodes.Status200OK);
        api.MapPost("/image-loras", UploadAsync)
            .DisableAntiforgery()
            .WithFormOptions(multipartBodyLengthLimit: ImageLoraLibrary.MaxUploadBytes)
            .WithMetadata(new RequestSizeLimitAttribute(ImageLoraLibrary.MaxUploadBytes + 64 * 1024))
            .WithName("UploadImageLora")
            .Produces<ImageLoraInfo>(StatusCodes.Status201Created)
            .ProducesValidationProblem();
        api.MapPost("/image-loras/suggestions/{name}", async (string name, ImageLoraLibrary loras) =>
        {
            if (ImageLoraLibrary.Suggestions.FirstOrDefault(s => s.Name == name) is not { } suggestion)
            {
                return Results.NotFound();
            }
            try
            {
                return await loras.FetchAsync(suggestion) is { } fetched
                    ? Results.Created($"/api/image-loras/{Uri.EscapeDataString(fetched.Name)}", fetched)
                    : Results.Accepted();
            }
            catch (ImageException exception)
            {
                return Results.Problem(exception.Message, statusCode: exception.Status);
            }
        })
            .WithName("FetchImageLora")
            .Produces<ImageLoraInfo>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status502BadGateway);
        api.MapPut("/image-loras/{name}", (string name, ImageLoraUpdate update, ImageLoraLibrary loras, Microsoft.Extensions.Options.IOptions<ImageOptions> options) =>
        {
            if (Invalid(update, options.Value) is { } problem)
            {
                return problem;
            }
            return loras.Update(name, update) is { } changed ? Results.Ok(changed) : Results.NotFound();
        })
            .WithName("UpdateImageLora")
            .Produces<ImageLoraInfo>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesValidationProblem();
        api.MapDelete("/image-loras/{name}", (string name, ImageLoraLibrary loras) =>
            loras.Delete(name) ? Results.NoContent() : Results.NotFound())
            .WithName("DeleteImageLora")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);
        return api;
    }

    /// <param name="name">What to call it; the file's own name without <c>.safetensors</c> when empty.</param>
    private static async Task<IResult> UploadAsync(
        IFormFile? file,
        [FromForm] string? name,
        [FromForm] string? label,
        [FromForm] string? model,
        [FromForm] string? trigger,
        ImageLoraLibrary loras,
        Microsoft.Extensions.Options.IOptions<ImageOptions> options,
        CancellationToken cancellationToken)
    {
        if (file is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["A .safetensors file is required."] });
        }
        name = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(file.FileName) : name.Trim();
        if (!LoraLibrary.ValidName(name))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["name"] = ["Letters, digits, spaces, dots, dashes and underscores, at most 100."],
            });
        }
        var settings = new ImageLoraUpdate(label, model, trigger, null);
        if (Invalid(settings, options.Value) is { } problem)
        {
            return problem;
        }
        await using var content = file.OpenReadStream();
        return await loras.SaveAsync(name, content, settings, null, cancellationToken) is { } saved
            ? Results.Created($"/api/image-loras/{Uri.EscapeDataString(saved.Name)}", saved)
            : Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["Not a safetensors file."] });
    }

    private static IResult? Invalid(ImageLoraUpdate update, ImageOptions options)
    {
        var errors = new Dictionary<string, string[]>();
        var takers = options.ResolvedModels.Where(m => m.TakesLoras).ToList();
        if (!string.IsNullOrWhiteSpace(update.Model) && !takers.Any(m => m.Id == update.Model.Trim()))
        {
            errors["model"] = [$"One of {string.Join(", ", takers.Select(m => m.Id))}."];
        }
        if (update.Strength is < 0 or > ImageLoraLibrary.MaxStrength)
        {
            errors["strength"] = [$"From 0 to {ImageLoraLibrary.MaxStrength}."];
        }
        if (update.Label is { Length: > 100 } || update.Trigger is { Length: > 300 })
        {
            errors["label"] = ["A name of at most 100 and trigger words of at most 300 characters."];
        }
        return errors.Count > 0 ? Results.ValidationProblem(errors) : null;
    }
}
