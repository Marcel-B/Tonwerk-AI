using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using YueUI.Api.Data;
using YueUI.Api.Export;
using YueUI.Api.Library;
using YueUI.Api.Share;
using YueUI.Api.Voices;

namespace YueUI.Api;

/// <summary>
/// Reference voices (this server's own where Seed-VC is installed, else ChangeMyVoice's) and songs sung with them
/// (<see cref="VoiceConverter"/>). A key for ChangeMyVoice stays in this server's configuration; the browser only learns ids.
/// </summary>
public static class VoiceEndpoints
{
    /// <summary>A reference voice is a few seconds of singing; ChangeMyVoice keeps only 25 anyway (the first, or from the chosen start).</summary>
    public const long MaxVoiceBytes = 64L * 1024 * 1024;

    public static RouteGroupBuilder MapVoiceEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/voice", (IOptions<VoiceOptions> options) =>
            new VoiceInfo(options.Value.VoicesConfigured, options.Value.ConversionConfigured, options.Value.StemsConfigured))
            .WithName("GetVoiceInfo")
            .Produces<VoiceInfo>(StatusCodes.Status200OK);
        api.MapGet("/voices", (VoiceEngine voices, CancellationToken cancellationToken) =>
            Call(async () => Results.Ok(await voices.ListVoicesAsync(cancellationToken))))
            .WithName("ListVoices")
            .Produces<IReadOnlyList<ReferenceVoice>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .ProducesProblem(StatusCodes.Status502BadGateway);
        api.MapPost("/voices", AddVoiceAsync)
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(MaxVoiceBytes + 64 * 1024))
            .WithFormOptions(multipartBodyLengthLimit: MaxVoiceBytes)
            .WithName("CreateVoice")
            .Produces<ReferenceVoice>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .ProducesProblem(StatusCodes.Status502BadGateway);
        api.MapGet("/voices/{id}/audio", VoiceAudioAsync)
            .WithName("GetVoiceAudio")
            .ProducesFile("audio/wav")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .ProducesProblem(StatusCodes.Status502BadGateway);
        api.MapDelete("/voices/{id}", (string id, VoiceEngine voices, CancellationToken cancellationToken) =>
            Call(async () =>
            {
                await voices.DeleteVoiceAsync(id, cancellationToken);
                return Results.NoContent();
            }))
            .WithName("DeleteVoice")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        api.MapPost("/songs/{run}/{song}/versions", AddVersionAsync)
            .WithName("CreateVersion")
            .Produces<VersionState>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .ProducesProblem(StatusCodes.Status502BadGateway);
        // Range requests, as for the song itself: the player seeks and starts before the whole FLAC is there.
        api.MapGet("/songs/{run}/{song}/versions/{id}/audio", (string run, string song, string id, bool? download, SqliteVersionStore store, SongLibrary library, TaggedFiles tagged) =>
            VersionAudio(run, song, id, download == true, store, library, tagged))
            .WithName("GetVersionAudio")
            .ProducesFile("audio/flac")
            .Produces(StatusCodes.Status404NotFound);
        // The player's copy, as for the song itself (LibraryEndpoints.StreamAsync).
        api.MapGet("/songs/{run}/{song}/versions/{id}/stream", VersionStreamAsync)
            .WithName("GetVersionStream")
            .ProducesFile("audio/mp4", "audio/flac")
            .Produces(StatusCodes.Status404NotFound);
        api.MapDelete("/songs/{run}/{song}/versions/{id}", (string run, string song, string id, SqliteVersionStore store, VoiceConverter converter) =>
        {
            if (store.Get(id) is not { } version || version.SongId != $"{run}/{song}")
            {
                return Results.NotFound();
            }
            converter.Delete(version);
            return Results.NoContent();
        })
            .WithName("DeleteVersion")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);
        return api;
    }

    private static async Task<IResult> AddVoiceAsync(
        [FromForm] string? label,
        IFormFile? file,
        [FromForm] double? startSeconds,
        [FromForm] double? endSeconds,
        [FromForm] string? singingVoice,
        VoiceEngine voices,
        SqliteSingingStore singing,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        // A voice of one's own from the singing page: its phrase (3 to 12 s of the recording, 24 kHz) is a reference
        // Seed-VC takes as it is, so it is copied over like an upload rather than recorded twice.
        var sung = singingVoice is null ? null : singing.Voices().FirstOrDefault(v => v.Id == singingVoice);
        var sungAudio = sung is null ? null : singing.VoiceAudioPath(sung.Id);
        if (singingVoice is not null)
        {
            if (sung is null || !File.Exists(sungAudio))
            {
                errors["singingVoice"] = ["An own voice of the singing page."];
            }
            if (file is not null)
            {
                errors["file"] = ["Either a recording or a voice of the singing page, not both."];
            }
            label = string.IsNullOrWhiteSpace(label) ? sung?.Label : label;
        }
        else if (file is null || file.Length == 0)
        {
            errors["file"] = ["A recording of the voice: WAV, MP3, FLAC, M4A or OGG."];
        }
        if (string.IsNullOrWhiteSpace(label) || label.Trim().Length > 100)
        {
            errors["label"] = ["A name for the voice, up to 100 characters."];
        }
        // Checked here too so the form hears it in its own words; ChangeMyVoice refuses the same.
        if (startSeconds is { } start && (!double.IsFinite(start) || start < 0))
        {
            errors["startSeconds"] = ["The start, in seconds from 0."];
        }
        if (endSeconds is { } end && (!double.IsFinite(end) || end <= (startSeconds ?? 0)))
        {
            errors["endSeconds"] = ["The end, in seconds after the start."];
        }
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }
        return await Call(async () =>
        {
            await using var audio = file?.OpenReadStream() ?? File.OpenRead(sungAudio!);
            var fileName = file?.FileName ?? $"{sung!.Id}.wav";
            var voice = await voices.AddVoiceAsync(label!.Trim(), audio, fileName, startSeconds, endSeconds, cancellationToken);
            return Results.Created($"/api/voices/{voice.Id}", voice);
        });
    }

    /// <summary>Passed through as it arrives, to listen to the voice again.</summary>
    private static async Task<IResult> VoiceAudioAsync(string id, VoiceEngine voices, CancellationToken cancellationToken)
    {
        try
        {
            // Buffered rather than streamed through: Safari plays audio only from a source that answers range
            // requests, and a reference voice is at most 25 seconds, a few megabytes.
            var audio = await voices.VoiceAudioAsync(id, cancellationToken);
            return Results.File(audio.Audio, audio.ContentType, enableRangeProcessing: true);
        }
        catch (VoiceServiceException exception)
        {
            return Problem(exception);
        }
    }

    private static async Task<IResult> AddVersionAsync(
        string run,
        string song,
        VersionRequest request,
        SongLibrary library,
        VoiceEngine voices,
        VoiceConverter converter,
        Machines.Machines machines,
        IOptions<VoiceOptions> options,
        CancellationToken cancellationToken)
    {
        if (!options.Value.ConversionConfigured)
        {
            return Results.Problem(title: "Voices are not set up: neither Seed-VC and the separator (deploy/setup-mac.sh --voices) nor ChangeMyVoice and StemMyWav (Voice:BaseUrl, Voice:StemsBaseUrl and their keys).", statusCode: StatusCodes.Status501NotImplemented);
        }
        var errors = request.Validate();
        machines.TryNormalize(request.Machine, out var machine, errors);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }
        if (library.SongDirectory(run, song) is not { } directory || !File.Exists(Path.Combine(directory, "audio.flac")))
        {
            return Results.NotFound();
        }
        return await Call(async () =>
        {
            // Asked for here rather than when the version runs: a wrong id is refused now, and the label is kept.
            var voice = (await voices.ListVoicesAsync(cancellationToken)).FirstOrDefault(v => v.Id == request.VoiceId);
            if (voice is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["voiceId"] = ["No such reference voice."] });
            }
            var version = converter.Enqueue($"{run}/{song}", library.TitleOf(run, directory), voice, request, machine);
            return Results.Accepted($"/api/songs/{run}/{song}/versions/{version.Id}/audio", version);
        });
    }

    private static async Task<IResult> VersionStreamAsync(
        string run, string song, string id, SqliteVersionStore store, SongLibrary library, StreamCopies streams, HttpContext context)
    {
        if (store.Get(id) is not { Stage: "done" } version || version.SongId != $"{run}/{song}" || !File.Exists(store.FilePath(id))
            || library.SongDirectory(run, song) is not { } directory)
        {
            return Results.NotFound();
        }
        return await streams.VersionAsync(id, StreamCopies.TagsFor(library, run, song, directory, version.VoiceLabel)) is { } copy
            ? LibraryEndpoints.StreamFile(context, copy, "audio/mp4")
            : LibraryEndpoints.StreamFile(context, store.FilePath(id), "audio/flac");
    }

    /// <summary>A download is a tagged copy with the song's cover and lyrics, like the song's own (LibraryEndpoints).</summary>
    private static IResult VersionAudio(
        string run, string song, string id, bool download, SqliteVersionStore store, SongLibrary library, TaggedFiles tagged)
    {
        if (store.Get(id) is not { Stage: "done" } version || version.SongId != $"{run}/{song}" || !File.Exists(store.FilePath(id)))
        {
            return Results.NotFound();
        }
        if (download && library.SongDirectory(run, song) is { } directory)
        {
            return tagged.Download(
                store.FilePath(id),
                StreamCopies.TagsFor(library, run, song, directory, version.VoiceLabel).Build(),
                "audio/flac",
                $"{LibraryEndpoints.FileName(library.TitleOf(run, directory), run)}-{song}-{LibraryEndpoints.FileName(version.VoiceLabel, "voice")}.flac");
        }
        return Results.File(store.FilePath(id), "audio/flac", enableRangeProcessing: true);
    }

    private static async Task<IResult> Call(Func<Task<IResult>> call)
    {
        try
        {
            return await call();
        }
        catch (VoiceServiceException exception)
        {
            return Problem(exception);
        }
    }

    private static IResult Problem(VoiceServiceException exception) =>
        Results.Problem(title: exception.Message, statusCode: (int)exception.Status);
}
