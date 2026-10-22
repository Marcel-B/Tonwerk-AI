using System.Text;
using Microsoft.Extensions.Options;
using YueUI.Api.Sketches;

namespace YueUI.Api;

/// <summary>
/// Written song sketches planned by Claude or a model in LM Studio (<see cref="SketchMaker"/>): a sketch in, a plan out after a few minutes,
/// and from the plan a score with lyrics for the create form, a MIDI draft and the score as a file.
/// </summary>
public static class SketchEndpoints
{
    public static RouteGroupBuilder MapSketchEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/sketches/info", (SketchMaker maker, CancellationToken cancellationToken) => maker.InfoAsync(cancellationToken))
            .WithName("GetSketchInfo")
            .Produces<SketchInfo>(StatusCodes.Status200OK);
        api.MapGet("/sketches", async (SketchMaker maker) =>
        {
            await maker.Loaded;
            return maker.Jobs().Select(SketchSummary.Of).ToList();
        })
            .WithName("ListSketches")
            .Produces<List<SketchSummary>>(StatusCodes.Status200OK);
        api.MapPost("/sketches", async (SketchRequest request, SketchMaker maker, IOptions<SketchOptions> options, CancellationToken cancellationToken) =>
        {
            var limit = options.Value.MaxSketchCharacters;
            if (string.IsNullOrWhiteSpace(request.Text))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["text"] = ["A sketch is required."] });
            }
            if (request.Text.Length > limit || (request.Instruction?.Length ?? 0) > 4000)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["text"] = [$"At most {limit} characters."] });
            }
            if (maker.IsLocal(request.Model))
            {
                // A local model must be one LM Studio has; any other id would fail only once its turn came.
                var local = (await maker.InfoAsync(cancellationToken)).Local ?? [];
                if (!local.Any(m => m.Id == request.Model!.Trim()))
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["model"] = [$"One of {string.Join(", ", options.Value.Offered.Concat(local.Select(m => m.Id)))}."],
                    });
                }
            }
            else if (!maker.Info.Configured)
            {
                return Results.Problem(
                    $"No Anthropic API key: set ANTHROPIC_API_KEY or write it into {options.Value.ResolvedKeyFile}, or choose a model in LM Studio.",
                    statusCode: StatusCodes.Status501NotImplemented);
            }
            var job = maker.Enqueue(request.Text, request.Instruction, request.Model);
            return Results.Accepted($"/api/sketches/{job.Id}", SketchSummary.Of(job));
        })
            .WithName("CreateSketch")
            .Produces<SketchSummary>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status501NotImplemented);
        api.MapGet("/sketches/{id}", async (string id, SketchMaker maker) =>
        {
            await maker.Loaded;
            if (maker.Get(id) is not { } job)
            {
                return Results.NotFound();
            }
            // A sketch still being written has no plan to make anything from yet.
            return job.Plan is null ? Results.Ok(new { job }) : Results.Ok(SketchScore.Result(job));
        })
            .WithName("GetSketch")
            // Before the plan only its job is there; the rest of the result follows with the plan.
            .Produces<SketchResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);
        api.MapGet("/sketches/{id}/midi", async (string id, SketchMaker maker) =>
        {
            await maker.Loaded;
            if (maker.Get(id) is not { Plan: { } plan } job)
            {
                return Results.NotFound();
            }
            return Results.File(SketchArranger.Midi(plan), "audio/midi", $"{FileName(job.Title)}.mid");
        })
            .WithName("DownloadSketchMidi")
            .ProducesFile("audio/midi")
            .Produces(StatusCodes.Status404NotFound);
        api.MapGet("/sketches/{id}/score", async (string id, SketchMaker maker) =>
        {
            await maker.Loaded;
            if (maker.Get(id) is not { Plan: not null } job)
            {
                return Results.NotFound();
            }
            var abc = SketchScore.Result(job).Abc;
            return Results.File(Encoding.UTF8.GetBytes(abc), "text/plain; charset=utf-8", $"{FileName(job.Title)}.abc");
        })
            .WithName("DownloadSketchScore")
            .ProducesFile("text/plain")
            .Produces(StatusCodes.Status404NotFound);
        api.MapDelete("/sketches/{id}", async (string id, SketchMaker maker) =>
        {
            await maker.Loaded;
            if (maker.Get(id) is not { } job)
            {
                return Results.NotFound();
            }
            maker.Delete(job);
            return Results.NoContent();
        })
            .WithName("DeleteSketch")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);
        return api;
    }

    /// <summary>The title as a file name: letters, digits, spaces and dashes.</summary>
    public static string FileName(string title)
    {
        var name = new string(title.Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' ? c : ' ').ToArray());
        name = string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return name.Length == 0 ? "sketch" : name;
    }
}
