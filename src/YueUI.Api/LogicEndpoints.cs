using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using YueToLogic.Core.Conversion;
using YueToLogic.Core.Diagnostics;
using YueToLogic.Core.Logic;
using YueToLogic.Core.MusicXml;
using YueToLogic.Core.Serialization;
using YueUI.Api.AudioMidi;
using YueUI.Api.Data;
using YueUI.Api.Library;
using YueUI.Api.Logic;
using YueUI.Api.Voices;

namespace YueUI.Api;

/// <summary>A MIDI file read back into a score.</summary>
/// <param name="Abc">The <c>score.abc</c> for the next song.</param>
/// <param name="Warnings">What could not be carried over, e.g. a track that was ignored.</param>
public sealed record MidiScore(string Abc, IReadOnlyList<string> Warnings);

/// <summary>
/// Scores as MIDI files and Logic Pro projects, and a MIDI file edited in Logic back into a score, all built in this
/// process by <c>YueToLogic.Core</c> (formerly the separate yue-to-logic-pro service). The library's button exports a
/// song with the defaults; the Logic page converts with its options and instruments, from a song of the library or an
/// uploaded score, and answers in YueToLogic.Core's own JSON (enums by name), which its frontend was written against.
/// </summary>
public static class LogicEndpoints
{
    /// <summary>The warnings of an export (compact JSON array of <see cref="Diagnostic"/>), since the body is the ZIP.</summary>
    public const string DiagnosticsHeader = "X-YueToLogic-Diagnostics";

    /// <summary>The stems the template has audio tracks for, in the order of <see cref="LogicAudio"/>'s vocals.</summary>
    private static readonly string[] VocalStems = ["vocals", "vocals_dry"];

    /// <summary>A song's MIDI file is a few hundred kilobytes, even with every generated track in it.</summary>
    public const long MaxMidiBytes = 4 * 1024 * 1024;

    /// <summary>YuE2's scores are a few kilobytes; anything near this is not one.</summary>
    public const long MaxScoreBytes = 1024 * 1024;

    /// <summary>A long song's 48 kHz FLAC stays well below this.</summary>
    public const long MaxAudioBytes = 250L * 1024 * 1024;

