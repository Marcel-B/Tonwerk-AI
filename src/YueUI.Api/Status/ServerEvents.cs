using YueUI.Api.Queue;
using YueUI.Api.Voices;
using YueUI.Api.Worker;

namespace YueUI.Api.Status;

/// <summary>
/// What <c>GET /api/events</c> sends, for the OpenAPI description only: each property is an SSE event name, its type
/// the JSON in that event's <c>data</c>. OpenAPI has no way to say "this event carries that type", so the apps' client
/// decodes the stream itself against these types. Every event name <see cref="StatusHub"/> publishes belongs here.
/// </summary>
/// <param name="Snapshot">First on every connection: everything at once.</param>
/// <param name="Ping">An empty object every <see cref="WorkerEndpoints.Heartbeat"/> while nothing else happens.</param>
/// <param name="Library">An empty object: songs were added, renamed, rated or deleted, so the library is read again.</param>
/// <param name="Queue">The whole queue of waiting jobs.</param>
public sealed record ServerEvents(
    StatusSnapshot Snapshot,
    EmptyEvent Ping,
    EmptyEvent Library,
    SongState Song,
    TranscriptionState Transcription,
    LogEntry Log,
    WorkerInfo Worker,
    LyricsState Lyrics,
    IReadOnlyList<QueuedJob> Queue,
    MemoryInfo Memory,
    VersionState Version,
    StemSetState Stems,
    SwapState Swap,
    Video.VideoState Video,
    Images.ImageState Image,
    Ace.AceJob Ace,
    Speech.SpeechTake Speech);

/// <summary>The <c>{}</c> of events that only say something happened.</summary>
public sealed record EmptyEvent;
