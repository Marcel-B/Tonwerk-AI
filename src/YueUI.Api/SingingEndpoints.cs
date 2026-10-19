using System.Text.Json;
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
        api.MapGet("/singing", async (SingingStudio studio, ISingingEngine engine, SingingVoices voices, IOptions<SingingOptions> options) =>
        {
            await studio.Loaded;
            return new SingingInfo(
                engine.Installed,
                engine.ModelDownloaded,
                options.Value.ResolvedPython,
                options.Value.ResolvedSource,
                options.Value.ResolvedModel,
                options.Value.ResolvedNltkData,
                voices.Infos(),
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
        api.MapPost("/singing/melody", MelodyAsync)
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(MaxFileBytes + 256 * 1024))
            .WithFormOptions(multipartBodyLengthLimit: MaxFileBytes + 128 * 1024)
            .WithName("ReadSingingMelody")
            .Produces<SingingMelody>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        api.MapPost("/singing/align", AlignAsync)
            .WithName("AlignSingingNotes")
            .Produces<SingingAligned>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .ProducesProblem(StatusCodes.Status501NotImplemented);
        api.MapGet("/singing/{id}/notes", async (string id, SingingStudio studio, SqliteSingingStore store) =>
        {
            await studio.Loaded;
            return studio.Get(id) is { } take
                ? Results.Ok(new SingingMelody(store.Notes(id), take.TempoBpm ?? 120, take.Lyrics, take.Title, null))
                : Results.NotFound();
        })
            .WithName("GetSingingNotes")
            .Produces<SingingMelody>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);
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
        api.MapPost("/singing/voices", AddVoiceAsync)
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(SingingVoices.MaxFileBytes + 256 * 1024))
            .WithFormOptions(multipartBodyLengthLimit: SingingVoices.MaxFileBytes + 128 * 1024)
            .WithName("CreateSingingVoice")
            .Produces<SingingVoiceCreated>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status501NotImplemented);
        api.MapGet("/singing/voices/{id}/audio", (string id, SqliteSingingStore store) =>
            store.Voices().Any(v => v.Id == id) && File.Exists(store.VoiceAudioPath(id))
                ? Results.File(store.VoiceAudioPath(id), "audio/wav", enableRangeProcessing: true)
                : Results.NotFound())
            .WithName("GetSingingVoiceAudio")
            .ProducesFile("audio/wav")
            .Produces(StatusCodes.Status404NotFound);
        api.MapDelete("/singing/voices/{id}", async (string id, SingingStudio studio, SqliteSingingStore store) =>
        {
            await studio.Loaded;
            if (!store.Voices().Any(v => v.Id == id))
            {
                return Results.NotFound();
            }
            // A waiting take would fail without it; finished ones keep their name and audio.
            if (studio.Takes().Any(t => t.VoiceId == id && t.Stage is not ("done" or "failed" or "cancelled")))
            {
                return Results.Problem("A take waiting to be sung uses this voice.", statusCode: StatusCodes.Status409Conflict);
            }
            store.RemoveVoice(id);
            return Results.NoContent();
        })
            .WithName("DeleteSingingVoice")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
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
    /// <param name="notes">Instead of a file or song: the note editor's melody, a JSON list of <see cref="SungNote"/>.</param>
    /// <param name="tempoBpm">The editor's tempo, kept with the take so it opens on the same grid.</param>
    /// <param name="pitchShift">Semitones the melody moves, -24 to 24.</param>
    /// <param name="autoShift">Moves the melody into the voice's range instead.</param>
    /// <param name="machine">The Mac to sing on: <c>local</c>, a helper's id, or empty for whichever is free.</param>
    private static async Task<IResult> SingAsync(
        IFormFile? file,
        [FromForm] string? song,
        [FromForm] string? notes,
        [FromForm] double? tempoBpm,
        [FromForm] string? lyrics,
        [FromForm] string? voice,
        [FromForm] int? pitchShift,
        [FromForm] bool? autoShift,
        [FromForm] string? title,
        [FromForm] string? machine,
        SingingStudio studio,
        SingingVoices voices,
        ISingingEngine engine,
        Machines.Machines machines,
        SongLibrary library,
        IAbcScoreParser parser,
        IOptions<SingingOptions> options,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var errors = new Dictionary<string, string[]>();
        var hasFile = file is { Length: > 0 };
        var hasNotes = !string.IsNullOrWhiteSpace(notes);
        if ((hasFile ? 1 : 0) + (string.IsNullOrWhiteSpace(song) ? 0 : 1) + (hasNotes ? 1 : 0) != 1)
        {
            errors["file"] = ["A MIDI file or score, a library song, or the editor's notes."];
        }
        List<SungNote>? drawn = null;
        if (hasNotes)
        {
            drawn = ReadNotes(notes!, errors);
        }
        if (tempoBpm is <= 0 or > 1000)
        {
            errors["tempoBpm"] = ["Between 0 and 1000."];
        }
        if (lyrics?.Length > MaxLyricsCharacters)
        {
            errors["lyrics"] = [$"At most {MaxLyricsCharacters} characters."];
        }
        var offered = voices.All();
        var chosen = offered.FirstOrDefault(v => v.Id == voice) ?? (voice is null ? offered.FirstOrDefault() : null);
        if (chosen is null)
        {
            errors["voice"] = [$"One of {string.Join(", ", offered.Select(v => v.Id))}."];
        }
        if (pitchShift is < -24 or > 24)
        {
            errors["pitchShift"] = ["Between -24 and 24 semitones."];
        }
        if (title?.Length > 200)
        {
            errors["title"] = ["At most 200 characters."];
        }
        machines.TryNormalize(machine, out var place, errors);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }
        if (!machines.CanRun(Machines.Engines.Singing, place, engine.Installed && engine.ModelDownloaded))
        {
            return Results.Problem(
                $"SoulX-Singer is not installed ({settings.ResolvedRoot}); run deploy/install-singing.sh on the Mac.",
                statusCode: StatusCodes.Status501NotImplemented);
        }

        SingingScore score;
        string name;
        string? songId = null;
        var words = lyrics?.Trim() ?? "";
        if (drawn is not null)
        {
            score = new SingingScore(drawn, null, null, tempoBpm ?? 120);
            name = "Tonwerk";
        }
        else if (hasFile)
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
            TempoBpm = drawn is not null ? tempoBpm ?? 120 : score.TempoBpm,
            Machine = place,
            CreatedAt = now,
            UpdatedAt = now,
        };
        studio.Enqueue(take, score.Notes);
        return Results.Accepted($"/api/singing/{take.Id}", take);
    }

    /// <param name="file">A recording of one's own singing, any format ffmpeg reads; 30 s are listened to.</param>
    /// <param name="label">The voice's name.</param>
    /// <param name="lyrics">The words sung in the recording, English, in order.</param>
    private static async Task<IResult> AddVoiceAsync(
        IFormFile? file,
        [FromForm] string? label,
        [FromForm] string? lyrics,
        ISingingEngine engine,
        SqliteSingingStore store,
        TimeProvider time,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(label) || label.Trim().Length > 100)
        {
            errors["label"] = ["A name for the voice, up to 100 characters."];
        }
        if (string.IsNullOrWhiteSpace(lyrics) || lyrics.Length > 2000)
        {
            errors["lyrics"] = ["The words sung in the recording, up to 2000 characters."];
        }
        if (file is not { Length: > 0 })
        {
            errors["file"] = ["A recording of the voice singing."];
        }
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }
        if (!engine.Installed)
        {
            return Results.Problem(
                "SoulX-Singer is not installed on this Mac; run deploy/install-singing.sh.", statusCode: StatusCodes.Status501NotImplemented);
        }

        var id = Guid.NewGuid().ToString("N");
        var audio = store.VoiceAudioPath(id);
        var metadata = store.VoiceMetadataPath(id);
        Directory.CreateDirectory(Path.GetDirectoryName(audio)!);
        var upload = Path.Combine(Path.GetDirectoryName(audio)!, $"upload-{id}{Extension(file!.FileName)}");
        try
        {
            await using (var stream = File.Create(upload))
            {
                await file.CopyToAsync(stream, cancellationToken);
            }
            var heard = await engine.MakeVoiceAsync(upload, lyrics!.Trim(), audio, metadata, cancellationToken);
            var voice = new SingingOwnVoice(
                id, label!.Trim(), lyrics.Trim(), heard.Text, heard.Seconds, heard.Notes, heard.Leftover, heard.Low, heard.High, time.GetUtcNow());
            store.AddVoice(voice);
            loggers.CreateLogger("YueUI.Api.Singing.SingingVoices").LogInformation(
                "Voice {Label}: {Seconds} s of {Recorded} s, {Notes} notes, {Held} held, {Leftover} syllables left over",
                voice.Label, heard.Seconds, heard.RecordedSeconds, heard.Notes, heard.Held, heard.Leftover);
            return Results.Created($"/api/singing/voices/{id}/audio", new SingingVoiceCreated(voice.Info, heard));
        }
        catch (SingingException exception)
        {
            File.Delete(audio);
            File.Delete(metadata);
            return Results.Problem(exception.Message, statusCode: exception.Status);
        }
        finally
        {
            File.Delete(upload);
        }
    }

    /// <summary>The upload's extension for ffmpeg, where it is a plain one.</summary>
    private static string Extension(string name)
    {
        var extension = Path.GetExtension(name);
        return extension.Length is > 1 and <= 6 && extension.Skip(1).All(char.IsAsciiLetterOrDigit) ? extension : "";
    }

    /// <param name="file">A MIDI file or a YuE2 score.</param>
    /// <param name="song">Instead of a file: a library song, with its lyrics.</param>
    private static async Task<IResult> MelodyAsync(
        IFormFile? file,
        [FromForm] string? song,
        SongLibrary library,
        IAbcScoreParser parser,
        CancellationToken cancellationToken)
    {
        var hasFile = file is { Length: > 0 };
        if (hasFile == !string.IsNullOrWhiteSpace(song))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["A MIDI file or score, or a library song."] });
        }
        SingingScore score;
        string title;
        var lyrics = "";
        if (hasFile)
        {
            await using var stream = file!.OpenReadStream();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            score = SingingScores.Read(buffer.ToArray(), parser);
            title = Path.GetFileNameWithoutExtension(file.FileName);
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
            lyrics = request?.Lyrics ?? "";
            title = string.IsNullOrWhiteSpace(request?.Title) ? song : request.Title;
        }
        if (score.Problem is { } problem)
        {
            return Results.Problem(problem, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
        if (score.Notes.Count > MaxNotes)
        {
            return Results.Problem($"The melody has more than {MaxNotes} notes.", statusCode: StatusCodes.Status422UnprocessableEntity);
        }
        return Results.Ok(new SingingMelody(score.Notes, score.TempoBpm, lyrics, title, score.Part));
    }

    private static async Task<IResult> AlignAsync(SingingAlignRequest request, ISingingEngine engine, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.Notes is null || request.Notes.Count > MaxNotes)
        {
            errors["notes"] = [$"Up to {MaxNotes} notes."];
        }
        else if (request.Notes.Any(n => !Valid(n)))
        {
            errors["notes"] = ["Every note needs a start, an end after it and a pitch from 0 to 127."];
        }
        if (request.Lyrics?.Length > MaxLyricsCharacters)
        {
            errors["lyrics"] = [$"At most {MaxLyricsCharacters} characters."];
        }
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }
        if (request.Notes!.Count == 0)
        {
            return Results.Ok(new SingingAligned([], 0));
        }
        try
        {
            return Results.Ok(await engine.AlignAsync(request.Notes, request.Lyrics, cancellationToken));
        }
        catch (SingingException exception)
        {
            return Results.Problem(exception.Message, statusCode: exception.Status);
        }
    }

    /// <summary>The editor's notes from the form; null with an error where they cannot be read.</summary>
    private static List<SungNote>? ReadNotes(string json, Dictionary<string, string[]> errors)
    {
        List<SungNote>? notes;
        try
        {
            notes = JsonSerializer.Deserialize<List<SungNote>>(json, JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            notes = null;
        }
        if (notes is null || notes.Any(n => !Valid(n)))
        {
            errors["notes"] = ["A JSON list of notes, each with a start, an end after it and a pitch from 0 to 127."];
            return null;
        }
        if (notes.Any(n => n.Lyric?.Length > 100))
        {
            errors["notes"] = ["At most 100 characters of words per note."];
            return null;
        }
        return [.. notes.OrderBy(n => n.Start).ThenByDescending(n => n.Pitch)];
    }

    private static bool Valid(SungNote note) =>
        note is not null && double.IsFinite(note.Start) && double.IsFinite(note.End) && note.Start >= 0 && note.End > note.Start
            && note.Pitch is >= 0 and <= 127;
}
