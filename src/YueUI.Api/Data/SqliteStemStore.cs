using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using YueUI.Api.Voices;

namespace YueUI.Api.Data;

/// <summary>
/// Songs split into stems (<see cref="StemSetState"/>). The files of a finished set lie in <c>stems/&lt;id&gt;/</c> next
/// to the database, like the versions in <c>versions/</c>: YuE Studio's song folders stay untouched.
/// </summary>
public sealed class SqliteStemStore(SqliteDatabase database)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Folder(string id) => Path.Combine(database.Directory, "stems", id);

    /// <summary>An upload waiting to be separated (<c>source</c> with the ending it came with), or null.</summary>
    public string? SourcePath(string id) =>
        Directory.Exists(Folder(id)) ? Directory.EnumerateFiles(Folder(id), "source*").FirstOrDefault() : null;

    /// <summary>The stored file of a stem, or null for a name the set does not have.</summary>
    public string? FilePath(StemSetState set, string name) =>
        set.Stems.FirstOrDefault(s => s.Name == name) is { } stem ? Path.Combine(Folder(set.Id), stem.File) : null;

    /// <summary>Newest first, as the page lists them.</summary>
    public IReadOnlyList<StemSetState> All() => Query("SELECT * FROM song_stems ORDER BY created_at DESC");

    public IReadOnlyList<StemSetState> Unfinished() =>
        Query("SELECT * FROM song_stems WHERE stage NOT IN ('done', 'failed', 'cancelled') ORDER BY created_at");

    public StemSetState? Get(string id) => Query("SELECT * FROM song_stems WHERE id = $id", ("$id", id)).FirstOrDefault();

    public void Add(StemSetState set)
    {
        using var connection = database.Open();
        SqliteDatabase.Execute(
            connection,
            null,
            """
            INSERT INTO song_stems (id, song_id, title, model, dereverb, stage, message, stems, machine, ran_on, created_at, updated_at)
            VALUES ($id, $song, $title, $model, $dereverb, $stage, $message, $stems, $machine, $ranOn, $created, $updated)
            """,
            ("$id", set.Id),
            ("$song", set.SongId),
            ("$title", set.Title),
            ("$model", set.Model),
            ("$dereverb", set.Dereverb ? 1 : 0),
            ("$stage", set.Stage),
            ("$message", (object?)set.Message ?? DBNull.Value),
            ("$stems", JsonSerializer.Serialize(set.Stems, Json)),
            ("$machine", (object?)set.Machine ?? DBNull.Value),
            ("$ranOn", (object?)set.RanOn ?? DBNull.Value),
            ("$created", Time(set.CreatedAt)),
            ("$updated", Time(set.UpdatedAt)));
    }

    /// <returns>False when the set was deleted meanwhile; it is not brought back.</returns>
    public bool Update(StemSetState set)
    {
        using var connection = database.Open();
        return SqliteDatabase.Execute(
            connection,
            null,
            "UPDATE song_stems SET stage = $stage, message = $message, stems = $stems, ran_on = $ranOn, updated_at = $updated WHERE id = $id",
            ("$id", set.Id),
            ("$stage", set.Stage),
            ("$message", (object?)set.Message ?? DBNull.Value),
            ("$stems", JsonSerializer.Serialize(set.Stems, Json)),
            ("$ranOn", (object?)set.RanOn ?? DBNull.Value),
            ("$updated", Time(set.UpdatedAt))) > 0;
    }

    /// <summary>Removes the set and its files.</summary>
    public void Remove(string id)
    {
        using (var connection = database.Open())
        {
            SqliteDatabase.Execute(connection, null, "DELETE FROM song_stems WHERE id = $id", ("$id", id));
        }
        DeleteFolder(id);
    }

    /// <summary>Every set of the song, with the song.</summary>
    public void RemoveSong(string songId) => RemoveWhere("song_id = $key", songId);

    /// <summary>Every set of the run's songs; a run name never contains a slash, so the prefix cannot reach another run.</summary>
    public void RemoveRun(string run) => RemoveWhere("substr(song_id, 1, length($key)) = $key", $"{run}/");

    private void RemoveWhere(string condition, string key)
    {
        var ids = Query($"SELECT * FROM song_stems WHERE {condition}", ("$key", key)).Select(s => s.Id).ToList();
        using (var connection = database.Open())
        {
            SqliteDatabase.Execute(connection, null, $"DELETE FROM song_stems WHERE {condition}", ("$key", key));
        }
        foreach (var id in ids)
        {
            DeleteFolder(id);
        }
    }

    private void DeleteFolder(string id)
    {
        try
        {
            if (Directory.Exists(Folder(id)))
            {
                Directory.Delete(Folder(id), recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private List<StemSetState> Query(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        using var reader = command.ExecuteReader();
        var sets = new List<StemSetState>();
        while (reader.Read())
        {
            sets.Add(Read(reader));
        }
        return sets;
    }

    private static StemSetState Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(reader.GetOrdinal("id")),
        SongId = reader.GetString(reader.GetOrdinal("song_id")),
        Title = reader.GetString(reader.GetOrdinal("title")),
        Model = reader.GetString(reader.GetOrdinal("model")),
        Dereverb = reader.GetInt64(reader.GetOrdinal("dereverb")) != 0,
        Stage = reader.GetString(reader.GetOrdinal("stage")),
        Message = reader.IsDBNull(reader.GetOrdinal("message")) ? null : reader.GetString(reader.GetOrdinal("message")),
        Stems = JsonSerializer.Deserialize<List<StemFile>>(reader.GetString(reader.GetOrdinal("stems")), Json) ?? [],
        Machine = reader.IsDBNull(reader.GetOrdinal("machine")) ? null : reader.GetString(reader.GetOrdinal("machine")),
        RanOn = reader.IsDBNull(reader.GetOrdinal("ran_on")) ? null : reader.GetString(reader.GetOrdinal("ran_on")),
        CreatedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at")), CultureInfo.InvariantCulture),
        UpdatedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("updated_at")), CultureInfo.InvariantCulture),
    };

    private static string Time(DateTimeOffset time) => time.ToString("O", CultureInfo.InvariantCulture);
}
