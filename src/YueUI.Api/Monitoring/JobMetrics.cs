using YueUI.Api.Voices;
using YueUI.Api.Worker;

namespace YueUI.Api.Monitoring;

/// <summary>
/// Listens to the status like a browser does and counts what finishes, by kind and outcome, with how long it worked;
/// keeps what is waiting or running for <c>/metrics</c>.
/// </summary>
/// <remarks>
/// Kinds: song, render, transcription, lyrics, version, stems, swap, video, image, ace, speech. A job's working time
/// runs from the first state that is no longer queued to the finished one, so the time spent waiting for the memory is
/// left out; a song takes its stage times, which the worker reports itself. Only the change to finished counts, as for
/// push notifications: a fresh snapshot after a dropped subscription marks what is already done.
/// </remarks>
public sealed class JobMetrics(Status.StatusHub hub) : BackgroundService
{
    /// <summary>Seconds; a draft takes a minute, a full-quality song or a slow separation an hour and more.</summary>
    public static readonly double[] Buckets = [5, 15, 30, 60, 120, 300, 600, 1200, 1800, 3600, 7200];

    /// <summary>Finished ids are kept to tell a repeated event from a new finish; beyond this the oldest go.</summary>
    private const int MaxRemembered = 5000;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Job> _jobs = [];
    private readonly Queue<string> _finishedOrder = new();
    private readonly Dictionary<(string Kind, string Outcome), long> _finished = [];
    private readonly Dictionary<string, Histogram> _durations = [];

    public static readonly IReadOnlyList<string> Kinds =
        ["song", "render", "transcription", "lyrics", "version", "stems", "swap", "video", "image", "ace", "speech"];

