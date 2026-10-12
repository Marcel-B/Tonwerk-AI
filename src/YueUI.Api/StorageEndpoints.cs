using YueUI.Api.Storage;

namespace YueUI.Api;

/// <summary>
/// Where the disk went (<see cref="StorageReport"/>) and clearing what nothing points to any more. The pieces themselves
/// are deleted through their own endpoints (songs, videos, stems, …), which know what is still being worked on.
/// </summary>
public static class StorageEndpoints
{
    public static RouteGroupBuilder MapStorageEndpoints(this RouteGroupBuilder api)
    {
        // Walking a few thousand files is disk work, not something to hold a request thread for.
        api.MapGet("/storage/usage", (StorageReport report) => Task.Run(report.Measure));
        api.MapPost("/storage/leftovers/delete", (StorageReport report, ILogger<StorageReport> logger) =>
        {
            var removed = report.RemoveLeftovers();
            logger.LogInformation("Removed {Count} leftover files and folders ({Bytes} bytes)", removed.Count, removed.Bytes);
            return removed;
        });
        return api;
    }
}
