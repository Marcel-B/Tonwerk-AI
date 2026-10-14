namespace YueUI.Api.Data;

/// <param name="Tempo">Beats per minute, as heard (or as the score asked for it, see <paramref name="From"/>).</param>
/// <param name="Key">The key as the key picker names it: <c>C</c>, <c>F#m</c>, <c>Bb</c>.</param>
/// <param name="From">
/// <c>audio</c> when tempo and key were heard in the song, <c>score</c> when they are only what its score (or ACE-Step's
/// request) asked for, since nothing to hear them with is installed.
/// </param>
/// <param name="Clarity">
/// How much of the lyrics Whisper understood, 0 to 1 (<see cref="Timing.LyricsTiming.Coverage"/>): a mumbled song scores
/// low. Null while the song has not been timed or has no lyrics.
/// </param>
/// <param name="ClarityFrom"><c>vocals</c> when Whisper heard a vocal stem, <c>mix</c> when the whole song, which scores lower.</param>
public sealed record SongTraits(double? Tempo, string? Key, string? From, double? Clarity, string? ClarityFrom);

/// <summary>
/// What was heard in each song, by song id, like the ratings: <see cref="Analysis.SongAnalyzer"/> writes tempo and key,
/// <see cref="Timing.LyricsTimer"/> the clarity, each without touching the other's columns.
/// </summary>
public sealed class SqliteSongTraitStore(SqliteDatabase database, TimeProvider time)
{
    public IReadOnlyDictionary<string, SongTraits> All()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT song_id, tempo, song_key, heard_from, clarity, clarity_from FROM song_traits";
        using var reader = command.ExecuteReader();
        var traits = new Dictionary<string, SongTraits>(StringComparer.Ordinal);
        while (reader.Read())
        {
            traits[reader.GetString(0)] = new SongTraits(
                reader.IsDBNull(1) ? null : reader.GetDouble(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetDouble(4),
                reader.IsDBNull(5) ? null : reader.GetString(5));
        }
        return traits;
    }

    public void SetSound(string songId, double? tempo, string? key, string from)
    {
        using var connection = database.Open();
        SqliteDatabase.Execute(
            connection,
            null,
            """
            INSERT INTO song_traits (song_id, tempo, song_key, heard_from, updated_at) VALUES ($song, $tempo, $key, $from, $at)
            ON CONFLICT (song_id) DO UPDATE SET tempo = excluded.tempo, song_key = excluded.song_key, heard_from = excluded.heard_from, updated_at = excluded.updated_at
            """,
            ("$song", songId),
            ("$tempo", (object?)tempo ?? DBNull.Value),
            ("$key", (object?)key ?? DBNull.Value),
            ("$from", from),
            ("$at", time.GetUtcNow().ToString("O")));
    }

    public void SetClarity(string songId, double clarity, string from)
    {
        using var connection = database.Open();
        SqliteDatabase.Execute(
            connection,
            null,
            """
            INSERT INTO song_traits (song_id, clarity, clarity_from, updated_at) VALUES ($song, $clarity, $from, $at)
            ON CONFLICT (song_id) DO UPDATE SET clarity = excluded.clarity, clarity_from = excluded.clarity_from, updated_at = excluded.updated_at
            """,
            ("$song", songId),
            ("$clarity", clarity),
            ("$from", from),
            ("$at", time.GetUtcNow().ToString("O")));
    }

    public void Remove(string songId)
    {
        using var connection = database.Open();
        SqliteDatabase.Execute(connection, null, "DELETE FROM song_traits WHERE song_id = $song", ("$song", songId));
    }

    /// <summary>Every song of the run; a run name never contains a slash, so the prefix cannot reach another run.</summary>
    public void RemoveRun(string run)
    {
        using var connection = database.Open();
        SqliteDatabase.Execute(
            connection,
            null,
            "DELETE FROM song_traits WHERE substr(song_id, 1, length($prefix)) = $prefix",
            ("$prefix", $"{run}/"));
    }
}
