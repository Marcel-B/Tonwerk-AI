using System.Globalization;
using Microsoft.Extensions.Options;
using YueUI.Api.Logs;

namespace YueUI.Api.Monitoring;

/// <summary>
/// Hands <c>deploy/update.sh</c>'s log to Loki as the source <c>update</c>, alongside the server's own lines; does
/// nothing while Loki is not configured.
/// </summary>
/// <remarks>
/// The script writes its own file, which the <see cref="LogStore"/> only reads when the log page asks. So the file is
/// read here every <see cref="LokiOptions.UpdateLogInterval"/>, and what is newer than the last line sent goes into the
/// <see cref="LogFeed"/> (not the store, which would copy it into the daily files). The time of the last line sent is
/// kept in <c>update-shipped</c> beside the daily files, since a deploy restarts the server right after the script
/// logged it. The newest entry waits until the file has been still for <see cref="LokiOptions.UpdateLogSettle"/>, as
/// setup-mac.sh's output keeps growing it line by line.
/// </remarks>
public sealed class UpdateLogShipper(
    LogStore store,
    LogFeed feed,
    IOptions<LogOptions> logOptions,
    IOptions<MonitoringOptions> options,
    TimeProvider time,
    ILogger<UpdateLogShipper> logger) : BackgroundService
{
    /// <summary>Without a mark (the first start) the last day goes out, so a deploy just before is not missed.</summary>
    private static readonly TimeSpan FirstLookBack = TimeSpan.FromDays(1);

    private LokiOptions Loki => options.Value.Loki;

    private string MarkPath => Path.Combine(logOptions.Value.ResolvedDirectory, "update-shipped");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!feed.Shipping)
        {
            return;
        }
        var sent = ReadMark() ?? time.GetUtcNow() - FirstLookBack;
        using var timer = new PeriodicTimer(Loki.UpdateLogInterval, time);
        try
        {
            do
            {
                sent = Ship(sent);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Feeds the entries newer than <paramref name="sent"/> and answers the time of the last one fed.</summary>
    public DateTimeOffset Ship(DateTimeOffset sent)
    {
        var lines = store.ReadUpdateLog();
        if (lines.Count == 0)
        {
            return sent;
        }
        var path = logOptions.Value.ResolvedUpdateLog;
        var settled = time.GetUtcNow() - File.GetLastWriteTimeUtc(path) >= Loki.UpdateLogSettle;
        var last = sent;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Time <= sent)
            {
                continue;
            }
            if (i == lines.Count - 1 && !settled)
            {
                break;
            }
            feed.Add(line);
            last = line.Time;
        }
        if (last != sent)
        {
            WriteMark(last);
        }
        return last;
    }

    private DateTimeOffset? ReadMark()
    {
        try
        {
            return File.Exists(MarkPath)
                && long.TryParse(File.ReadAllText(MarkPath).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds)
                ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
                : null;
        }
        catch (Exception caught) when (caught is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void WriteMark(DateTimeOffset at)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MarkPath)!);
            File.WriteAllText(MarkPath, at.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception caught) when (caught is IOException or UnauthorizedAccessException)
        {
            // Only costs sending some lines again after a restart; Loki drops exact repeats.
            logger.LogDebug(caught, "Could not keep the update log's mark");
        }
    }
}
