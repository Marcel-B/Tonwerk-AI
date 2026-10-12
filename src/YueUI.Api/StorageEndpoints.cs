using YueUI.Api.Storage;

namespace YueUI.Api;

/// <summary>
/// How the disk is shared among songs, videos, stems, models and the rest (<see cref="StorageSurvey"/>). Deleting goes
/// through each part's own endpoint, which already knows what may not go yet.
/// </summary>
public static class StorageEndpoints
{
    public static RouteGroupBuilder MapStorageEndpoints(this RouteGroupBuilder api)
    {
        // Walking the model caches reads a few thousand directory entries; not on the request thread.
        api.MapGet("/storage/usage", (StorageSurvey survey) => Task.Run(survey.Survey));
        return api;
    }
}
