using YueUI.Api.Storage;

namespace YueUI.Api;

/// <summary>Where the disk space goes (<see cref="StorageScanner"/>), and cleaning up what can be made again.</summary>
public static class StorageEndpoints
{
    public static RouteGroupBuilder MapStorageEndpoints(this RouteGroupBuilder api)
    {
        // Off the request thread: the walk over the models and songs reads a few thousand directory entries.
        api.MapGet("/storage/overview", (StorageScanner scanner) => Task.Run(scanner.Scan))
            .WithName("GetStorageOverview")
            .Produces<StorageOverview>(StatusCodes.Status200OK);
        api.MapPost("/storage/cleanup/{target}", (string target, StorageScanner scanner) =>
            StorageScanner.CleanupTargets.Contains(target) ? Results.Ok(scanner.Cleanup(target)) : Results.NotFound())
            .WithName("CleanUpStorage")
            .Produces<StorageCleanup>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);
        return api;
    }
}
