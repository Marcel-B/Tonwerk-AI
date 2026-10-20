using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Options;
using YueUI.Api.Logs;

namespace YueUI.Api.Monitoring;

/// <summary>
/// Sends the collected log to Loki (<c>POST /loki/api/v1/push</c>) in batches, labelled <c>app</c>, <c>host</c>,
/// <c>source</c> and <c>level</c>; does nothing while <see cref="LokiOptions.Url"/> is empty.
/// </summary>
/// <remarks>
/// The labels are the few values a query filters by (Loki wants few distinct label sets); the song, the stage and
/// everything else stay in the line. A batch Loki refuses is tried again with growing pauses while new lines wait in
/// the <see cref="LogFeed"/>; one that keeps failing is dropped, since the lines are still in the daily files on the
/// Mac. Its own trouble is logged only when the state changes, or every failed batch would log another line to send.
/// </remarks>
public sealed class LokiShipper(
    LogFeed feed,
    IOptions<MonitoringOptions> options,
    IHttpClientFactory clients,
    TimeProvider time,
    ILogger<LokiShipper> logger) : BackgroundService
{
    public const string HttpClientName = "loki";

    /// <summary>Loki's default limit for a push is 4 MB; a thousand lines stay well below it.</summary>
    private const int MaxBatch = 1000;

    private static readonly TimeSpan[] Retries =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(60)];

    private bool _failing;

    private LokiOptions Loki => options.Value.Loki;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Said once at every start, since a settings file that lost its address switches shipping off without a
        // trace (2026-10-15 to 10-20 went unnoticed that way).
        if (feed.Lines is not { } lines)
        {
            logger.LogInformation("Loki is off: Monitoring:Loki:Url is not set, the log stays on this Mac");
            return;
        }
        var host = string.IsNullOrWhiteSpace(Loki.Host) ? Environment.MachineName : Loki.Host;
        logger.LogInformation("Sending the log to Loki as host {Host}", host);
        try
        {
            while (await lines.WaitToReadAsync(stoppingToken))
            {
                // Lines logged together go out together.
                await Task.Delay(Loki.BatchDelay, stoppingToken);
                List<LogLine> batch = [];
                while (batch.Count < MaxBatch && lines.TryRead(out var line))
                {
                    batch.Add(line);
                }
                await SendAsync(Body(batch, host), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Loki's push format: one stream per label set, each with its lines as [nanoseconds, text], oldest first.</summary>
    public static object Body(IEnumerable<LogLine> lines, string host) => new
    {
        streams = lines
            .GroupBy(line => (line.Source, line.Level))
            .Select(group => new
            {
                stream = new Dictionary<string, string>
                {
                    ["app"] = "tonwerk",
                    ["host"] = host,
                    ["source"] = group.Key.Source,
                    ["level"] = group.Key.Level,
                },
                values = group
                    .OrderBy(line => line.Time)
                    .Select(line => new[] { Nanoseconds(line.Time), Text(line) })
                    .ToList(),
            })
            .ToList(),
    };

    private static string Nanoseconds(DateTimeOffset time) =>
        ((time.ToUnixTimeMilliseconds() * 1_000_000) + (time.UtcTicks % TimeSpan.TicksPerMillisecond * 100)).ToString(CultureInfo.InvariantCulture);

    private static string Text(LogLine line) => line.Exception is null ? line.Message : line.Message + "\n" + line.Exception;

    private async Task SendAsync(object body, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            string? problem;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(Loki.Url!.TrimEnd('/') + "/"), "loki/api/v1/push"))
                {
                    Content = JsonContent.Create(body),
                };
                if (!string.IsNullOrEmpty(Loki.Username))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue(
                        "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Loki.Username}:{Loki.Password}")));
                }
                using var response = await clients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    feed.Sent(time.GetUtcNow());
                    if (_failing)
                    {
                        _failing = false;
                        logger.LogInformation("Loki takes the log again");
                    }
                    return;
                }
                var text = await response.Content.ReadAsStringAsync(cancellationToken);
                problem = $"{(int)response.StatusCode} {text.Trim()}";
                // 400 is a line Loki will never take (too old, too long); trying again changes nothing.
                if ((int)response.StatusCode is >= 400 and < 500 and not 408 and not 429)
                {
                    Failed(problem);
                    return;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                problem = exception.Message;
            }
            if (attempt >= Retries.Length)
            {
                Failed(problem);
                return;
            }
            await Task.Delay(Retries[attempt], cancellationToken);
        }
    }

    private void Failed(string problem)
    {
        feed.Failed(time.GetUtcNow());
        if (!_failing)
        {
            _failing = true;
            logger.LogWarning("Could not send the log to Loki, lines are dropped until it takes them again: {Problem}", problem);
        }
    }
}
