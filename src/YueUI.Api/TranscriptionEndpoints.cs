using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using YueUI.Api.Library;
using YueUI.Api.Queue;
using YueUI.Api.Worker;

namespace YueUI.Api;

/// <summary>
/// Recordings to melody scores with SheetSage2: upload and follow a transcription (its progress comes with the
/// worker's event stream; one waits in the <see cref="JobQueue"/> while another runs or another model holds the
/// memory), and the finished ones on disk with their score and all their files, or deleted.
/// </summary>
public static partial class TranscriptionEndpoints
{
    /// <summary>A six-minute WAV in 24 bit is about 100 MB; leave room for longer recordings.</summary>
    public const long MaxUploadBytes = 300L * 1024 * 1024;

    public static RouteGroupBuilder MapTranscriptionEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/transcriptions", (TranscriptionLibrary library) => library.List())
            .WithName("ListTranscriptions")
            .Produces<TranscriptionList>(StatusCodes.Status200OK);
        api.MapPost("/transcriptions", Transcribe)
            // A recording is posted from a plain form; there is no login and no cookie that a forged request could use.
            .DisableAntiforgery()
            .WithFormOptions(multipartBodyLengthLimit: MaxUploadBytes)
            .WithMetadata(new RequestSizeLimitAttribute(MaxUploadBytes))
            .WithName("CreateTranscription")
            .Produces<TranscriptionState>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        api.MapPost("/transcriptions/{id}/cancel", async (string id, WorkerHost host, JobQueue queue, CancellationToken cancellationToken) =>
            queue.Cancel(id) || await host.CancelTranscriptionAsync(id, cancellationToken)
                ? Results.Accepted()
                : Results.Problem(title: "Not running", statusCode: StatusCodes.Status404NotFound))
            .WithName("CancelTranscription")
            .Produces(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound);
        api.MapGet("/transcriptions/{id}/score", (string id, bool? download, TranscriptionLibrary library) =>
            library.Directory(id) is { } directory && File.Exists(Path.Combine(directory, "score.abc"))
                ? Results.File(Path.Combine(directory, "score.abc"), "text/vnd.abc; charset=utf-8", download == true ? $"{id}.abc" : null)
                : Results.NotFound())
            .WithName("GetTranscriptionScore")
            .ProducesFile("text/vnd.abc")
            .Produces(StatusCodes.Status404NotFound);
        // score.abc plus SheetSage2's MIDI parts and timed annotations; the recording's own copy is not in the folder.
        api.MapGet("/transcriptions/{id}/zip", (string id, TranscriptionLibrary library) =>
            library.Directory(id) is { } directory
                ? LibraryEndpoints.Zip($"{id}.zip", Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                    .Select(path => (path, Path.GetRelativePath(directory, path))))
                : Results.NotFound())
            .WithName("DownloadTranscriptionZip")
            .ProducesFile("application/zip")
            .Produces(StatusCodes.Status404NotFound);
        api.MapDelete("/transcriptions/{id}", (string id, TranscriptionLibrary library) =>
            library.Delete(id) ? Results.NoContent() : Results.NotFound())
            .WithName("DeleteTranscription")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);
        return api;
    }

    private static async Task<IResult> Transcribe(
        IFormFile? file, [FromForm] string? task, YuePaths paths, JobQueue queue, TimeProvider time, CancellationToken cancellationToken)
    {
        task ??= "melody-full";
        var errors = new Dictionary<string, string[]>();
        if (file is null || file.Length == 0)
        {
            errors["file"] = ["A recording is required."];
        }
        if (task is not ("melody-full" or "melody-vocal"))
        {
            errors["task"] = ["Either 'melody-full' or 'melody-vocal'."];
        }
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }
        if (!paths.SheetSageInstalled)
        {
            return Results.Problem(
                title: "SheetSage2 is not installed",
                detail: "Install it once in YuE Studio (Transcribe recording → Install transcription support).",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        // The worker names the result folder after the file, and afconvert recognizes the format by its extension:
        // keep the name, but only characters that are safe in a path.
        var id = Guid.NewGuid().ToString("N")[..12];
        var uploadDirectory = Path.Combine(Path.GetTempPath(), "yueui-uploads", id);
        Directory.CreateDirectory(uploadDirectory);
        var audioPath = Path.Combine(uploadDirectory, SafeFileName(file!.FileName));
        await using (var target = File.Create(audioPath))
        {
            await file.CopyToAsync(target, cancellationToken);
        }

        var transcription = new TranscriptionState
        {
            Id = id,
            FileName = file.FileName,
            Task = task,
            UploadDirectory = uploadDirectory,
            UpdatedAt = time.GetUtcNow(),
        };
        try
        {
            if (await queue.TranscribeAsync(transcription, audioPath, cancellationToken) is not null)
            {
                return Results.Accepted(value: transcription with { Stage = "queued" });
            }
        }
        catch (WorkerUnavailableException exception)
        {
            return Results.Problem(title: "YuE Studio not found", detail: exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        return Results.Accepted(value: transcription);
    }

    private static string SafeFileName(string name)
    {
        var extension = Unsafe().Replace(Path.GetExtension(name), "");
        var stem = Unsafe().Replace(Path.GetFileNameWithoutExtension(name), "-").Trim('-', '.');
        return $"{(stem.Length > 0 ? stem[..Math.Min(stem.Length, 80)] : "recording")}{extension}";
    }

    [GeneratedRegex(@"[^\p{L}\p{N}._ -]+")]
    private static partial Regex Unsafe();
}
