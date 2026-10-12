using System.Threading.Channels;
using Microsoft.Extensions.Options;
using YueUI.Api.Queue;
using YueUI.Api.Voices;
using YueUI.Api.Worker;

namespace YueUI.Api.Status;

/// <summary>An event for the browsers: <see cref="Type"/> becomes the SSE event name, <see cref="Data"/> its JSON.</summary>
public sealed record ServerEvent(string Type, object Data);

/// <summary>What the worker contributes to a <see cref="StatusSnapshot"/>: its state, the songs in flight, its log and the transcriptions.</summary>
public sealed record WorkerPart(
    WorkerInfo Worker,
    IReadOnlyList<SongState> Songs,
    IReadOnlyList<LogEntry> Log,
    IReadOnlyList<TranscriptionState> Transcriptions);

/// <summary>The worker's side of the snapshot, read by <see cref="StatusHub"/> (<see cref="WorkerHost"/>).</summary>
public interface IWorkerStatus
{
    /// <summary>Looks again whether YuE Studio runs; a scan of the process table, so not under the hub's lock.</summary>
    void CheckStudio();

    /// <summary>The worker's part as of now, with the last known answer about YuE Studio.</summary>
    WorkerPart Part();
}

/// <summary>
/// What every browser sees: the state of everything in the works and every change to it, fanned out to the
/// subscribers (the browsers' event streams, push notifications, automatic versions, streaming copies).
/// </summary>
/// <remarks>
/// The worker keeps its own state (<see cref="WorkerHost"/>) and hands its part over through
/// <see cref="IWorkerStatus"/>; everything else in the works (lyrics drafts, the queue, versions, stems, swaps,
/// videos, covers, takes, the memory's holder) is kept here by whoever does the work, through the <c>Update…</c>
/// methods. A subscriber gets a snapshot and every change after it with nothing lost: changes are made before they are
/// published, and a subscription is registered under the same lock the events go out under, so an event either reaches
/// it or was already in its snapshot. The worker takes its own lock only inside <see cref="IWorkerStatus.Part"/> and
/// never calls in here while holding it, so the two locks are always taken in the same order.
/// </remarks>
public sealed class StatusHub(Func<IWorkerStatus> worker, IOptions<QueueOptions> queueOptions)
{
    private const int SubscriberCapacity = 1000;

    private readonly Lock _gate = new();
    private readonly List<Channel<ServerEvent>> _subscribers = [];
    private readonly Dictionary<string, VersionState> _versions = [];
    private readonly Dictionary<string, StemSetState> _stems = [];
    private readonly Dictionary<string, SwapState> _swaps = [];
    private readonly Dictionary<string, Video.VideoState> _videos = [];
    private readonly Dictionary<string, Images.ImageState> _images = [];
    private readonly Dictionary<string, Ace.AceJob> _ace = [];
    /// <summary>The speech lab's takes in the works, in the order they are spoken (a list, since several share a time).</summary>
    private readonly List<Speech.SpeechTake> _speech = [];
    private LyricsState? _lyrics;
    private IReadOnlyList<QueuedJob> _queue = [];
    private MemoryInfo _memory = new(null);

    public StatusSnapshot Snapshot()
    {
        var source = worker();
        source.CheckStudio();
        lock (_gate)
        {
            return SnapshotLocked(source.Part());
        }
    }

    /// <summary>The jobs waiting in <see cref="JobQueue"/>, without a whole snapshot (<c>/metrics</c>).</summary>
    public IReadOnlyList<QueuedJob> Queue
    {
        get
        {
            lock (_gate)
            {
                return _queue;
            }
        }
    }

    /// <summary>A snapshot and every change after it, with nothing lost in between. Dispose to unsubscribe.</summary>
    /// <param name="checkStudio">
    /// False for subscribers that do not show whether YuE Studio runs (push notifications): the snapshot then carries
    /// the last known answer instead of scanning the process table, whose result would be kept for the next browser.
    /// </param>
    public Subscription Subscribe(bool checkStudio = true)
    {
        var channel = Channel.CreateBounded<ServerEvent>(new BoundedChannelOptions(SubscriberCapacity) { SingleReader = true });
        var source = worker();
        if (checkStudio)
        {
            source.CheckStudio();
        }
        lock (_gate)
        {
            _subscribers.Add(channel);
            return new Subscription(SnapshotLocked(source.Part()), channel.Reader, () => Unsubscribe(channel));
        }
    }

    /// <summary>Sends an event that changes nothing kept here: the worker's own, whose state the worker keeps.</summary>
    public void Publish(string type, object data)
    {
        lock (_gate)
        {
            PublishLocked(type, data);
        }
    }

    /// <summary>Tells the browsers that songs were written, changed or deleted, so that every open library reloads.</summary>
    public void LibraryChanged() => Publish("library", new { });

    /// <summary>Keeps the lyrics draft for the snapshot of browsers that connect later, and sends it to the others.</summary>
    public void UpdateLyrics(LyricsState lyrics)
    {
        lock (_gate)
        {
            _lyrics = lyrics;
            PublishLocked("lyrics", lyrics);
        }
    }

    /// <summary>Keeps the jobs waiting in <see cref="JobQueue"/> for the snapshot and sends them to the browsers.</summary>
    public void UpdateQueue(IReadOnlyList<QueuedJob> queue)
    {
        lock (_gate)
        {
            _queue = queue;
            PublishLocked("queue", queue);
        }
    }

