using System.Globalization;
using Microsoft.Data.Sqlite;
using YueUI.Api.Video;

namespace YueUI.Api.Data;

/// <summary>
/// Music videos made of songs (<see cref="VideoState"/>). Each has a folder in <c>videos/&lt;id&gt;/</c> next to the
/// database with the layers the browser drew and, once done, the video (<c>video.mp4</c>).
/// </summary>
public sealed class SqliteVideoStore(SqliteDatabase database)
{
    public const string VideoFile = "video.mp4";

    /// <summary>The lyrics' times are written and read here only, in the API's camelCase.</summary>
    public static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);

    public string Folder(string id) => Path.Combine(database.Directory, "videos", id);

    public string VideoPath(string id) => Path.Combine(Folder(id), VideoFile);

    /// <summary>The copy to send through a messenger, made on its first request and kept with the video.</summary>
    public string SmallPath(string id) => Path.Combine(Folder(id), "small.mp4");

    /// <summary>A photo video's pictures cut to the song (<see cref="MontageBuilder"/>), made just before the video.</summary>
    public string MontagePath(string id) => Path.Combine(Folder(id), "montage.mp4");

    /// <summary>The planner's stills and shots; deleted once the video is done.</summary>
    public string WorkFolder(string id) => Path.Combine(Folder(id), "work");

    public VideoLayers Layers(VideoState video) => new(
        video.Media is null ? Path.Combine(Folder(video.Id), "background.png") : MontagePath(video.Id),
        video.ShowCover ? Path.Combine(Folder(video.Id), "cover.png") : null,
        video.ShowTitle ? Path.Combine(Folder(video.Id), "title.png") : null,
        video.Motion switch
        {
            VideoMotions.Particles => [Path.Combine(Folder(video.Id), "particles.png")],
            VideoMotions.Plasma => [.. Enumerable.Range(0, VideoLayout.PlasmaFrames).Select(i => Path.Combine(Folder(video.Id), $"plasma{i}.png"))],
            _ => [],
        },
        video.Media is not null && File.Exists(Path.Combine(Folder(video.Id), "shade.png")) ? Path.Combine(Folder(video.Id), "shade.png") : null,
        LyricsStrips(video.Id));

    /// <summary>The lyrics' times as the endpoint wrote them beside their strips; a video without lyrics has none.</summary>
    public string LyricsCuesPath(string id) => Path.Combine(Folder(id), "lyrics.json");

    private IReadOnlyList<LyricsStrip>? LyricsStrips(string id)
    {
        try
        {
            if (!File.Exists(LyricsCuesPath(id))
                || System.Text.Json.JsonSerializer.Deserialize<List<LyricsCue>>(File.ReadAllText(LyricsCuesPath(id)), Json) is not { Count: > 0 } cues)
            {
                return null;
            }
            return [.. cues.Chunk(VideoLayout.LyricsPerStrip).Select((chunk, i) => new LyricsStrip(Path.Combine(Folder(id), $"lyrics{i}.png"), chunk))];
        }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>The song's videos, newest first, as the dialog lists them.</summary>
    public IReadOnlyList<VideoState> ForSong(string songId) =>
        Query("SELECT * FROM song_videos WHERE song_id = $song ORDER BY created_at DESC", ("$song", songId));

    /// <summary>The formats each song has a finished video in, for the library.</summary>
    public Dictionary<string, IReadOnlyList<string>> FinishedFormats() =>
        Query("SELECT * FROM song_videos WHERE stage = 'done' ORDER BY format")
            .GroupBy(v => v.SongId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Select(v => v.Format).Distinct()]);

    public IReadOnlyList<VideoState> Unfinished() =>
        Query("SELECT * FROM song_videos WHERE stage NOT IN ('done', 'failed', 'cancelled') ORDER BY created_at");

    public VideoState? Get(string id) => Query("SELECT * FROM song_videos WHERE id = $id", ("$id", id)).FirstOrDefault();

    public void Add(VideoState video)
    {
        using var connection = database.Open();
        SqliteDatabase.Execute(
            connection,
            null,
            """
            INSERT INTO song_videos (id, song_id, title, format, effect, color, motion, show_cover, show_title, media, motif, stage, message, bytes, created_at, updated_at)
            VALUES ($id, $song, $title, $format, $effect, $color, $motion, $showCover, $showTitle, $media, $motif, $stage, $message, $bytes, $created, $updated)
            """,
            ("$id", video.Id),
            ("$song", video.SongId),
            ("$title", video.Title),
            ("$format", video.Format),
            ("$effect", video.Effect),
            ("$color", (object?)video.Color ?? DBNull.Value),
            ("$motion", video.Motion),
            ("$showCover", video.ShowCover ? 1 : 0),
            ("$showTitle", video.ShowTitle ? 1 : 0),
            ("$media", (object?)video.Media ?? DBNull.Value),
            ("$motif", (object?)video.Motif ?? DBNull.Value),
            ("$stage", video.Stage),
            ("$message", (object?)video.Message ?? DBNull.Value),
            ("$bytes", (object?)video.Bytes ?? DBNull.Value),
            ("$created", Time(video.CreatedAt)),
            ("$updated", Time(video.UpdatedAt)));
    }

    /// <returns>False when the video was deleted meanwhile; it is not brought back.</returns>
    public bool Update(VideoState video)
    {
        using var connection = database.Open();
        return SqliteDatabase.Execute(
            connection,
            null,
            "UPDATE song_videos SET stage = $stage, message = $message, bytes = $bytes, updated_at = $updated WHERE id = $id",
            ("$id", video.Id),
            ("$stage", video.Stage),
            ("$message", (object?)video.Message ?? DBNull.Value),
            ("$bytes", (object?)video.Bytes ?? DBNull.Value),
            ("$updated", Time(video.UpdatedAt))) > 0;
    }

    /// <summary>Removes the video with its layers.</summary>
    public void Remove(string id)
    {
        using (var connection = database.Open())
        {
            SqliteDatabase.Execute(connection, null, "DELETE FROM song_videos WHERE id = $id", ("$id", id));
        }
        DeleteFolder(id);
    }

    /// <summary>Every video of the song, when the song is deleted.</summary>
    public void RemoveSong(string songId) => RemoveWhere("song_id = $key", songId);

    /// <summary>Every video of the run's songs; a run name never contains a slash, so the prefix cannot reach another run.</summary>
    public void RemoveRun(string run) => RemoveWhere("substr(song_id, 1, length($key)) = $key", $"{run}/");

    private void RemoveWhere(string condition, string key)
    {
        var ids = Query($"SELECT * FROM song_videos WHERE {condition}", ("$key", key)).Select(v => v.Id).ToList();
        using (var connection = database.Open())
        {
            SqliteDatabase.Execute(connection, null, $"DELETE FROM song_videos WHERE {condition}", ("$key", key));
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

    private List<VideoState> Query(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        using var reader = command.ExecuteReader();
        var videos = new List<VideoState>();
        while (reader.Read())
        {
            videos.Add(Read(reader));
        }
        return videos;
    }

    private static VideoState Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(reader.GetOrdinal("id")),
        SongId = reader.GetString(reader.GetOrdinal("song_id")),
        Title = reader.GetString(reader.GetOrdinal("title")),
        Format = reader.GetString(reader.GetOrdinal("format")),
        Effect = reader.GetString(reader.GetOrdinal("effect")),
        Color = reader.IsDBNull(reader.GetOrdinal("color")) ? null : reader.GetString(reader.GetOrdinal("color")),
        Motion = reader.GetString(reader.GetOrdinal("motion")),
        ShowCover = reader.GetInt64(reader.GetOrdinal("show_cover")) != 0,
        ShowTitle = reader.GetInt64(reader.GetOrdinal("show_title")) != 0,
        Media = reader.IsDBNull(reader.GetOrdinal("media")) ? null : reader.GetString(reader.GetOrdinal("media")),
        Motif = reader.IsDBNull(reader.GetOrdinal("motif")) ? null : reader.GetString(reader.GetOrdinal("motif")),
        Stage = reader.GetString(reader.GetOrdinal("stage")),
        Message = reader.IsDBNull(reader.GetOrdinal("message")) ? null : reader.GetString(reader.GetOrdinal("message")),
        Bytes = reader.IsDBNull(reader.GetOrdinal("bytes")) ? null : reader.GetInt64(reader.GetOrdinal("bytes")),
        CreatedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at")), CultureInfo.InvariantCulture),
        UpdatedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("updated_at")), CultureInfo.InvariantCulture),
    };

    private static string Time(DateTimeOffset time) => time.ToString("O", CultureInfo.InvariantCulture);
}
