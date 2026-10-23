using YueUI.Api.Monitoring;
using YueUI.Api.Worker;

namespace YueUI.Api.Status;

/// <summary>
/// Since when Tonwerk has had nothing to do (<c>GET /api/idle</c>), for a guard on the Mac that pauses other models'
/// work (a document or mail assistant) while Tonwerk needs the memory and lets it go on once Tonwerk was quiet a while.
/// </summary>
/// <remarks>
/// It listens to the status like a browser and takes every event for activity but the library's and the log's (a
/// rating or a worker's idle chatter is no work): a job that starts and ends between two of the guard's polls still
/// moves the clock. The endpoint marks it too while something runs or waits, since a model held for a while (a chat
/// kept loaded) sends nothing meanwhile. After a start the clock counts from then, so a deploy never reads as long quiet.
/// </remarks>
public sealed class IdleClock(StatusHub hub, TimeProvider time) : BackgroundService
{
    private readonly Lock _gate = new();
    private DateTimeOffset _lastActive = time.GetUtcNow();

    public DateTimeOffset LastActive
    {
        get
        {
            lock (_gate)
            {
                return _lastActive;
            }
        }
    }

    public void Mark()
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (now > _lastActive)
            {
                _lastActive = now;
            }
        }
    }

    /// <summary>What runs, what waits and since when nothing has.</summary>
    public IdleInfo Read(BusyInfo busy, JobMetrics metrics)
    {
        var waiting = metrics.Live()
            .Where(pair => pair.Key.State == "waiting" && pair.Value > 0)
            .Select(pair => pair.Key.Kind)
            .Concat(hub.Queue.Select(job => job.Kind switch
            {
                Queue.JobKind.Render => "render",
                Queue.JobKind.Lyrics => "lyrics",
                Queue.JobKind.Transcription => "transcription",
                _ => "song",
            }))
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();
        var running = metrics.Live()
            .Where(pair => pair.Key.State == "running" && pair.Value > 0)
            .Select(pair => pair.Key.Kind);
        if (busy.Busy || waiting.Count > 0 || running.Any())
        {
            Mark();
            return new IdleInfo(false, 0, null, busy.Reasons, waiting);
        }
        var since = LastActive;
        var seconds = Math.Max(0, (time.GetUtcNow() - since).TotalSeconds);
        return new IdleInfo(true, Math.Round(seconds), since, [], []);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var subscription = hub.Subscribe(checkStudio: false);
            try
            {
                await foreach (var item in subscription.Reader.ReadAllAsync(stoppingToken))
                {
                    if (item.Type is not ("library" or "log"))
                    {
                        Mark();
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
