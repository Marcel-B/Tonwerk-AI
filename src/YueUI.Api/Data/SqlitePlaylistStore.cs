using Microsoft.Data.Sqlite;

namespace YueUI.Api.Data;

/// <summary>A playlist as stored: its songs (<c>run/songN</c>) in the order they play.</summary>
public sealed record StoredPlaylist(long Id, string Name, IReadOnlyList<string> SongIds);

/// <summary>
/// The playlists and their songs. On the server rather than in the browser, because the phone's home-screen app, its
/// Safari and the Mac each have their own storage and should still share the same playlists.
/// </summary>
/// <remarks>
/// Songs deleted since stay in the table; readers skip them (<see cref="PlaylistEndpoints"/>). There is always at least
/// one playlist (the one schema version 1 created), so "add to playlist" always has somewhere to go.
/// </remarks>
public sealed class SqlitePlaylistStore(SqliteDatabase database)
{
    /// <summary>Every playlist, oldest first, with its songs.</summary>
    public IReadOnlyList<StoredPlaylist> List()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.id, p.name, s.song_id
            FROM playlists p LEFT JOIN playlist_songs s ON s.playlist_id = p.id
            ORDER BY p.id, s.position
            """;
        using var reader = command.ExecuteReader();
        var playlists = new List<StoredPlaylist>();
        List<string>? songIds = null;
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            if (playlists.Count == 0 || playlists[^1].Id != id)
            {
                songIds = [];
                playlists.Add(new StoredPlaylist(id, reader.GetString(1), songIds));
            }
            if (!reader.IsDBNull(2))
            {
                songIds!.Add(reader.GetString(2));
            }
        }
        return playlists;
    }

    public StoredPlaylist? Find(long playlistId) => List().FirstOrDefault(p => p.Id == playlistId);

    public StoredPlaylist Create(string name)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        // Not INSERT ... RETURNING: its row is handed out before the implicit commit, so a commit that finds another
        // connection reading fails at once with "database is locked" instead of waiting like other statements do.
        command.CommandText = "INSERT INTO playlists (name) VALUES ($name)";
        command.Parameters.AddWithValue("$name", name);
        command.ExecuteNonQuery();
        command.CommandText = "SELECT last_insert_rowid()";
        command.Parameters.Clear();
        return new StoredPlaylist((long)command.ExecuteScalar()!, name, []);
    }

    /// <returns>False when there is no such playlist.</returns>
    public bool Rename(long playlistId, string name)
    {
        using var connection = database.Open();
        return SqliteDatabase.Execute(connection, null, "UPDATE playlists SET name = $name WHERE id = $playlist", ("$name", name), ("$playlist", playlistId)) > 0;
    }

    /// <summary>Replaces the playlist's songs in one transaction; the caller passes each song once.</summary>
    /// <returns>False when there is no such playlist.</returns>
    public bool Replace(long playlistId, IReadOnlyList<string> songIds)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        if (!Exists(connection, transaction, playlistId))
        {
            return false;
        }
        SqliteDatabase.Execute(connection, transaction, "DELETE FROM playlist_songs WHERE playlist_id = $playlist", ("$playlist", playlistId));
        for (var position = 0; position < songIds.Count; position++)
        {
            SqliteDatabase.Execute(
                connection,
                transaction,
                "INSERT INTO playlist_songs (playlist_id, position, song_id) VALUES ($playlist, $position, $song)",
                ("$playlist", playlistId),
                ("$position", position),
                ("$song", songIds[position]));
        }
        transaction.Commit();
        return true;
    }

    /// <summary>Deletes a playlist with its songs, unless it is the last one.</summary>
    public PlaylistDeletion Delete(long playlistId)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        if (!Exists(connection, transaction, playlistId))
        {
            return PlaylistDeletion.NotFound;
        }
        using var count = connection.CreateCommand();
        count.Transaction = transaction;
        count.CommandText = "SELECT COUNT(*) FROM playlists";
        if ((long)count.ExecuteScalar()! <= 1)
        {
            return PlaylistDeletion.LastOne;
        }
        SqliteDatabase.Execute(connection, transaction, "DELETE FROM playlists WHERE id = $playlist", ("$playlist", playlistId));
        transaction.Commit();
        return PlaylistDeletion.Deleted;
    }

    private static bool Exists(SqliteConnection connection, SqliteTransaction transaction, long playlistId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM playlists WHERE id = $playlist";
        command.Parameters.AddWithValue("$playlist", playlistId);
        return command.ExecuteScalar() is not null;
    }
}

public enum PlaylistDeletion
{
    Deleted,
    NotFound,
    LastOne,
}
