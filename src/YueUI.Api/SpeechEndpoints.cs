using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using YueUI.Api.Data;
using YueUI.Api.Speech;

namespace YueUI.Api;

/// <summary>
/// The speech lab (<see cref="SpeechLab"/>): record a voice in the browser, have text spoken by local models with it,
/// and compare the takes; or clean a speech recording of noise (<c>/speech/cleanups</c>). Takes are spoken in the background; their progress arrives as <c>speech</c> events.
/// </summary>
public static class SpeechEndpoints
{
    /// <summary>A browser recording of half a minute is well under a megabyte; an uploaded WAV may be larger.</summary>
    public const long MaxRecordingBytes = 50L * 1024 * 1024;

    /// <summary>A recording to clean: an hour of talk as WAV is 600 MB, as the phone's M4A or a video far less.</summary>
    public const long MaxCleanupBytes = 1024L * 1024 * 1024;

    /// <summary>Anything shorter gives the models too little to go on.</summary>
    public const double MinVoiceSeconds = 2;

    public static RouteGroupBuilder MapSpeechEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/speech", (IOptions<SpeechOptions> options, ISpeechEngine engine, SqliteSpeechStore store) =>
            new SpeechInfo(
                engine.Installed,
                options.Value.ResolvedPython,
                engine.CanPrepareVoices,
                [.. options.Value.ResolvedModels.Select(m => Info(m, engine))],
                [.. options.Value.ResolvedCleanModels.Select(m => Info(m, engine))],
                store.Voices(),
                store.Takes()))
            .WithName("GetSpeechLab")
            .Produces<SpeechInfo>(StatusCodes.Status200OK);

        api.MapPost("/speech/voices", AddVoiceAsync)
            // Posted from a plain form; there is no login and no cookie that a forged request could use.
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(MaxRecordingBytes + 64 * 1024))
            .WithFormOptions(multipartBodyLengthLimit: MaxRecordingBytes)
            .WithName("CreateSpeechVoice")
            .Produces<SpeechVoice>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .ProducesProblem(StatusCodes.Status501NotImplemented);
        api.MapGet("/speech/voices/{id}/audio", (string id, SqliteSpeechStore store) =>
            store.Voice(id) is not null && File.Exists(store.VoicePath(id))
                ? Results.File(store.VoicePath(id), "audio/wav", enableRangeProcessing: true)
                : Results.NotFound())
            .WithName("GetSpeechVoiceAudio")
            .ProducesFile("audio/wav")
            .Produces(StatusCodes.Status404NotFound);
        api.MapDelete("/speech/voices/{id}", (string id, SqliteSpeechStore store) =>
            store.RemoveVoice(id) ? Results.NoContent() : Results.NotFound())
            .WithName("DeleteSpeechVoice")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);

        api.MapPost("/speech/takes", AddTakes)
            .WithName("CreateSpeechTakes")
            .Produces<IReadOnlyList<SpeechTake>>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status501NotImplemented);
        api.MapPost("/speech/cleanups", AddCleanupAsync)
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(MaxCleanupBytes + 64 * 1024))
            .WithFormOptions(multipartBodyLengthLimit: MaxCleanupBytes)
            .WithName("CreateSpeechCleanup")
            .Produces<SpeechTake>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .ProducesProblem(StatusCodes.Status501NotImplemented);
        // The recording as it was uploaded (decoded to WAV), to hear before and after.
        api.MapGet("/speech/takes/{id}/source", (string id, SqliteSpeechStore store) =>
            store.Take(id) is { Source: not null } && File.Exists(store.SourcePath(id))
                ? Results.File(store.SourcePath(id), "audio/wav", enableRangeProcessing: true)
                : Results.NotFound())
            .WithName("GetSpeechTakeSource")
            .ProducesFile("audio/wav")
            .Produces(StatusCodes.Status404NotFound);
        // Range requests: Safari plays audio only from a source that answers them.
        api.MapGet("/speech/takes/{id}/audio", (string id, bool? download, SqliteSpeechStore store) =>
            store.Take(id) is { Stage: "done" } take && File.Exists(store.TakePath(id))
                ? Results.File(store.TakePath(id), "audio/wav", download == true ? DownloadName(take) : null, enableRangeProcessing: true)
                : Results.NotFound())
            .WithName("GetSpeechTakeAudio")
            .ProducesFile("audio/wav")
            .Produces(StatusCodes.Status404NotFound);
        api.MapDelete("/speech/takes/{id}", (string id, SqliteSpeechStore store, SpeechLab lab) =>
        {
            if (store.Take(id) is not { } take)
            {
                return Results.NotFound();
            }
            lab.Delete(take);
            return Results.NoContent();
        })
            .WithName("DeleteSpeechTake")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);
        return api;
    }

    private static async Task<IResult> AddVoiceAsync(
        [FromForm] string? label,
        [FromForm] string? transcript,
        IFormFile? file,
        SqliteSpeechStore store,
        ISpeechEngine engine,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(label) || label.Trim().Length > 100)
        {
            errors["label"] = ["A name for the voice, up to 100 characters."];
        }
        if (transcript?.Trim().Length > 1000)
        {
            errors["transcript"] = ["What is said in the recording, up to 1000 characters."];
        }
        if (file is null || file.Length == 0)
        {
            errors["file"] = ["A recording of the voice."];
        }
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }
        if (!engine.CanPrepareVoices)
        {
            return Results.Problem(title: "ffmpeg is not installed; recordings cannot be converted.", statusCode: StatusCodes.Status501NotImplemented);
        }

        var id = Guid.NewGuid().ToString("N");
        var upload = Path.Combine(Path.GetTempPath(), $"yueui-speech-upload-{id}{Extension(file!.FileName)}");
        var target = store.VoicePath(id);
        try
        {
            await using (var stream = File.Create(upload))
            {
                await file.CopyToAsync(stream, cancellationToken);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await engine.PrepareVoiceAsync(upload, target, cancellationToken);
            var seconds = WavInfo.Seconds(target) ?? 0;
            if (seconds < MinVoiceSeconds)
            {
                File.Delete(target);
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["file"] = [$"Only {seconds:0.#} seconds of speech; record at least {MinVoiceSeconds:0} (5 to 15 are best)."],
                });
            }
            var voice = new SpeechVoice(id, label!.Trim(), transcript?.Trim() ?? "", seconds, time.GetUtcNow());
            store.AddVoice(voice);
            return Results.Created($"/api/speech/voices/{id}/audio", voice);
        }
        catch (SpeechException exception)
        {
            File.Delete(target);
            return Results.Problem(title: exception.Message, statusCode: exception.Status);
        }
        finally
        {
            File.Delete(upload);
        }
    }

    private static IResult AddTakes(
        SpeechRequest request,
        SqliteSpeechStore store,
        ISpeechEngine engine,
        SpeechLab lab,
        Machines.Machines machines,
        IOptions<SpeechOptions> options)
    {
        var offered = options.Value.ResolvedModels;
        var errors = request.Validate(offered);
        machines.TryNormalize(request.Machine, out var machine, errors);
        var voice = request.VoiceId is { Length: > 0 } voiceId ? store.Voice(voiceId) : null;
        if (request.VoiceId is { Length: > 0 } && voice is null)
        {
            errors["voiceId"] = ["No such recorded voice."];
        }
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }
        if (!machines.CanRun(Machines.Engines.Speech, machine, engine.Installed))
        {
            return Results.Problem(
                title: $"mlx-audio is not installed ({options.Value.ResolvedPython}); run deploy/install-speech.sh on the Mac.",
                statusCode: StatusCodes.Status501NotImplemented);
        }
        // In the order the models are offered, whatever order they were ticked in: the page compares them side by side.
        var models = offered.Where(m => request.Models!.Contains(m.Id));
        return Results.Accepted("/api/speech", lab.Enqueue(request.Text!.Trim(), voice, models, machine));
    }

    private static SpeechModelInfo Info(SpeechModel model, ISpeechEngine engine) =>
        new(model.Id, model.Label, model.Repo, model.DownloadGb, model.Clones, model.License, model.Note, engine.Downloaded(model));

    /// <summary>A cleaned recording keeps its own name, so it sits beside the original in the downloads.</summary>
    private static string DownloadName(SpeechTake take) => take.Source is { } source
        ? $"{LibraryEndpoints.FileName(Path.GetFileNameWithoutExtension(source), "recording")}-clean.wav"
        : $"{LibraryEndpoints.FileName(take.ModelLabel, "take")}-{take.Id[..8]}.wav";

    private static async Task<IResult> AddCleanupAsync(
        [FromForm] string? model,
        [FromForm] string? machine,
        IFormFile? file,
        SqliteSpeechStore store,
        ISpeechEngine engine,
        SpeechLab lab,
        Machines.Machines machines,
        IOptions<SpeechOptions> options,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        machines.TryNormalize(machine, out var place, errors);
        var chosen = options.Value.ResolvedCleanModels.FirstOrDefault(m => m.Id == model);
        if (chosen is null)
        {
            errors["model"] = ["One of the clean models GET /api/speech lists."];
        }
        if (file is null || file.Length == 0)
        {
            errors["file"] = ["A speech recording."];
        }
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }
        if (!machines.CanRun(Machines.Engines.Speech, place, engine.Installed))
        {
            return Results.Problem(
                title: $"mlx-audio is not installed ({options.Value.ResolvedPython}); run deploy/install-speech.sh on the Mac.",
                statusCode: StatusCodes.Status501NotImplemented);
        }
        if (!engine.CanPrepareVoices)
        {
            return Results.Problem(title: "ffmpeg is not installed; recordings cannot be converted.", statusCode: StatusCodes.Status501NotImplemented);
        }

        var id = Guid.NewGuid().ToString("N");
        var upload = Path.Combine(Path.GetTempPath(), $"yueui-speech-upload-{id}{Extension(file!.FileName)}");
        var target = store.SourcePath(id);
        try
        {
            await using (var stream = File.Create(upload))
            {
                await file.CopyToAsync(stream, cancellationToken);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await engine.PrepareRecordingAsync(upload, target, cancellationToken);
            if ((WavInfo.Seconds(target) ?? 0) < 1)
            {
                File.Delete(target);
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["The recording holds no audio."] });
            }
            var name = Path.GetFileName(file.FileName) is { Length: > 0 and <= 200 } given ? given : "recording";
            return Results.Accepted("/api/speech", lab.EnqueueCleanup(id, name, chosen!, place));
        }
        catch (SpeechException exception)
        {
            File.Delete(target);
            return Results.Problem(title: exception.Message, statusCode: exception.Status);
        }
        catch (OperationCanceledException)
        {
            File.Delete(target);
            throw;
        }
        finally
        {
            File.Delete(upload);
        }
    }

    /// <summary>ffmpeg reads the container from the content, but a known extension saves it guessing.</summary>
    private static string Extension(string fileName) =>
        Path.GetExtension(fileName) is { Length: > 1 and <= 6 } extension && extension[1..].All(char.IsAsciiLetterOrDigit) ? extension : "";
}
