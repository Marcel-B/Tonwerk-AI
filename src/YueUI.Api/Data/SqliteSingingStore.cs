using System.Text.Json;
using YueUI.Api.Singing;

namespace YueUI.Api.Data;

/// <summary>
/// The singing page's takes (<see cref="SingingStudio"/>), one JSON each with the notes beside it, so that a queued
/// take outlives a restart. The singing lies in <c>singing/</c> next to the database, named by id.
/// </summary>
public sealed class SqliteSingingStore(SqliteDatabase database)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string AudioPath(string id) => Path.Combine(database.Directory, "singing", $"{id}.wav");

    /// <summary>Every take, oldest first.</summary>
    public IReadOnlyList<SingTake> All()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT take FROM sing_takes ORDER BY created_at, id";
        using var reader = command.ExecuteReader();
        var takes = new List<SingTake>();
        while (reader.Read())
        {
            if (JsonSerializer.Deserialize<SingTake>(reader.GetString(0), Json) is { } take)
            {
                takes.Add(take);
            }
        }
        return takes;
    }

    /// <summary>The melody of a take, which the list leaves out.</summary>
    public IReadOnlyList<SungNote> Notes(string id)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT notes FROM sing_takes WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<List<SungNote>>(json, Json) ?? [] : [];
    }

    public void Add(SingTake take, IReadOnlyList<SungNote> notes)
    {
        using var connection = database.Open();
        SqliteDatabase.Execute(
            connection,
            null,
            "INSERT INTO sing_takes (id, take, notes, created_at) VALUES ($id, $take, $notes, $created)",
            ("$id", take.Id),
            ("$take", JsonSerializer.Serialize(take, Json)),
            ("$notes", JsonSerializer.Serialize(notes, Json)),
            ("$created", take.CreatedAt.ToString("O")));
    }

    /// <returns>False when the take was deleted meanwhile.</returns>
    public bool Update(SingTake take)
    {
        using var connection = database.Open();
        return SqliteDatabase.Execute(
            connection,
            null,
            "UPDATE sing_takes SET take = $take WHERE id = $id",
            ("$id", take.Id),
            ("$take", JsonSerializer.Serialize(take, Json))) > 0;
    }

    public void Remove(string id)
    {
        using (var connection = database.Open())
        {
            SqliteDatabase.Execute(connection, null, "DELETE FROM sing_takes WHERE id = $id", ("$id", id));
        }
        try
        {
            File.Delete(AudioPath(id));
        }
        catch (IOException)
        {
        }
    }

    /// <summary>A voice of one's own: the phrase SoulX-Singer clones.</summary>
    public string VoiceAudioPath(string id) => Path.Combine(database.Directory, "singing", "voices", $"{id}.wav");

    /// <summary>Its notes and words in SoulX-Singer's metadata format.</summary>
    public string VoiceMetadataPath(string id) => Path.Combine(database.Directory, "singing", "voices", $"{id}.json");

    /// <summary>The voices of one's own, oldest first.</summary>
    public IReadOnlyList<SingingOwnVoice> Voices()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT voice FROM sing_voices ORDER BY created_at, id";
        using var reader = command.ExecuteReader();
        var voices = new List<SingingOwnVoice>();
        while (reader.Read())
        {
            if (JsonSerializer.Deserialize<SingingOwnVoice>(reader.GetString(0), Json) is { } voice)
            {
                voices.Add(voice);
            }
        }
        return voices;
    }

    public void AddVoice(SingingOwnVoice voice)
    {
        using var connection = database.Open();
        SqliteDatabase.Execute(
            connection,
            null,
            "INSERT INTO sing_voices (id, voice, created_at) VALUES ($id, $voice, $created)",
            ("$id", voice.Id),
            ("$voice", JsonSerializer.Serialize(voice, Json)),
            ("$created", voice.CreatedAt.ToString("O")));
    }

    public void RemoveVoice(string id)
    {
        using (var connection = database.Open())
        {
            SqliteDatabase.Execute(connection, null, "DELETE FROM sing_voices WHERE id = $id", ("$id", id));
        }
        foreach (var path in (string[])[VoiceAudioPath(id), VoiceMetadataPath(id)])
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }
}
