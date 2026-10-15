using System.Text;
using Microsoft.Extensions.Options;
using YueUI.Api.Sketches;

namespace YueUI.Api;

/// <summary>
/// Written song sketches planned by Claude (<see cref="SketchMaker"/>): a sketch in, a plan out after a few minutes,
/// and from the plan a score with lyrics for the create form, a MIDI draft and the score as a file.
/// </summary>
public static class SketchEndpoints
{
    public static RouteGroupBuilder MapSketchEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/sketches/info", (SketchMaker maker) => maker.Info);
        api.MapGet("/sketches", async (SketchMaker maker) =>
        {
            await maker.Loaded;
            return maker.Jobs().Select(SketchSummary.Of).ToList();
        });
        api.MapPost("/sketches", (SketchRequest request, SketchMaker maker, IOptions<SketchOptions> options) =>
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
            if (!maker.Info.Configured)
            {
                return Results.Problem(
                    $"No Anthropic API key: set ANTHROPIC_API_KEY or write it into {options.Value.ResolvedKeyFile}.",
                    statusCode: StatusCodes.Status501NotImplemented);
            }
            var job = maker.Enqueue(request.Text, request.Instruction);
            return Results.Accepted($"/api/sketches/{job.Id}", SketchSummary.Of(job));
        });
        api.MapGet("/sketches/{id}", async (string id, SketchMaker maker) =>
        {
            await maker.Loaded;
            if (maker.Get(id) is not { } job)
            {
                return Results.NotFound();
            }
            // A sketch still being written has no plan to make anything from yet.
            return job.Plan is null ? Results.Ok(new { job }) : Results.Ok(SketchScore.Result(job));
        });
        api.MapGet("/sketches/{id}/midi", async (string id, SketchMaker maker) =>
        {
            await maker.Loaded;
            if (maker.Get(id) is not { Plan: { } plan } job)
            {
                return Results.NotFound();
            }
            return Results.File(SketchArranger.Midi(plan), "audio/midi", $"{FileName(job.Title)}.mid");
        });
        api.MapGet("/sketches/{id}/score", async (string id, SketchMaker maker) =>
        {
            await maker.Loaded;
            if (maker.Get(id) is not { Plan: not null } job)
            {
                return Results.NotFound();
            }
            var abc = SketchScore.Result(job).Abc;
            return Results.File(Encoding.UTF8.GetBytes(abc), "text/plain; charset=utf-8", $"{FileName(job.Title)}.abc");
        });
        api.MapDelete("/sketches/{id}", async (string id, SketchMaker maker) =>
        {
            await maker.Loaded;
            if (maker.Get(id) is not { } job)
            {
                return Results.NotFound();
            }
            maker.Delete(job);
            return Results.NoContent();
        });
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
