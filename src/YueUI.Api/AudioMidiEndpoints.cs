using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using YueUI.Api.AudioMidi;
using YueUI.Api.Data;
using YueUI.Api.Library;
using YueUI.Api.Voices;

namespace YueUI.Api;

/// <summary>
/// A track to MIDI with Basic Pitch (<see cref="IAudioMidiEngine"/>): an uploaded recording, or a stem the voices page
/// separated. Answered at once with the MIDI file, since a song's track takes seconds; the browser fetches it so a
/// refusal becomes a message.
/// </summary>
public static partial class AudioMidiEndpoints
{
    /// <summary>A six-minute WAV in 24 bit is about 100 MB.</summary>
    public const long MaxUploadBytes = 200L * 1024 * 1024;

    public const string NotesHeader = "X-Audio-Midi-Notes";

    public static RouteGroupBuilder MapAudioMidiEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/audio-midi", (IOptions<AudioMidiOptions> options, IAudioMidiEngine engine) =>
            new AudioMidiInfo(engine.Installed, options.Value.ResolvedPython))
            .WithName("GetAudioMidiInfo")
            .Produces<AudioMidiInfo>(StatusCodes.Status200OK);
        api.MapPost("/audio-midi", ConvertUploadAsync)
            // A recording is posted from a plain form; there is no login and no cookie that a forged request could use.
            .DisableAntiforgery()
            .WithFormOptions(multipartBodyLengthLimit: MaxUploadBytes)
            .WithMetadata(new RequestSizeLimitAttribute(MaxUploadBytes))
            .WithName("ConvertUploadToMidi")
            .ProducesAudioMidi();
        api.MapPost("/stems/{id}/{name}/midi", (string id, string name, AudioMidiSettings? settings, SqliteStemStore store, SongLibrary library, IAudioMidiEngine engine, HttpResponse response, CancellationToken cancellationToken) =>
            store.Get(id) is { } set
                ? ConvertStemAsync(set, name, settings, store, library, engine, response, cancellationToken)
                : Task.FromResult(Results.NotFound()))
            .WithName("ConvertStemToMidi")
            .ProducesAudioMidi();
        // The song's route, for pages loaded before the one above.
        api.MapPost("/songs/{run}/{song}/stems/{id}/{name}/midi", (string run, string song, string id, string name, AudioMidiSettings? settings, SqliteStemStore store, SongLibrary library, IAudioMidiEngine engine, HttpResponse response, CancellationToken cancellationToken) =>
            store.Get(id) is { } set && set.SongId == $"{run}/{song}"
                ? ConvertStemAsync(set, name, settings, store, library, engine, response, cancellationToken)
                : Task.FromResult(Results.NotFound()))
            .WithName("ConvertStemToMidiForSong")
            .ProducesAudioMidi();
        return api;
    }

    /// <summary>
    /// What every conversion answers: the MIDI file (its note count in <see cref="NotesHeader"/>), or why not. The
    /// engine's own refusals carry its status (a run that failed or took too long).
    /// </summary>
    private static RouteHandlerBuilder ProducesAudioMidi(this RouteHandlerBuilder builder) => builder
        .ProducesFile("audio/midi")
        .ProducesValidationProblem()
        .Produces(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesProblem(StatusCodes.Status500InternalServerError)
        .ProducesProblem(StatusCodes.Status501NotImplemented);

    private static async Task<IResult> ConvertUploadAsync(
        IFormFile? file,
        [FromForm] bool? mono,
        [FromForm] bool? quantize,
        [FromForm] bool? bends,
        [FromForm] double? tempo,
        IAudioMidiEngine engine,
        IAudioMixer mixer,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var settings = new AudioMidiSettings(mono ?? true, quantize ?? false, bends ?? false, tempo);
        var errors = settings.Validate(tempoKnown: false);
        if (file is null || file.Length == 0)
        {
            errors["file"] = ["A recording is required."];
        }
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }
        if (!engine.Installed)
        {
            return NotInstalled();
        }
        var folder = Directory.CreateTempSubdirectory("yueui-midi-").FullName;
        try
        {
            // Through ffmpeg first: librosa reads WAV and FLAC by itself, but an MP3 or a phone's M4A only with luck.
            var upload = Path.Combine(folder, "upload" + SafeExtension(file!.FileName));
            await using (var target = File.Create(upload))
            {
                await file.CopyToAsync(target, cancellationToken);
            }
            var audio = Path.Combine(folder, "audio.wav");
            try
            {
                await mixer.DecodeAsync(upload, audio, cancellationToken);
            }
            catch (Exception exception) when (exception is VoiceServiceException or InvalidOperationException)
            {
                return Results.Problem(title: "The recording could not be read.", detail: exception.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
            }
            var name = Path.GetFileNameWithoutExtension(file.FileName);
            return await ConvertAsync(engine, response, audio, folder, "Vocal", settings, AudioMidiKind.Voice, $"{LibraryEndpoints.FileName(name, "recording")}.mid", cancellationToken);
        }
        finally
        {
            TryDelete(folder);
        }
    }

    /// <summary>A song's stem at the song's tempo; an uploaded file's at the one given, or 120.</summary>
    private static async Task<IResult> ConvertStemAsync(
        StemSetState set,
        string name,
        AudioMidiSettings? settings,
        SqliteStemStore store,
        SongLibrary library,
        IAudioMidiEngine engine,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        if (set.Stage != "done" || store.FilePath(set, name) is not { } path || !File.Exists(path))
        {
            return Results.NotFound();
        }
        double? songTempo = null;
        if (!set.Upload)
        {
            if (library.SongDirectory(set.Run, set.Song) is not { } directory)
            {
                return Results.NotFound();
            }
            songTempo = SongTempo(directory);
        }
        settings ??= new AudioMidiSettings();
        var errors = settings.Validate(tempoKnown: songTempo is not null);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }
        if (!engine.Installed)
        {
            return NotInstalled();
        }
        var folder = Directory.CreateTempSubdirectory("yueui-midi-").FullName;
        try
        {
            var fileName = $"{StemEndpoints.DownloadPrefix(set, library)}-{LibraryEndpoints.FileName(name, "stem")}.mid";
            return await ConvertAsync(
                engine, response, path, folder, TrackName(name), settings with { Tempo = settings.Tempo ?? songTempo }, KindOf(name), fileName, cancellationToken);
        }
        finally
        {
            TryDelete(folder);
        }
    }

    private static async Task<IResult> ConvertAsync(
        IAudioMidiEngine engine, HttpResponse response, string audio, string folder, string trackName, AudioMidiSettings settings, AudioMidiKind kind, string fileName, CancellationToken cancellationToken)
    {
        var output = Path.Combine(folder, "track.mid");
        try
        {
            var result = await engine.ConvertAsync(new AudioMidiJob(audio, output, trackName, settings, settings.Tempo ?? 120, kind), cancellationToken);
            if (result.Notes == 0)
            {
                return Results.Problem(title: "No notes were heard in this track.", statusCode: StatusCodes.Status422UnprocessableEntity);
            }
            // For the page to say what came out; the body is the file.
            response.Headers[NotesHeader] = result.Notes.ToString(CultureInfo.InvariantCulture);
        }
        catch (AudioMidiException exception)
        {
            return Results.Problem(title: exception.Message, statusCode: exception.Status);
        }
        // Read into memory: the folder goes when the request ends, and a MIDI file is a few KB.
        return Results.File(await File.ReadAllBytesAsync(output, cancellationToken), "audio/midi", fileName);
    }

    /// <summary>
    /// The song's tempo from its score's <c>Q:</c> field, which YuE2 sings at; null where there is none (the score
    /// parser would assume 120, which is a guess the grid should not snap to).
    /// </summary>
    internal static double? SongTempo(string songDirectory)
    {
        var score = Path.Combine(songDirectory, "score.abc");
        string text;
        try
        {
            if (!File.Exists(score))
            {
                return null;
            }
            text = File.ReadAllText(score);
        }
        catch (IOException)
        {
            return null;
        }
        if (TempoField().Match(text) is not { Success: true } match
            || !double.TryParse(match.Groups["bpm"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var bpm))
        {
            return null;
        }
        // Q:1/8=180 counts eighths; the file's tempo counts quarters.
        if (match.Groups["num"].Success)
        {
            bpm *= 4.0 * int.Parse(match.Groups["num"].Value, CultureInfo.InvariantCulture) / int.Parse(match.Groups["den"].Value, CultureInfo.InvariantCulture);
        }
        return bpm is >= AudioMidiSettings.MinTempo and <= AudioMidiSettings.MaxTempo ? bpm : null;
    }

    [GeneratedRegex(@"^Q:\s*(?:(?<num>[1-9]\d?)/(?<den>[1-9]\d?)\s*=\s*)?(?<bpm>\d+(?:\.\d+)?)", RegexOptions.Multiline)]
    private static partial Regex TempoField();

    /// <summary>Every vocal stem the separators make (vocals, vocals_dry, …) is sung; the rest are instruments.</summary>
    internal static bool IsVoice(string stem) => stem.Contains("vocal", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What a stem is by its name: the separators call them <c>vocals</c>, <c>vocals_dry</c>, <c>bass</c>, <c>drums</c>
    /// (and a drum separator <c>kick</c>, <c>snare</c>, … which are left to Basic Pitch, since each holds one drum).
    /// </summary>
    internal static AudioMidiKind KindOf(string stem) =>
        IsVoice(stem) ? AudioMidiKind.Voice
        : stem.Equals("bass", StringComparison.OrdinalIgnoreCase) ? AudioMidiKind.Bass
        : stem.Equals("drums", StringComparison.OrdinalIgnoreCase) ? AudioMidiKind.Drums
        : AudioMidiKind.Other;

    /// <summary>
    /// Named as the Logic page names its tracks, so a vocal stem comes back as the melody (<c>Vocal</c>) and a bass stem
    /// lands on the bass's channel and sound.
    /// </summary>
    internal static string TrackName(string stem) =>
        IsVoice(stem) ? "Vocal" : stem.Length == 0 ? "Track" : char.ToUpperInvariant(stem[0]) + stem[1..].Replace('_', ' ');

    internal static string SafeExtension(string name)
    {
        var extension = Path.GetExtension(name);
        return extension.Length is > 1 and <= 6 && extension[1..].All(char.IsAsciiLetterOrDigit) ? extension : ".audio";
    }

    private static void TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static IResult NotInstalled() =>
        Results.Problem(title: "Basic Pitch is not installed: run deploy/install-midi.sh on the Mac (or deploy/setup-mac.sh with --midi).", statusCode: StatusCodes.Status501NotImplemented);
}
