using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using YueUI.Api.Logs;

namespace YueUI.Api.Monitoring;

/// <summary>
/// Every line the <see cref="LogStore"/> writes passes here: counted for <c>/metrics</c>, and handed to the
/// <see cref="LokiShipper"/> when Loki is configured.
/// </summary>
/// <remarks>
/// A class of its own without dependencies, since the shipper logs through the same store it reads from: the store
/// taking the shipper would make the logger need itself. Lines wait in a bounded channel that drops the oldest, so a
/// Loki that is down never holds up what logs or fills the memory.
/// </remarks>
public sealed class LogFeed
{
    private readonly ConcurrentDictionary<(string Source, string Level), long> _counts = new();
    private readonly Channel<LogLine>? _lines;
    private readonly int _minRank;

    public LogFeed(IOptions<MonitoringOptions> options)
    {
        var loki = options.Value.Loki;
        if (loki.Enabled)
        {
            _lines = Channel.CreateBounded<LogLine>(new BoundedChannelOptions(Math.Max(100, loki.Capacity))
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });
        }
        _minRank = LogLevels.Rank(loki.MinLevel);
    }

    /// <summary>The lines waiting for Loki; null while it is not configured.</summary>
    public ChannelReader<LogLine>? Lines => _lines?.Reader;

    public IReadOnlyDictionary<(string Source, string Level), long> Counts => _counts;

    /// <summary>Whether shipping to Loki is configured at all; a missing address otherwise switches it off silently.</summary>
    public bool Shipping => _lines is not null;

    /// <summary>When Loki last took a batch; null before the first one.</summary>
    public DateTimeOffset? LastSent { get; private set; }

    /// <summary>Since when Loki has refused or not answered; null while it takes the log.</summary>
    public DateTimeOffset? FailingSince { get; private set; }

    /// <summary>Set by the <see cref="LokiShipper"/>, read by the log report.</summary>
    public void Sent(DateTimeOffset time)
    {
        LastSent = time;
        FailingSince = null;
    }

    public void Failed(DateTimeOffset time) => FailingSince ??= time;

    public void Add(LogLine line)
    {
        _counts.AddOrUpdate((line.Source, line.Level), 1, (_, count) => count + 1);
        if (_lines is not null && LogLevels.Rank(line.Level) >= _minRank)
        {
            _lines.Writer.TryWrite(line);
        }
    }
}
