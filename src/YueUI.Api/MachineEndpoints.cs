using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using YueUI.Api.Lyrics;
using YueUI.Api.Machines;
using YueUI.Api.Memory;
using YueUI.Api.Worker;

namespace YueUI.Api;

/// <summary>
/// The Macs a job can run on (<c>GET /api/machines</c>, for the pickers), and <c>/api/helper</c>, where this Mac
/// computes for another Tonwerk when <see cref="MachineOptions.Serve"/> is on. The helper routes are for that other
/// server, not the apps, so they stay out of the OpenAPI description.
/// </summary>
public static class MachineEndpoints
{
    public static RouteGroupBuilder MapMachineEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/machines", (Machines.Machines machines, EngineInventory inventory) => Results.Ok(machines.List(inventory.Installed())))
            .WithName("ListMachines")
            .Produces<IReadOnlyList<MachineInfo>>(StatusCodes.Status200OK);

        var helper = api.MapGroup("/helper").ExcludeFromDescription().AddEndpointFilter(async (context, next) =>
        {
            var options = context.HttpContext.RequestServices.GetRequiredService<IOptions<MachineOptions>>().Value;
            if (!options.Serve)
            {
                return Results.NotFound();
            }
            var key = options.ResolveKey();
            var given = context.HttpContext.Request.Headers[HelperClient.KeyHeader].ToString();
            if (key is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(given)))
            {
                return Results.Problem(detail: "Wrong or missing key.", statusCode: StatusCodes.Status401Unauthorized);
            }
            return await next(context);
        });
        helper.MapGet("/status", (IOptions<MachineOptions> options, ModelMemory memory, WorkerHost host, EngineInventory inventory, Ace.IAceEngine ace) =>
        {
            var engines = inventory.Installed();
            return Results.Ok(new HelperStatus(
                string.IsNullOrWhiteSpace(options.Value.Name) ? Environment.MachineName : options.Value.Name,
                memory.Holder,
                host.InUse,
                engines,
                engines.Contains(Engines.Ace) ? ace.Files() : null));
        });
        helper.MapGet("/lyrics/models", async (LyricsWriter writer, CancellationToken cancellationToken) =>
        {
            try
            {
                var models = await writer.ListModelsAsync(cancellationToken);
                // Claude runs at Anthropic from either Mac; the other one lists it itself.
                return Results.Ok(models with { Models = [.. models.Models.Where(m => !m.Cloud)] });
            }
            catch (LyricsUnavailableException exception)
            {
                return Results.Problem(detail: exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });
        helper.MapPost("/runs/{kind}", (string kind, HttpContext context, HelperRuns runs) => runs.RunAsync(kind, context))
            .DisableAntiforgery()
            .WithFormOptions(multipartBodyLengthLimit: 4L * 1024 * 1024 * 1024);
        helper.MapGet("/runs/{run}/files/{name}", (string run, string name) =>
        {
            if (!HelperRuns.IsRunId(run) || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '_' and not '-') || name.StartsWith('.'))
            {
                return Results.NotFound();
            }
            var path = Path.Combine(HelperRuns.OutputFolder(run), name);
            return File.Exists(path) ? Results.File(path, "application/octet-stream", enableRangeProcessing: true) : Results.NotFound();
        });
        helper.MapDelete("/runs/{run}", (string run) =>
        {
            HelperRuns.Forget(run);
            return Results.NoContent();
        });
        return api;
    }
}
