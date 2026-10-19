using System.Text.Json;
using Microsoft.Extensions.Options;
using YueUI.Api.Ace;
using YueUI.Api.Library;

namespace YueUI.Api;

/// <summary>
/// Songs made with ACE-Step 1.5 (<see cref="AceComposer"/>): the page's request in, a job out, whose progress arrives
/// as <c>ace</c> events; the finished songs are in the library like YuE2's.
/// </summary>
public static class AceEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static RouteGroupBuilder MapAceEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/ace", (IAceEngine engine, AceComposer composer, IOptions<AceOptions> options) =>
        {
            var files = engine.Files();
            return new AceInfo(
                engine.Installed,
                engine.Installed && files.Complete,
                options.Value.ResolvedBinaries,
                options.Value.ResolvedModels,
                files.Lm,
                files.Dit,
                files.Adapters,
                composer.Jobs());
        })
            .WithName("GetAce")
            .Produces<AceInfo>(StatusCodes.Status200OK);
        api.MapPost("/ace", Compose)
            .WithName("CreateAceJob")
            .Produces<AceJob>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status501NotImplemented);
        api.MapDelete("/ace/{id}", (string id, AceComposer composer) =>
        {
            if (composer.Get(id) is not { } job)
            {
                return Results.NotFound();
            }
            composer.Delete(job);
            return Results.NoContent();
        })
            .WithName("DeleteAceJob")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);
        api.MapGet("/songs/{run}/{song}/ace", (string run, string song, SongLibrary library) =>
        {
            if (library.SongDirectory(run, song) is not { } directory
                || Path.Combine(directory, AceComposer.RequestFile) is var path && !File.Exists(path))
            {
                return Results.NotFound();
            }
            try
            {
                return JsonSerializer.Deserialize<AceSongRequest>(File.ReadAllText(path), Json) is { } request
                    ? Results.Ok(request)
                    : Results.NotFound();
            }
            catch (JsonException)
            {
                return Results.NotFound();
            }
        })
            .WithName("GetSongAceRequest")
            .Produces<AceSongRequest>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);
        return api;
    }

    private static IResult Compose(
        AceSongRequest request,
        IAceEngine engine,
        AceComposer composer,
        SongLibrary library,
        Machines.Machines machines,
        IOptions<AceOptions> options)
    {
        var placeErrors = new Dictionary<string, string[]>();
        machines.TryNormalize(request.Machine, out var machine, placeErrors);
        var local = engine.Files();
        var files = FilesFor(machine, local, machines);
        var errors = Validate(request, files, library);
        foreach (var (field, messages) in placeErrors)
        {
            errors[field] = messages;
        }
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }
        if (!machines.CanRun(Machines.Engines.Ace, machine, engine.Installed && local.Complete))
        {
            return Results.Problem(
                engine.Installed
                    ? $"The ACE-Step models in {options.Value.ResolvedModels} are incomplete; run deploy/install-ace.sh."
                    : $"acestep.cpp is not built ({options.Value.ResolvedBinaries}); run deploy/install-ace.sh.",
                statusCode: StatusCodes.Status501NotImplemented);
        }
        // Chosen here rather than by acestep.cpp, so the songs' seeds are known and stored with them.
        var seed = request.Seed ?? Random.Shared.NextInt64(0, (long)uint.MaxValue + 1);
        var job = composer.Enqueue(Trimmed(request) with { Machine = machine }, seed);
        return Results.Accepted($"/api/ace/{job.Id}", job);
    }

    private static AceSongRequest Trimmed(AceSongRequest request) => request with
    {
        Title = request.Title?.Trim() is { Length: > 0 } title ? title : null,
        Caption = request.Caption?.Trim(),
        Lyrics = request.Lyrics?.Trim() is { Length: > 0 } lyrics ? lyrics : null,
        Language = string.IsNullOrWhiteSpace(request.Language) ? null : request.Language,
        Key = string.IsNullOrWhiteSpace(request.Key) ? null : request.Key.Trim(),
        NegativePrompt = string.IsNullOrWhiteSpace(request.NegativePrompt) ? null : request.NegativePrompt.Trim(),
        Adapter = string.IsNullOrWhiteSpace(request.Adapter) ? null : request.Adapter,
        Source = request.UsesLm ? null : request.Source,
    };

    /// <summary>
    /// The model files a request may name: those of the Mac it is asked for, or for whichever is free those of any (the
    /// job then fails on a Mac without them). A helper not heard from yet is taken to have this Mac's.
    /// </summary>
    private static AceModelFiles FilesFor(string? machine, AceModelFiles local, Machines.Machines machines)
    {
        if (machine == Machines.Machines.Local)
        {
            return local;
        }
        var helpers = machine is null ? machines.Helpers : [.. machines.Helpers.Where(h => h.Id == machine)];
        var theirs = helpers.Select(h => machines.StatusOf(h)?.Ace ?? local).ToList();
        if (machine is not null)
        {
            return theirs.FirstOrDefault() ?? local;
        }
        return theirs.Aggregate(local, (all, other) => new AceModelFiles(
            [.. all.Lm.Union(other.Lm)],
            [.. all.Dit.Union(other.Dit)],
            all.TextEncoder || other.TextEncoder,
            all.Vae || other.Vae,
            [.. all.Adapters.Union(other.Adapters)]));
    }

    internal static Dictionary<string, string[]> Validate(AceSongRequest request, AceModelFiles files, SongLibrary library)
    {
        var errors = new Dictionary<string, string[]>();
        void Check(bool ok, string field, string message)
        {
            if (!ok)
            {
                errors[field] = [message];
            }
        }

        var caption = request.Caption?.Trim() ?? "";
        Check(caption.Length is > 0 and <= AceSongRequest.MaxCaptionLength, "caption", $"A style, up to {AceSongRequest.MaxCaptionLength} characters.");
        Check((request.Lyrics?.Length ?? 0) <= AceSongRequest.MaxLyricsLength, "lyrics", $"Up to {AceSongRequest.MaxLyricsLength} characters.");
        Check((request.Title?.Length ?? 0) <= 120, "title", "Up to 120 characters.");
        var task = request.Task ?? "text2music";
        Check(AceSongRequest.Tasks.Contains(task), "task", $"One of {string.Join(", ", AceSongRequest.Tasks)}.");
        Check(request.Language is null or "" || AceSongRequest.Languages.Contains(request.Language), "language", "A language ACE-Step knows, e.g. de or en.");
        Check(request.Duration is null or (>= 10 and <= 600), "duration", "From 10 to 600 seconds.");
        Check(request.Bpm is null or (>= 30 and <= 300), "bpm", "From 30 to 300.");
        Check((request.Key?.Length ?? 0) <= 20, "key", "A key like C major or F# minor.");
        Check(request.TimeSignature is null || AceSongRequest.TimeSignatures.Contains(request.TimeSignature.Value), "timeSignature", "2, 3, 4 or 6.");
        Check(request.Seed is null or (>= 0 and <= uint.MaxValue), "seed", $"From 0 to {uint.MaxValue}.");
        Check(request.Batch is null or (>= 1 and <= AceSongRequest.MaxBatch), "batch", $"From 1 to {AceSongRequest.MaxBatch}.");
        Check(request.Steps is null or (>= 1 and <= 200), "steps", "From 1 to 200.");
        Check(request.Guidance is null or (>= 1 and <= 15), "guidance", "From 1 to 15.");
        Check(request.Shift is null or (>= 1 and <= 10), "shift", "From 1 to 10.");
        Check(request.Solver is null or "" || AceSongRequest.Solvers.Contains(request.Solver), "solver", $"One of {string.Join(", ", AceSongRequest.Solvers)}.");
        Check(request.LmTemperature is null or (> 0 and <= 2), "lmTemperature", "Above 0, up to 2.");
        Check(request.LmCfgScale is null or (>= 1 and <= 10), "lmCfgScale", "From 1 to 10.");
        Check(request.LmTopP is null or (> 0 and <= 1), "lmTopP", "Above 0, up to 1.");
        Check(request.LmTopK is null or (>= 0 and <= 1000), "lmTopK", "From 0 to 1000.");
        Check((request.NegativePrompt?.Length ?? 0) <= 1000, "negativePrompt", "Up to 1000 characters.");
        Check(request.AdapterScale is null or (>= 0 and <= 2), "adapterScale", "From 0 to 2.");
        Check(request.CoverStrength is null or (>= 0 and <= 1), "coverStrength", "From 0 to 1.");
        Check(request.LmModel is null || files.Lm.Contains(request.LmModel), "lmModel", "One of the LMs in the models folder.");
        Check(request.DitModel is null || files.Dit.Contains(request.DitModel), "ditModel", "One of the DiTs in the models folder.");
        Check(string.IsNullOrWhiteSpace(request.Adapter) || files.Adapters.Contains(request.Adapter), "adapter", "One of the adapters in the adapters folder.");
        if (task is "cover" or "repaint")
        {
            Check(
                request.Source?.Split('/') is [var run, var song] && library.SongDirectory(run, song) is { } directory
                    && File.Exists(Path.Combine(directory, "audio.flac")),
                "source",
                "A song of the library to start from.");
        }
        if (task == "repaint")
        {
            Check(request.RepaintStart is null or (>= -600 and <= 600), "repaintStart", "From -600 to 600 seconds.");
            Check(
                request.RepaintEnd is null || request.RepaintEnd > (request.RepaintStart ?? 0),
                "repaintEnd",
                "After the start.");
        }
        return errors;
    }
}
