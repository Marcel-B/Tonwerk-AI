using System.Globalization;
using Microsoft.Data.Sqlite;
using YueUI.Api.Speech;

namespace YueUI.Api.Data;

/// <summary>
/// The speech lab's recorded voices and takes. Their audio lies in <c>speech/voices/</c> and <c>speech/takes/</c> next
/// to the database, named by id; the rows say what it is. A take that cleans a recording keeps the recording in
/// <c>speech/sources/</c> under the take's id, to compare before and after.
/// </summary>
public sealed class SqliteSpeechStore(SqliteDatabase database)
{
    public string VoicePath(string id) => Path.Combine(database.Directory, "speech", "voices", $"{id}.wav");

    public string TakePath(string id) => Path.Combine(database.Directory, "speech", "takes", $"{id}.wav");

    public string SourcePath(string id) => Path.Combine(database.Directory, "speech", "sources", $"{id}.wav");

    public IReadOnlyList<SpeechVoice> Voices() => Query("SELECT * FROM speech_voices ORDER BY created_at DESC", ReadVoice);

    public SpeechVoice? Voice(string id) => Query("SELECT * FROM speech_voices WHERE id = $id", ReadVoice, ("$id", id)).FirstOrDefault();

    public void AddVoice(SpeechVoice voice)
    {
        using var connection = database.Open();
        SqliteDatabase.Execute(
            connection,
            null,
            "INSERT INTO speech_voices (id, label, transcript, seconds, created_at) VALUES ($id, $label, $transcript, $seconds, $created)",
            ("$id", voice.Id),
            ("$label", voice.Label),
            ("$transcript", voice.Transcript),
            ("$seconds", voice.Seconds),
            ("$created", Time(voice.CreatedAt)));
    }

    /// <returns>False when there was no such voice.</returns>
    public bool RemoveVoice(string id)
    {
        int removed;
        using (var connection = database.Open())
        {
            removed = SqliteDatabase.Execute(connection, null, "DELETE FROM speech_voices WHERE id = $id", ("$id", id));
        }
        DeleteFile(VoicePath(id));
        return removed > 0;
    }

    /// <summary>The newest first, as the page lists them.</summary>
    public IReadOnlyList<SpeechTake> Takes(int limit = 200) =>
        Query("SELECT * FROM speech_takes ORDER BY created_at DESC, rowid DESC LIMIT $limit", ReadTake, ("$limit", limit));

    /// <summary>Oldest first: the order they are spoken in.</summary>
    public IReadOnlyList<SpeechTake> Unfinished() =>
        Query("SELECT * FROM speech_takes WHERE stage NOT IN ('done', 'failed', 'cancelled') ORDER BY created_at, rowid", ReadTake);

    public SpeechTake? Take(string id) => Query("SELECT * FROM speech_takes WHERE id = $id", ReadTake, ("$id", id)).FirstOrDefault();

    public void AddTake(SpeechTake take)
    {
        using var connection = database.Open();
        SqliteDatabase.Execute(
            connection,
            null,
            """
            INSERT INTO speech_takes (id, model_id, model_label, text, source, voice_id, voice_label, stage, message,
                progress, seconds, load_seconds, speak_seconds, peak_memory_gb, created_at, updated_at)
            VALUES ($id, $model, $modelLabel, $text, $source, $voice, $voiceLabel, $stage, $message, $progress, $seconds,
                $load, $speak, $memory, $created, $updated)
            """,
            TakeParameters(take));
    }

    /// <returns>False when the take was deleted meanwhile; it is not brought back.</returns>
    public bool UpdateTake(SpeechTake take)
    {
        using var connection = database.Open();
        return SqliteDatabase.Execute(
            connection,
            null,
            """
            UPDATE speech_takes SET stage = $stage, message = $message, progress = $progress, seconds = $seconds, load_seconds = $load,
                speak_seconds = $speak, peak_memory_gb = $memory, updated_at = $updated
            WHERE id = $id
            """,
            TakeParameters(take)) > 0;
    }

    public void RemoveTake(string id)
    {
        using (var connection = database.Open())
        {
            SqliteDatabase.Execute(connection, null, "DELETE FROM speech_takes WHERE id = $id", ("$id", id));
        }
        DeleteFile(TakePath(id));
        DeleteFile(SourcePath(id));
    }

    private static (string, object)[] TakeParameters(SpeechTake take) =>
    [
        ("$id", take.Id),
        ("$model", take.ModelId),
        ("$modelLabel", take.ModelLabel),
        ("$text", take.Text),
        ("$source", Nullable(take.Source)),
        ("$progress", Nullable(take.Progress)),
        ("$voice", Nullable(take.VoiceId)),
        ("$voiceLabel", Nullable(take.VoiceLabel)),
        ("$stage", take.Stage),
        ("$message", Nullable(take.Message)),
        ("$seconds", Nullable(take.Seconds)),
        ("$load", Nullable(take.LoadSeconds)),
        ("$speak", Nullable(take.SpeakSeconds)),
        ("$memory", Nullable(take.PeakMemoryGb)),
        ("$created", Time(take.CreatedAt)),
        ("$updated", Time(take.UpdatedAt)),
    ];

    private static object Nullable(object? value) => value ?? DBNull.Value;

    private static void DeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> read, params (string Name, object Value)[] parameters)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read())
        {
            rows.Add(read(reader));
        }
        return rows;
    }

    private static SpeechVoice ReadVoice(SqliteDataReader reader) => new(
        reader.GetString(reader.GetOrdinal("id")),
        reader.GetString(reader.GetOrdinal("label")),
        reader.GetString(reader.GetOrdinal("transcript")),
        reader.GetDouble(reader.GetOrdinal("seconds")),
        DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at")), CultureInfo.InvariantCulture));

    private static SpeechTake ReadTake(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(reader.GetOrdinal("id")),
        ModelId = reader.GetString(reader.GetOrdinal("model_id")),
        ModelLabel = reader.GetString(reader.GetOrdinal("model_label")),
        Text = reader.GetString(reader.GetOrdinal("text")),
        Source = Text(reader, "source"),
        Progress = Number(reader, "progress"),
        VoiceId = Text(reader, "voice_id"),
        VoiceLabel = Text(reader, "voice_label"),
        Stage = reader.GetString(reader.GetOrdinal("stage")),
        Message = Text(reader, "message"),
        Seconds = Number(reader, "seconds"),
        LoadSeconds = Number(reader, "load_seconds"),
        SpeakSeconds = Number(reader, "speak_seconds"),
        PeakMemoryGb = Number(reader, "peak_memory_gb"),
        CreatedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at")), CultureInfo.InvariantCulture),
        UpdatedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("updated_at")), CultureInfo.InvariantCulture),
    };

    private static string? Text(SqliteDataReader reader, string column) =>
        reader.IsDBNull(reader.GetOrdinal(column)) ? null : reader.GetString(reader.GetOrdinal(column));

    private static double? Number(SqliteDataReader reader, string column) =>
        reader.IsDBNull(reader.GetOrdinal(column)) ? null : reader.GetDouble(reader.GetOrdinal(column));

    private static string Time(DateTimeOffset time) => time.ToString("O", CultureInfo.InvariantCulture);
}