    // Header values must be single-line, so the diagnostics header uses unindented JSON.
    private static readonly YueToLogicJsonContext CompactJson = new(new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    });

    public static RouteGroupBuilder MapLogicEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/songs/{run}/{song}/logic", ExportSongAsync)
            .WithName("ExportSongLogicProject")
            .ProducesFile("application/zip")
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        // The way back: a song edited in Logic and exported as MIDI becomes the score of a new song.
        api.MapPost("/midi/abc", MidiToAbcAsync)
            .DisableAntiforgery()
            .WithName("ConvertMidiToAbc")
            .Produces<MidiScore>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // The Logic page's answers are YueToLogic.Core's JSON (enums by name, not this API's camelCase ones), so they
        // are described as opaque objects rather than with a schema whose enum values would be wrong. A 422 there is
        // either a ConversionResult that says why or a problem (no notes heard in a stem), both JSON objects.
        var logic = api.MapGroup("/logic");
        logic.MapPost("/convert", ConvertAsync)
            .DisableAntiforgery()
            .WithFormOptions(multipartBodyLengthLimit: MaxScoreBytes + 64 * 1024)
            .WithName("ConvertLogicScore")
            .Produces<JsonElement>(StatusCodes.Status200OK)
            .Produces<JsonElement>(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout);
        logic.MapPost("/musicxml", MusicXmlAsync)
            .DisableAntiforgery()
            .WithFormOptions(multipartBodyLengthLimit: MaxScoreBytes + 64 * 1024)
            .WithName("ExportLogicMusicXml")
            .ProducesFile("application/vnd.recordare.musicxml+xml")
            .Produces<JsonElement>(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout);
        logic.MapPost("/export", ExportAsync)
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(MaxAudioBytes + MaxScoreBytes + 64 * 1024))
            .WithFormOptions(multipartBodyLengthLimit: MaxAudioBytes + MaxScoreBytes + 64 * 1024)
            .WithName("ExportLogicProject")
            .ProducesFile("application/zip")
            .Produces<JsonElement>(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout);
        logic.MapGet("/presets", (SqliteLogicPresetStore store) => Results.Ok(store.List()))
            .WithName("ListLogicPresets")
            .Produces<IReadOnlyList<LogicPreset>>(StatusCodes.Status200OK);
        logic.MapPut("/presets/{name}", SavePreset)
            .WithName("SaveLogicPreset")
            .Produces<LogicPreset>(StatusCodes.Status200OK)
            .Produces<LogicPreset>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest);
        logic.MapDelete("/presets/{name}", (string name, SqliteLogicPresetStore store) =>
            store.Delete(name.Trim())
                ? Results.NoContent()
                : Results.Problem(title: "Unknown preset", detail: $"There is no preset named '{name}'.", statusCode: StatusCodes.Status404NotFound))
            .WithName("DeleteLogicPreset")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);
        logic.MapPost("/import", Import)
            .WithName("ImportLogicSettings")
            .Produces<LogicImportResult>(StatusCodes.Status200OK);
        return api;
    }

    private static async Task<IResult> ExportSongAsync(
        string run,
        string song,
        SongLibrary library,
        IScoreConverter converter,
        ILogicProjectWriter writer,
        IOptions<LogicOptions> options,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (library.SongDirectory(run, song) is not { } directory
            || !File.Exists(Path.Combine(directory, "audio.flac"))
            || !File.Exists(Path.Combine(directory, "score.abc")))
        {
            return Results.NotFound();
        }

        const string title = "This song cannot become a Logic project.";
        var name = $"{LibraryEndpoints.FileName(library.TitleOf(run, directory), run)}-{song}";
        await using var score = File.OpenRead(Path.Combine(directory, "score.abc"));
        await using var audio = File.OpenRead(Path.Combine(directory, "audio.flac"));
        var built = await BuildAsync(
            converter, writer, score, new LogicAudio(audio), new ConversionOptions(), name, options.Value.SplitSections, new Dictionary<string, LogicInstrument>(), cancellationToken);
        return built.Zip is { } zip
            ? Package(zip, built.Diagnostics, name, context)
            : Results.Problem(
                title: title,
                detail: string.Join(" ", [title, .. Messages(built.Diagnostics, DiagnosticSeverity.Error)]),
                statusCode: StatusCodes.Status422UnprocessableEntity);
    }

    /// <summary>The Logic page's preview: the converted score with its notes and the MIDI file, or why there is none.</summary>
    private static async Task<IResult> ConvertAsync(
        [FromForm] string? song,
        IFormFile? file,
        [FromForm] string? options,
        [FromForm] string? bassStems,
        [FromForm] string? drumStems,
        SongLibrary library,
        IScoreConverter converter,
        IMidiToAbcConverter midiConverter,
        SqliteStemStore stemStore,
        StemParts stemParts,
        CancellationToken cancellationToken)
    {
        if (ParseOptions(options) is not { } parsed)
        {
            return BadRequest("Invalid options", "Form field 'options' is not valid ConversionOptions JSON.");
        }
        var source = await OpenScoreAsync(song, file, library, midiConverter, cancellationToken);
        if (source.Problem is not null)
        {
            return source.Problem;
        }
        var recorded = await RecordedAsync(bassStems, drumStems, source, stemStore, stemParts, cancellationToken);
        if (recorded.Problem is not null)
        {
            await source.Score!.DisposeAsync();
            return recorded.Problem;
        }
        parsed.Recorded = recorded.Parts;
        ConversionResult result;
        await using (var score = source.Score!)
        {
            result = await converter.ConvertAsync(score, parsed, cancellationToken);
        }
        result = result with { Diagnostics = [.. source.Read, .. result.Diagnostics] };
        return Results.Json(
            result,
            YueToLogicJsonContext.Default.ConversionResult,
            statusCode: result.Success ? StatusCodes.Status200OK : StatusCodes.Status422UnprocessableEntity);
    }

    /// <summary>
    /// The score as MusicXML for notation programs, converted with the page's options like the preview; a score that
    /// cannot be read answers 422 with the <see cref="ConversionResult"/> that says why.
    /// </summary>
    private static async Task<IResult> MusicXmlAsync(
        [FromForm] string? song,
        IFormFile? file,
        [FromForm] string? options,
        [FromForm] string? name,
        [FromForm] string? bassStems,
        [FromForm] string? drumStems,
        SongLibrary library,
        IScoreConverter converter,
        IMusicXmlWriter writer,
        IMidiToAbcConverter midiConverter,
        SqliteStemStore stemStore,
        StemParts stemParts,
        CancellationToken cancellationToken)
    {
        if (ParseOptions(options) is not { } parsed)
        {
            return BadRequest("Invalid options", "Form field 'options' is not valid ConversionOptions JSON.");
        }
        var source = await OpenScoreAsync(song, file, library, midiConverter, cancellationToken);
        if (source.Problem is not null)
        {
            return source.Problem;
        }
        var recorded = await RecordedAsync(bassStems, drumStems, source, stemStore, stemParts, cancellationToken);
        if (recorded.Problem is not null)
        {
            await source.Score!.DisposeAsync();
            return recorded.Problem;
        }
        parsed.Recorded = recorded.Parts;
        var fallbackName = source.Directory is { } directory
            ? $"{LibraryEndpoints.FileName(library.TitleOf(source.Run!, directory), source.Run!)}-{source.Song}"
            : Path.GetFileNameWithoutExtension(file!.FileName);
        var fileName = PackageName(name, fallbackName);
        ConversionResult result;
        await using (var score = source.Score!)
        {
            result = await converter.ConvertAsync(score, parsed, cancellationToken);
        }
        if (!result.Success)
        {
            return Results.Json(result, YueToLogicJsonContext.Default.ConversionResult, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
        // The song's title, since YuE2's scores leave T: empty.
        var title = source.Directory is { } songDirectory ? library.TitleOf(source.Run!, songDirectory) : fileName;
        var xml = writer.Write(result.Score!, new MusicXmlOptions { IncludeChordTrack = parsed.IncludeChordTrack }, title);
        return Results.File(xml, "application/vnd.recordare.musicxml+xml", $"{fileName}.musicxml");
    }

    /// <summary>The Logic page's export, with its options, the instruments the tracks play and a name of its choosing.</summary>
    private static async Task<IResult> ExportAsync(
        [FromForm] string? song,
        IFormFile? file,
        IFormFile? audio,
        [FromForm] string? options,
        [FromForm] string? name,
        [FromForm] bool? splitSections,
        [FromForm] string? instruments,
        [FromForm] string? stems,
        [FromForm] string? bassStems,
        [FromForm] string? drumStems,
        SongLibrary library,
        IScoreConverter converter,
        ILogicProjectWriter writer,
        IMidiToAbcConverter midiConverter,
        SqliteStemStore stemStore,
        StemParts stemParts,
        IAudioMixer mixer,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (ParseOptions(options) is not { } parsed)
        {
            return BadRequest("Invalid options", "Form field 'options' is not valid ConversionOptions JSON.");
        }
        var (logicInstruments, invalidInstruments) = ParseInstruments(instruments);
        if (invalidInstruments is not null)
        {
            return invalidInstruments;
        }
        if (audio is { Length: > MaxAudioBytes })
        {
            return BadRequest("Audio file too large", $"The audio may have at most {MaxAudioBytes / (1024 * 1024)} MB.");
        }
        var source = await OpenScoreAsync(song, file, library, midiConverter, cancellationToken);
        if (source.Problem is not null)
        {
            return source.Problem;
        }
        var recorded = await RecordedAsync(bassStems, drumStems, source, stemStore, stemParts, cancellationToken);
        if (recorded.Problem is not null)
        {
            await source.Score!.DisposeAsync();
            return recorded.Problem;
        }
        parsed.Recorded = recorded.Parts;

        // A song of the library brings its own recording; an uploaded score takes the uploaded one, if any.
        var fallbackName = source.Directory is { } directory
            ? $"{LibraryEndpoints.FileName(library.TitleOf(source.Run!, directory), source.Run!)}-{source.Song}"
            : Path.GetFileNameWithoutExtension(file!.FileName);
        var packageName = PackageName(name, fallbackName);
        await using var score = source.Score!;
        await using var audioStream = source.Directory is { } songDirectory
            ? File.Exists(Path.Combine(songDirectory, "audio.flac")) ? File.OpenRead(Path.Combine(songDirectory, "audio.flac")) : null
            : audio is { Length: > 0 } ? audio.OpenReadStream() : null;

        // The song's separated vocals go onto the template's two vocal tracks, as they did on yue-to-logic-pro's page
        // with StemMyWav; here they come from a set made on the voices page.
        StemSetState? stemSet = null;
        if (!string.IsNullOrWhiteSpace(stems))
        {
            if (source.Directory is null)
            {
                return BadRequest("Stems need a song", "Stems can only go along with a song of the library.");
            }
            stemSet = stemStore.Get(stems);
            if (stemSet is null || stemSet.SongId != $"{source.Run}/{source.Song}" || stemSet.Stage != "done")
            {
                return Results.Problem(title: "Unknown stems", detail: "These stems do not exist or are not finished for this song.", statusCode: StatusCodes.Status404NotFound);
            }
            if (!VocalStems.Any(name => stemSet.Stems.Any(s => s.Name == name)))
            {
                return BadRequest("No vocal stems", "The Logic project takes the vocals and the dry vocals; these stems have neither.");
            }
        }

        var stemFolder = Path.Combine(Path.GetTempPath(), $"yueui-logic-stems-{Guid.NewGuid():N}");
        var vocals = new Stream?[VocalStems.Length];
        (FileStream? Zip, IReadOnlyList<Diagnostic> Diagnostics) built;
        try
        {
            if (stemSet is not null)
            {
                Directory.CreateDirectory(stemFolder);
                for (var i = 0; i < VocalStems.Length; i++)
                {
                    if (stemStore.FilePath(stemSet, VocalStems[i]) is { } stored && File.Exists(stored))
                    {
                        var wave = Path.Combine(stemFolder, $"{VocalStems[i]}.wav");
                        await mixer.DecodeWaveAsync(stored, wave, cancellationToken);
                        vocals[i] = File.OpenRead(wave);
                    }
                }
            }
            built = await BuildAsync(
                converter, writer, score, new LogicAudio(audioStream, vocals[0], vocals[1]), parsed, packageName, splitSections ?? false, logicInstruments, cancellationToken);
        }
        catch (VoiceServiceException exception)
        {
            return Results.Problem(title: exception.Message, statusCode: (int)exception.Status);
        }
        catch (InvalidOperationException exception)
        {
            return Results.Problem(title: "The stems could not be prepared for Logic.", detail: exception.Message, statusCode: StatusCodes.Status500InternalServerError);
        }
        finally
        {
            foreach (var stream in vocals)
            {
                if (stream is not null)
                {
                    await stream.DisposeAsync();
                }
            }
            if (Directory.Exists(stemFolder))
            {
                Directory.Delete(stemFolder, recursive: true);
            }
        }
        IReadOnlyList<Diagnostic> diagnostics = [.. source.Read, .. built.Diagnostics];
        return built.Zip is { } zip
            ? Package(zip, diagnostics, packageName, context)
            : Results.Json(
                new ConversionResult(false, null, null, diagnostics),
                YueToLogicJsonContext.Default.ConversionResult,
                statusCode: StatusCodes.Status422UnprocessableEntity);
    }

    /// <summary>
    /// Converts the score and writes the project into a temporary file (deleted once sent): ZipArchive writes
    /// synchronously, which Kestrel does not allow on the response, and the ZIP is as large as the FLAC.
    /// </summary>
    /// <returns>The ZIP, positioned at its start, or null with the diagnostics that say why there is none.</returns>
    private static async Task<(FileStream? Zip, IReadOnlyList<Diagnostic> Diagnostics)> BuildAsync(
        IScoreConverter converter,
        ILogicProjectWriter writer,
        Stream score,
        LogicAudio audio,
        ConversionOptions options,
        string name,
        bool splitSections,
        IReadOnlyDictionary<string, LogicInstrument> instruments,
        CancellationToken cancellationToken)
    {
        var converted = await converter.ConvertAsync(score, options, cancellationToken);
        if (!converted.Success)
        {
            return (null, converted.Diagnostics);
        }

        var zip = new FileStream(
            Path.Combine(Path.GetTempPath(), $"yueui-logic-{Guid.NewGuid():N}.zip"),
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 81920,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        LogicProjectResult logic;
        try
        {
            using var sink = new ZipLogicPackageSink(zip, name);
            logic = await writer.WriteAsync(
                converted.Score!,
                audio,
                sink,
                new LogicProjectOptions
                {
                    ProjectName = name,
                    SplitRegionsAtSections = splitSections,
                    Channels = options.MidiChannels,
                    Instruments = instruments,
                },
                cancellationToken);
        }
        catch
        {
            await zip.DisposeAsync();
            throw;
        }

        IReadOnlyList<Diagnostic> diagnostics = [.. converted.Diagnostics, .. logic.Diagnostics];
        if (!logic.Success)
        {
            await zip.DisposeAsync();
            return (null, diagnostics);
        }
        zip.Position = 0;
        return (zip, diagnostics);
    }

    private static IResult Package(FileStream zip, IReadOnlyList<Diagnostic> diagnostics, string name, HttpContext context)
    {
        var warnings = diagnostics.Where(d => d.Severity != DiagnosticSeverity.Info).ToArray();
        if (warnings.Length > 0)
        {
            // JSON escapes non-ASCII characters by default, so the value is a valid header.
            context.Response.Headers[DiagnosticsHeader] = JsonSerializer.Serialize(warnings, CompactJson.DiagnosticArray);
        }
        return Results.File(zip, "application/zip", $"{name}.logicx.zip");
    }

    /// <summary>
    /// The bass and drums of a library song's stem sets (form fields <c>bassStems</c>, <c>drumStems</c>: a set's id) as
    /// MIDI, which take the place of the generated ones; none without either field.
    /// </summary>
    private static async Task<(RecordedParts? Parts, IResult? Problem)> RecordedAsync(
        string? bassStems,
        string? drumStems,
        (Stream? Score, string? Directory, string? Run, string? Song, IResult? Problem, IReadOnlyList<Diagnostic> Read) source,
        SqliteStemStore store,
        StemParts parts,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(bassStems) && string.IsNullOrWhiteSpace(drumStems))
        {
            return (null, null);
        }
        if (source.Directory is null)
        {
            return (null, BadRequest("Stems need a song", "Bass and drums from stems can only come from a song of the library."));
        }
        var tempo = AudioMidiEndpoints.SongTempo(source.Directory);
        try
        {
            async Task<byte[]?> PartAsync(string? id, string stem)
            {
                if (string.IsNullOrWhiteSpace(id))
                {
                    return null;
                }
                var set = store.Get(id);
                if (set is null || set.SongId != $"{source.Run}/{source.Song}" || set.Stage != "done" || set.Stems.All(s => s.Name != stem))
                {
                    throw new AudioMidiException($"These stems do not exist, are not finished for this song or have no {stem}.", StatusCodes.Status404NotFound);
                }
                if (!parts.Installed)
                {
                    throw new AudioMidiException("Basic Pitch is not installed: run deploy/install-midi.sh on the Mac (or deploy/setup-mac.sh with --midi).", StatusCodes.Status501NotImplemented);
                }
                return await parts.MidiAsync(set, stem, tempo, cancellationToken);
            }

            var bass = await PartAsync(bassStems, StemParts.BassStem);
            var drums = await PartAsync(drumStems, StemParts.DrumStem);
            return (new RecordedParts(bass, drums), null);
        }
        catch (AudioMidiException exception)
        {
            return (null, Results.Problem(title: exception.Message, statusCode: exception.Status));
        }
    }

    /// <summary>
    /// The score of a library song (<c>run/songN</c>) or an uploaded one; exactly one of them must be given. An uploaded
    /// MIDI file (a melody to add backing vocals to, say) is read into a score first; what that reading said comes back
    /// in <c>Read</c>, to be shown with the conversion's own diagnostics.
    /// </summary>
    private static async Task<(Stream? Score, string? Directory, string? Run, string? Song, IResult? Problem, IReadOnlyList<Diagnostic> Read)> OpenScoreAsync(
        string? song,
        IFormFile? file,
        SongLibrary library,
        IMidiToAbcConverter midiConverter,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(song))
        {
            var parts = song.Split('/');
            if (parts.Length != 2
                || library.SongDirectory(parts[0], parts[1]) is not { } directory
                || !File.Exists(Path.Combine(directory, "score.abc")))
            {
                return (null, null, null, null, Results.NotFound(), []);
            }
            return (File.OpenRead(Path.Combine(directory, "score.abc")), directory, parts[0], parts[1], null, []);
        }
        if (file is null || file.Length == 0)
        {
            return (null, null, null, null, BadRequest("Missing score", "Name a song as form field 'song' or send a score.abc or MIDI file as form field 'file'."), []);
        }
        if (file.Length > MaxScoreBytes)
        {
            return (null, null, null, null, BadRequest("Score file too large", $"A score.abc or MIDI file is at most {MaxScoreBytes / 1024} KB here."), []);
        }

        var upload = new MemoryStream();
        await using (var stream = file.OpenReadStream())
        {
            await stream.CopyToAsync(upload, cancellationToken);
        }
        if (!IsMidi(upload.GetBuffer().AsSpan(0, (int)upload.Length)))
        {
            upload.Position = 0;
            return (upload, null, null, null, null, []);
        }

        var read = midiConverter.Convert(upload.ToArray());
        if (!read.Success || read.Abc is not { Length: > 0 } abc)
        {
            return (null, null, null, null, Results.Json(
                new ConversionResult(false, null, null, read.Diagnostics),
                YueToLogicJsonContext.Default.ConversionResult,
                statusCode: StatusCodes.Status422UnprocessableEntity), []);
        }
        return (new MemoryStream(System.Text.Encoding.UTF8.GetBytes(abc)), null, null, null, null, read.Diagnostics);
    }

    /// <summary>A Standard MIDI File starts with its header chunk, whatever the file is called.</summary>
    private static bool IsMidi(ReadOnlySpan<byte> content) => content.StartsWith("MThd"u8);

    private static ConversionOptions? ParseOptions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new ConversionOptions();
        }
        try
        {
            return JsonSerializer.Deserialize(json, YueToLogicJsonContext.Default.ConversionOptions) ?? new ConversionOptions();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The instruments the tracks play, from the form field of that name: track name → name, port and channel as JSON.
    /// The browser sends what its routing table shows, so the export needs no look into the store.
    /// </summary>
    private static (IReadOnlyDictionary<string, LogicInstrument> Instruments, IResult? Problem) ParseInstruments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return (new Dictionary<string, LogicInstrument>(), null);
        }
        Dictionary<string, LogicInstrument>? instruments;
        try
        {
            instruments = JsonSerializer.Deserialize(json, YueToLogicJsonContext.Default.DictionaryStringLogicInstrument);
        }
        catch (JsonException exception)
        {
            return (new Dictionary<string, LogicInstrument>(), BadRequest("Invalid instruments", $"Form field 'instruments' is not valid JSON: {exception.Message}"));
        }
        foreach (var (track, instrument) in instruments ?? [])
        {
            if (string.IsNullOrWhiteSpace(instrument.Name))
            {
                return (new Dictionary<string, LogicInstrument>(), BadRequest("Invalid instruments", $"The instrument of track '{track}' has no name."));
            }
            if (instrument.Channel is not (>= 1 and <= 16))
            {
                return (new Dictionary<string, LogicInstrument>(), BadRequest("Invalid instruments", $"The channel of track '{track}' must be between 1 and 16, not {instrument.Channel}."));
            }
        }
        return (instruments ?? [], null);
    }

    /// <summary>A safe file name from the requested name, else from the song or the score's file name.</summary>
    private static string PackageName(string? requested, string fallback)
    {
        var candidate = string.IsNullOrWhiteSpace(requested) ? fallback : requested;
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':']).ToHashSet();
        var cleaned = new string(candidate.Where(c => !invalid.Contains(c) && !char.IsControl(c)).ToArray()).Trim().TrimStart('.');
        if (cleaned.Length > 100)
        {
            cleaned = cleaned[..100];
        }
        return cleaned.Length == 0 ? "Tonwerk" : cleaned;
    }

    private static async Task<IResult> MidiToAbcAsync(IFormFile? file, IMidiToAbcConverter converter, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return Results.Problem(title: "Send the MIDI file as form field 'file'.", statusCode: StatusCodes.Status400BadRequest);
        }
        if (file.Length > MaxMidiBytes)
        {
            return Results.Problem(
                title: $"A MIDI file may have at most {MaxMidiBytes / (1024 * 1024)} MB.",
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        await using var midi = file.OpenReadStream();
        var result = await converter.ConvertAsync(midi, new MidiToAbcOptions(), cancellationToken);
        if (!result.Success || result.Abc is not { Length: > 0 } abc)
        {
            const string title = "No score can be read from this MIDI file.";
            return Results.Problem(
                title: title,
                detail: string.Join(" ", [title, .. Messages(result.Diagnostics, DiagnosticSeverity.Error)]),
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }
        return Results.Ok(new MidiScore(abc, Messages(result.Diagnostics, DiagnosticSeverity.Warning)));
    }

    private static IResult SavePreset(string name, [FromBody] LogicPresetInput input, SqliteLogicPresetStore store)
    {
        if (input.Problems(name) is { Count: > 0 } problems)
        {
            return BadRequest("Invalid preset", string.Join("; ", problems) + ".");
        }
        var (preset, created) = store.Save(name.Trim(), input.Form!.Value);
        return created ? Results.Created($"/api/logic/presets/{Uri.EscapeDataString(preset.Name)}", preset) : Results.Ok(preset);
    }

    /// <summary>
    /// Takes over what yue-to-logic-pro's own server kept. Invalid entries are skipped and named rather than failing
    /// the whole import, and running it twice changes nothing, since instruments and presets are matched by name.
    /// </summary>
    private static IResult Import([FromBody] LogicImport import, SqliteInstrumentStore instruments, SqliteLogicPresetStore presets)
    {
        var skipped = new List<string>();
        var valid = new List<(long, InstrumentValues)>();
        foreach (var instrument in import.Instruments ?? [])
        {
            var input = new InstrumentInput(instrument.Name, instrument.Port, instrument.Channel, instrument.Kind, instrument.Drums);
            if (input.Problems() is { Count: > 0 } problems)
            {
                skipped.Add($"instrument '{instrument.Name}': {string.Join("; ", problems)}");
            }
            else
            {
                valid.Add((instrument.Id, input.Cleaned()));
            }
        }
        var assignments = (import.Assignments ?? new Dictionary<string, long>())
            .Where(entry => entry.Key.Trim().Length is > 0 and <= InstrumentEndpoints.MaxTrackLength)
            .ToDictionary(entry => entry.Key.Trim(), entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        var (instrumentCount, assignmentCount) = instruments.Import(valid, assignments);

        var validPresets = new List<(string, JsonElement)>();
        foreach (var preset in import.Presets ?? [])
        {
            var input = new LogicPresetInput(preset.Form);
            if (input.Problems(preset.Name ?? "") is { Count: > 0 } problems)
            {
                skipped.Add($"preset '{preset.Name}': {string.Join("; ", problems)}");
            }
            else
            {
                validPresets.Add((preset.Name!.Trim(), preset.Form!.Value));
            }
        }
        var presetCount = presets.SaveAll(validPresets);
        return Results.Ok(new LogicImportResult(instrumentCount, assignmentCount, presetCount, skipped));
    }

    private static List<string> Messages(IEnumerable<Diagnostic> diagnostics, DiagnosticSeverity severity) =>
        diagnostics.Where(d => d.Severity == severity).Select(d => d.Message).ToList();

    private static IResult BadRequest(string title, string detail) =>
        Results.Problem(title: title, detail: detail, statusCode: StatusCodes.Status400BadRequest);
}