    /// <summary>Keeps which large model holds the memory (<see cref="Memory.ModelMemory"/>) for the snapshot and sends it to the browsers.</summary>
    public void UpdateMemory(Memory.LargeModel? holder)
    {
        var memory = new MemoryInfo(holder);
        lock (_gate)
        {
            _memory = memory;
            PublishLocked("memory", memory);
        }
    }

    /// <summary>
    /// Keeps a song's version in the works (Voices/VoiceConverter.cs) for the snapshot and sends it to the browsers; a
    /// finished one leaves the snapshot with the next, since the library lists it from then on.
    /// </summary>
    public void UpdateVersion(VersionState version) => Keep(_versions, version.Id, version, v => v.Finished, "version");

    /// <summary>
    /// Keeps a song's stems in the works (Voices/VoiceConverter.cs) for the snapshot and sends them to the browsers; a
    /// finished set leaves the snapshot with the next, since the voices page lists it from then on.
    /// </summary>
    public void UpdateStems(StemSetState set) => Keep(_stems, set.Id, set, s => s.Finished, "stems");

    /// <summary>
    /// Keeps an uploaded recording being sung with another voice (Voices/VoiceConverter.cs) for the snapshot and sends it
    /// to the browsers; a finished one leaves the snapshot with the next, since the voices page lists it from then on.
    /// </summary>
    public void UpdateSwap(SwapState swap) => Keep(_swaps, swap.Id, swap, s => s.Finished, "swap");

    /// <summary>
    /// Keeps a music video (Video/VideoMaker.cs) for the snapshot, so the queue page shows it after a reload, and sends
    /// it to the browsers; a finished one leaves the snapshot with the next, since the song's video dialog lists it.
    /// </summary>
    public void UpdateVideo(Video.VideoState video) => Keep(_videos, video.Id, video, v => v.Finished, "video");

    /// <summary>
    /// Keeps a cover picture in the works (Images/ImageMaker.cs) for the snapshot, so the queue page shows what holds or
    /// waits for the memory after a reload, and sends it to the browsers; a finished one leaves the snapshot with the
    /// next, since the song's cover dialog lists it.
    /// </summary>
    public void UpdateImage(Images.ImageState image) => Keep(_images, image.Id, image, i => i.Finished, "image");

    /// <summary>
    /// Keeps an ACE-Step job (Ace/AceComposer.cs) for the snapshot, so the queue page shows what holds or waits for the
    /// memory after a reload, and sends it to the browsers; a finished one leaves the snapshot with the next, since the
    /// ACE-Step page lists it from then on.
    /// </summary>
    public void UpdateAce(Ace.AceJob job) => Keep(_ace, job.Id, job, j => j.Finished, "ace");

    /// <summary>
    /// Keeps a take of the speech lab (Speech/SpeechLab.cs) for the snapshot, so the queue shows what holds or waits for
    /// the memory after a reload, and sends it to the browsers; a finished take leaves the snapshot with the next change,
    /// since the lab page lists it from then on.
    /// </summary>
    public void UpdateSpeech(Speech.SpeechTake take)
    {
        lock (_gate)
        {
            _speech.RemoveAll(other => other.Finished && other.Id != take.Id);
            var index = _speech.FindIndex(other => other.Id == take.Id);
            if (index >= 0)
            {
                _speech[index] = take;
            }
            else
            {
                _speech.Add(take);
            }
            PublishLocked("speech", take);
        }
    }

    /// <summary>Keeps <paramref name="item"/> and drops the finished ones before it, then sends it.</summary>
    private void Keep<T>(Dictionary<string, T> items, string id, T item, Func<T, bool> finished, string type)
        where T : notnull
    {
        lock (_gate)
        {
            foreach (var old in items.Where(pair => finished(pair.Value)).Select(pair => pair.Key).ToList())
            {
                items.Remove(old);
            }
            items[id] = item;
            PublishLocked(type, item);
        }
    }

    private void PublishLocked(string type, object data)
    {
        var item = new ServerEvent(type, data);
        foreach (var subscriber in _subscribers)
        {
            // A browser that stopped reading is dropped; its EventSource reconnects and starts from a fresh snapshot.
            if (!subscriber.Writer.TryWrite(item))
            {
                subscriber.Writer.TryComplete();
            }
        }
    }

    private void Unsubscribe(Channel<ServerEvent> channel)
    {
        lock (_gate)
        {
            _subscribers.Remove(channel);
        }
        channel.Writer.TryComplete();
    }

    private StatusSnapshot SnapshotLocked(WorkerPart part) => new(
        part.Worker,
        part.Songs,
        part.Log,
        part.Transcriptions,
        _lyrics,
        [.. _versions.Values.OrderBy(v => v.CreatedAt)],
        _queue,
        queueOptions.Value.BundleWindow.TotalSeconds,
        [.. _stems.Values.OrderBy(s => s.CreatedAt)],
        [.. _speech],
        [.. _swaps.Values.OrderBy(s => s.CreatedAt)],
        [.. _videos.Values.OrderBy(v => v.CreatedAt)],
        [.. _images.Values.OrderBy(i => i.CreatedAt)],
        _memory,
        [.. _ace.Values.OrderBy(j => j.CreatedAt)]);

    public sealed class Subscription(StatusSnapshot snapshot, ChannelReader<ServerEvent> reader, Action unsubscribe) : IDisposable
    {
        public StatusSnapshot Snapshot { get; } = snapshot;

        public ChannelReader<ServerEvent> Reader { get; } = reader;

        public void Dispose() => unsubscribe();
    }
}
