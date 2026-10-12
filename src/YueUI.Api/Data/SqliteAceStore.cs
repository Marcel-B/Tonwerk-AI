using System.Text.Json;
using YueUI.Api.Ace;

namespace YueUI.Api.Data;

/// <summary>
/// ACE-Step's jobs (<see cref="AceComposer"/>), so that waiting ones survive a restart and finished ones stay listed on
/// the page until deleted. The songs themselves are run folders in <c>ace-songs/</c>, which the library reads.
/// </summary>
public sealed class SqliteAceStore(SqliteDatabase database)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Every job, oldest first.</summary>
    public IReadOnlyList<AceJob> All()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT job FROM ace_jobs ORDER BY created_at, id";
        using var reader = command.ExecuteReader();
        var jobs = new List<AceJob>();
        while (reader.Read())
        {
            if (JsonSerializer.Deserialize<AceJob>(reader.GetString(0), Json) is { } job)
            {
                jobs.Add(job);
            }
        }
        return jobs;
    }

    public void Save(AceJob job)
    {
        using var connection = database.Open();
        SqliteDatabase.Execute(
            connection,
            null,
            """
            INSERT INTO ace_jobs (id, job, created_at) VALUES ($id, $job, $created)
            ON CONFLICT (id) DO UPDATE SET job = excluded.job
            """,
            ("$id", job.Id),
            ("$job", JsonSerializer.Serialize(job, Json)),
            ("$created", job.CreatedAt.ToString("O")));
    }

    public void Remove(string id)
    {
        using var connection = database.Open();
        SqliteDatabase.Execute(connection, null, "DELETE FROM ace_jobs WHERE id = $id", ("$id", id));
    }
}
