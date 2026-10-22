using System.Text.Json;
using YueUI.Api.Writing;

namespace YueUI.Api.Data;

/// <summary>
/// The writing page's conversations (<see cref="WritingDesk"/>), each with all its messages, so that they outlive a
/// restart and go into the nightly backup with the database.
/// </summary>
public sealed class SqliteWritingStore(SqliteDatabase database)
{
    // Enums by name, so that a menu's order never changes what a stored chat says.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Every conversation, oldest first.</summary>
    public IReadOnlyList<WritingChat> All()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT chat FROM writing_chats ORDER BY created_at, id";
        using var reader = command.ExecuteReader();
        var chats = new List<WritingChat>();
        while (reader.Read())
        {
            if (JsonSerializer.Deserialize<WritingChat>(reader.GetString(0), Json) is { } chat)
            {
                chats.Add(chat);
            }
        }
        return chats;
    }

    public void Save(WritingChat chat)
    {
        using var connection = database.Open();
        SqliteDatabase.Execute(
            connection,
            null,
            """
            INSERT INTO writing_chats (id, chat, created_at) VALUES ($id, $chat, $created)
            ON CONFLICT (id) DO UPDATE SET chat = excluded.chat
            """,
            ("$id", chat.Id),
            ("$chat", JsonSerializer.Serialize(chat, Json)),
            ("$created", chat.CreatedAt.ToString("O")));
    }

    public void Remove(string id)
    {
        using var connection = database.Open();
        SqliteDatabase.Execute(connection, null, "DELETE FROM writing_chats WHERE id = $id", ("$id", id));
    }
}
