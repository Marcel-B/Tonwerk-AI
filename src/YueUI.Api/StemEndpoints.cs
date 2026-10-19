using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using YueUI.Api.Data;
using YueUI.Api.Library;
using YueUI.Api.Voices;

namespace YueUI.Api;

/// <summary>
/// Songs split into their stems by StemMyWav, for the voices page: asked for per song or for an uploaded file, made
/// one at a time in <see cref="VoiceConverter"/>'s queue (the separation needs the memory YuE2 and Seed-VC need), kept
/// per song.
/// </summary>
public static class StemEndpoints
{
    /// <summary>The same room as a voice swap's upload: a six-minute WAV in 24 bit is about 100 MB.</summary>
    public const long MaxUploadBytes = SwapEndpoints.MaxUploadBytes;

    public static RouteGroupBuilder MapStemEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/stems/models", ListModelsAsync)
            .WithName("ListStemModels")
            .Produces<List<StemModel>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status501NotImplemented);
        api.MapGet("/stems", (SqliteStemStore store) => Results.Ok(store.All()))
            .WithName("ListStemSets")
            .Produces<IReadOnlyList<StemSetState>>(StatusCodes.Status200OK);
        api.MapPost("/songs/{run}/{song}/stems", Separate)
            .WithName("CreateStemSet")
            .Produces<StemSetState>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status501NotImplemented);
        api.MapPost("/stems", UploadAsync)
            // Posted from a plain form; there is no login and no cookie that a forged request could use.
            .DisableAntiforgery()
            .WithFormOptions(multipartBodyLengthLimit: MaxUploadBytes)
            .WithMetadata(new RequestSizeLimitAttribute(MaxUploadBytes + 64 * 1024))
            .WithName("CreateStemSetFromUpload")
            .Produces<StemSetState>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status501NotImplemented);
        // By the set alone, which is all an upload has; the song's routes below stay for pages loaded before.
        api.MapGet("/stems/{id}/{name}", (string id, string name, bool? download, SqliteStemStore store, SongLibrary library) =>
            store.Get(id) is { } set ? StemAudio(set, name, download == true, store, library) : Results.NotFound())
            .WithName("GetStemFile")
            .ProducesFile("audio/flac", "audio/wav")
            .Produces(StatusCodes.Status404NotFound);
        api.MapDelete("/stems/{id}", (string id, SqliteStemStore store, VoiceConverter converter) =>
        {
            if (store.Get(id) is not { } set)
            {
                return Results.NotFound();
            }
            converter.DeleteStems(set);
            return Results.NoContent();
        })
            .WithName("DeleteStemSet")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);
        // Range requests, as for the song itself: the page seeks and Safari plays only from a source that answers them.
        api.MapGet("/songs/{run}/{song}/stems/{id}/{name}", (string run, string song, string id, string name, bool? download, SqliteStemStore store, SongLibrary library) =>
            store.Get(id) is { } set && set.SongId == $"{run}/{song}" ? StemAudio(set, name, download == true, store, library) : Results.NotFound())
            .WithName("GetStemFileForSong")
            .ProducesFile("audio/flac", "audio/wav")
            .Produces(StatusCodes.Status404NotFound);
        api.MapDelete("/songs/{run}/{song}/stems/{id}", (string run, string song, string id, SqliteStemStore store, VoiceConverter converter) =>
        {
            if (store.Get(id) is not { } set || set.SongId != $"{run}/{song}")
            {
                return Results.NotFound();
            }
            converter.DeleteStems(set);
            return Results.NoContent();
        })
            .WithName("DeleteStemSetForSong")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);
        return api;
    }

    /// <summary>
    /// StemMyWav's catalog with this server's model marked as the default; only that model where the service lists
    /// none (its Mac API may not answer <c>api/models</c>, only its gateway surely does).
    /// </summary>
    private static async Task<IResult> ListModelsAsync(StemSeparator stems, IOptions<VoiceOptions> options, CancellationToken cancellationToken)
    {
        if (!options.Value.StemsConfigured)
        {
            return NotConfigured();
        }
        var configured = options.Value.StemModel;
        var listed = await stems.ListModelsAsync(cancellationToken) ?? [];
        List<StemModel> models = [.. listed.Where(m => !string.IsNullOrWhiteSpace(m.Id)).Select(m => m with { IsDefault = m.Id == configured })];
        if (!models.Any(m => m.IsDefault))
        {
            models.Insert(0, new StemModel(configured, configured, IsDefault: true));
        }
        return Results.Ok(models);
    }

    private static IResult Separate(
        string run,
        string song,
        StemRequest? request,
        SongLibrary library,
        VoiceConverter converter,
        Machines.Machines machines,
        IOptions<VoiceOptions> options)
    {
        if (!options.Value.StemsConfigured)
        {
            return NotConfigured();
        }
        request ??= new StemRequest();
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
        var set = converter.EnqueueStems($"{run}/{song}", library.TitleOf(run, directory), request.Model ?? options.Value.StemModel, request.Dereverb, machine);
        return Results.Accepted($"/api/stems", set);
    }

    /// <summary>An uploaded recording, queued to be split like a song; it waits in the set's folder until then.</summary>
    private static async Task<IResult> UploadAsync(
        IFormFile? file,
        [FromForm] string? model,
        [FromForm] bool? dereverb,
        [FromForm] string? machine,
        VoiceConverter converter,
        Machines.Machines machines,
        SqliteStemStore store,
        IOptions<VoiceOptions> options,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (!options.Value.StemsConfigured)
        {
            return NotConfigured();
        }
        var request = new StemRequest(string.IsNullOrEmpty(model) ? null : model, dereverb ?? false);
        var errors = request.Validate();
        machines.TryNormalize(machine, out var place, errors);
        if (file is null || file.Length == 0)
        {
            errors["file"] = ["A recording: WAV, MP3, FLAC, M4A or OGG."];
        }
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }
        var fileName = Path.GetFileName(file!.FileName);
        var title = Path.GetFileNameWithoutExtension(fileName).Trim();
        var now = time.GetUtcNow();
        var set = new StemSetState
        {
            Id = Guid.NewGuid().ToString("N"),
            SongId = "",
            Title = title is { Length: > 0 and <= 200 } ? title : "upload",
            Model = request.Model ?? options.Value.StemModel,
            Dereverb = request.Dereverb,
            Machine = place,
            CreatedAt = now,
            UpdatedAt = now,
        };
        // The ending only tells ffmpeg what it is; anything odd becomes none.
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (extension.Length > 6 || !extension.Skip(1).All(char.IsAsciiLetterOrDigit))
        {
            extension = "";
        }
        Directory.CreateDirectory(store.Folder(set.Id));
        await using (var target = File.Create(Path.Combine(store.Folder(set.Id), $"source{extension}")))
        {
            await file.CopyToAsync(target, cancellationToken);
        }
        return Results.Accepted("/api/stems", converter.EnqueueStems(set));
    }

    private static IResult StemAudio(StemSetState set, string name, bool download, SqliteStemStore store, SongLibrary library)
    {
        if (set.Stage != "done" || store.FilePath(set, name) is not { } path || !File.Exists(path))
        {
            return Results.NotFound();
        }
        var extension = Path.GetExtension(path);
        var type = extension.Equals(".flac", StringComparison.OrdinalIgnoreCase) ? "audio/flac" : "audio/wav";
        var fileName = download ? $"{DownloadPrefix(set, library)}-{LibraryEndpoints.FileName(name, "stem")}{extension}" : null;
        return Results.File(path, type, fileName, enableRangeProcessing: true);
    }

    /// <summary>
    /// What a stem's download (or its MIDI file) is named before the stem: the run's title and the song, or the
    /// upload's name.
    /// </summary>
    internal static string DownloadPrefix(StemSetState set, SongLibrary library) =>
        set.Upload
            ? LibraryEndpoints.FileName(set.Title, "upload")
            : library.SongDirectory(set.Run, set.Song) is { } directory
                ? $"{LibraryEndpoints.FileName(library.TitleOf(set.Run, directory), set.Run)}-{set.Song}"
                : $"{LibraryEndpoints.FileName(set.Title, set.Run)}-{set.Song}";

    private static IResult NotConfigured() =>
        Results.Problem(title: "No separator is installed (deploy/setup-mac.sh --voices) and no stem service configured (Voice:StemsBaseUrl and its key).", statusCode: StatusCodes.Status501NotImplemented);
}
