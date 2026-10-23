using System.Text.Json;
using System.Text.Json.Nodes;
using YueUI.Api.Library;
using YueUI.Api.Status;
using YueUI.Api.Voices;

namespace YueUI.Api.Worker;

/// <summary>
/// Owns the one worker process and turns its event stream into state: the songs in flight, a log, and whether the
/// worker is up. Every change goes out through <see cref="StatusHub"/>, which also builds the snapshot from its part.
/// </summary>
/// <remarks>
/// The worker starts with the first command rather than with the server, so an idle server costs nothing; the
/// worker itself drops the model after ten idle minutes. Its protocol is documented at the top of
/// <c>yue2_worker.py</c>: songs are addressed by the absolute path of their <c>audio.flac</c>, which is mapped to
/// the library's <c>run/songN</c> ids here.
/// </remarks>
public sealed class WorkerHost(
    IWorkerLauncher launcher,
    SongLibrary library,
    IStudioDetector studio,
    StatusHub hub,
    TimeProvider time,
    Logs.LogStore logs,
    Stats.SynthesisStats synthesisStats,
    ILogger<WorkerHost> logger) : IHostedService, IAsyncDisposable, IWorkerStatus
{
    private const int LogCapacity = 300;
    private const int FinishedCapacity = 30;
    private const int FinishedTranscriptionCapacity = 5;

    /// <summary>The worker reports progress for every step; browsers (phones on mobile data) need far fewer.</summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(300);

    private static readonly TimeSpan StudioCheckInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a sent song counts as busy without its started event. The worker answers a command within moments of
    /// reading it, and reads only once it is up; this only keeps a command it refused silently from blocking forever.
    /// </summary>
    private static readonly TimeSpan ExpectationTimeout = TimeSpan.FromMinutes(5);

    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly Dictionary<string, SongState> _songs = [];
    private readonly Dictionary<string, TranscriptionState> _transcriptions = [];
    private readonly Dictionary<string, DateTimeOffset> _lastProgress = [];
    /// <summary>Songs a render was asked for and that have not started yet; the worker's started event does not say.</summary>
    private readonly HashSet<string> _renders = [];
    /// <summary>
    /// Songs announced (<see cref="ExpectSongs"/>) whose started event has not come yet, oldest first, with the voice
    /// they are to be sung with afterwards. The worker answers its commands in the order it reads them, so the next
    /// started event belongs to the oldest entry. The command goes along for <see cref="_requests"/>.
    /// </summary>
    private readonly List<(DateTimeOffset At, SongVoice? Voice, JsonObject? Command)> _expected = [];
    /// <summary>
    /// The generate command of each started song until it is saved: YuE Studio's <c>request.json</c> keeps only style,
    /// lyrics, planning, seed and score, so the rest (title, quality, length, sampling, …) is written beside it
    /// (<see cref="SongLibrary.SaveAppRequest"/>) once the song's folder holds a song.
    /// </summary>
    private readonly Dictionary<string, JsonObject> _requests = [];
    private readonly LinkedList<LogEntry> _log = [];
    private IWorkerConnection? _connection;
    private WorkerStatus _status = WorkerStatus.Stopped;
    private string? _lastError;
    private bool? _extensions;
    private bool _studioRunning;
    private DateTimeOffset _studioCheckedAt = DateTimeOffset.MinValue;

    /// <summary>The worker's state, for those that only need that (the snapshot's <see cref="StatusSnapshot.Worker"/>).</summary>
    public WorkerInfo Info()
    {
        var studioRunning = StudioRunning();
        lock (_gate)
        {
            return WorkerInfoLocked(studioRunning);
        }
    }

    public void CheckStudio() => StudioRunning();

    public WorkerPart Part()
    {
        lock (_gate)
        {
            return new WorkerPart(
                WorkerInfoLocked(_studioRunning),
                [.. _songs.Values.OrderBy(s => s.Run, StringComparer.Ordinal).ThenBy(s => s.Index)],
                [.. _log],
                [.. _transcriptions.Values.OrderBy(t => t.UpdatedAt)]);
        }
    }

    /// <summary>Songs are in flight or on their way to the worker: its model holds the memory.</summary>
    public bool IsBusy
    {
        get
        {
            lock (_gate)
            {
                return BusyLocked();
            }
        }
    }

    /// <summary>
    /// Songs or a transcription keep the worker: it must not be shut down to make room for another model. A
    /// transcription holds little memory (SheetSage2 runs on the CPU), but it runs inside the worker's process and
    /// would end with it. Unlike <see cref="IsBusy"/> this does not mean that YuE2 is loaded, so songs still go ahead.
    /// </summary>
    public bool InUse
    {
        get
        {
            lock (_gate)
            {
                return BusyLocked() || TranscribingLocked();
            }
        }
    }

    /// <summary>A transcription is running, or about to be sent (see <see cref="TranscribeAsync"/>).</summary>
    public bool IsTranscribing
    {
        get
        {
            lock (_gate)
            {
                return TranscribingLocked();
            }
        }
    }

    /// <summary>
    /// Counts the worker as busy from now until the started event of the command about to be sent. Without it the
    /// lyrics writer or a voice conversion could see an idle worker in between, shut it down and lose the command.
    /// Announce before checking whether the memory is free, as they check the worker after claiming it.
    /// </summary>
    /// <param name="voice">The voice the songs of this command are sung with once they are ready.</param>
    /// <param name="command">The generate command about to be sent, kept with its songs; null for a render.</param>
    public void ExpectSongs(SongVoice? voice = null, JsonObject? command = null)
    {
        lock (_gate)
        {
            _expected.Add((time.GetUtcNow(), voice, command));
        }
    }

    /// <summary>Takes back <see cref="ExpectSongs"/> for a command that was not sent after all.</summary>
    public void ExpectNoSongs()
    {
        lock (_gate)
        {
            if (_expected.Count > 0)
            {
                _expected.RemoveAt(_expected.Count - 1);
            }
        }
    }

    public Task GenerateAsync(JsonObject command, CancellationToken cancellationToken) => SendAsync(command, cancellationToken);

    /// <summary>Synthesizes a song again from its saved tokens, e.g. a draft at full quality.</summary>
    public Task RenderAsync(string songDirectory, string quality, string? engines, CancellationToken cancellationToken)
    {
        var command = new JsonObject { ["cmd"] = "render", ["path"] = songDirectory, ["quality"] = quality };
        lock (_gate)
        {
            _renders.Add(library.IdFor(songDirectory));
        }
        if (engines is not null)
        {
            command["engines"] = engines;
        }
        return SendAsync(command, cancellationToken);
    }

    /// <returns>False when the song is not in flight.</returns>
    public async Task<bool> CancelAsync(string id, CancellationToken cancellationToken)
    {
        string path;
        lock (_gate)
        {
            if (_connection is null || !_songs.TryGetValue(id, out var song) || song.Finished)
            {
                return false;
            }
            path = song.AudioPath;
        }
        await SendAsync(new JsonObject { ["cmd"] = "cancel", ["path"] = path }, cancellationToken);
        return true;
    }

    /// <summary>
    /// Hands an uploaded recording to SheetSage2 through the worker, which runs one transcription at a time; its
    /// progress arrives as <c>transcription</c> events.
    /// </summary>
    /// <param name="memoryTaken">
    /// Asked after the transcription has claimed the worker (<see cref="InUse"/>): the lyrics writer, the voice
    /// converter and the speech lab claim the memory first and then look at the worker, so one side always sees the
    /// other and none shuts the worker down under a transcription.
    /// </param>
    /// <returns>False when a transcription is already running or another model holds the memory; nothing was sent.</returns>
    public async Task<bool> TranscribeAsync(
        TranscriptionState transcription, string audioPath, bool offline, CancellationToken cancellationToken, Func<bool>? memoryTaken = null)
    {
        lock (_gate)
        {
            if (TranscribingLocked())
            {
                return false;
            }
            _transcriptions[transcription.Id] = transcription;
        }
        if (memoryTaken?.Invoke() == true)
        {
            lock (_gate)
            {
                _transcriptions.Remove(transcription.Id);
            }
            return false;
        }
        lock (_gate)
        {
            PruneFinishedTranscriptionsLocked();
        }
        Publish("transcription", transcription);
        try
        {
            await SendAsync(new JsonObject
            {
                ["cmd"] = "transcribe",
                ["id"] = transcription.Id,
                ["audio"] = audioPath,
                ["task"] = transcription.Task,
                ["offline"] = offline,
            }, cancellationToken);
        }
        catch
        {
            UpdateTranscription(transcription.Id, t => t with { Stage = "failed", Message = "The worker could not be started." });
            throw;
        }
        return true;
    }

    /// <returns>False when that transcription is not running.</returns>
    public async Task<bool> CancelTranscriptionAsync(string id, CancellationToken cancellationToken)
    {
        IWorkerConnection? connection;
        lock (_gate)
        {
            connection = _connection;
            if (connection is null || !_transcriptions.TryGetValue(id, out var transcription) || transcription.Finished)
            {
                return false;
            }
        }
        // The worker cancels whichever transcription runs; there is only ever one.
        await connection.SendAsync("""{"cmd": "transcribe_cancel"}""", cancellationToken);
        return true;
    }

    /// <summary>Whether the worker is still on a song of the run (or, with <paramref name="song"/>, on that one).</summary>
    public bool IsWorkingOn(string run, string? song = null)
    {
        lock (_gate)
        {
            return _songs.Values.Any(s => !s.Finished
                && (song is null ? s.Id.StartsWith($"{run}/", StringComparison.Ordinal) : s.Id == $"{run}/{song}"));
        }
    }

    /// <summary>The queue shows the run's songs by their new title too; then every browser reloads the library.</summary>
    public void RunRenamed(string run, string title)
    {
        List<SongState> renamed;
        lock (_gate)
        {
            renamed = [.. _songs.Values.Where(s => s.Run == run).Select(s => s with { Title = title })];
            foreach (var song in renamed)
            {
                _songs[song.Id] = song;
            }
        }
        foreach (var song in renamed)
        {
            Publish("song", song);
        }
        hub.LibraryChanged();
    }

    /// <summary>Something went wrong outside the worker that the queue's log should show, e.g. a queued song that could not start.</summary>
    public void LogError(string message) => AddLog("error", message);

    /// <summary>Cancels every song. Does not start a worker just for that.</summary>
    public async Task StopAllAsync(CancellationToken cancellationToken)
    {
        var connection = _connection;
        if (connection is not null)
        {
            await connection.SendAsync("""{"cmd": "stop"}""", cancellationToken);
        }
    }

    /// <summary>Ends the worker process and with it all the memory the model holds (for when YuE Studio should run).</summary>
    public async Task ShutdownWorkerAsync()
    {
        await _startGate.WaitAsync();
        try
        {
            var connection = _connection;
            if (connection is not null)
            {
                AddLog("info", "Stopping the worker");
                await connection.DisposeAsync();
                // Right away rather than when the pump notices the end of the output: a command sent in between
                // would otherwise go to the process that is ending and be lost. Marks what was still running as failed.
                Exited(connection);
            }
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task SendAsync(JsonObject command, CancellationToken cancellationToken)
    {
        var connection = await EnsureStartedAsync(cancellationToken);
        await connection.SendAsync(command.ToJsonString(), cancellationToken);
    }

    private async Task<IWorkerConnection> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        await _startGate.WaitAsync(cancellationToken);
        try
        {
            if (_connection is { } running)
            {
                return running;
            }

            var connection = launcher.Launch();
            lock (_gate)
            {
                _connection = connection;
                _status = WorkerStatus.Starting;
                _lastError = null;
            }
            PublishWorker();
            // Commands sent before "ready" wait in the pipe: the worker reads stdin only once it is up.
            _ = Task.Run(() => PumpAsync(connection), CancellationToken.None);
            return connection;
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task PumpAsync(IWorkerConnection connection)
    {
        try
        {
            await foreach (var line in connection.Output.ReadAllAsync())
            {
                try
                {
                    Handle(line);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Could not handle worker line {Line}", line.Text);
                }
            }
        }
        finally
        {
            Exited(connection);
        }
    }

    private void Exited(IWorkerConnection connection)
    {
        var failed = new List<SongState>();
        List<string> transcriptions;
        lock (_gate)
        {
            if (!ReferenceEquals(_connection, connection))
            {
                return;
            }
            _connection = null;
            _status = WorkerStatus.Stopped;
            _renders.Clear();
            _expected.Clear();
            _requests.Clear();
            foreach (var song in _songs.Values.Where(s => !s.Finished).ToList())
            {
                var now = time.GetUtcNow();
                var updated = song.Entering("failed", now) with { Message = "The worker stopped.", UpdatedAt = now };
                _songs[song.Id] = updated;
                failed.Add(updated);
            }
            transcriptions = [.. _transcriptions.Values.Where(t => !t.Finished).Select(t => t.Id)];
        }
        foreach (var song in failed)
        {
            Publish("song", song);
        }
        foreach (var id in transcriptions)
        {
            UpdateTranscription(id, t => t with { Stage = "failed", Message = "The worker stopped." });
        }
        PublishWorker();
    }

    private void Handle(WorkerLine line)
    {
        if (line.IsError || !line.Text.StartsWith('{'))
        {
            // Python warnings and tracebacks; the worker's own messages come as "log" events.
            if (!string.IsNullOrWhiteSpace(line.Text))
            {
                AddLog(line.IsError ? "stderr" : "info", line.Text);
            }
            return;
        }

        JsonObject? message;
        try
        {
            message = JsonNode.Parse(line.Text) as JsonObject;
        }
        catch (JsonException)
        {
            message = null;
        }
        if (message is null)
        {
            AddLog("info", line.Text);
            return;
        }

        switch (Text(message["event"]))
        {
            case "ready":
                lock (_gate)
                {
                    _status = WorkerStatus.Ready;
                    // YuE Studio's worker started without the extension says nothing about it.
                    _extensions = message["yueui_extensions"] is JsonValue flag && flag.TryGetValue(out bool active) && active;
                }
                // Why extensions or LoRAs are off, as a warning, so the log page's report for Claude carries it.
                if (Text(message["yueui_problem"]) is { Length: > 0 } problem)
                {
                    AddLog("warning", problem);
                }
                PublishWorker();
                break;
            case "log":
                AddLog("info", Text(message["message"]) ?? "");
                break;
            case "error":
                var error = Text(message["message"]) ?? "Unknown error";
                lock (_gate)
                {
                    _lastError = error;
                    // Typically a command the worker refused, which then never starts.
                    _expected.Clear();
                }
                AddLog("error", error);
                PublishWorker();
                break;
            case "started":
                Started(message);
                break;
            case "stage":
                Update(message, song => song.Entering(Text(message["stage"]) ?? song.Stage, time.GetUtcNow())
                    .Saying(Text(message["detail"]) ?? "", time.GetUtcNow()) with
                {
                    Engine = Text(message["engine"]) ?? song.Engine,
                    // A new stage starts from zero; the worker's progress events follow.
                    Fraction = Text(message["stage"]) == "ready" ? 1 : 0,
                });
                break;
            case "progress":
                Update(message, song => song.Saying(Text(message["detail"]) ?? song.Detail, time.GetUtcNow()) with
                {
                    Fraction = Number(message["fraction"]) ?? song.Fraction,
                }, throttle: true);
                break;
            case "song":
                SaveRequest(message);
                Update(message, song => song with
                {
                    Seconds = Number(message["seconds"]),
                    Quality = Text(message["quality"]),
                    Engine = Text(message["engine"]) ?? song.Engine,
                });
                hub.LibraryChanged();
                break;
            case "failed":
                lock (_gate)
                {
                    _requests.Remove(library.IdFor(Text(message["path"]) ?? ""));
                }
                Update(message, song => song.Entering("failed", time.GetUtcNow()) with { Message = Text(message["message"]) });
                AddLog("error", $"{library.IdFor(Text(message["path"]) ?? "")}: {Text(message["message"])}");
                break;
            case "idle":
                PublishWorker();
                break;
            case "transcribe":
                Transcribed(message);
                break;
        }
    }

    private void Transcribed(JsonObject message)
    {
        var stage = Text(message["stage"]);
        var updated = UpdateTranscription(Text(message["id"]) ?? "", t => t with
        {
            // "progress" without a fraction is a heartbeat: keep the last one.
            Stage = stage ?? t.Stage,
            Fraction = stage == "done" ? 1 : Number(message["fraction"]) ?? t.Fraction,
            Detail = Text(message["detail"]) ?? t.Detail,
            Abc = Text(message["abc"]) ?? t.Abc,
            Warnings = message["warnings"] is JsonArray warnings
                ? [.. warnings.Select(w => Text(w) ?? w?.ToJsonString() ?? "")]
                : t.Warnings,
            Result = Text(message["output"]) is { } output ? Path.GetFileName(output) : t.Result,
            Message = Text(message["message"]) ?? t.Message,
            Code = Text(message["code"]) ?? t.Code,
        });
        if (updated is { Stage: "failed" })
        {
            AddLog("error", $"Transcription of {updated.FileName}: {updated.Message}");
        }
    }

    /// <summary>Applies a change to a transcription and publishes it; a finished one no longer needs its upload.</summary>
    private TranscriptionState? UpdateTranscription(string id, Func<TranscriptionState, TranscriptionState> change)
    {
        TranscriptionState updated;
        lock (_gate)
        {
            if (!_transcriptions.TryGetValue(id, out var transcription) || transcription.Finished)
            {
                return null;
            }
            updated = change(transcription) with { UpdatedAt = time.GetUtcNow() };
            _transcriptions[id] = updated;
        }
        if (updated.Finished)
        {
            DeleteUpload(updated);
        }
        Publish("transcription", updated);
        return updated;
    }

    private void DeleteUpload(TranscriptionState transcription)
    {
        try
        {
            if (Directory.Exists(transcription.UploadDirectory))
            {
                Directory.Delete(transcription.UploadDirectory, recursive: true);
            }
        }
        catch (IOException exception)
        {
            logger.LogWarning(exception, "Could not delete the upload {Directory}", transcription.UploadDirectory);
        }
    }

    private void PruneFinishedTranscriptionsLocked()
    {
        foreach (var old in _transcriptions.Values.Where(t => t.Finished).OrderByDescending(t => t.UpdatedAt).Skip(FinishedTranscriptionCapacity).ToList())
        {
            _transcriptions.Remove(old.Id);
        }
    }

    /// <summary>Writes the finished song's generate command beside it, before the library event makes browsers read it.</summary>
    private void SaveRequest(JsonObject message)
    {
        var path = Text(message["path"]) ?? "";
        JsonObject? command;
        lock (_gate)
        {
            if (!_requests.Remove(library.IdFor(path), out command))
            {
                return;
            }
        }
        try
        {
            library.SaveAppRequest(Path.GetDirectoryName(path) ?? "", command);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not save the parameters of {Path}", path);
        }
    }

    private void Started(JsonObject message)
    {
        var run = Text(message["job"]) ?? "";
        // A render of a run renamed in this app would otherwise announce it by its old name.
        var title = library.RenamedTitle(run) ?? Text(message["title"]) ?? "";
        var started = new List<SongState>();
        foreach (var node in message["songs"]?.AsArray() ?? [])
        {
            if (node is not JsonObject entry || Text(entry["path"]) is not { } path)
            {
                continue;
            }
            var now = time.GetUtcNow();
            started.Add(new SongState
            {
                Id = library.IdFor(path),
                Stages = [new StageTime("queued", now)],
                Run = run,
                Title = title,
                Index = (int)(Number(entry["index"]) ?? 1),
                Seed = (long?)Number(entry["seed"]),
                AudioPath = path,
                UpdatedAt = now,
            });
        }

        lock (_gate)
        {
            SongVoice? voice = null;
            JsonObject? command = null;
            if (_expected.Count > 0)
            {
                (_, voice, command) = _expected[0];
                _expected.RemoveAt(0);
            }
            for (var i = 0; i < started.Count; i++)
            {
                if (_renders.Remove(started[i].Id))
                {
                    started[i] = started[i] with { Render = true };
                }
                else
                {
                    if (voice is not null)
                    {
                        started[i] = started[i] with { Voice = voice };
                    }
                    if (command is not null)
                    {
                        _requests[started[i].Id] = command;
                    }
                }
                _songs[started[i].Id] = started[i];
            }
            PruneFinishedLocked();
        }
        foreach (var song in started)
        {
            Publish("song", song);
        }
        PublishWorker();
    }

    private void Update(JsonObject message, Func<SongState, SongState> change, bool throttle = false)
    {
        var id = library.IdFor(Text(message["path"]) ?? "");
        SongState updated;
        var now = time.GetUtcNow();
        bool publish;
        lock (_gate)
        {
            if (!_songs.TryGetValue(id, out var song))
            {
                return;
            }
            updated = change(song) with { UpdatedAt = now };
            _songs[id] = updated;
            publish = !throttle || updated.Fraction >= 1
                || !_lastProgress.TryGetValue(id, out var last) || now - last >= ProgressInterval;
            if (publish)
            {
                _lastProgress[id] = now;
            }
        }
        if (publish)
        {
            Publish("song", updated);
        }
        if (updated.Finished)
        {
            PublishWorker();
        }
    }

    private void PruneFinishedLocked()
    {
        foreach (var old in _songs.Values.Where(s => s.Finished).OrderByDescending(s => s.UpdatedAt).Skip(FinishedCapacity).ToList())
        {
            _songs.Remove(old.Id);
            _lastProgress.Remove(old.Id);
        }
    }

    private void AddLog(string level, string text)
    {
        var entry = new LogEntry(time.GetUtcNow(), level, text);
        lock (_gate)
        {
            _log.AddLast(entry);
            if (_log.Count > LogCapacity)
            {
                _log.RemoveFirst();
            }
        }
        Publish("log", entry);
        synthesisStats.Observe(text, entry.Time);
        // Only the last few hundred lines stay in memory for the queue page; the log page keeps them for days.
        logs.Add(Logs.LogSources.Worker, level switch
        {
            "error" => Logs.LogLevels.Error,
            "warning" => Logs.LogLevels.Warning,
            _ => Logs.LogLevels.Info,
        }, text);
    }

    private void PublishWorker()
    {
        var studioRunning = StudioRunning();
        WorkerInfo info;
        lock (_gate)
        {
            info = WorkerInfoLocked(studioRunning);
        }
        Publish("worker", info);
    }

    private void Publish(string type, object data) => hub.Publish(type, data);

    private WorkerInfo WorkerInfoLocked(bool studioRunning) =>
        new(_status, BusyLocked(), studioRunning, _lastError, _extensions);

    private bool TranscribingLocked() => _transcriptions.Values.Any(t => !t.Finished);

    private bool BusyLocked()
    {
        _expected.RemoveAll(sent => time.GetUtcNow() - sent.At > ExpectationTimeout);
        return _expected.Count > 0 || _songs.Values.Any(s => !s.Finished);
    }

    /// <summary>Scanning the process table takes a moment, and worker events come in bursts: look at most every few seconds.</summary>
    private bool StudioRunning()
    {
        var now = time.GetUtcNow();
        if (now - _studioCheckedAt >= StudioCheckInterval)
        {
            _studioRunning = studio.IsRunning();
            _studioCheckedAt = now;
        }
        return _studioRunning;
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static double? Number(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken) => await ShutdownWorkerAsync();

    public async ValueTask DisposeAsync()
    {
        if (_connection is { } connection)
        {
            await connection.DisposeAsync();
        }
    }
}
