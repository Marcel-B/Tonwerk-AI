using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using YueUI.Api.Data;
using YueUI.Api.Library;

namespace YueUI.Api;

/// <param name="Settings">A JSON object; null keeps what is stored.</param>
public sealed record LogicSessionChange(string? Name, JsonElement? Settings);

/// <summary>
/// The Logic page's sessions (<see cref="SqliteLogicSessionStore"/>): the page saves its state for a song or an uploaded
/// score as it changes, and opens it again later, in any browser or the app. The settings are the clients' business;
/// the server checks only that they are an object and keeps the uploaded files.
/// </summary>
public static class LogicSessionEndpoints
{
    public const int MaxNameLength = 200;

    /// <summary>A copy of each track's sound goes in, a few kilobytes each; this leaves room for any song.</summary>
    public const int MaxSettingsLength = 1_000_000;

    public static RouteGroupBuilder MapLogicSessionEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/logic/sessions", (SqliteLogicSessionStore store) => store.List())
            .WithName("ListLogicSessions");

        api.MapGet("/logic/sessions/{id:long}", (long id, SqliteLogicSessionStore store) =>
            store.Find(id) is { } session ? Results.Ok(session) : Results.NotFound())
            .WithName("GetLogicSession")
            .Produces<LogicSession>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        api.MapPost("/logic/sessions", CreateAsync)
            // Posted from a plain form; there is no login and no cookie that a forged request could use.
            .DisableAntiforgery()
            .WithFormOptions(multipartBodyLengthLimit: LogicEndpoints.MaxMidiBytes + MaxSettingsLength + 64 * 1024)
            .WithName("CreateLogicSession")
            .Produces<LogicSession>(StatusCodes.Status201Created)
            .ProducesValidationProblem();

