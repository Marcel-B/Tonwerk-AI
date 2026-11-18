using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace YueUI.Api.OnkelBummel;

/// <summary>One message for Onkel Bummel.</summary>
/// <param name="Key">Sent as <c>Idempotency-Key</c>, so a try whose answer was lost does not leave a second message.</param>
/// <param name="Priority">1 to 5 as in ntfy; 3 normal, 4 high.</param>
/// <param name="Tags">Emoji short names (<c>white_check_mark</c>, <c>warning</c>) show as an emoji before the title.</param>
/// <param name="Click">What a tap opens; null for nothing.</param>
public sealed record OnkelBummelMessage(string Key, string Title, string Body, int Priority, IReadOnlyList<string> Tags, Uri? Click);

/// <summary>What a test message found; the apps say it in their language by <see cref="State"/>.</summary>
/// <param name="State"><c>ok</c> (sent), <c>token</c> (Onkel Bummel does not know the token), <c>topic</c> (the token
/// may not send to the topic), <c>unreachable</c>, <c>failed</c> (another answer, see <see cref="Detail"/>) or
/// <c>notConfigured</c>.</param>
public sealed record OnkelBummelCheck(string State, string? Detail)
{
    public const string Ok = "ok";
    public const string Token = "token";
    public const string Topic = "topic";
    public const string Unreachable = "unreachable";
    public const string Failed = "failed";
    public const string NotConfigured = "notConfigured";
}

/// <summary>Onkel Bummel refused or could not be reached; <see cref="Status"/> is its HTTP status where it answered.</summary>
public sealed class OnkelBummelException(string message, int? status = null) : Exception(message)
{
    public int? Status { get; } = status;

    /// <summary>Worth another try: no answer, a server error or too many requests. A 4xx stays wrong.</summary>
    public bool Transient => Status is null or >= 500 or 429;
}

/// <summary>
/// Onkel Bummel's sending API: <c>POST /api/v1/messages</c> with JSON (<c>topic</c>, <c>title</c>, <c>message</c>,
/// <c>priority</c>, <c>tags</c>, <c>click</c>) and the publish token as <c>Authorization: Bearer ob_…</c>
/// (docs/SPEC.md#senden there).
/// </summary>
public sealed class OnkelBummelClient(IHttpClientFactory clients, IOptions<OnkelBummelOptions> options)
{
    public const string HttpClientName = "onkel-bummel";

    /// <summary>Onkel Bummel's limits; longer texts are cut rather than refused.</summary>
    public const int MaxTitle = 250;
    public const int MaxBody = 32_000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task SendAsync(OnkelBummelMessage message, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (settings.BaseUri is not { } baseUri || settings.ResolvedToken is not { } token)
        {
            throw new OnkelBummelException("Onkel Bummel is not configured.");
        }
        var body = new
        {
            topic = settings.ResolvedTopic,
            title = Cut(message.Title, MaxTitle),
            message = Cut(message.Body.Length == 0 ? message.Title : message.Body, MaxBody),
            priority = message.Priority,
            tags = message.Tags,
            click = message.Click?.ToString(),
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "api/v1/messages"))
        {
            Content = JsonContent.Create(body, options: Json),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Idempotency-Key", Cut(message.Key, 200));
        HttpResponseMessage response;
        try
        {
            response = await clients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new OnkelBummelException($"Onkel Bummel is not reachable at {baseUri}: {exception.Message}");
        }
        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                return;
            }
            // Onkel Bummel answers with problem details (RFC 7807).
            string? problem = null;
            try
            {
                using var answer = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                problem = answer.RootElement.ValueKind != JsonValueKind.Object ? null
                    : (answer.RootElement.TryGetProperty("detail", out var detail) ? detail.GetString() : null)
                        ?? (answer.RootElement.TryGetProperty("title", out var title) ? title.GetString() : null);
            }
            catch (JsonException)
            {
            }
            throw new OnkelBummelException(
                $"Onkel Bummel answered {(int)response.StatusCode}{(problem is null ? "" : $": {problem}")}", (int)response.StatusCode);
        }
    }

    /// <summary>
    /// Sends a test message: Onkel Bummel has no way to check a publish token without sending, and a message on the
    /// phone is the proof that matters anyway.
    /// </summary>
    public async Task<OnkelBummelCheck> TestAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.Configured)
        {
            return new OnkelBummelCheck(OnkelBummelCheck.NotConfigured, null);
        }
        try
        {
            var (title, body) = OnkelBummelTexts.Test(settings.Language);
            await SendAsync(
                new OnkelBummelMessage($"tonwerk-test-{Guid.NewGuid():N}", title, body, 3, ["white_check_mark", "tonwerk"], settings.LinkUri),
                cancellationToken);
            return new OnkelBummelCheck(OnkelBummelCheck.Ok, null);
        }
        catch (OnkelBummelException exception)
        {
            return new OnkelBummelCheck(
                exception.Status switch
                {
                    401 => OnkelBummelCheck.Token,
                    403 => OnkelBummelCheck.Topic,
                    null => OnkelBummelCheck.Unreachable,
                    _ => OnkelBummelCheck.Failed,
                },
                exception.Message);
        }
    }

    private static string Cut(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}
