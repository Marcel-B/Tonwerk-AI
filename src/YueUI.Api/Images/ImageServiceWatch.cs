using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using YueUI.Api.Memory;

namespace YueUI.Api.Images;

/// <summary>
/// Watches what img-o-mat paints (<c>GET /v1/busy</c>) and tells <see cref="ModelMemory"/> while another app's picture
/// holds this Mac's memory: pictures that come through the LLM gate never pass Tonwerk, which would otherwise load
/// YuE2 or a lyrics model beside them. Tonwerk's own pictures (<see cref="ImgOMatEngine.AppName"/>) are left out,
/// since <see cref="ImageMaker"/> claims the memory for those itself. Runs only with <see cref="ImageOptions.Service"/>.
/// </summary>
public sealed class ImageServiceWatch(
    IHttpClientFactory clients,
    IOptions<ImageOptions> options,
    ModelMemory memory,
    TimeProvider time,
    ILogger<ImageServiceWatch> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(options.Value.Service))
        {
            return;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            await PollAsync(stoppingToken);
            try
            {
                await Task.Delay(options.Value.BusyInterval, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        memory.SetOutside(null);
    }

    /// <summary>One look; public for the tests.</summary>
    public async Task PollAsync(CancellationToken cancellationToken)
    {
        string? outside = null;
        try
        {
            using var client = clients.CreateClient(ImgOMatEngine.HttpClientName);
            client.Timeout = TimeSpan.FromSeconds(5);
            var busy = JsonNode.Parse(await client.GetStringAsync($"{options.Value.Service!.TrimEnd('/')}/v1/busy", cancellationToken));
            if ((bool?)busy?["busy"] == true && (string?)busy["app"] is { } app && app != ImgOMatEngine.AppName)
            {
                outside = $"{app} ({(string?)busy["model"]})";
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException
            && !cancellationToken.IsCancellationRequested)
        {
            // img-o-mat away: it paints nothing then. Its engine says so where it matters.
            logger.LogDebug("img-o-mat busy check failed: {Message}", exception.Message);
        }
        memory.SetOutside(outside);
    }
}
