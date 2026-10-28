using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using YueUI.Api.Sketches;
using YueUI.Api.Writing;

namespace YueUI.Api.Lyrics;

/// <summary>
/// The LLM gate on the Mac (scripts repository, <c>bin/llm-gate</c>, <c>docs/llm-gate.md</c>): a shared queue in
/// front of LM Studio for the Mac's other apps. Tonwerk does not go through it, its own memory rules come first, but
/// tells it which LM Studio models are Tonwerk's, so the gate shows them, can install them and never unloads them for
/// another app. Configured under <c>LlmGate</c>; an empty <see cref="Url"/> leaves the gate alone.
/// </summary>
public sealed class LlmGateOptions
{
    public const string Section = "LlmGate";

    /// <summary>The gate without <c>/gate</c>; it listens on port 1235 beside LM Studio's 1234.</summary>
    public string? Url { get; set; } = "http://127.0.0.1:1235";

    /// <summary>The name Tonwerk is listed under; the gate keeps its settings by it.</summary>
    public string App { get; set; } = "tonwerk-ai";

    /// <summary>
    /// Models to list beside the ones the settings name, with where <c>lms get</c> finds them, so the gate can
    /// install them on a new Mac (for example the writing models of <see cref="LyricsOptions.Roles"/>).
    /// </summary>
    public LlmGateModel[] Models { get; set; } = [];

    /// <summary>How often the listing is sent again, so models downloaded since are listed too.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>The first wait after the gate did not answer; it doubles up to <see cref="Interval"/>.</summary>
    public TimeSpan RetryAfter { get; set; } = TimeSpan.FromMinutes(1);
}

/// <param name="Model">LM Studio's key, as a draft names it.</param>
/// <param name="Source">What <c>lms get</c> downloads it from (a hub name or a Hugging Face repository); null is the key.</param>
/// <param name="Context">The context Tonwerk loads it with.</param>
/// <param name="RamGb">Its memory when loaded.</param>
/// <param name="SizeGb">Its download size, which the gate checks against the free space.</param>
public sealed record LlmGateModel(string Model, string? Source = null, int? Context = null, double? RamGb = null, double? SizeGb = null);

/// <summary>What <see cref="LlmGateRegistration"/> sends to <c>PUT /gate/apps/{app}</c>.</summary>
public sealed record LlmGateApp(bool Direct, int Priority, string Note, IReadOnlyList<LlmGateModel> Models);

/// <summary>
/// Lists Tonwerk's LM Studio models at the gate at start and every <see cref="LlmGateOptions.Interval"/>, as a
/// direct app (<c>direct: true</c>): the default lyrics model, the writing page's, every downloaded model matching
/// <see cref="LyricsOptions.Roles"/> and <see cref="LlmGateOptions.Models"/>, each with the largest context Tonwerk
/// loads it with.
/// </summary>
/// <remarks>
/// The gate may not run at all (it is optional, and Tonwerk starts first after a reboot), so a gate that does not
/// answer is tried again with growing pauses and logged only once. LM Studio is asked for its list only if it already
/// answers; it is not started for this.
/// </remarks>
public sealed class LlmGateRegistration(
    IHttpClientFactory clients,
    LmStudio lmStudio,
    IOptions<LlmGateOptions> options,
    IOptions<LyricsOptions> lyrics,
    IOptions<WritingOptions> writing,
    IOptions<SketchOptions> sketches,
    ILogger<LlmGateRegistration> logger) : BackgroundService
{
    public const string HttpClientName = "llm-gate";

    /// <summary>Whether the last listing reached the gate; for the tests.</summary>
    public bool Registered { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.Url))
        {
            return;
        }
        var retry = settings.RetryAfter;
        var failing = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan wait;
            try
            {
                await RegisterAsync(settings, stoppingToken);
                if (failing || !Registered)
                {
                    logger.LogInformation("Listed Tonwerk's LM Studio models at the LLM gate {Url}", settings.Url);
                }
                Registered = true;
                failing = false;
                retry = settings.RetryAfter;
                wait = settings.Interval;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !stoppingToken.IsCancellationRequested)
            {
                if (!failing)
                {
                    logger.LogInformation("The LLM gate {Url} does not answer ({Message}); trying again later", settings.Url, exception.Message);
                }
                failing = true;
                wait = retry;
                retry = TimeSpan.FromTicks(Math.Min(retry.Ticks * 2, settings.Interval.Ticks));
            }
            try
            {
                await Task.Delay(wait, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RegisterAsync(LlmGateOptions settings, CancellationToken cancellationToken)
    {
        var app = new LlmGateApp(true, 0, "Tonwerk-AI: Songtexte, Schreiben, Ausarbeitung; lädt und entlädt selbst, hat Vorrang",
            await ModelsAsync(cancellationToken));
        using var http = clients.CreateClient(HttpClientName);
        http.Timeout = TimeSpan.FromSeconds(15);
        using var response = await http.PutAsJsonAsync(
            $"{settings.Url!.TrimEnd('/')}/gate/apps/{Uri.EscapeDataString(settings.App)}", app, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Tonwerk's local models with the largest context it loads each with.</summary>
    public async Task<IReadOnlyList<LlmGateModel>> ModelsAsync(CancellationToken cancellationToken)
    {
        var lyricsSettings = lyrics.Value;
        var writingContext = Math.Max(writing.Value.ContextLength, sketches.Value.LocalContextLength);
        var models = new List<LlmGateModel> { new(lyricsSettings.Model, Context: Math.Max(lyricsSettings.ContextLength, writingContext)) };
        if (!string.IsNullOrWhiteSpace(writing.Value.Model))
        {
            models.Add(new(writing.Value.Model, Context: writingContext));
        }
        List<LyricsModel>? installed = null;
        try
        {
            using var http = lmStudio.CreateClient();
            installed = await LmStudio.NativeModelsAsync(http, cancellationToken);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // LM Studio busy or off: the configured models are listed all the same.
        }
        foreach (var model in installed ?? [])
        {
            if (lyricsSettings.RoleOf(model.Id) is not null && lyricsSettings.MissingMemoryFor(model.Id) is null)
            {
                models.Add(new(model.Id, Context: Math.Max(lyricsSettings.ContextLength, writingContext),
                    SizeGb: model.SizeBytes is { } size ? Math.Round(size / 1e9, 1) : null));
            }
        }
        models.AddRange(options.Value.Models.Where(model => !string.IsNullOrWhiteSpace(model.Model)));
        // One entry per key: the larger context and whatever any entry knows about the rest.
        return [.. models.Where(model => !string.IsNullOrWhiteSpace(model.Model)).GroupBy(model => model.Model.Trim()).Select(group => new LlmGateModel(
            group.Key,
            group.Select(model => model.Source).FirstOrDefault(source => !string.IsNullOrWhiteSpace(source)),
            group.Max(model => model.Context),
            group.Max(model => model.RamGb),
            group.Max(model => model.SizeGb)))];
    }
}
