namespace YueUI.Api.OnkelBummel;

/// <summary>
/// Onkel Bummel, Marcel's own notification service (github.com/Marcel-B/onkel-bummel), configured under
/// <c>OnkelBummel</c>. Once address and publish token are set, every finished song batch and every other long job is
/// also sent there as a message (<see cref="OnkelBummelNotifier"/>), beside Web Push, so it lands in Onkel Bummel's
/// history and on the iPhone through its app.
/// </summary>
/// <remarks>
/// Address and token stay out of the repository: <c>ONKELBUMMEL_URL</c>, <c>ONKELBUMMEL_TOKEN</c> (a publish token
/// <c>ob_…</c> with the right for <see cref="ResolvedTopic"/>), <c>ONKELBUMMEL_TOPIC</c>, <c>ONKELBUMMEL_LINK</c> and
/// <c>ONKELBUMMEL_OFF</c> as environment variables or lines of <see cref="EnvFile"/>, like Tonregal's
/// (<see cref="EnvValues"/>); the settings of both apps write that file (<c>PUT /api/onkel-bummel</c>). The names match
/// the monitoring stack's <c>.env</c>, which sends Grafana's alarms to the same service.
/// </remarks>
public sealed class OnkelBummelOptions
{
    public const string Section = "OnkelBummel";

    public const string UrlVariable = "ONKELBUMMEL_URL";
    public const string TokenVariable = "ONKELBUMMEL_TOKEN";
    public const string TopicVariable = "ONKELBUMMEL_TOPIC";
    public const string LinkVariable = "ONKELBUMMEL_LINK";
    public const string OffVariable = "ONKELBUMMEL_OFF";

    public const string DefaultTopic = "tonwerk";

    public string? EnvFile { get; set; } = "~/.config/tonwerk/onkel-bummel.env";

    /// <summary>Fallback for <c>ONKELBUMMEL_URL</c>, e.g. <c>https://onkel-bummel.tail6105c0.ts.net</c>.</summary>
    public string? Url { get; set; }

    /// <summary>Fallback for <c>ONKELBUMMEL_TOKEN</c>.</summary>
    public string? Token { get; set; }

    /// <summary>Fallback for <c>ONKELBUMMEL_TOPIC</c>; Onkel Bummel creates a topic with the first message to it.</summary>
    public string? Topic { get; set; }

    /// <summary>
    /// Fallback for <c>ONKELBUMMEL_LINK</c>: Tonwerk's own address as the phone reaches it (e.g.
    /// <c>https://mac-mini.tail6105c0.ts.net:8443</c>), so a message opens the song or page; none without it.
    /// </summary>
    public string? Link { get; set; }

    /// <summary>Texts are German unless this says <c>en</c>; Marcel reads German.</summary>
    public string Language { get; set; } = "de";

    /// <summary>How long a message waits before each new try after Onkel Bummel could not be reached or answered 5xx/429.</summary>
    public TimeSpan[] RetryDelays { get; set; } = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)];

    public Uri? BaseUri => AbsoluteHttp(EnvValues.Resolve(UrlVariable, EnvFile, Url));

    public string? ResolvedToken => EnvValues.Resolve(TokenVariable, EnvFile, Token);

    public string ResolvedTopic => EnvValues.Resolve(TopicVariable, EnvFile, Topic) ?? DefaultTopic;

    public Uri? LinkUri => AbsoluteHttp(EnvValues.Resolve(LinkVariable, EnvFile, Link));

    /// <summary>Switched off in the settings, keeping address and token.</summary>
    public bool Off => EnvValues.Resolve(OffVariable, EnvFile, null) is { } off
        && (off == "1" || off.Equals("true", StringComparison.OrdinalIgnoreCase));

    public bool Configured => BaseUri is not null && ResolvedToken is not null;

    /// <summary>Configured and not switched off: only then are messages sent.</summary>
    public bool Active => Configured && !Off;

    /// <summary>Whether an environment variable sets one of the values, which the settings page then cannot change.</summary>
    public static bool SetByEnvironment =>
        new[] { UrlVariable, TokenVariable, TopicVariable, LinkVariable, OffVariable }.Any(EnvValues.FromEnvironment);

    private static Uri? AbsoluteHttp(string? value) =>
        Uri.TryCreate(value is null ? null : value.TrimEnd('/') + "/", UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"
            ? uri
            : null;
}
