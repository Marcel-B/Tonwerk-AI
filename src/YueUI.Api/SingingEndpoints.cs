using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using YueToLogic.Core.Abc;
using YueUI.Api.Data;
using YueUI.Api.Library;
using YueUI.Api.Singing;

namespace YueUI.Api;

/// <summary>
/// The singing page (<see cref="SingingStudio"/>): a melody from a MIDI file, a score or a library song's score, with
/// words, sung by SoulX-Singer. Takes are sung in the background; their progress arrives as <c>singing</c> events.
/// </summary>
public static class SingingEndpoints
{
    /// <summary>A MIDI file or a score is a few kilobytes; a whole song with every track still under a megabyte.</summary>
    public const long MaxFileBytes = 5L * 1024 * 1024;

    public const int MaxLyricsCharacters = 20_000;

    /// <summary>More than a song has; the model takes the melody in segments, but the take must end some time.</summary>
    public const int MaxNotes = 5000;

    public static RouteGroupBuilder MapSingingEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/singing", async (SingingStudio studio, ISingingEngine engine, IOptions<SingingOptions> options) =>
        {
            await studio.Loaded;
            return new SingingInfo(
                engine.Installed,
                engine.ModelDownloaded,
                options.Value.ResolvedPython,
                options.Value.ResolvedSource,
                options.Value.ResolvedModel,
                options.Value.ResolvedNltkData,
                [.. options.Value.ResolvedVoices.Select(v => new SingingVoiceInfo(v.Id, v.Label, v.Language))],
                studio.Takes());
        })
            .WithName("GetSinging")
            .Produces<SingingInfo>(StatusCodes.Status200OK);
        api.MapPost("/singing", SingAsync)
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(MaxFileBytes + 256 * 1024))
            .WithFormOptions(multipartBodyLengthLimit: MaxFileBytes + 128 * 1024)
            .WithName("CreateSingingTake")
            .Produces<SingTake>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status501NotImplemented);
        // Range requests: Safari and AVPlayer play audio only from a source that answers them.
        api.MapGet("/singing/{id}/audio", async (string id, bool? download, SingingStudio studio, SqliteSingingStore store) =>
        {
            await studio.Loaded;
            return studio.Get(id) is { Stage: "done" } take && File.Exists(store.AudioPath(id))
                ? Results.File(store.AudioPath(id), "audio/wav", download == true ? $"{SketchEndpoints.FileName(take.Title)}.wav" : null, enableRangeProcessing: true)
                : Results.NotFound();
        })
            .WithName("GetSingingAudio")
            .ProducesFile("audio/wav")
            .Produces(StatusCodes.Status404NotFound);
        api.MapDelete("/singing/{id}", async (string id, SingingStudio studio) =>
        {
            await studio.Loaded;
            if (studio.Get(id) is not { } take)
            {
                return Results.NotFound();
            }
            studio.Delete(take);
            return Results.NoContent();
        })
            .WithName("DeleteSingingTake")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);
        return api;
    }

    /// <param name="file">A MIDI file (its melody track, with its lyric events if it has them) or a YuE2 score.</param>
    /// <param name="song">Instead of a file: a library song (<c>run/songN</c>), whose <c>score.abc</c> is sung.</param>
    /// <param name="lyrics">The words; for a library song its own lyrics when left empty.</param>
    /// <param name="pitchShift">Semitones the melody moves, -24 to 24.</param>
    /// <param name="autoShift">Moves the melody into the voice's range instead.</param>
    private static async Task<IResult> SingAsync(
        IFormFile? file,
        [FromForm] string? song,
        [FromForm] string? lyrics,
        [FromForm] string? voice,
        [FromForm] int? pitchShift,
        [FromForm] bool? autoShift,
        [FromForm] string? title,
        SingingStudio studio,
        ISingingEngine engine,
        SongLibrary library,
        IAbcScoreParser parser,
        IOptions<SingingOptions> options,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var errors = new Dictionary<string, string[]>();
        var hasFile = file is { Length: > 0 };
        if (hasFile == !string.IsNullOrWhiteSpace(song))
        {
            errors["file"] = ["A MIDI file or score, or a library song."];
        }
        if (lyrics?.Length > MaxLyricsCharacters)
        {
            errors["lyrics"] = [$"At most {MaxLyricsCharacters} characters."];
        }
        var chosen = settings.ResolvedVoices.FirstOrDefault(v => v.Id == voice) ?? (voice is null ? settings.ResolvedVoices.FirstOrDefault() : null);
        if (chosen is null)
        {
            errors["voice"] = [$"One of {string.Join(", ", settings.ResolvedVoices.Select(v => v.Id))}."];
        }
        if (pitchShift is < -24 or > 24)
        {
            errors["pitchShift"] = ["Between -24 and 24 semitones."];
        }
        if (title?.Length > 200)
        {
            errors["title"] = ["At most 200 characters."];
        }
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }
        if (!engine.Installed || !engine.ModelDownloaded)
        {
            return Results.Problem(
                $"SoulX-Singer is not installed ({settings.ResolvedRoot}); run deploy/install-singing.sh on the Mac.",
                statusCode: StatusCodes.Status501NotImplemented);
        }

        SingingScore score;
        string name;
        string? songId = null;
        var words = lyrics?.Trim() ?? "";
        if (hasFile)
        {
            await using var stream = file!.OpenReadStream();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            score = SingingScores.Read(buffer.ToArray(), parser);
            name = Path.GetFileNameWithoutExtension(file.FileName);
        }
        else
        {
            var parts = song!.Split('/');
            if (parts.Length != 2 || library.SongDirectory(parts[0], parts[1]) is not { } directory)
            {
                return Results.Problem($"There is no song {song}.", statusCode: StatusCodes.Status404NotFound);
            }
            var scorePath = Path.Combine(directory, "score.abc");
            if (!File.Exists(scorePath))
            {
                return Results.Problem("The song has no score to sing.", statusCode: StatusCodes.Status422UnprocessableEntity);
            }
            score = SingingScores.FromAbc(await File.ReadAllTextAsync(scorePath, cancellationToken), parser);
            var request = library.ReadRequest(parts[0], parts[1]);
            if (words.Length == 0)
            {
                words = request?.Lyrics ?? "";
            }
            name = string.IsNullOrWhiteSpace(request?.Title) ? song : request.Title;
            songId = song;
        }
        if (score.Problem is { } problem)
        {
            return Results.Problem(problem, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
        if (score.Notes.Count == 0)
        {
            return Results.Problem("The melody has no notes.", statusCode: StatusCodes.Status422UnprocessableEntity);
        }
        if (score.Notes.Count > MaxNotes || score.Notes[^1].End > settings.MaxSeconds)
        {
            return Results.Problem(
                $"The melody is too long: at most {MaxNotes} notes and {settings.MaxSeconds / 60:0} minutes.",
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }
        if (words.Length == 0 && !score.Notes.Any(n => n.Lyric is { Length: > 0 }))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["lyrics"] = ["The words to sing; the melody carries none."] });
        }

        var now = time.GetUtcNow();
        var take = new SingTake
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = string.IsNullOrWhiteSpace(title) ? name : title.Trim(),
            VoiceId = chosen!.Id,
            VoiceLabel = chosen.Label,
            SongId = songId,
            FileName = hasFile ? Path.GetFileName(file!.FileName) : null,
            Lyrics = words,
            PitchShift = pitchShift ?? 0,
            AutoShift = autoShift ?? false,
            NoteCount = score.Notes.Count,
            CreatedAt = now,
            UpdatedAt = now,
        };
        studio.Enqueue(take, score.Notes);
        return Results.Accepted($"/api/singing/{take.Id}", take);
    }
}