        api.MapPut("/logic/sessions/{id:long}", (long id, LogicSessionChange change, SqliteLogicSessionStore store) =>
        {
            var errors = new Dictionary<string, string[]>();
            var name = change.Name is null ? null : ValidName(change.Name, errors);
            var settings = change.Settings is { ValueKind: not JsonValueKind.Null } given ? ValidSettings(given, errors) : null;
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }
            return store.Update(id, name, settings) ? Results.Ok(store.Find(id)) : Results.NotFound();
        })
            .WithName("UpdateLogicSession")
            .Produces<LogicSession>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesValidationProblem();

        api.MapDelete("/logic/sessions/{id:long}", (long id, SqliteLogicSessionStore store) =>
            store.Delete(id) ? Results.NoContent() : Results.NotFound())
            .WithName("DeleteLogicSession")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);

        api.MapGet("/logic/sessions/{id:long}/file", (long id, SqliteLogicSessionStore store) =>
            store.Find(id) is { } session && store.ScorePath(session) is { } path
                ? Results.File(path, path.EndsWith(".mid", StringComparison.Ordinal) ? "audio/midi" : "text/plain", session.ScoreFile)
                : Results.NotFound())
            .WithName("GetLogicSessionFile")
            .ProducesFile("audio/midi", "text/plain")
            .Produces(StatusCodes.Status404NotFound);

        // Range requests, as for a song's FLAC: the page fetches it whole, but a player may seek.
        api.MapGet("/logic/sessions/{id:long}/audio", (long id, SqliteLogicSessionStore store) =>
            store.Find(id) is { } session && store.AudioPath(session) is { } path
                ? Results.File(path, "audio/flac", session.AudioFile, enableRangeProcessing: true)
                : Results.NotFound())
            .WithName("GetLogicSessionAudio")
            .ProducesFile("audio/flac")
            .Produces(StatusCodes.Status404NotFound);

        api.MapPut("/logic/sessions/{id:long}/audio", SetAudioAsync)
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(LogicEndpoints.MaxAudioBytes + 64 * 1024))
            .WithFormOptions(multipartBodyLengthLimit: LogicEndpoints.MaxAudioBytes + 64 * 1024)
            .WithName("SetLogicSessionAudio")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesValidationProblem();

        api.MapDelete("/logic/sessions/{id:long}/audio", async (long id, SqliteLogicSessionStore store, CancellationToken cancellationToken) =>
            await store.SetAudioAsync(id, null, cancellationToken) ? Results.NoContent() : Results.NotFound())
            .WithName("DeleteLogicSessionAudio")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);
        return api;
    }

    /// <summary>For a library song (<c>song</c>) or an uploaded score.abc or MIDI file (<c>file</c>), never both.</summary>
    private static async Task<IResult> CreateAsync(
        [FromForm] string? name,
        [FromForm] string? song,
        [FromForm] string? settings,
        IFormFile? file,
        SqliteLogicSessionStore store,
        SongLibrary library,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var validName = ValidName(name, errors);
        JsonElement? validSettings = null;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(settings) ? "{}" : settings);
            validSettings = ValidSettings(document.RootElement.Clone(), errors);
        }
        catch (JsonException)
        {
            errors["settings"] = ["The settings must be a JSON object."];
        }
        var songId = string.IsNullOrWhiteSpace(song) ? null : song.Trim();
        if ((songId is null) == (file is null))
        {
            errors["song"] = ["A session is for a library song or an uploaded score, one of the two."];
        }
        else if (songId is not null && (songId.Split('/') is not [var run, var name2] || library.SongDirectory(run, name2) is null))
        {
            errors["song"] = [$"There is no song '{songId}'."];
        }
        if (file is { Length: > LogicEndpoints.MaxMidiBytes })
        {
            errors["file"] = [$"A score.abc or MIDI file is at most {LogicEndpoints.MaxMidiBytes / (1024 * 1024)} MB here."];
        }
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        if (file is null)
        {
            var created = await store.CreateAsync(validName!, songId, null, validSettings!.Value, cancellationToken);
            return Results.Created($"/api/logic/sessions/{created.Id}", created);
        }
        // Read whole, a few kilobytes: the first bytes tell a MIDI file from a score, whatever the file's name says.
        using var content = new MemoryStream();
        await file.CopyToAsync(content, cancellationToken);
        var extension = content.Length >= 4 && content.GetBuffer().AsSpan(0, 4).SequenceEqual("MThd"u8) ? ".mid" : ".abc";
        content.Position = 0;
        var fileName = Path.GetFileName(file.FileName);
        var session = await store.CreateAsync(
            validName!, null, (string.IsNullOrWhiteSpace(fileName) ? "score" + extension : fileName, extension, content), validSettings!.Value, cancellationToken);
        return Results.Created($"/api/logic/sessions/{session.Id}", session);
    }

    private static async Task<IResult> SetAudioAsync(long id, IFormFile? audio, SqliteLogicSessionStore store, CancellationToken cancellationToken)
    {
        if (audio is null || audio.Length == 0)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["audio"] = ["A FLAC file is needed."] });
        }
        if (audio.Length > LogicEndpoints.MaxAudioBytes)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["audio"] = [$"The audio may have at most {LogicEndpoints.MaxAudioBytes / (1024 * 1024)} MB."],
            });
        }
        await using var stream = audio.OpenReadStream();
        var name = Path.GetFileName(audio.FileName);
        return await store.SetAudioAsync(id, (string.IsNullOrWhiteSpace(name) ? "audio.flac" : name, stream), cancellationToken)
            ? Results.NoContent()
            : Results.NotFound();
    }

    private static string? ValidName(string? name, Dictionary<string, string[]> errors)
    {
        if (name?.Trim() is { Length: > 0 and <= MaxNameLength } trimmed)
        {
            return trimmed;
        }
        errors["name"] = [$"A session needs a name of 1 to {MaxNameLength} characters."];
        return null;
    }

    private static JsonElement? ValidSettings(JsonElement settings, Dictionary<string, string[]> errors)
    {
        if (settings.ValueKind != JsonValueKind.Object)
        {
            errors["settings"] = ["The settings must be a JSON object."];
            return null;
        }
        if (settings.GetRawText().Length > MaxSettingsLength)
        {
            errors["settings"] = [$"The settings may be at most {MaxSettingsLength} characters of JSON."];
            return null;
        }
        return settings;
    }
}
