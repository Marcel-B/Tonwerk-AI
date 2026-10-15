using YueUI.Api.Data;
using YueUI.Api.Library;

namespace YueUI.Api;

/// <param name="SongIds">Song ids (<c>run/songN</c>) in the order they play.</param>
public sealed record PlaylistInfo(long Id, string Name, IReadOnlyList<string> SongIds);

public sealed record PlaylistSongsRequest(IReadOnlyList<string>? SongIds);

public sealed record PlaylistNameRequest(string? Name);

/// <summary>
/// The playlists: list them, create, rename and delete one, or replace one's songs as a whole (adding, removing and
/// moving are all done in the browser). The last playlist cannot be deleted.
/// </summary>
public static class PlaylistEndpoints
{
    public const int MaxNameLength = 100;

    public static RouteGroupBuilder MapPlaylistEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/playlists", (SqlitePlaylistStore store, SongLibrary library) =>
            store.List().Select(p => Info(p, library)).ToList())
            .WithName("ListPlaylists")
            .Produces<List<PlaylistInfo>>(StatusCodes.Status200OK);

        api.MapPost("/playlists", (PlaylistNameRequest request, SqlitePlaylistStore store) =>
        {
            if (ValidName(request.Name) is not { } name)
            {
                return NameProblem();
            }
            var created = store.Create(name);
            return Results.Created($"/api/playlists/{created.Id}", new PlaylistInfo(created.Id, created.Name, []));
        })
            .WithName("CreatePlaylist")
            .Produces<PlaylistInfo>(StatusCodes.Status201Created)
            .ProducesValidationProblem();

        api.MapPut("/playlists/{id:long}/name", (long id, PlaylistNameRequest request, SqlitePlaylistStore store, SongLibrary library) =>
        {
            if (ValidName(request.Name) is not { } name)
            {
                return NameProblem();
            }
            return store.Rename(id, name) && store.Find(id) is { } renamed ? Results.Ok(Info(renamed, library)) : Results.NotFound();
        })
            .WithName("RenamePlaylist")
            .Produces<PlaylistInfo>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesValidationProblem();

        api.MapPut("/playlists/{id:long}/songs", (long id, PlaylistSongsRequest request, SqlitePlaylistStore store, SongLibrary library) =>
        {
            var songIds = request.SongIds ?? [];
            if (songIds.FirstOrDefault(songId => !IsSongId(songId, library)) is { } invalid)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["songIds"] = [$"Not a song of the library: {invalid}"],
                });
            }
            // One entry per song, as the page shows it; a song twice would play twice after reordering.
            return store.Replace(id, [.. songIds.Distinct(StringComparer.Ordinal)]) && store.Find(id) is { } stored
                ? Results.Ok(Info(stored, library))
                : Results.NotFound();
        })
            .WithName("SetPlaylistSongs")
            .Produces<PlaylistInfo>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesValidationProblem();

        api.MapDelete("/playlists/{id:long}", (long id, SqlitePlaylistStore store) => store.Delete(id) switch
        {
            PlaylistDeletion.Deleted => Results.NoContent(),
            PlaylistDeletion.LastOne => Results.Problem("The last playlist cannot be deleted.", statusCode: StatusCodes.Status409Conflict),
            _ => Results.NotFound(),
        })
            .WithName("DeletePlaylist")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        return api;
    }

    /// <summary>A deleted song stays in the database (it costs nothing) but is not handed out.</summary>
    private static PlaylistInfo Info(StoredPlaylist playlist, SongLibrary library) =>
        new(playlist.Id, playlist.Name, [.. playlist.SongIds.Where(id => IsSongId(id, library))]);

    private static string? ValidName(string? name) =>
        name?.Trim() is { Length: > 0 and <= MaxNameLength } trimmed ? trimmed : null;

    private static IResult NameProblem() => Results.ValidationProblem(new Dictionary<string, string[]>
    {
        ["name"] = [$"A playlist needs a name of 1 to {MaxNameLength} characters."],
    });

    private static bool IsSongId(string? id, SongLibrary library) =>
        id?.Split('/') is [var run, var song] && library.SongDirectory(run, song) is not null;
}
