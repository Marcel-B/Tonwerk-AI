using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using YueUI.Api.Data;
using YueUI.Api.Library;
using YueUI.Api.Lyrics;
using YueUI.Api.Speech;
using YueUI.Api.Voices;
using YueUI.Api.Worker;

namespace YueUI.Api.Queue;

/// <summary>
/// One queue for songs, renders, lyrics drafts and transcriptions: what cannot start right away waits here and starts on its turn,
/// instead of being refused with 409.
/// </summary>
/// <remarks>
/// The rule is the one the lyrics writer and the voice converter follow on their own: on 24 GB only one large model
/// fits, YuE2, the lyrics model or separation and Seed-VC. Songs and renders go to the worker as soon as neither a
/// draft nor a voice conversion holds the memory; the worker queues them itself and batches what it can. A lyrics
/// draft needs the worker idle as well. Jobs start in order, with one exception that bundles them by model: while
/// YuE2 holds the memory, songs and renders pass drafts waiting ahead of them and go to the worker, which batches
/// them with the songs it has, instead of YuE2 being unloaded for the draft and loaded again after it. That lasts only
/// as long as the draft at the front has waited less than <see cref="QueueOptions.BundleWindow"/>; after that songs
/// wait behind it, so the worker runs empty and the draft gets its turn. Voice conversions keep their own queue
/// (<see cref="VoiceConverter"/>), which waits for the memory in the same way; a version that has waited that long
/// holds back new songs as well. So does a take of the speech lab (<see cref="SpeechLab"/>), for as long as it speaks.
/// Waiting jobs are kept in <see cref="SqliteJobStore"/> and resumed after a restart.
/// <para>
/// Transcriptions run beside YuE2 (SheetSage2 needs about 2 GB and the CPU) but inside the worker's process, which the
/// other models shut down to make room; so a running one keeps them waiting (<see cref="WorkerHost.InUse"/>), and it
/// waits for them in turn. They form a lane of their own: the worker takes one at a time, and one waiting for that does
/// not hold back the songs behind it, nor does a song hold back a transcription. A draft ahead of one does, as it does
/// songs, bundling aside, so that recordings in a row cannot keep the lyrics model out.
/// </para>
/// </remarks>
public sealed class JobQueue(
    SqliteJobStore store,
    WorkerHost host,
    Status.StatusHub hub,
    LyricsWriter lyrics,
    VoiceConverter voices,
    Memory.ModelMemory memory,
    SongLibrary library,
    YuePaths paths,
    IOptions<QueueOptions> options,
    TimeProvider time,
    ILogger<JobQueue> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// How often the head of the queue is tried again. Nothing announces that a draft or a conversion ended, and a
    /// job waits minutes anyway; a new job is tried at once.
    /// </summary>
    public static TimeSpan CheckInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Where a song job's payload keeps its <see cref="SongVoice"/>; not part of the worker's protocol.</summary>
    private const string VoiceKey = "yueui_voice";

    /// <summary>Held while a job is started, so that a request and the queue's loop never start two at once.</summary>
    private readonly SemaphoreSlim _starting = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0);
    private readonly Lock _gate = new();
    private readonly List<(QueuedJob Job, JsonObject Payload)> _jobs = [];

    public IReadOnlyList<QueuedJob> Jobs
    {
        get
        {
            lock (_gate)
            {
                return [.. _jobs.Select(j => j.Job)];
            }
        }
    }

    /// <summary>A new run: to the worker now if the memory is free and nothing waits, otherwise into the queue.</summary>
    /// <returns>The waiting job, or null when the song went to the worker.</returns>
    /// <exception cref="WorkerUnavailableException">The worker could not be started.</exception>
    /// <param name="voice">Sings each finished song again with this voice.</param>
    /// <param name="loraPath">The file of the request's LoRA.</param>
    public Task<QueuedJob?> GenerateAsync(GenerateRequest request, SongVoice? voice, string? loraPath, CancellationToken cancellationToken)
    {
        var job = new QueuedJob(NewId(), JobKind.Song, request.Title?.Trim() ?? "", time.GetUtcNow(), Batch: request.Batch, Quality: request.Quality, VoiceLabel: voice?.VoiceLabel);
        var payload = request.ToWorkerCommand(loraPath);
        if (voice is not null)
        {
            // Kept beside the worker's command, so that it survives a restart with it; taken off before sending.
            payload[VoiceKey] = JsonSerializer.SerializeToNode(voice, Json);
        }
        return SubmitAsync(job, payload, cancellationToken);
    }

    /// <inheritdoc cref="GenerateAsync"/>
    public Task<QueuedJob?> RenderAsync(string run, string song, string title, string quality, string? engines, CancellationToken cancellationToken)
    {
        var job = new QueuedJob(NewId(), JobKind.Render, title, time.GetUtcNow(), SongId: $"{run}/{song}", Quality: quality);
        var payload = new JsonObject { ["run"] = run, ["song"] = song, ["quality"] = quality, ["engines"] = engines };
        return SubmitAsync(job, payload, cancellationToken);
    }

    /// <summary>A lyrics draft or revision; its result arrives as a <c>lyrics</c> event under the id returned.</summary>
    /// <returns>The draft's state: writing, or queued while it waits.</returns>
    public async Task<LyricsState> LyricsAsync(LyricsRequest request, CancellationToken cancellationToken)
    {
        var id = LyricsWriter.NewId();
        var title = request.Keywords?.Trim() ?? "";
        var job = new QueuedJob(id, JobKind.Lyrics, title.Length > 80 ? title[..80] + "…" : title, time.GetUtcNow(), Revision: request.Instruction is not null);
        var queued = await SubmitAsync(job, JsonSerializer.SerializeToNode(request, Json)!.AsObject(), cancellationToken);
        return new LyricsState { Id = id, Stage = queued is null ? "writing" : "queued", UpdatedAt = time.GetUtcNow() };
    }

    /// <summary>
    /// A recording for SheetSage2, already stored in its upload folder, which is deleted when the transcription ends
    /// (or leaves the queue).
    /// </summary>
    /// <returns>The waiting job, or null when it went to the worker.</returns>
    /// <exception cref="WorkerUnavailableException">The worker could not be started.</exception>
    public Task<QueuedJob?> TranscribeAsync(TranscriptionState transcription, string audioPath, CancellationToken cancellationToken)
    {
        var job = new QueuedJob(transcription.Id, JobKind.Transcription, transcription.FileName, time.GetUtcNow(), TranscriptionTask: transcription.Task);
        var payload = new JsonObject
        {
            ["audio"] = audioPath,
            ["uploadDirectory"] = transcription.UploadDirectory,
            ["fileName"] = transcription.FileName,
            ["task"] = transcription.Task,
        };
        return SubmitAsync(job, payload, cancellationToken);
    }

    /// <returns>False when no such job waits (it may have started meanwhile).</returns>
    public bool Cancel(string id)
    {
        JsonObject? payload;
        lock (_gate)
        {
            var index = _jobs.FindIndex(j => j.Job.Id == id);
            if (index < 0)
            {
                return false;
            }
            payload = _jobs[index].Job.Kind == JobKind.Transcription ? _jobs[index].Payload : null;
            _jobs.RemoveAt(index);
        }
        if (payload is not null)
        {
            DeleteUpload(payload);
        }
        Persist(() => store.Remove(id));
        Published();
        return true;
    }

    /// <summary>Moves a waiting job by <paramref name="offset"/> places, towards the front when negative.</summary>
    /// <returns>False when no such job waits.</returns>
    public bool Move(string id, int offset)
    {
        List<string> order;
        lock (_gate)
        {
            var index = _jobs.FindIndex(j => j.Job.Id == id);
            if (index < 0)
            {
                return false;
            }
            var entry = _jobs[index];
            _jobs.RemoveAt(index);
            _jobs.Insert(Math.Clamp(index + offset, 0, _jobs.Count), entry);
            order = [.. _jobs.Select(j => j.Job.Id)];
        }
        Persist(() => store.Reorder(order));
        Published();
        // The job now at the front may be one that can start.
        _wake.Release();
        return true;
    }

    private async Task<QueuedJob?> SubmitAsync(QueuedJob job, JsonObject payload, CancellationToken cancellationToken)
    {
        await _starting.WaitAsync(cancellationToken);
        try
        {
            bool first;
            lock (_gate)
            {
                // Transcriptions keep a lane of their own (see the remarks).
                first = IsTranscription(job)
                    ? !_jobs.Any(j => IsTranscription(j.Job)) && TranscriptionMayGoLocked(_jobs.Count)
                    : Lane().Count == 0 || (IsYue(job) && !_jobs.Any(j => IsYue(j.Job)) && MayPassLocked());
            }
            // Behind waiting jobs even if it could start: the order is the one the user sees, bundling aside.
            if (first && await TryStartAsync(job, payload, cancellationToken))
            {
                return null;
            }
            lock (_gate)
            {
                _jobs.Add((job, payload));
            }
            Persist(() => store.Add(job, payload));
        }
        finally
        {
            _starting.Release();
        }
        Published();
        return job;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            var stored = store.All();
            lock (_gate)
            {
                _jobs.AddRange(stored);
            }
            if (stored.Count > 0)
            {
                logger.LogInformation("Resuming {Count} queued jobs", stored.Count);
                Published();
            }
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(exception, "Could not read the queued jobs");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await StartWaitingAsync(stoppingToken);
                await _wake.WaitAsync(CheckInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "The queue could not start a job");
            }
        }
    }

    /// <summary>Starts what can start in either lane, for as long as anything does.</summary>
    private async Task StartWaitingAsync(CancellationToken stoppingToken)
    {
        while (true)
        {
            var started = await StartTranscriptionAsync(stoppingToken);
            if (!await StartFrontAsync(stoppingToken) && !started)
            {
                return;
            }
        }
    }

    /// <summary>The first waiting transcription, when the worker has none and the memory is free for it.</summary>
    /// <returns>Whether one left the queue.</returns>
    private async Task<bool> StartTranscriptionAsync(CancellationToken stoppingToken)
    {
        await _starting.WaitAsync(stoppingToken);
        (QueuedJob Job, JsonObject Payload) next;
        try
        {
            lock (_gate)
            {
                var index = _jobs.FindIndex(j => IsTranscription(j.Job));
                if (index < 0 || !TranscriptionMayGoLocked(index))
                {
                    return false;
                }
                next = _jobs[index];
            }
            try
            {
                if (!await TryStartAsync(next.Job, next.Payload, stoppingToken))
                {
                    return false;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Failed(next.Job, exception.Message);
                DeleteUpload(next.Payload);
            }
            lock (_gate)
            {
                _jobs.RemoveAll(j => j.Job.Id == next.Job.Id);
            }
            Persist(() => store.Remove(next.Job.Id));
        }
        finally
        {
            _starting.Release();
        }
        Published();
        return true;
    }

    /// <summary>
    /// Starts one job from the front of the songs' and drafts' lane; when the front is a draft that cannot start yet,
    /// the first song or render behind it may (see the remarks).
    /// </summary>
    /// <returns>Whether one left the queue.</returns>
    private async Task<bool> StartFrontAsync(CancellationToken stoppingToken)
    {
        await _starting.WaitAsync(stoppingToken);
        try
        {
            (QueuedJob Job, JsonObject Payload) next;
            (QueuedJob Job, JsonObject Payload)? passing;
            lock (_gate)
            {
                var lane = Lane();
                if (lane.Count == 0)
                {
                    return false;
                }
                next = lane[0];
                var index = MayPassLocked() ? lane.FindIndex(j => IsYue(j.Job)) : -1;
                passing = index > 0 ? lane[index] : null;
            }
            try
            {
                if (!await TryStartAsync(next.Job, next.Payload, stoppingToken))
                {
                    if (passing is null || !await TryStartAsync(passing.Value.Job, passing.Value.Payload, stoppingToken))
                    {
                        return false;
                    }
                    next = passing.Value;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Failed(next.Job, exception.Message);
            }
            lock (_gate)
            {
                _jobs.RemoveAll(j => j.Job.Id == next.Job.Id);
            }
            Persist(() => store.Remove(next.Job.Id));
        }
        finally
        {
            _starting.Release();
        }
        Published();
        return true;
    }

    /// <summary>Songs and renders are for YuE2; drafts for the lyrics model.</summary>
    private static bool IsYue(QueuedJob job) => job.Kind is JobKind.Song or JobKind.Render;

    private static bool IsTranscription(QueuedJob job) => job.Kind == JobKind.Transcription;

    /// <summary>The waiting songs, renders and drafts, in their order; transcriptions keep a lane of their own.</summary>
    private List<(QueuedJob Job, JsonObject Payload)> Lane() => [.. _jobs.Where(j => !IsTranscription(j.Job))];

    /// <summary>
    /// A transcription at <paramref name="index"/> waits behind a draft ahead of it, unless songs may pass that draft
    /// too: it needs the worker for minutes, and the draft would wait for it (see the remarks).
    /// </summary>
    private bool TranscriptionMayGoLocked(int index) =>
        !_jobs.Take(index).Any(j => j.Job.Kind == JobKind.Lyrics) || MayPassLocked();

    /// <summary>
    /// Whether a song may go ahead of the draft at the front: only while YuE2 holds the memory anyway, since otherwise
    /// the draft can start itself, and only until the draft has waited <see cref="QueueOptions.BundleWindow"/>.
    /// </summary>
    private bool MayPassLocked() =>
        Lane() is [var front, ..]
        && front.Job.Kind == JobKind.Lyrics
        && host.IsBusy
        && time.GetUtcNow() - front.Job.CreatedAt < options.Value.BundleWindow;

    /// <summary>A version waiting for longer than the window gets the memory next: new songs wait until it has it.</summary>
    private bool VersionOverdue() =>
        voices.WaitingSince is { } since && time.GetUtcNow() - since >= options.Value.BundleWindow;

    /// <returns>False when the memory is taken and the job has to wait.</returns>
    private async Task<bool> TryStartAsync(QueuedJob job, JsonObject payload, CancellationToken cancellationToken)
    {
        if (job.Kind == JobKind.Lyrics)
        {
            return StartLyrics(job, payload);
        }
        if (job.Kind == JobKind.Transcription)
        {
            return await StartTranscriptionAsync(job, payload, cancellationToken);
        }

        JsonObject? command = null;
        SongVoice? voice = null;
        string? directory = null;
        if (job.Kind == JobKind.Song)
        {
            command = payload.DeepClone().AsObject();
            if (command.TryGetPropertyValue(VoiceKey, out var stored))
            {
                voice = stored?.Deserialize<SongVoice>(Json);
                command.Remove(VoiceKey);
            }
        }
        else if (library.SongDirectory(Text(payload["run"]), Text(payload["song"])) is not { } found)
        {
            throw new InvalidOperationException("The song is gone.");
        }
        else
        {
            directory = found;
        }

        // Announced before looking: the other models claim the memory first and then look at the worker
        // (Memory/ModelMemory.cs), so one of the two always sees the other.
        host.ExpectSongs(voice, command);
        if (memory.Holder is not null || VersionOverdue())
        {
            host.ExpectNoSongs();
            return false;
        }
        try
        {
            if (command is not null)
            {
                await host.GenerateAsync(command, cancellationToken);
            }
            else
            {
                await host.RenderAsync(directory!, Text(payload["quality"]), payload["engines"]?.GetValue<string>(), cancellationToken);
            }
        }
        catch
        {
            host.ExpectNoSongs();
            throw;
        }
        return true;
    }

    private async Task<bool> StartTranscriptionAsync(QueuedJob job, JsonObject payload, CancellationToken cancellationToken)
    {
        var audio = Text(payload["audio"]);
        if (!File.Exists(audio))
        {
            // The upload lives in the temp folder, which a reboot may have emptied while it waited.
            throw new InvalidOperationException("The recording is gone.");
        }
        var transcription = new TranscriptionState
        {
            Id = job.Id,
            FileName = Text(payload["fileName"]),
            Task = Text(payload["task"]),
            UploadDirectory = Text(payload["uploadDirectory"]),
            UpdatedAt = time.GetUtcNow(),
        };
        return await host.TranscribeAsync(
            transcription,
            audio,
            offline: paths.SheetSageModelsCached,
            cancellationToken,
            memoryTaken: () => memory.Holder is not null || VersionOverdue());
    }

    /// <summary>The upload of a transcription that leaves the queue without reaching the worker.</summary>
    private void DeleteUpload(JsonObject payload)
    {
        var directory = Text(payload["uploadDirectory"]);
        try
        {
            if (directory.Length > 0 && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not delete the upload {Directory}", directory);
        }
    }

    private bool StartLyrics(QueuedJob job, JsonObject payload)
    {
        var request = payload.Deserialize<LyricsRequest>(Json) ?? throw new JsonException("The lyrics request is unreadable.");
        var revision = request.Lyrics is not null && request.Instruction is not null ? new LyricsRevision(request.Lyrics, request.Instruction) : null;
        try
        {
            lyrics.Start(
                request.Keywords,
                request.Style,
                request.Language ?? LyricsLanguage.English,
                request.Model,
                request.Image,
                revision,
                job.Id);
            return true;
        }
        catch (LyricsBusyException)
        {
            return false;
        }
    }

    /// <summary>A job that could not start leaves the queue; the log says why, and a draft's form hears it too.</summary>
    private void Failed(QueuedJob job, string message)
    {
        logger.LogWarning("The queued {Kind} {Id} could not start: {Message}", job.Kind, job.Id, message);
        if (job.Kind == JobKind.Lyrics)
        {
            hub.UpdateLyrics(new LyricsState { Id = job.Id, Stage = "failed", Message = message, UpdatedAt = time.GetUtcNow() });
        }
        else
        {
            host.LogError($"{(job.Title.Length > 0 ? job.Title : job.SongId ?? job.Kind.ToString())}: {message}");
        }
    }

    private void Published() => hub.UpdateQueue(Jobs);

    /// <summary>The queue works from memory; a database that cannot be written only costs the resumption after a restart.</summary>
    private void Persist(Action write)
    {
        try
        {
            write();
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not store the queue");
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..12];

    private static string Text(JsonNode? node) => node?.GetValue<string>() ?? "";
}
