using System.Threading.Channels;
using Microsoft.Extensions.Options;
using YueUI.Api.Data;
using YueUI.Api.Memory;
using YueUI.Api.Worker;

namespace YueUI.Api.Speech;

/// <summary>
/// The speech lab's takes, spoken one at a time: a text, a model and optionally a recorded voice to clone
/// (<see cref="ISpeechEngine"/>). For hearing which local model could speak a podcast before building one. A take can
/// also be a speech recording cleaned of noise (<see cref="SpeechTake.Source"/>), which waits in the same line.
/// </summary>
/// <remarks>
/// A speech model is 3 to 17 GB, so it follows the rule the lyrics writer and the voice converter follow on 24 GB:
/// a take waits while songs are generated, lyrics written or a song sung with another voice, an idle YuE worker is
/// shut down first, and while it holds the memory (<see cref="ModelMemory"/>) the others wait. A take may be handed to
/// another Mac instead (<see cref="Machines.Machines"/>), which then waits for its own memory. Takes are not in
/// <c>JobQueue</c>: a take is short, and the lab lists its own. Queued takes survive a restart; one that was halfway is marked failed.
/// </remarks>
public sealed class SpeechLab(
    SqliteSpeechStore store,
    ISpeechEngine engine,
    Machines.Machines machines,
    Machines.RemoteEngines remote,
    Status.StatusHub hub,
    IOptions<SpeechOptions> options,
    TimeProvider time,
    Export.TaggedFiles tagged,
    ILogger<SpeechLab> logger) : BackgroundService
{
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Lock _gate = new();
    private string? _running;
    private CancellationTokenSource? _cancel;

    /// <summary>One take per model, spoken in the order given.</summary>
    /// <param name="machine">Where to speak, as <see cref="Machines.Machines.Normalize"/> stores it.</param>
    public IReadOnlyList<SpeechTake> Enqueue(string text, SpeechVoice? voice, IEnumerable<SpeechModel> models, string? machine = null)
    {
        var takes = new List<SpeechTake>();
        foreach (var model in models)
        {
            var now = time.GetUtcNow();
            var take = new SpeechTake
            {
                Id = Guid.NewGuid().ToString("N"),
                ModelId = model.Id,
                ModelLabel = model.Label,
                Text = text,
                VoiceId = voice?.Id,
                VoiceLabel = voice?.Label,
                Machine = machine,
                CreatedAt = now,
                UpdatedAt = now,
            };
            store.AddTake(take);
            hub.UpdateSpeech(take);
            _queue.Writer.TryWrite(take.Id);
            takes.Add(take);
        }
        return takes;
    }

    /// <summary>
    /// A recording to clean, already decoded to <see cref="SqliteSpeechStore.SourcePath"/> under <paramref name="id"/>.
    /// It waits in the same line as the spoken takes: an enhancement model is small, but loads through the same
    /// mlx-audio and would otherwise run beside a 9 GB speech model.
    /// </summary>
    public SpeechTake EnqueueCleanup(string id, string source, SpeechModel model, string? machine = null)
    {
        var now = time.GetUtcNow();
        var take = new SpeechTake
        {
            Id = id,
            ModelId = model.Id,
            ModelLabel = model.Label,
            Text = "",
            Source = source,
            Machine = machine,
            CreatedAt = now,
            UpdatedAt = now,
        };
        store.AddTake(take);
        hub.UpdateSpeech(take);
        _queue.Writer.TryWrite(take.Id);
        return take;
    }

    /// <summary>Stops the take if it is being spoken, and removes it with its audio.</summary>
    public void Delete(SpeechTake take)
    {
        lock (_gate)
        {
            if (_running == take.Id)
            {
                _cancel?.Cancel();
            }
        }
        store.RemoveTake(take.Id);
        hub.UpdateSpeech(take with { Stage = "cancelled", UpdatedAt = time.GetUtcNow() });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            foreach (var take in store.Unfinished())
            {
                if (take.Stage == "queued")
                {
                    // Into the snapshot as well, so the queue shows it before its turn comes.
                    hub.UpdateSpeech(take);
                    _queue.Writer.TryWrite(take.Id);
                }
                else
                {
                    Update(take, t => t with { Stage = "failed", Message = "The server restarted while the take was spoken." });
                }
            }
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not read the queued takes");
        }

        try
        {
            await foreach (var id in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                await ProcessAsync(id, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task ProcessAsync(string id, CancellationToken stoppingToken)
    {
        if (store.Take(id) is not { Finished: false } take)
        {
            return;
        }
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        lock (_gate)
        {
            _running = id;
            _cancel = cancel;
        }
        var work = Path.Combine(Path.GetTempPath(), $"yueui-speech-{id}");
        try
        {
            var cleaning = take.Source is not null;
            var model = (cleaning ? options.Value.ResolvedCleanModels : options.Value.ResolvedModels).FirstOrDefault(m => m.Id == take.ModelId)
                ?? throw new SpeechException($"The model {take.ModelId} is no longer offered.");
            var voice = take.VoiceId is { } voiceId ? store.Voice(voiceId) ?? throw new SpeechException("The recorded voice is gone.") : null;
            var source = store.SourcePath(id);
            if (cleaning && !File.Exists(source))
            {
                throw new SpeechException("The recording is gone.");
            }

            await using var placement = await machines.PlaceAsync(
                LargeModel.Speech, Machines.Engines.Speech, take.Machine, options.Value.WaitInterval, cancel.Token, engine.Installed);
            var speaker = placement.Helper is { } helper ? remote.Speech(helper) : engine;

            take = Update(take, t => t with { Stage = "loading", RanOn = placement.Name, StartedAt = time.GetUtcNow() });
            Directory.CreateDirectory(work);
            var output = Path.Combine(work, "take.wav");
            var working = cleaning ? "cleaning" : "speaking";
            void Staged(string stage) => take = Update(take, t => t with { Stage = stage is "loading" ? "loading" : working });
            // The script reports in steps of a percent at most, so every report may go to the browsers.
            void Progressed(double share) => take = Update(take, t => t with { Stage = working, Progress = Math.Round(share, 3) });
            var result = cleaning
                ? await speaker.CleanAsync(new CleanJob(model, source, output, WavInfo.Seconds(source) ?? 0), Staged, Progressed, cancel.Token)
                : await speaker.SpeakAsync(
                    new SpeechJob(model, take.Text, voice is null ? null : store.VoicePath(voice.Id), voice?.Transcript is { Length: > 0 } words ? words : null, output),
                    Staged,
                    cancel.Token);

            var target = store.TakePath(id);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(output, target, overwrite: true);
            // A spoken take is made by the model; a cleaned recording stays the speaker's own (Art. 50(2) of the AI
            // Act leaves standard editing out).
            if (!cleaning)
            {
                tagged.TryMark(target, model.Label is { Length: > 0 } label ? label : model.Id);
            }
            if (store.Take(id) is null)
            {
                // Deleted while it was spoken.
                File.Delete(target);
            }
            else
            {
                Update(take, t => t with
                {
                    Stage = "done",
                    Seconds = WavInfo.Seconds(target),
                    LoadSeconds = result.LoadSeconds,
                    SpeakSeconds = result.SpeakSeconds,
                    PeakMemoryGb = result.PeakMemoryGb,
                });
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            if (!stoppingToken.IsCancellationRequested)
            {
                Update(take, t => t with { Stage = "cancelled" });
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "The take {Id} with {Model} failed", id, take.ModelId);
            Update(take, t => t with { Stage = "failed", Message = exception.Message });
        }
        finally
        {
            lock (_gate)
            {
                _running = null;
                _cancel = null;
            }
            try
            {
                if (Directory.Exists(work))
                {
                    Directory.Delete(work, recursive: true);
                }
            }
            catch (IOException exception)
            {
                logger.LogWarning(exception, "Could not delete {Directory}", work);
            }
        }
    }

    /// <summary>Stores the change (a take deleted meanwhile stays deleted) and tells the browsers.</summary>
    private SpeechTake Update(SpeechTake take, Func<SpeechTake, SpeechTake> change)
    {
        var updated = change(take) with { UpdatedAt = time.GetUtcNow() };
        try
        {
            if (!store.UpdateTake(updated))
            {
                return updated;
            }
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            logger.LogWarning(exception, "Could not store the take {Id}", take.Id);
        }
        hub.UpdateSpeech(updated);
        return updated;
    }
}
