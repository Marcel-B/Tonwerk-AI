using System.Text.Json.Serialization;
using YueUI.Api.Queue;
using YueUI.Api.Voices;

namespace YueUI.Api.Worker;

public enum WorkerStatus
{
    /// <summary>No worker process; the next command starts one.</summary>
    Stopped,

    /// <summary>The process runs but has not said <c>ready</c> yet (Python imports take a few seconds).</summary>
    Starting,

    Ready,
}

/// <summary>
/// A song on its way through the worker, keyed by <see cref="Id"/> (<c>run/songN</c>, as in the library).
/// <see cref="Stage"/> is the worker's: queued, planning, tokens, synth, decode, ready, failed or cancelled.
/// </summary>
public sealed record SongState
{
    public required string Id { get; init; }

    public required string Run { get; init; }

    public string Title { get; init; } = "";

    public int Index { get; init; }

    public long? Seed { get; init; }

    public string Stage { get; init; } = "queued";

    public string Detail { get; init; } = "";

    /// <summary>Progress of the current stage, 0–1.</summary>
    public double Fraction { get; init; }

    /// <summary>Where the stage runs, e.g. "gpu" or "ane", when the worker says.</summary>
    public string? Engine { get; init; }

    /// <summary>Length of the finished audio.</summary>
    public double? Seconds { get; init; }

    public string? Quality { get; init; }

    /// <summary>Why the song failed.</summary>
    public string? Message { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>
    /// The stages this song went through, each with the time it began, oldest first; the last one is <see cref="Stage"/>.
    /// The worker only says when a stage begins, so this is where the interface gets how long each one took.
    /// </summary>
    public IReadOnlyList<StageTime> Stages { get; init; } = [];

    /// <summary>A render from saved tokens: it has no planning or composing ahead of it.</summary>
    public bool Render { get; init; }

    /// <summary>The voice the form chose: once the song is ready, a version with it is queued (Voices/AutoVersions.cs).</summary>
    public Voices.SongVoice? Voice { get; init; }

    public bool Finished => Stage is "ready" or "failed" or "cancelled";

    /// <summary>
    /// Since when the current stage waits for an engine (the worker's detail "waiting" or "paused · …"), so its clock
    /// can stop meanwhile; null while it works. Closed waits are in the stage's <see cref="StageTime.Waited"/>.
    /// </summary>
    public DateTimeOffset? WaitingSince { get; init; }

    /// <summary>Moves on to <paramref name="stage"/>; the worker repeats a stage's event when only its detail changes.</summary>
    public SongState Entering(string stage, DateTimeOffset at)
    {
        if (stage == Stage)
        {
            return this;
        }
        var song = StopWaiting(at);
        return song with { Stage = stage, Stages = [.. song.Stages, new StageTime(stage, at)] };
    }

    /// <summary>
    /// Takes the worker's new detail and starts or ends a wait with it. Waiting in the queue is that stage's whole
    /// point, so only the stages after it count their waits.
    /// </summary>
    public SongState Saying(string detail, DateTimeOffset at)
    {
        var waiting = Stage != "queued" && !Finished && IsWaiting(detail);
        var song = waiting ? this : StopWaiting(at);
        return song with { Detail = detail, WaitingSince = waiting ? WaitingSince ?? at : null };
    }

    /// <summary>
    /// What YuE Studio's scheduler says while a song holds no engine: "waiting" for one, "paused · the GPU is busy …"
    /// while another song has it.
    /// </summary>
    public static bool IsWaiting(string detail) =>
        detail == "waiting" || detail.StartsWith("paused", StringComparison.Ordinal);

    private SongState StopWaiting(DateTimeOffset at)
    {
        if (WaitingSince is not { } since || Stages.Count == 0)
        {
            return this with { WaitingSince = null };
        }
        var last = Stages[^1];
        var waited = Math.Max(0, (at - since).TotalSeconds);
        return this with
        {
            WaitingSince = null,
            Stages = [.. Stages.Take(Stages.Count - 1), last with { Waited = last.Waited + waited }],
        };
    }

    /// <summary>The worker addresses songs by this path (cancel).</summary>
    [JsonIgnore]
    public string AudioPath { get; init; } = "";
}

/// <param name="Waited">Seconds the stage spent waiting for an engine; its working time is its length minus these.</param>
public sealed record StageTime(string Stage, DateTimeOffset StartedAt, double Waited = 0);

public sealed record LogEntry(DateTimeOffset Time, string Level, string Message);

/// <param name="Status">Whether a worker process runs.</param>
/// <param name="Busy">Whether any song is queued or running.</param>
/// <param name="StudioRunning">
/// The YuE Studio app is open. Its own worker then holds a second copy of the model, and two generating workers
/// can run the machine out of memory, so the interface warns.
/// </param>
/// <param name="LastError">The worker's last error event, until it starts again.</param>
/// <param name="Extensions">
/// Whether the last worker that said ready took YueUI's extra generate fields (sampling, full-quality steps, songs
/// over six minutes); null until a worker has started. False when a YuE Studio update changed what the extension
/// patches: the worker then runs as shipped and ignores those fields.
/// </param>
public sealed record WorkerInfo(WorkerStatus Status, bool Busy, bool StudioRunning, string? LastError, bool? Extensions = null);

/// <summary>Everything a client needs to draw the queue; also the first event of every event stream.</summary>
/// <param name="Versions">Songs being sung with another voice, and the last one that finished.</param>
/// <param name="Queue">Songs, renders and lyrics drafts waiting for their turn (Queue/JobQueue.cs), in order.</param>
/// <param name="BundleWindowSeconds">
/// <see cref="QueueOptions.BundleWindow"/>, so that the page can say how long songs may still pass a waiting draft or
/// version.
/// </param>
/// <param name="Stems">Songs being split into stems, and the last set that finished.</param>
/// <param name="Swaps">Uploaded recordings being sung with another voice, and the last one that finished.</param>
/// <param name="Videos">Music videos being rendered or waiting for it, and the last one that finished.</param>
public sealed record StatusSnapshot(
    WorkerInfo Worker,
    IReadOnlyList<SongState> Songs,
    IReadOnlyList<LogEntry> Log,
    IReadOnlyList<TranscriptionState>? Transcriptions = null,
    LyricsState? Lyrics = null,
    IReadOnlyList<VersionState>? Versions = null,
    IReadOnlyList<QueuedJob>? Queue = null,
    double? BundleWindowSeconds = null,
    IReadOnlyList<Voices.StemSetState>? Stems = null,
    IReadOnlyList<Speech.SpeechTake>? Speech = null,
    IReadOnlyList<Voices.SwapState>? Swaps = null,
    IReadOnlyList<Video.VideoState>? Videos = null,
    IReadOnlyList<Images.ImageState>? Images = null,
    MemoryInfo? Memory = null,
    IReadOnlyList<Ace.AceJob>? Ace = null,
    IReadOnlyList<Singing.SingTake>? Singing = null);

/// <summary>Which large model besides YuE2 holds the memory (Memory/ModelMemory.cs); null while none does.</summary>
public sealed record MemoryInfo(Memory.LargeModel? Holder);

/// <summary>Whether a restart would cut work off (<c>GET /api/busy</c>).</summary>
/// <param name="Reasons">songs, transcription, lyrics, voices (a version or stems), speech, video, images, ace or singing.</param>
public sealed record BusyInfo(bool Busy, IReadOnlyList<string> Reasons);

/// <summary>Whether Tonwerk has nothing to do and for how long (<c>GET /api/idle</c>, Status/IdleClock.cs).</summary>
/// <param name="Idle">Nothing runs and nothing waits, in any of the models' queues.</param>
/// <param name="IdleSeconds">Since the last activity; 0 while not idle. Counts from the server's start at most.</param>
/// <param name="Busy">What <c>GET /api/busy</c> names: what runs or holds the memory.</param>
/// <param name="Waiting">Kinds of jobs that wait for their turn: song, render, lyrics, transcription, version, stems, swap, video, image, ace, speech, singing, writing, sketch.</param>
public sealed record IdleInfo(bool Idle, double IdleSeconds, DateTimeOffset? IdleSince, IReadOnlyList<string> Busy, IReadOnlyList<string> Waiting);

/// <summary>
/// The last lyrics draft (Lyrics/LyricsWriter.cs). It arrives as an event rather than as the answer to the request:
/// loading the model and writing take long enough for a phone to lock or a proxy to give up on the connection.
/// </summary>
public sealed record LyricsState
{
    public required string Id { get; init; }

