using System.Text;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Options;
using YueUI.Api.Sketches;

namespace YueUI.Api.Images;

/// <summary>Turns a picture's description into English before Klein reads it; <see cref="ClaudePromptTranslator"/>, faked in tests.</summary>
public interface IPromptTranslator
{
    /// <summary>Whether it can translate at all (switched on and a key there); read on every call.</summary>
    bool Configured { get; }

    /// <returns>The English description, or null when it is English already, no key is there or the call failed.</returns>
    Task<string?> TranslateAsync(string prompt, CancellationToken cancellationToken);
}

/// <summary>
/// Klein reads its prompt through Qwen3 and was trained on English captions, so a German description paints worse
/// than its English counterpart. Claude translates it with the sketches' key (<see cref="SketchOptions.ResolveApiKey"/>),
/// in the cloud, so it claims no memory. Without a key, or when Claude fails, the description is painted as written:
/// a missing translation must never cost the picture.
/// </summary>
public sealed class ClaudePromptTranslator(
    IOptions<ImageOptions> options,
    IOptions<SketchOptions> keys,
    IHttpClientFactory httpClients,
    ILogger<ClaudePromptTranslator> logger) : IPromptTranslator
{
    /// <summary>
    /// Quoted words are the ones the picture shows (the song's title), so they stay as written. English input comes back
    /// unchanged, which tells it apart without a guess of our own.
    /// </summary>
    public const string System = """
        You prepare descriptions for a text-to-image model that understands English best.
        If the description is already in English, answer it exactly as given, character for character.
        Otherwise translate it into natural English. Keep its meaning, its order and its level of detail: add nothing, leave nothing out, do not embellish.
        Correct obvious typos only where the meaning is clear from context.
        Text in quotation marks is lettering that should appear in the picture: keep it exactly as written, untranslated, in straight double quotes.
        Answer with the description alone, without quotes around it, comments or explanations.
        """;

    public bool Configured => options.Value.Translate && keys.Value.ResolveApiKey() is not null;

    public async Task<string?> TranslateAsync(string prompt, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.Translate || keys.Value.ResolveApiKey() is not { } key)
        {
            return null;
        }
        var client = new AnthropicClient
        {
            ApiKey = key,
            Timeout = settings.TranslateTimeout,
            // The sketches' named client, so tests answer for it in Anthropic's place.
            HttpClient = httpClients.CreateClient(ClaudeSketchModel.HttpClientName),
        };
        var parameters = new MessageCreateParams
        {
            Model = settings.TranslateModel,
            MaxTokens = 2048,
            System = System,
            Messages = [new() { Role = Role.User, Content = prompt }],
        };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(settings.TranslateTimeout);
            var message = await client.Messages.Create(parameters, timeout.Token);
            if (message.StopReason?.Raw() is not ("end_turn" or null))
            {
                logger.LogWarning("Claude did not translate the picture's description: {Reason}", message.StopReason?.Raw());
                return null;
            }
            var text = new StringBuilder();
            foreach (var block in message.Content)
            {
                if (block.TryPickText(out var piece))
                {
                    text.Append(piece.Text);
                }
            }
            var english = text.ToString().Trim();
            return english.Length == 0 || english == prompt.Trim() ? null : english;
        }
        catch (Exception exception) when (exception is AnthropicApiException or HttpRequestException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger.LogWarning(exception, "Could not translate the picture's description; it is painted as written");
            return null;
        }
    }
}
