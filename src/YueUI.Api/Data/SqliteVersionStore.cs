using System.Globalization;
using Microsoft.Data.Sqlite;
using YueUI.Api.Voices;

namespace YueUI.Api.Data;

/// <summary>
/// Songs sung with another voice (<see cref="VersionState"/>), by song id. The audio of a finished one lies in
/// <c>versions/</c> next to the database, named by the version's id: YuE Studio's song folders stay untouched.
/// </summary>
public sealed class SqliteVersionStore(SqliteDatabase database)
{
    public string FilePath(string id) => Path.Combine(database.Directory, "versions", $"{id}.flac");

    /// <summary>The version's AAC for the player (<see cref="Share.StreamCopies"/>), deleted with the version.</summary>
    public string StreamPath(string id) => Path.Combine(database.Directory, "versions", $"{id}.m4a");

    public IReadOnlyList<VersionState> All() => Query("SELECT * FROM song_versions ORDER BY created_at");

    public IReadOnlyList<VersionState> Unfinished() =>
        Query("SELECT * FROM song_versions WHERE stage NOT IN ('done', 'failed', 'cancelled') ORDER BY created_at");

    public VersionState? Get(string id) => Query("SELECT * FROM song_versions WHERE id = $id", ("$id", id)).FirstOrDefault();

    public void Add(VersionState version)
    {
        using var connection = database.Open();
        SqliteDatabase.Execute(
            connection,
            null,
            """
            INSERT INTO song_versions (id, song_id, title, voice_id, voice_label, semi_tone_shift, strength, diffusion_steps,
                keep_reverb, stem_model, stage, message, machine, ran_on, created_at, updated_at)
            VALUES ($id, $song, $title, $voice, $label, $shift, $strength, $steps, $reverb, $stem, $stage, $message, $machine, $ranOn, $created, $updated)
            """,
            ("$id", version.Id),
            ("$song", version.SongId),
            ("$title", version.Title),
            ("$voice", version.VoiceId),
            ("$label", version.VoiceLabel),
            ("$shift", version.SemiToneShift),
            ("$strength", version.Strength),
            ("$steps", version.DiffusionSteps),
            ("$reverb", version.KeepReverb ? 1 : 0),
            ("$stem", (object?)version.StemModel ?? DBNull.Value),
            ("$stage", version.Stage),
            ("$message", (object?)version.Message ?? DBNull.Value),
            ("$machine", (object?)version.Machine ?? DBNull.Value),
            ("$ranOn", (object?)version.RanOn ?? DBNull.Value),
            ("$created", Time(version.CreatedAt)),
            ("$updated", Time(version.UpdatedAt)));
    }

    /// <returns>False when the version was deleted meanwhile; it is not brought back.</returns>
    public bool Update(VersionState version)
    {
        using var connection = database.Open();
        return SqliteDatabase.Execute(
            connection,
            null,
            "UPDATE song_versions SET stage = $stage, message = $message, ran_on = $ranOn, updated_at = $updated WHERE id = $id",
            ("$id", version.Id),
            ("$stage", version.Stage),
            ("$message", (object?)version.Message ?? DBNull.Value),
            ("$ranOn", (object?)version.RanOn ?? DBNull.Value),
            ("$updated", Time(version.UpdatedAt))) > 0;
    }

    /// <summary>Removes the version and its audio.</summary>
    public void Remove(string id)
    {
        using (var connection = database.Open())
        {
            SqliteDatabase.Execute(connection, null, "DELETE FROM song_versions WHERE id = $id", ("$id", id));
        }
        DeleteFile(id);
    }

    /// <summary>Every version of the song, with the song.</summary>
    public void RemoveSong(string songId) => RemoveWhere("song_id = $key", songId);

    /// <summary>Every version of the run's songs; a run name never contains a slash, so the prefix cannot reach another run.</summary>
    public void RemoveRun(string run) => RemoveWhere("substr(song_id, 1, length($key)) = $key", $"{run}/");

    private void RemoveWhere(string condition, string key)
    {
        var ids = Query($"SELECT * FROM song_versions WHERE {condition}", ("$key", key)).Select(v => v.Id).ToList();
        using (var connection = database.Open())
        {
            SqliteDatabase.Execute(connection, null, $"DELETE FROM song_versions WHERE {condition}", ("$key", key));
        }
        foreach (var id in ids)
        {
            DeleteFile(id);
        }
    }

    private void DeleteFile(string id)
    {
        try
        {
            File.Delete(FilePath(id));
            File.Delete(StreamPath(id));
            File.Delete(Share.StreamCopies.StampPath(StreamPath(id)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private List<VersionState> Query(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        using var reader = command.ExecuteReader();
        var versions = new List<VersionState>();
        while (reader.Read())
        {
            versions.Add(Read(reader));
        }
        return versions;
    }

    private static VersionState Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(reader.GetOrdinal("id")),
        SongId = reader.GetString(reader.GetOrdinal("song_id")),
        Title = reader.GetString(reader.GetOrdinal("title")),
        VoiceId = reader.GetString(reader.GetOrdinal("voice_id")),
        VoiceLabel = reader.GetString(reader.GetOrdinal("voice_label")),
        SemiToneShift = reader.GetInt32(reader.GetOrdinal("semi_tone_shift")),
        Strength = reader.GetDouble(reader.GetOrdinal("strength")),
        DiffusionSteps = reader.GetInt32(reader.GetOrdinal("diffusion_steps")),
        KeepReverb = reader.GetInt64(reader.GetOrdinal("keep_reverb")) != 0,
        StemModel = reader.IsDBNull(reader.GetOrdinal("stem_model")) ? null : reader.GetString(reader.GetOrdinal("stem_model")),
        Stage = reader.GetString(reader.GetOrdinal("stage")),
        Message = reader.IsDBNull(reader.GetOrdinal("message")) ? null : reader.GetString(reader.GetOrdinal("message")),
        Machine = reader.IsDBNull(reader.GetOrdinal("machine")) ? null : reader.GetString(reader.GetOrdinal("machine")),
        RanOn = reader.IsDBNull(reader.GetOrdinal("ran_on")) ? null : reader.GetString(reader.GetOrdinal("ran_on")),
        CreatedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at")), CultureInfo.InvariantCulture),
        UpdatedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("updated_at")), CultureInfo.InvariantCulture),
    };

    private static string Time(DateTimeOffset time) => time.ToString("O", CultureInfo.InvariantCulture);
}
