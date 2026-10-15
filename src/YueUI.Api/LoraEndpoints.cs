using Microsoft.AspNetCore.Mvc;
using YueUI.Api.Loras;

namespace YueUI.Api;

/// <summary>The LoRAs a song can be made with (<see cref="LoraLibrary"/>): listing, uploading, deleting.</summary>
public static class LoraEndpoints
{
    public static RouteGroupBuilder MapLoraEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/loras", (LoraLibrary loras) => Results.Ok(new LoraList(loras.Folder, loras.List())))
            .WithName("ListLoras")
            .Produces<LoraList>(StatusCodes.Status200OK);
        api.MapPost("/loras", UploadAsync)
            .DisableAntiforgery()
            .WithFormOptions(multipartBodyLengthLimit: LoraLibrary.MaxUploadBytes)
            .WithMetadata(new RequestSizeLimitAttribute(LoraLibrary.MaxUploadBytes + 64 * 1024))
            .WithName("UploadLora")
            .Produces<LoraInfo>(StatusCodes.Status201Created)
            .ProducesValidationProblem();
        api.MapDelete("/loras/{name}", (string name, LoraLibrary loras) =>
            loras.Delete(name) ? Results.NoContent() : Results.NotFound())
            .WithName("DeleteLora")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);
        return api;
    }

    /// <param name="name">What to call it; the file's own name without <c>.safetensors</c> when empty.</param>
    private static async Task<IResult> UploadAsync(
        IFormFile? file, [FromForm] string? name, LoraLibrary loras, CancellationToken cancellationToken)
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
        await using var content = file.OpenReadStream();
        return await loras.SaveAsync(name, content, cancellationToken) is { } saved
            ? Results.Created($"/api/loras/{Uri.EscapeDataString(saved.Name)}", saved)
            : Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["Not a safetensors file."] });
    }
}
