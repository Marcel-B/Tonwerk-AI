using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using YueUI.Api.Data;
using YueUI.Api.Queue;
using YueUI.Api.Worker;

namespace YueUI.Api.Lyrics;

/// <param name="Keywords">What the song is about, typically dictated into an iPhone shortcut.</param>
/// <param name="Style">The style prompt; the newest song's when left out.</param>
/// <param name="Language"><c>de</c> or <c>en</c> (also <c>german</c>, <c>english</c>); German when left out.</param>
/// <param name="Title">The run's title; the keywords' beginning when left out.</param>
/// <param name="Batch">Songs to make from the one text, 1 to <see cref="GenerateRequest.MaxBatch"/>.</param>
/// <param name="Quality"><c>draft</c> or <c>full</c>.</param>
public sealed record QuickSongRequest(
    string? Keywords,
    string? Style = null,
    string? Language = null,
    string? Title = null,
    int? Batch = null,
    string? Quality = null);

/// <summary>What <c>POST /api/quick</c> answers: the lyrics draft that runs first, and what the song will be made with.</summary>
public sealed record QuickSongStarted(string DraftId, string Stage, string Title, string Style);

/// <summary>
/// A song from a few words in one call, for an iPhone shortcut (or Siri), which cannot wait the minutes a draft takes:
/// the lyrics are drafted like the form's (<see cref="LyricsWriter"/>, through the <see cref="JobQueue"/>), and once
/// the draft is done this queues the song with it. Push notifications say when the song is there.
/// </summary>
/// <remarks>
/// The waiting songs are kept in <c>quick_songs</c> by their draft's id, so a restart between draft and song (the
/// queue keeps the draft) still makes the song. Listens to the status like <see cref="Voices.AutoVersions"/>. A
/// failed draft drops its song; its notification says why. A finished draft's own notification is left out, since
/// the song's follows.
/// </remarks>
public sealed class QuickSongs(
    Status.StatusHub hub,
    JobQueue queue,
    SqliteDatabase database,
    ILogger<QuickSongs> logger) : BackgroundService
{
    public const int MaxTitleLength = 60;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Drafts whose song was queued, so that their notification stays away even after the row is gone.</summary>
    private readonly ConcurrentDictionary<string, bool> _handled = new();

    /// <summary>Whether the draft belongs to a quick song, whose own notification comes later.</summary>
    public bool Owns(string draftId) => _handled.ContainsKey(draftId) || Pending(draftId) is not null;

    /// <summary>Remembers the song to make once the draft <paramref name="draftId"/> is done.</summary>
    /// <remarks>
    /// The draft is queued before this, and may start at once and finish (a model already loaded, a quick server)
    /// before the row is there, when the listener has already passed over it; so the draft's state is looked at here
    /// too. Whichever comes second finds the row gone.
    /// </remarks>
    public async Task ExpectAsync(string draftId, GenerateRequest song, CancellationToken cancellationToken)
    {
        Insert(draftId, song);
        if (hub.Lyrics is { } lyrics && lyrics.Id == draftId)
        {
            await ConsiderAsync(lyrics, cancellationToken);
        }
    }

    private void Insert(string draftId, GenerateRequest song)
    {
        using var connection = database.Open();
        SqliteDatabase.Execute(
            connection,
            null,
            "INSERT INTO quick_songs (draft_id, request, created_at) VALUES ($id, $request, $at)",
            ("$id", draftId),
            ("$request", JsonSerializer.Serialize(song, Json)),
            ("$at", DateTimeOffset.UtcNow.ToString("O")));
    }

    /// <summary>The title a quick song gets from its keywords: their beginning, cut at a word.</summary>
    public static string TitleFrom(string keywords)
    {
        var text = string.Join(' ', keywords.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length > MaxTitleLength)
        {
            var cut = text.LastIndexOf(' ', MaxTitleLength);
            text = text[..(cut > 20 ? cut : MaxTitleLength)];
        }
        return text.Length > 0 ? char.ToUpperInvariant(text[0]) + text[1..] : text;
    }

    public static LyricsLanguage? LanguageFrom(string? language) => language?.Trim().ToLowerInvariant() switch
    {
        null or "" or "de" or "german" or "deutsch" => LyricsLanguage.German,
        "en" or "english" or "englisch" => LyricsLanguage.English,
        _ => null,
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            // A dropped subscriber's channel completes; subscribing again starts from a fresh snapshot.
            using var subscription = hub.Subscribe(checkStudio: false);
            if (subscription.Snapshot.Lyrics is { } current)
            {
                await ConsiderAsync(current, stoppingToken);
            }
            try
            {
                await foreach (var item in subscription.Reader.ReadAllAsync(stoppingToken))
                {
                    if (item.Data is LyricsState lyrics)
                    {
                        await ConsiderAsync(lyrics, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task ConsiderAsync(LyricsState lyrics, CancellationToken cancellationToken)
    {
        // Forget is the claim: the listener and ExpectAsync may both get here for one draft.
        if (!lyrics.Finished || Pending(lyrics.Id) is not { } song || !Forget(lyrics.Id))
        {
            return;
        }
        if (lyrics.Stage != "done" || string.IsNullOrWhiteSpace(lyrics.Lyrics))
        {
            logger.LogWarning("The quick song {Title} is dropped: its lyrics failed ({Message})", song.Title, lyrics.Message);
            return;
        }
        _handled[lyrics.Id] = true;
        try
        {
            await queue.GenerateAsync(song with { Lyrics = lyrics.Lyrics }, null, null, cancellationToken);
            logger.LogInformation("Quick song {Title} queued", song.Title);
        }
        catch (Exception exception) when (exception is WorkerUnavailableException or SqliteException or IOException)
        {
            logger.LogWarning(exception, "The quick song {Title} could not be queued", song.Title);
        }
    }

    private GenerateRequest? Pending(string draftId)
    {
        try
        {
            using var connection = database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT request FROM quick_songs WHERE draft_id = $id";
            command.Parameters.AddWithValue("$id", draftId);
            return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<GenerateRequest>(json, Json) : null;
        }
        catch (Exception exception) when (exception is SqliteException or JsonException)
        {
            return null;
        }
    }

    /// <returns>Whether this call removed the row.</returns>
    private bool Forget(string draftId)
    {
        using var connection = database.Open();
        return SqliteDatabase.Execute(connection, null, "DELETE FROM quick_songs WHERE draft_id = $id", ("$id", draftId)) > 0;
    }
}
