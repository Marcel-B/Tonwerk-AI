using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using YueUI.Api.Images;

namespace YueUI.Api;

/// <summary>
/// Cover styles (<see cref="CoverStyleLibrary"/>): a look under a name with its reference pictures, and pictures handed
/// in for one cover only (<c>/image-references</c>), which a painting then takes along.
/// </summary>
public static class CoverStyleEndpoints
{
    public static RouteGroupBuilder MapCoverStyleEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/cover-styles", (CoverStyleLibrary styles) => Results.Ok(styles.List()))
            .WithName("ListCoverStyles")
            .Produces<IReadOnlyList<CoverStyle>>(StatusCodes.Status200OK);
        api.MapPost("/cover-styles", (CoverStyleRequest request, CoverStyleLibrary styles, IOptions<ImageOptions> options) =>
        {
            var name = request.Name?.Trim() ?? "";
            if (Invalid(request, name.Length == 0, options.Value) is { } problem)
            {
                return problem;
            }
            return styles.Create(name, request.Prompt, request.Model, request.Loras) is { } style
                ? Results.Created($"/api/cover-styles/{style.Id}", style)
                : Taken();
        })
            .WithName("CreateCoverStyle")
            .Produces<CoverStyle>(StatusCodes.Status201Created)
            .ProducesValidationProblem();
        api.MapPut("/cover-styles/{id:long}", (long id, CoverStyleRequest request, CoverStyleLibrary styles, IOptions<ImageOptions> options) =>
        {
            if (Invalid(request, request.Name is not null && request.Name.Trim().Length == 0, options.Value) is { } problem)
            {
                return problem;
            }
            var style = styles.Update(id, request with { Name = request.Name?.Trim() }, out var taken);
            return style is null ? Results.NotFound() : taken ? Taken() : Results.Ok(style);
        })
            .WithName("UpdateCoverStyle")
            .Produces<CoverStyle>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesValidationProblem();
        api.MapDelete("/cover-styles/{id:long}", (long id, CoverStyleLibrary styles) =>
            styles.Delete(id) ? Results.NoContent() : Results.NotFound())
            .WithName("DeleteCoverStyle")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);

        api.MapPost("/cover-styles/{id:long}/images", AddImageAsync)
            .DisableAntiforgery()
            .WithFormOptions(multipartBodyLengthLimit: ReferencePictures.MaxBytes)
            .WithMetadata(new RequestSizeLimitAttribute(ReferencePictures.MaxBytes + 64 * 1024))
            .WithName("AddCoverStyleImage")
            .Produces<CoverStyle>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesValidationProblem();
        api.MapGet("/cover-styles/{id:long}/images/{imageId}", (long id, string imageId, CoverStyleLibrary styles) =>
            styles.ImagePath(id, imageId) is { } path
                ? Results.File(path, ReferencePictures.ContentType(path))
                : Results.NotFound())
            .WithName("GetCoverStyleImage")
            .ProducesFile("image/jpeg", "image/png", "image/webp")
            .Produces(StatusCodes.Status404NotFound);
        api.MapPut("/cover-styles/{id:long}/images/{imageId}", (long id, string imageId, CoverStyleImageUpdate update, CoverStyleLibrary styles) =>
        {
            if (!ReferenceRoles.All.Contains(update.Role ?? ""))
            {
                return RoleProblem();
            }
            return styles.SetRole(id, imageId, update.Role!) is { } style ? Results.Ok(style) : Results.NotFound();
        })
            .WithName("UpdateCoverStyleImage")
            .Produces<CoverStyle>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesValidationProblem();
        api.MapDelete("/cover-styles/{id:long}/images/{imageId}", (long id, string imageId, CoverStyleLibrary styles) =>
            styles.RemoveImage(id, imageId) is { } style ? Results.Ok(style) : Results.NotFound())
            .WithName("DeleteCoverStyleImage")
            .Produces<CoverStyle>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        api.MapPost("/image-references", UploadReferenceAsync)
            .DisableAntiforgery()
            .WithFormOptions(multipartBodyLengthLimit: ReferencePictures.MaxBytes)
            .WithMetadata(new RequestSizeLimitAttribute(ReferencePictures.MaxBytes + 64 * 1024))
            .WithName("UploadImageReference")
            .Produces<ImageReferenceUpload>(StatusCodes.Status201Created)
            .ProducesValidationProblem();
        return api;
    }

    /// <summary>A picture for a style: a file of one's own, or an earlier candidate (<paramref name="candidate"/>).</summary>
    private static async Task<IResult> AddImageAsync(
        long id,
        IFormFile? file,
        [FromForm] string? candidate,
        [FromForm] string? role,
        CoverStyleLibrary styles,
        ImageMaker images,
        CancellationToken cancellationToken)
    {
        role = string.IsNullOrWhiteSpace(role) ? ReferenceRoles.Style : role.Trim();
        if (!ReferenceRoles.All.Contains(role))
        {
            return RoleProblem();
        }
        if (styles.Get(id) is not { } style)
        {
            return Results.NotFound();
        }
        if (style.Images.Count >= CoverStyleLibrary.MaxImages)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["file"] = [$"A style keeps at most {CoverStyleLibrary.MaxImages} pictures."],
            });
        }
        (byte[] Bytes, string Extension)? picture = null;
        if (file is not null)
        {
            picture = await ReferencePictures.ReadAsync(file, cancellationToken);
        }
        else if (!string.IsNullOrWhiteSpace(candidate)
            && images.Get(candidate) is { Stage: "done" }
            && File.Exists(images.ImagePath(candidate)))
        {
            picture = (await File.ReadAllBytesAsync(images.ImagePath(candidate), cancellationToken), ".jpg");
        }
        if (picture is not { } chosen)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["file"] = [$"A JPEG, PNG or WebP picture up to {ReferencePictures.MaxBytes >> 20} MB, or a finished candidate."],
            });
        }
        return await styles.AddImageAsync(id, chosen.Bytes, chosen.Extension, role, cancellationToken) is { } changed
            ? Results.Created($"/api/cover-styles/{id}", changed)
            : Results.NotFound();
    }

    private static async Task<IResult> UploadReferenceAsync(IFormFile? file, ImageMaker images, CancellationToken cancellationToken)
    {
        if (file is null || await ReferencePictures.ReadAsync(file, cancellationToken) is not { } picture)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["file"] = [$"A JPEG, PNG or WebP picture up to {ReferencePictures.MaxBytes >> 20} MB."],
            });
        }
        var id = await images.UploadAsync(picture.Bytes, picture.Extension, cancellationToken);
        return Results.Created($"/api/image-references/{id}", new ImageReferenceUpload(id));
    }

    private static IResult? Invalid(CoverStyleRequest request, bool nameMissing, ImageOptions options)
    {
        var errors = new Dictionary<string, string[]>();
        if (nameMissing || request.Name?.Trim().Length > CoverStyleLibrary.MaxNameLength)
        {
            errors["name"] = [$"A name of up to {CoverStyleLibrary.MaxNameLength} characters."];
        }
        if (request.Prompt?.Trim().Length > CoverStyleLibrary.MaxPromptLength)
        {
            errors["prompt"] = [$"At most {CoverStyleLibrary.MaxPromptLength} characters."];
        }
        if (!string.IsNullOrWhiteSpace(request.Model) && options.ResolvedModels.All(m => m.Id != request.Model.Trim()))
        {
            errors["model"] = [$"One of {string.Join(", ", options.ResolvedModels.Select(m => m.Id))}."];
        }
        if (request.Loras is { } loras
            && (loras.Count > ImageLoraLibrary.MaxPerPicture || loras.Any(l => l.Strength is < 0 or > ImageLoraLibrary.MaxStrength)))
        {
            errors["loras"] = [$"At most {ImageLoraLibrary.MaxPerPicture}, each at a strength from 0 to {ImageLoraLibrary.MaxStrength}."];
        }
        return errors.Count > 0 ? Results.ValidationProblem(errors) : null;
    }

    private static IResult Taken() =>
        Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["A style of that name exists."] });

    private static IResult RoleProblem() =>
        Results.ValidationProblem(new Dictionary<string, string[]> { ["role"] = [$"One of {string.Join(", ", ReferenceRoles.All)}."] });
}