    /// <summary>queued (only in the answer to the request; see Queue/JobQueue.cs), writing, done or failed.</summary>
    public string Stage { get; init; } = "writing";

    public string? Lyrics { get; init; }

    /// <summary>Why it failed, e.g. LM Studio's own message.</summary>
    public string? Message { get; init; }

    /// <summary>The Mac it was asked for (<c>local</c> or a helper's id, Machines/); null for whichever is free.</summary>
    public string? Machine { get; init; }

    /// <summary>The name of the Mac that runs it, once one does.</summary>
    public string? RanOn { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public bool Finished => Stage is "done" or "failed";
}

/// <summary>
/// A transcription in flight: a recording going through SheetSage2 in the worker, keyed by <see cref="Id"/> (this
/// server's; the worker echoes it). <see cref="Stage"/> is the worker's: starting, progress, done, failed or cancelled.
/// </summary>
public sealed record TranscriptionState
{
    public required string Id { get; init; }

    /// <summary>The recording's file name as uploaded.</summary>
    public required string FileName { get; init; }

    /// <summary>"melody-full" or "melody-vocal".</summary>
    public required string Task { get; init; }

    public string Stage { get; init; } = "starting";

    /// <summary>0–1 when SheetSage2 reports it; null while it only says that it is still busy.</summary>
    public double? Fraction { get; init; }

    public string Detail { get; init; } = "";

    /// <summary>The melody score, once done.</summary>
    public string? Abc { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>The folder in the transcription library, once done.</summary>
    public string? Result { get; init; }

    /// <summary>Why it failed, and the worker's code for it (busy, no_env, afconvert, abc_error, crash).</summary>
    public string? Message { get; init; }

    public string? Code { get; init; }

    /// <summary>
    /// When work on it began, on whichever Mac, for the queue page; null while it waits. Only in memory: a job a
    /// restart cuts off fails, one still waiting starts anew.
    /// </summary>
    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public bool Finished => Stage is "done" or "failed" or "cancelled";

    /// <summary>The uploaded copy the worker reads; deleted once the transcription has finished.</summary>
    [JsonIgnore]
    public string UploadDirectory { get; init; } = "";
}
