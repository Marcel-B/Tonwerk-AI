using System.Globalization;
using System.Text.Json;
using YueUI.Api.Export;
using YueUI.Api.Images;

namespace YueUI.Api.Data;

/// <param name="Path">The image file in <c>covers/</c> next to the database.</param>
/// <param name="UpdatedAt">When it was set; the library sends it along so a browser asks for a new one after a change.</param>
public sealed record SongCover(string SongId, string Path, string ContentType, DateTimeOffset UpdatedAt);

/// <summary>
/// Covers of songs, by song id (<c>run/songN</c>), so a photo chosen once stays with the song for the library, the
/// player and every later export. The images lie in <c>covers/</c> next to the database, like the versions and stems:
/// YuE Studio's song folders stay untouched. A song without a row has the cover the browser draws.
/// </summary>
public sealed class SqliteCoverStore(SqliteDatabase database)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Folder => Path.Combine(database.Directory, "covers");

    /// <summary>When each song's cover was set.</summary>
    public IReadOnlyDictionary<string, DateTimeOffset> All()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT song_id, updated_at FROM song_covers";
        using var reader = command.ExecuteReader();
        var covers = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        while (reader.Read())
        {
            covers[reader.GetString(0)] = Parse(reader.GetString(1));
        }
        return covers;
    }

    /// <summary>The song's cover, or null without one or when its file went missing.</summary>
    public SongCover? Get(string songId)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT file, content_type, updated_at FROM song_covers WHERE song_id = $song";
        command.Parameters.AddWithValue("$song", songId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }
        var path = Path.Combine(Folder, reader.GetString(0));
        return File.Exists(path) ? new SongCover(songId, path, reader.GetString(1), Parse(reader.GetString(2))) : null;
    }

    /// <summary>How the song's cover was painted; null for a photo, the drawn cover or one taken before this was kept.</summary>
    public CoverPainting? Painting(string songId)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT painting FROM song_covers WHERE song_id = $song";
        command.Parameters.AddWithValue("$song", songId);
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<CoverPainting>(json, Json) : null;
    }

    /// <summary>
    /// Replaces the song's cover; the old image is deleted once the row points at the new one. A photo clears what the
    /// painted cover before it was painted with.
    /// </summary>
    public SongCover Set(string songId, CoverImage image, CoverPainting? painting = null)
    {
        Directory.CreateDirectory(Folder);
        var file = $"{Guid.NewGuid():N}{(image.MimeType == "image/png" ? ".png" : ".jpg")}";
        var path = Path.Combine(Folder, file);
        File.WriteAllBytes(path, image.Data);
        var previous = FileOf(songId);
        var updated = DateTimeOffset.UtcNow;
        try
        {
            using var connection = database.Open();
            SqliteDatabase.Execute(
                connection,
                null,
                """
                INSERT INTO song_covers (song_id, file, content_type, updated_at, painting)
                VALUES ($song, $file, $type, $updated, $painting)
                ON CONFLICT (song_id) DO UPDATE SET
                    file = excluded.file, content_type = excluded.content_type, updated_at = excluded.updated_at,
                    painting = excluded.painting
                """,
                ("$song", songId),
                ("$painting", painting is null ? DBNull.Value : JsonSerializer.Serialize(painting, Json)),
                ("$file", file),
                ("$type", image.MimeType),
                ("$updated", updated.ToString("O", CultureInfo.InvariantCulture)));
        }
        catch
        {
            File.Delete(path);
            throw;
        }
        DeleteFile(previous);
        return new SongCover(songId, path, image.MimeType, updated);
    }

    /// <returns>False when the song had no cover.</returns>
    public bool Remove(string songId)
    {
        var file = FileOf(songId);
        using var connection = database.Open();
        var removed = SqliteDatabase.Execute(connection, null, "DELETE FROM song_covers WHERE song_id = $song", ("$song", songId)) > 0;
        DeleteFile(file);
        return removed;
    }

    /// <summary>Every song of the run; a run name never contains a slash, so the prefix cannot reach another run.</summary>
    public void RemoveRun(string run)
    {
        foreach (var songId in All().Keys.Where(id => id.StartsWith($"{run}/", StringComparison.Ordinal)))
        {
            Remove(songId);
        }
    }

    private string? FileOf(string songId)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT file FROM song_covers WHERE song_id = $song";
        command.Parameters.AddWithValue("$song", songId);
        return command.ExecuteScalar() as string;
    }

    private void DeleteFile(string? file)
    {
        if (file is not null)
        {
            File.Delete(Path.Combine(Folder, file));
        }
    }

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
