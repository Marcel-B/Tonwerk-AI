using System.Threading.Channels;
using Microsoft.Extensions.Options;
using YueUI.Api.Data;
using YueUI.Api.Memory;
using YueUI.Api.Push;
using YueUI.Api.Speech;
using YueUI.Api.Worker;

namespace YueUI.Api.Singing;

/// <summary>
/// Sings melodies with words, one take at a time (<see cref="ISingingEngine"/>), and keeps the takes with their notes
/// in the database. The melody comes from a MIDI file or a score, the words from the file's lyric events or a text.
/// </summary>
/// <remarks>
/// SoulX-Singer is small for a model (2.8 GB of weights), but runs on the GPU beside YuE2's 10 to 12 GB, so it follows
/// the rule the speech lab follows: it waits while songs are generated or another large model holds the memory, shuts
/// an idle YuE worker down, and holds the memory itself while it sings (<see cref="ModelMemory"/>); or it sings on another
/// Mac (<see cref="Machines.Machines"/>), which waits for its own memory. Queued takes
/// survive a restart; one that was halfway is marked failed.
/// </remarks>
public sealed class SingingStudio(
    SqliteSingingStore store,
    SingingVoices voices,
    ISingingEngine engine,
    Machines.Machines machines,
    Machines.RemoteEngines remote,
    Status.StatusHub hub,
    PushStore pushStore,
    IPushSender pushSender,
    IOptions<SingingOptions> options,
    TimeProvider time,
    Export.TaggedFiles tagged,
    ILogger<SingingStudio> logger) : BackgroundService
{
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Lock _gate = new();
    private readonly Dictionary<string, SingTake> _takes = [];
    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _running;
    private CancellationTokenSource? _cancel;

    /// <summary>Waits until the stored takes are read, so that a request right after start sees them.</summary>
    public Task Loaded => _loaded.Task;

    /// <summary>Every take, newest first.</summary>
    public IReadOnlyList<SingTake> Takes()
    {
        lock (_gate)
        {
            return [.. _takes.Values.OrderByDescending(t => t.CreatedAt)];
        }
    }

    public SingTake? Get(string id)
    {
        lock (_gate)
        {
            return _takes.GetValueOrDefault(id);
        }
    }

    public SingTake Enqueue(SingTake take, IReadOnlyList<SungNote> notes)
    {
        store.Add(take, notes);
        lock (_gate)
        {
            _takes[take.Id] = take;
        }
        hub.UpdateSinging(take);
        _queue.Writer.TryWrite(take.Id);
        return take;
    }

    /// <summary>Stops the take if it is being sung, and removes it with its audio.</summary>
    public void Delete(SingTake take)
    {
        lock (_gate)
        {
            if (_running == take.Id)
            {
                _cancel?.Cancel();
            }
            _takes.Remove(take.Id);
        }
        try
        {
            store.Remove(take.Id);
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            logger.LogWarning(exception, "Could not remove the take {Id}", take.Id);
        }
        hub.UpdateSinging(take with { Stage = "cancelled", UpdatedAt = time.GetUtcNow() });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        Load();
        try
        {
            await foreach (var id in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                await SingAsync(id, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private void Load()
    {
        try
        {
            foreach (var take in store.All())
            {
                lock (_gate)
                {
                    _takes[take.Id] = take;
                }
                if (take.Stage == "queued")
                {
                    // Into the snapshot as well, so the queue shows it before its turn comes.
                    hub.UpdateSinging(take);
                    _queue.Writer.TryWrite(take.Id);
                }
                else if (!take.Finished)
                {
                    Update(take, t => t with { Stage = "failed", Message = "The server restarted while the melody was sung." });
                }
            }
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not read the stored takes");
        }
        finally
        {
            _loaded.TrySetResult();
        }
    }

    private async Task SingAsync(string id, CancellationToken stoppingToken)
    {
        if (Get(id) is not { Stage: "queued" } take)
        {
            return;
        }
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        lock (_gate)
        {
            _running = id;
            _cancel = cancel;
        }
        var work = Path.Combine(Path.GetTempPath(), $"yueui-singing-{id}");
        try
        {
            var voice = voices.Find(take.VoiceId)
                ?? throw new SingingException($"The voice {take.VoiceId} is no longer offered.");
            var notes = store.Notes(id);

            await using var placement = await machines.PlaceAsync(
                LargeModel.Singing, Machines.Engines.Singing, take.Machine, options.Value.WaitInterval, cancel.Token,
                engine.Installed && engine.ModelDownloaded);
            var singer = placement.Helper is { } helper ? remote.Singing(helper) : engine;

            take = Update(take, t => t with { Stage = "loading", RanOn = placement.Name });
            Directory.CreateDirectory(work);
            var output = Path.Combine(work, "take.wav");
            var result = await singer.SingAsync(
                new SingingJob(notes, take.Lyrics, voice, take.PitchShift, take.AutoShift, output),
                stage => take = Update(take, t => t with { Stage = stage is "loading" ? "loading" : "singing" }),
                share => take = Update(take, t => t with { Stage = "singing", Progress = Math.Round(share, 3) }),
                aligned => take = Update(take, t => t with { Sung = aligned.Sung, Unsung = aligned.Unsung, Leftover = aligned.Leftover }),
                cancel.Token);

            var target = store.AudioPath(id);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(output, target, overwrite: true);
            tagged.TryMark(target, "SoulX-Singer");
            if (Get(id) is null)
            {
                // Deleted while it was sung.
                File.Delete(target);
            }
            else
            {
                take = Update(take, t => t with
                {
                    Stage = "done",
                    Progress = 1,
                    Seconds = WavInfo.Seconds(target),
                    LoadSeconds = result.LoadSeconds,
                    SingSeconds = result.SingSeconds,
                    PeakMemoryGb = result.PeakMemoryGb,
                });
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            if (!stoppingToken.IsCancellationRequested)
            {
                take = Update(take, t => t with { Stage = "cancelled" });
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "The take {Id} failed", id);
            take = Update(take, t => t with { Stage = "failed", Message = exception.Message });
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
        if (take.Stage is "done" or "failed" && Get(id) is not null)
        {
            await NotifyAsync(take);
        }
    }

    /// <summary>Stores the change (a take deleted meanwhile stays deleted) and tells the apps.</summary>
    private SingTake Update(SingTake take, Func<SingTake, SingTake> change)
    {
        var updated = change(take) with { UpdatedAt = time.GetUtcNow() };
        lock (_gate)
        {
            if (!_takes.ContainsKey(take.Id))
            {
                return updated;
            }
            _takes[take.Id] = updated;
        }
        try
        {
            store.Update(updated);
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            logger.LogWarning(exception, "Could not store the take {Id}", take.Id);
        }
        hub.UpdateSinging(updated);
        return updated;
    }

    private async Task NotifyAsync(SingTake take)
    {
        foreach (var subscription in pushStore.Subscriptions)
        {
            try
            {
                var payload = PushNotifier.Payload(PushTexts.Singing(take, subscription.Language), $"singing-{take.Id}");
                if (await pushSender.SendAsync(subscription, payload, CancellationToken.None) == PushResult.Gone)
                {
                    pushStore.Remove(subscription.Endpoint);
                }
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not announce the take {Id}", take.Id);
            }
        }
    }
}
