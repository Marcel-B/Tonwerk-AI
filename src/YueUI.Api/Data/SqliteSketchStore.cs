using System.Text.Json;
using YueUI.Api.Sketches;

namespace YueUI.Api.Data;

/// <summary>
/// Song sketches and their plans (<see cref="SketchMaker"/>), so that a plan, which costs a call to Claude, outlives a
/// restart and stays on the page until deleted. Score, lyrics and MIDI are made from the plan when asked for.
/// </summary>
public sealed class SqliteSketchStore(SqliteDatabase database)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Every sketch, oldest first.</summary>
    public IReadOnlyList<SketchJob> All()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sketch FROM song_sketches ORDER BY created_at, id";
        using var reader = command.ExecuteReader();
        var jobs = new List<SketchJob>();
        while (reader.Read())
        {
            if (JsonSerializer.Deserialize<SketchJob>(reader.GetString(0), Json) is { } job)
            {
                jobs.Add(job);
            }
        }
        return jobs;
    }

    public void Save(SketchJob job)
    {
        using var connection = database.Open();
        SqliteDatabase.Execute(
            connection,
            null,
            """
            INSERT INTO song_sketches (id, sketch, created_at) VALUES ($id, $sketch, $created)
            ON CONFLICT (id) DO UPDATE SET sketch = excluded.sketch
            """,
            ("$id", job.Id),
            ("$sketch", JsonSerializer.Serialize(job, Json)),
            ("$created", job.CreatedAt.ToString("O")));
    }

    public void Remove(string id)
    {
        using var connection = database.Open();
        SqliteDatabase.Execute(connection, null, "DELETE FROM song_sketches WHERE id = $id", ("$id", id));
    }
}
