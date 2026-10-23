using System.Diagnostics;
using System.Text;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Options;
using YueUI.Api.Sketches;

namespace YueUI.Api.Lyrics;

/// <summary>Writes lyrics in the cloud instead of LM Studio; <see cref="ClaudeLyricsModel"/>, faked in tests.</summary>
public interface ICloudLyricsModel
{
    /// <summary>Whether a key is there to ask with; read on every call, like the sketches' key.</summary>
    bool Configured { get; }

    /// <param name="image">A photo as a <c>data:image/…;base64,</c> URL, or null.</param>
    /// <returns>The model's answer as it came, before <see cref="LyricsWriter.Clean"/>.</returns>
    /// <exception cref="LyricsUnavailableException">No key, an API error, a refusal or an answer cut short.</exception>
    Task<string> WriteAsync(string model, string system, string prompt, string? image, CancellationToken cancellationToken);
}

/// <summary>
/// Lyrics through Anthropic's Messages API with the official SDK, the key shared with the sketches
/// (<see cref="SketchOptions.ResolveApiKey"/>). Nothing of it runs on the Mac, so a draft here needs no memory and
/// waits for no model.
/// </summary>
public sealed class ClaudeLyricsModel(
    IOptions<LyricsOptions> options,
    IOptions<SketchOptions> keys,
    IHttpClientFactory httpClients,
    Monitoring.CloudUsage usage,
    ILogger<ClaudeLyricsModel> logger) : ICloudLyricsModel
{
    public bool Configured => keys.Value.ResolveApiKey() is not null;

    public async Task<string> WriteAsync(string model, string system, string prompt, string? image, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var key = keys.Value.ResolveApiKey() ?? throw new LyricsUnavailableException("No Anthropic API key is set.");
        var client = new AnthropicClient
        {
            ApiKey = key,
            Timeout = TimeSpan.FromMinutes(10),
            // The sketches' named client, so tests answer for both in Anthropic's place.
            HttpClient = httpClients.CreateClient(ClaudeSketchModel.HttpClientName),
        };

        MessageParamContent content = image is null
            ? prompt
            : new List<ContentBlockParam>
            {
                new ImageBlockParam { Source = ImageSource(image) },
                new TextBlockParam { Text = prompt },
            };
        var parameters = new MessageCreateParams
        {
            Model = model,
            MaxTokens = settings.ClaudeMaxTokens,
            System = system,
            Thinking = new ThinkingConfigAdaptive(),
            OutputConfig = new OutputConfig { Effort = ParseEffort(settings.ClaudeEffort) },
            Messages = [new() { Role = Role.User, Content = content }],
        };

        // Streamed like the sketches: with thinking the answer can take long enough to risk an unstreamed timeout.
        var text = new StringBuilder();
        string? stopReason = null;
        long? inputTokens = null;
        long? outputTokens = null;
        var started = Stopwatch.GetTimestamp();
        try
        {
            await foreach (var streamEvent in client.Messages.CreateStreaming(parameters, cancellationToken))
            {
                if (streamEvent.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var piece))
                {
                    text.Append(piece.Text);
                }
                else if (streamEvent.TryPickStart(out var start))
                {
                    inputTokens = start.Message.Usage.InputTokens;
                }
                else if (streamEvent.TryPickDelta(out var messageDelta))
                {
                    stopReason = messageDelta.Delta.StopReason?.Raw();
                    outputTokens = messageDelta.Usage.OutputTokens;
                }
            }
        }
        catch (AnthropicApiException error)
        {
            usage.Record("lyrics", model, inputTokens, outputTokens, false, Stopwatch.GetElapsedTime(started), logger);
            throw new LyricsUnavailableException($"Claude: {error.Message}");
        }
        usage.Record("lyrics", model, inputTokens, outputTokens, stopReason is not ("refusal" or "max_tokens"), Stopwatch.GetElapsedTime(started), logger);
        return stopReason switch
        {
            "refusal" => throw new LyricsUnavailableException("Claude declined to write these lyrics."),
            "max_tokens" => throw new LyricsUnavailableException($"Claude's answer outgrew {settings.ClaudeMaxTokens} tokens (Lyrics:ClaudeMaxTokens)."),
            _ => text.ToString(),
        };
    }

    /// <summary>The endpoint has already checked the data URL (JPEG, PNG or WebP, valid base64).</summary>
    private static Base64ImageSource ImageSource(string dataUrl)
    {
        var comma = dataUrl.IndexOf(',');
        var mediaType = dataUrl["data:".Length..dataUrl.IndexOf(';')] switch
        {
            "image/png" => MediaType.ImagePng,
            "image/webp" => MediaType.ImageWebP,
            _ => MediaType.ImageJpeg,
        };
        return new Base64ImageSource { Data = dataUrl[(comma + 1)..], MediaType = mediaType };
    }

    private static Effort ParseEffort(string effort) => effort.Trim().ToLowerInvariant() switch
    {
        "low" => Effort.Low,
        "high" => Effort.High,
        "xhigh" => Effort.Xhigh,
        "max" => Effort.Max,
        _ => Effort.Medium,
    };
}
