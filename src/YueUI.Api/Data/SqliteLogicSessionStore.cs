using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace YueUI.Api.Data;

/// <param name="SongId">The library song the session works on (<c>run/songN</c>), or null for an uploaded score.</param>
/// <param name="ScoreFile">The uploaded score's file name as it was chosen (<c>melody.mid</c>), null for a library song.</param>
/// <param name="AudioFile">The uploaded recording's file name, null without one.</param>
/// <param name="Settings">The page's state (options, routing, mixer, sounds, …); the clients' business, never read here.</param>
public sealed record LogicSession(
    long Id,
    string Name,
    string? SongId,
    string? ScoreFile,
    string? AudioFile,
    JsonElement Settings,
    string CreatedAt,
    string UpdatedAt);

/// <summary>
/// The Logic page's sessions: what the page was set to for one song or one uploaded score, so the work can go on later
/// or on another device. An uploaded score and its recording are kept beside the row in <c>logic-sessions/&lt;id&gt;/</c>
/// next to the database, as <c>score.abc</c> or <c>score.mid</c> and <c>audio.flac</c>; their own names stay in the row.
/// </summary>
public sealed class SqliteLogicSessionStore(SqliteDatabase database, TimeProvider time)
{
    private const string Columns = "id, name, song_id, score_file, audio_file, settings, created_at, updated_at";

    public string Folder => Path.Combine(database.Directory, "logic-sessions");

    /// <summary>The most recently changed first, which is the one a page offers to go on with.</summary>
    public IReadOnlyList<LogicSession> List()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM logic_sessions ORDER BY updated_at DESC, id DESC";
        using var reader = command.ExecuteReader();
        var sessions = new List<LogicSession>();
        while (reader.Read())
        {
            sessions.Add(Read(reader));
        }
        return sessions;
    }

    public LogicSession? Find(long id)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM logic_sessions WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    /// <summary>A session for a library song, or for a score whose bytes are written into the session's folder.</summary>
    public async Task<LogicSession> CreateAsync(
        string name, string? songId, (string Name, string Extension, Stream Content)? score, JsonElement settings, CancellationToken cancellationToken)
    {
        long id;
        var now = time.GetUtcNow().ToString("O");
        using (var connection = database.Open())
        {
            SqliteDatabase.Execute(
                connection,
                null,
                """
                INSERT INTO logic_sessions (name, song_id, score_file, settings, created_at, updated_at)
                VALUES ($name, $song, $score, $settings, $at, $at)
                """,
                ("$name", name),
                ("$song", (object?)songId ?? DBNull.Value),
                ("$score", (object?)score?.Name ?? DBNull.Value),
                ("$settings", settings.GetRawText()),
                ("$at", now));
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT last_insert_rowid()";
            id = (long)command.ExecuteScalar()!;
        }
        if (score is { } upload)
        {
            try
            {
                var directory = Directory.CreateDirectory(DirectoryOf(id)).FullName;
                await using var target = File.Create(Path.Combine(directory, "score" + upload.Extension));
                await upload.Content.CopyToAsync(target, cancellationToken);
            }
            catch
            {
                // A session whose score never arrived cannot be opened again.
                Delete(id);
                throw;
            }
        }
        return Find(id)!;
    }

    /// <summary>Renames the session, replaces its settings, or both; what is <c>null</c> stays.</summary>
    public bool Update(long id, string? name, JsonElement? settings)
    {
        using var connection = database.Open();
        return SqliteDatabase.Execute(
            connection,
            null,
            """
            UPDATE logic_sessions SET
                name = coalesce($name, name),
                settings = coalesce($settings, settings),
                updated_at = $at
            WHERE id = $id
            """,
            ("$id", id),
            ("$name", (object?)name ?? DBNull.Value),
            ("$settings", settings is { } json ? json.GetRawText() : (object)DBNull.Value),
            ("$at", time.GetUtcNow().ToString("O"))) > 0;
    }

    /// <summary>Keeps the uploaded recording beside the score, replacing an earlier one; null removes it.</summary>
    public async Task<bool> SetAudioAsync(long id, (string Name, Stream Content)? audio, CancellationToken cancellationToken)
    {
        if (Find(id) is not { ScoreFile: not null })
        {
            return false;
        }
        var path = Path.Combine(DirectoryOf(id), "audio.flac");
        if (audio is { } upload)
        {
            Directory.CreateDirectory(DirectoryOf(id));
            // Into a temporary file first, so a cut-off upload never replaces the recording that was there.
            var temporary = path + ".part";
            await using (var target = File.Create(temporary))
            {
                await upload.Content.CopyToAsync(target, cancellationToken);
            }
            File.Move(temporary, path, overwrite: true);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
        using var connection = database.Open();
        SqliteDatabase.Execute(
            connection,
            null,
            "UPDATE logic_sessions SET audio_file = $audio, updated_at = $at WHERE id = $id",
            ("$id", id),
            ("$audio", (object?)audio?.Name ?? DBNull.Value),
            ("$at", time.GetUtcNow().ToString("O")));
        return true;
    }

    /// <summary>The uploaded score's path, or null for a library song's session or a file gone missing.</summary>
    public string? ScorePath(LogicSession session)
    {
        if (session.ScoreFile is null || !Directory.Exists(DirectoryOf(session.Id)))
        {
            return null;
        }
        return Directory.EnumerateFiles(DirectoryOf(session.Id), "score.*").FirstOrDefault();
    }

    public string? AudioPath(LogicSession session)
    {
        var path = Path.Combine(DirectoryOf(session.Id), "audio.flac");
        return session.AudioFile is not null && File.Exists(path) ? path : null;
    }

    public bool Delete(long id)
    {
        using var connection = database.Open();
        var removed = SqliteDatabase.Execute(connection, null, "DELETE FROM logic_sessions WHERE id = $id", ("$id", id)) > 0;
        if (Directory.Exists(DirectoryOf(id)))
        {
            Directory.Delete(DirectoryOf(id), recursive: true);
        }
        return removed;
    }

    /// <summary>A deleted song's sessions have nothing left to open.</summary>
    public void RemoveSong(string songId)
    {
        foreach (var id in IdsWhere("song_id = $song", ("$song", songId)))
        {
            Delete(id);
        }
    }

    public void RemoveRun(string run)
    {
        foreach (var id in IdsWhere("song_id LIKE $prefix ESCAPE '\\'", ("$prefix", Escape(run) + "/%")))
        {
            Delete(id);
        }
    }

    private List<long> IdsWhere(string condition, (string Name, object Value) parameter)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT id FROM logic_sessions WHERE {condition}";
        command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        using var reader = command.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
        }
        return ids;
    }

    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private string DirectoryOf(long id) => Path.Combine(Folder, id.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>The settings are parsed so that they go out as JSON, not as a string of JSON.</summary>
    private static LogicSession Read(SqliteDataReader reader)
    {
        using var document = JsonDocument.Parse(reader.GetString(5));
        return new LogicSession(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            document.RootElement.Clone(),
            reader.GetString(6),
            reader.GetString(7));
    }
}