    public static readonly IReadOnlyList<string> Outcomes = ["done", "failed", "cancelled"];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var subscription = hub.Subscribe(checkStudio: false);
            Remember(subscription.Snapshot);
            try
            {
                await foreach (var item in subscription.Reader.ReadAllAsync(stoppingToken))
                {
                    if (Describe(item.Data) is { } state)
                    {
                        Observe(state);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>Jobs per kind that wait (in their own model's queue or the worker's) or run right now.</summary>
    public IReadOnlyDictionary<(string Kind, string State), int> Live()
    {
        lock (_gate)
        {
            return _jobs.Values
                .Where(job => !job.Finished)
                .GroupBy(job => (job.Kind, job.Started is null ? "waiting" : "running"))
                .ToDictionary(group => group.Key, group => group.Count());
        }
    }

    public IReadOnlyDictionary<(string Kind, string Outcome), long> Finished()
    {
        lock (_gate)
        {
            return new Dictionary<(string, string), long>(_finished);
        }
    }

    public IReadOnlyDictionary<string, Histogram> Durations()
    {
        lock (_gate)
        {
            return _durations.ToDictionary(pair => pair.Key, pair => pair.Value.Copy());
        }
    }

    private void Remember(StatusSnapshot snapshot)
    {
        IEnumerable<object> all =
        [
            .. snapshot.Songs,
            .. snapshot.Transcriptions ?? [],
            .. snapshot.Versions ?? [],
            .. snapshot.Stems ?? [],
            .. snapshot.Swaps ?? [],
            .. snapshot.Videos ?? [],
            .. snapshot.Images ?? [],
            .. snapshot.Ace ?? [],
            .. snapshot.Speech ?? [],
        ];
        if (snapshot.Lyrics is { } lyrics)
        {
            all = all.Append(lyrics);
        }
        lock (_gate)
        {
            foreach (var state in all.Select(Describe).OfType<State>())
            {
                // What finished before this subscription was counted then, or before the server started.
                _jobs[state.Key] = new Job(state.Kind, state.Finished, state.Working ? state.StartedAt ?? state.UpdatedAt : null);
                if (state.Finished)
                {
                    Forget(state.Key);
                }
            }
        }
    }

    private void Observe(State state)
    {
        lock (_gate)
        {
            _jobs.TryGetValue(state.Key, out var job);
            if (job is { Finished: true } && state.Finished)
            {
                return;
            }
            // A render reuses its song's id: a finished job that runs again starts over.
            var started = job is { Finished: false } ? job.Started : null;
            if (state.Working || state.Finished)
            {
                started ??= state.StartedAt ?? (state.Finished ? null : state.UpdatedAt);
            }
            _jobs[state.Key] = new Job(state.Kind, state.Finished, started);
            if (!state.Finished)
            {
                return;
            }
            Forget(state.Key);
            var key = (state.Kind, state.Outcome);
            _finished[key] = _finished.GetValueOrDefault(key) + 1;
            if (state.Outcome == "done" && started is { } from && state.UpdatedAt >= from)
            {
                if (!_durations.TryGetValue(state.Kind, out var histogram))
                {
                    _durations[state.Kind] = histogram = new Histogram(Buckets);
                }
                histogram.Observe((state.UpdatedAt - from).TotalSeconds);
            }
        }
    }

    private void Forget(string key)
    {
        _finishedOrder.Enqueue(key);
        while (_finishedOrder.Count > MaxRemembered)
        {
            var old = _finishedOrder.Dequeue();
            if (_jobs.TryGetValue(old, out var job) && job.Finished)
            {
                _jobs.Remove(old);
            }
        }
    }

    /// <summary>The parts of each kind's state that count, or null for an event that is no job.</summary>
    private static State? Describe(object data) => data switch
    {
        SongState song => new State(
            $"song:{song.Id}",
            song.Render ? "render" : "song",
            song.Stage,
            song.Stage != "queued",
            song.Finished,
            song.UpdatedAt,
            song.Stages.FirstOrDefault(stage => stage.Stage != "queued")?.StartedAt),
        TranscriptionState t => new State($"transcription:{t.Id}", "transcription", t.Stage, true, t.Finished, t.UpdatedAt),
        LyricsState l => new State($"lyrics:{l.Id}", "lyrics", l.Stage, l.Stage != "queued", l.Finished, l.UpdatedAt),
        VersionState v => Queued($"version:{v.Id}", "version", v.Stage, v.Finished, v.UpdatedAt),
        StemSetState s => Queued($"stems:{s.Id}", "stems", s.Stage, s.Finished, s.UpdatedAt),
        SwapState s => Queued($"swap:{s.Id}", "swap", s.Stage, s.Finished, s.UpdatedAt),
        Video.VideoState v => Queued($"video:{v.Id}", "video", v.Stage, v.Finished, v.UpdatedAt),
        Images.ImageState i => Queued($"image:{i.Id}", "image", i.Stage, i.Finished, i.UpdatedAt),
        Ace.AceJob a => Queued($"ace:{a.Id}", "ace", a.Stage, a.Finished, a.UpdatedAt),
        Speech.SpeechTake s => Queued($"speech:{s.Id}", "speech", s.Stage, s.Finished, s.UpdatedAt),
        _ => null,
    };

    private static State Queued(string key, string kind, string stage, bool finished, DateTimeOffset updatedAt) =>
        new(key, kind, stage, stage != "queued", finished, updatedAt);

    /// <param name="Working">Past waiting: running, or finished after running.</param>
    /// <param name="StartedAt">When it began working, where the state says so itself.</param>
    private sealed record State(
        string Key,
        string Kind,
        string Stage,
        bool Working,
        bool Finished,
        DateTimeOffset UpdatedAt,
        DateTimeOffset? StartedAt = null)
    {
        public string Outcome => Stage switch
        {
            "failed" => "failed",
            "cancelled" => "cancelled",
            _ => "done",
        };
    }

    private sealed record Job(string Kind, bool Finished, DateTimeOffset? Started);
}

/// <summary>A Prometheus histogram: cumulative counts per upper bound, plus sum and count.</summary>
public sealed class Histogram(double[] bounds)
{
    private readonly long[] _counts = new long[bounds.Length];

    public IReadOnlyList<double> Bounds => bounds;

    /// <summary>Observations at or below each bound, cumulative as Prometheus expects.</summary>
    public IReadOnlyList<long> Counts => _counts;

    public double Sum { get; private set; }

    public long Count { get; private set; }

    public void Observe(double value)
    {
        for (var i = 0; i < bounds.Length; i++)
        {
            if (value <= bounds[i])
            {
                _counts[i]++;
            }
        }
        Sum += value;
        Count++;
    }

    public Histogram Copy()
    {
        var copy = new Histogram(bounds) { Sum = Sum, Count = Count };
        _counts.CopyTo(copy._counts, 0);
        return copy;
    }
}
