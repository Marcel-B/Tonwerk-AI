namespace YueUI.Api.Tonregal;

/// <summary>
/// Tonregal, the music library on the Mac with the external disk (github.com/Marcel-B/Tonregal), configured under
/// <c>Tonregal</c>. A song handed over lands there tagged and in its folder, without Tonregal's own matching, and
/// Navidrome streams it from there. Off until an address and an API key are set; <c>GET /api/tonregal</c> says so and
/// the library and player menus leave the entry out.
/// </summary>
/// <remarks>
/// Address and key stay out of the repository: <c>TONREGAL_URL</c> and <c>TONREGAL_API_KEY</c> as environment variables
/// or lines of <see cref="EnvFile"/>, like the backup's Nextcloud (<see cref="EnvValues"/>). The key is the one under
/// <c>Tonregal:Ingest:Keys:tonwerk-ai</c> in Tonregal's <c>appsettings.Production.json</c>.
/// </remarks>
public sealed class TonregalOptions
{
    public const string Section = "Tonregal";

    public const string UrlVariable = "TONREGAL_URL";
    public const string KeyVariable = "TONREGAL_API_KEY";

    public string? EnvFile { get; set; } = "~/.config/tonwerk/tonregal.env";

    /// <summary>Fallback for <c>TONREGAL_URL</c>, e.g. <c>http://oi-o-mat.tail6105c0.ts.net:5100</c>.</summary>
    public string? Url { get; set; }

    /// <summary>Fallback for <c>TONREGAL_API_KEY</c>.</summary>
    public string? ApiKey { get; set; }

    /// <summary>How long a handover waits for Tonregal to file the song before it answers that it is on its way.</summary>
    public TimeSpan Wait { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>How often Tonregal is asked meanwhile.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>A FLAC of a long song over Tailscale.</summary>
    public TimeSpan UploadTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public Uri? BaseUri =>
        Uri.TryCreate(EnvValues.Resolve(UrlVariable, EnvFile, Url) is { } url ? url.TrimEnd('/') + "/" : null, UriKind.Absolute, out var uri)
        && uri.Scheme is "https" or "http" ? uri : null;

    public string? ResolvedKey => EnvValues.Resolve(KeyVariable, EnvFile, ApiKey);

    public bool Configured => BaseUri is not null && ResolvedKey is not null;
}
