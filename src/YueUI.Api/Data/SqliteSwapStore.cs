using System.Globalization;
using Microsoft.Data.Sqlite;
using YueUI.Api.Voices;

namespace YueUI.Api.Data;

/// <summary>
/// Uploaded recordings sung with another voice (<see cref="SwapState"/>). Each has a folder in <c>swaps/&lt;id&gt;/</c>
/// next to the database with the upload as it came (<c>source.*</c>) and, once done, the result (<c>result.flac</c>).
/// </summary>
public sealed class SqliteSwapStore(SqliteDatabase database)
{
    public const string ResultFile = "result.flac";

    public string Folder(string id) => Path.Combine(database.Directory, "swaps", id);

    public string ResultPath(string id) => Path.Combine(Folder(id), ResultFile);

    /// <summary>The upload, whatever ending it came with; null when it is gone.</summary>
    public string? SourcePath(string id) =>
        Directory.Exists(Folder(id)) ? Directory.EnumerateFiles(Folder(id), "source.*").FirstOrDefault() : null;

    /// <summary>Newest first, as the page lists them.</summary>
    public IReadOnlyList<SwapState> All() => Query("SELECT * FROM voice_swaps ORDER BY created_at DESC");

    public IReadOnlyList<SwapState> Unfinished() =>
        Query("SELECT * FROM voice_swaps WHERE stage NOT IN ('done', 'failed', 'cancelled') ORDER BY created_at");

    public SwapState? Get(string id) => Query("SELECT * FROM voice_swaps WHERE id = $id", ("$id", id)).FirstOrDefault();

    public void Add(SwapState swap)
    {
        using var connection = database.Open();
        SqliteDatabase.Execute(
            connection,
            null,
            """
            INSERT INTO voice_swaps (id, file_name, voice_id, voice_label, semi_tone_shift, strength, diffusion_steps, separate,
                keep_reverb, stem_model, stage, message, machine, ran_on, created_at, updated_at)
            VALUES ($id, $file, $voice, $label, $shift, $strength, $steps, $separate, $reverb, $model, $stage, $message, $machine, $ranOn, $created, $updated)
            """,
            ("$id", swap.Id),
            ("$file", swap.FileName),
            ("$voice", swap.VoiceId),
            ("$label", swap.VoiceLabel),
            ("$shift", swap.SemiToneShift),
            ("$strength", swap.Strength),
            ("$steps", swap.DiffusionSteps),
            ("$separate", swap.Separate ? 1 : 0),
            ("$reverb", swap.KeepReverb ? 1 : 0),
            ("$model", (object?)swap.StemModel ?? DBNull.Value),
            ("$stage", swap.Stage),
            ("$message", (object?)swap.Message ?? DBNull.Value),
            ("$machine", (object?)swap.Machine ?? DBNull.Value),
            ("$ranOn", (object?)swap.RanOn ?? DBNull.Value),
            ("$created", Time(swap.CreatedAt)),
            ("$updated", Time(swap.UpdatedAt)));
    }

    /// <returns>False when the swap was deleted meanwhile; it is not brought back.</returns>
    public bool Update(SwapState swap)
    {
        using var connection = database.Open();
        return SqliteDatabase.Execute(
            connection,
            null,
            "UPDATE voice_swaps SET stage = $stage, message = $message, ran_on = $ranOn, updated_at = $updated WHERE id = $id",
            ("$id", swap.Id),
            ("$stage", swap.Stage),
            ("$message", (object?)swap.Message ?? DBNull.Value),
            ("$ranOn", (object?)swap.RanOn ?? DBNull.Value),
            ("$updated", Time(swap.UpdatedAt))) > 0;
    }

    /// <summary>Removes the swap with its upload and result.</summary>
    public void Remove(string id)
    {
        using (var connection = database.Open())
        {
            SqliteDatabase.Execute(connection, null, "DELETE FROM voice_swaps WHERE id = $id", ("$id", id));
        }
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

    private List<SwapState> Query(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        using var reader = command.ExecuteReader();
        var swaps = new List<SwapState>();
        while (reader.Read())
        {
            swaps.Add(Read(reader));
        }
        return swaps;
    }

    private static SwapState Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(reader.GetOrdinal("id")),
        FileName = reader.GetString(reader.GetOrdinal("file_name")),
        VoiceId = reader.GetString(reader.GetOrdinal("voice_id")),
        VoiceLabel = reader.GetString(reader.GetOrdinal("voice_label")),
        SemiToneShift = reader.GetInt32(reader.GetOrdinal("semi_tone_shift")),
        Strength = reader.GetDouble(reader.GetOrdinal("strength")),
        DiffusionSteps = reader.GetInt32(reader.GetOrdinal("diffusion_steps")),
        Separate = reader.GetInt64(reader.GetOrdinal("separate")) != 0,
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
