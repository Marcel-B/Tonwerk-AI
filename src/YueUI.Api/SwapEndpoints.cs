using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;
using YueUI.Api.Data;
using YueUI.Api.Voices;

namespace YueUI.Api;

/// <summary>
/// Uploaded recordings sung with a reference voice, on the voices page: a vocal track (or a whole song, separated
/// first) goes into <see cref="VoiceConverter"/>'s queue, since Seed-VC needs the memory YuE2 needs, and the result
/// is kept beside the upload to compare.
/// </summary>
public static class SwapEndpoints
{
    /// <summary>The same room as a transcription's upload: a six-minute WAV in 24 bit is about 100 MB.</summary>
    public const long MaxUploadBytes = TranscriptionEndpoints.MaxUploadBytes;

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public static RouteGroupBuilder MapSwapEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/swaps", (SqliteSwapStore store) => Results.Ok(store.All()))
            .WithName("ListSwaps")
            .Produces<IReadOnlyList<SwapState>>(StatusCodes.Status200OK);
        api.MapPost("/swaps", AddAsync)
            // Posted from a plain form; there is no login and no cookie that a forged request could use.
            .DisableAntiforgery()
            .WithFormOptions(multipartBodyLengthLimit: MaxUploadBytes)
            .WithMetadata(new RequestSizeLimitAttribute(MaxUploadBytes + 64 * 1024))
            .WithName("CreateSwap")
            .Produces<SwapState>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .ProducesProblem(StatusCodes.Status502BadGateway);
        // Range requests: the page seeks, and Safari plays only from a source that answers them.
        api.MapGet("/swaps/{id}/audio", (string id, bool? download, SqliteSwapStore store) =>
        {
            if (store.Get(id) is not { Stage: "done" } swap || !File.Exists(store.ResultPath(id)))
            {
                return Results.NotFound();
            }
            var name = download == true
                ? $"{LibraryEndpoints.FileName(Path.GetFileNameWithoutExtension(swap.FileName), "upload")}-{LibraryEndpoints.FileName(swap.VoiceLabel, "voice")}.flac"
                : null;
            return Results.File(store.ResultPath(id), "audio/flac", name, enableRangeProcessing: true);
        })
            .WithName("GetSwapAudio")
            .ProducesFile("audio/flac")
            .Produces(StatusCodes.Status404NotFound);
        api.MapGet("/swaps/{id}/source", (string id, SqliteSwapStore store) =>
            store.Get(id) is not null && store.SourcePath(id) is { } path
                ? Results.File(path, ContentTypes.TryGetContentType(path, out var type) ? type : "application/octet-stream", enableRangeProcessing: true)
                : Results.NotFound())
            // The upload as it came, typed by its file name's ending (the usual ones; anything else is octet-stream).
            .WithName("GetSwapSource")
            .ProducesFile("audio/wav", "audio/mpeg", "audio/flac", "audio/mp4", "audio/ogg", "application/octet-stream")
            .Produces(StatusCodes.Status404NotFound);
        api.MapDelete("/swaps/{id}", (string id, SqliteSwapStore store, VoiceConverter converter) =>
        {
            if (store.Get(id) is not { } swap)
            {
                return Results.NotFound();
            }
            converter.DeleteSwap(swap);
            return Results.NoContent();
        })
            .WithName("DeleteSwap")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);
        return api;
    }

    private static async Task<IResult> AddAsync(
        IFormFile? file,
        [FromForm] string? voiceId,
        [FromForm] int? semiToneShift,
        [FromForm] double? strength,
        [FromForm] int? diffusionSteps,
        [FromForm] bool? separate,
        [FromForm] bool? keepReverb,
        VoiceEngine voices,
        VoiceConverter converter,
        SqliteSwapStore store,
        IOptions<VoiceOptions> options,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var whole = separate == true;
        if (!(whole ? settings.ConversionConfigured : settings.VoicesConfigured))
        {
            return Results.Problem(
                title: whole
                    ? "A whole song needs Seed-VC and the separator (deploy/setup-mac.sh --voices) or ChangeMyVoice and StemMyWav."
                    : "Voices are not set up: neither Seed-VC (deploy/setup-mac.sh --voices) nor ChangeMyVoice (Voice:BaseUrl and its key).",
                statusCode: StatusCodes.Status501NotImplemented);
        }
        var request = new VersionRequest(voiceId, semiToneShift ?? 0, strength ?? 0.7, diffusionSteps ?? 50, keepReverb ?? true);
        var errors = request.Validate();
        if (file is null || file.Length == 0)
        {
            errors["file"] = ["A recording: WAV, MP3, FLAC, M4A or OGG."];
        }
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }
        try
        {
            // Asked for now rather than when it runs: a wrong id is refused at once, and the label is kept.
            var voice = (await voices.ListVoicesAsync(cancellationToken)).FirstOrDefault(v => v.Id == request.VoiceId);
            if (voice is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["voiceId"] = ["No such reference voice."] });
            }
            var now = time.GetUtcNow();
            var swap = new SwapState
            {
                Id = Guid.NewGuid().ToString("N"),
                FileName = Path.GetFileName(file!.FileName) is { Length: > 0 and <= 200 } name ? name : "upload",
                VoiceId = voice.Id,
                VoiceLabel = voice.Label,
                SemiToneShift = request.SemiToneShift,
                Strength = request.Strength,
                DiffusionSteps = request.DiffusionSteps,
                Separate = whole,
                KeepReverb = whole && request.KeepReverb,
                StemModel = whole ? settings.StemModel : null,
                CreatedAt = now,
                UpdatedAt = now,
            };
            // The ending only tells ffmpeg and the browser what it is; anything odd becomes none.
            var extension = Path.GetExtension(swap.FileName).ToLowerInvariant();
            if (extension.Length > 6 || !extension.Skip(1).All(char.IsAsciiLetterOrDigit))
            {
                extension = "";
            }
            Directory.CreateDirectory(store.Folder(swap.Id));
            await using (var target = File.Create(Path.Combine(store.Folder(swap.Id), $"source{extension}")))
            {
                await file.CopyToAsync(target, cancellationToken);
            }
            return Results.Accepted($"/api/swaps/{swap.Id}/audio", converter.EnqueueSwap(swap));
        }
        catch (VoiceServiceException exception)
        {
            return Results.Problem(title: exception.Message, statusCode: (int)exception.Status);
        }
    }
}
