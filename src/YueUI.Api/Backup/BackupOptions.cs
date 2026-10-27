namespace YueUI.Api.Backup;

/// <summary>
/// The nightly backup to a Nextcloud (or any WebDAV server), configured under <c>Backup</c>. Without an address, a user
/// and a password it is off: <c>GET /api/backup</c> says so and the settings menu leaves it out.
/// </summary>
/// <remarks>
/// Address, user and password stay out of the repository and out of <c>appsettings.json</c>: they go into
/// <see cref="EnvFile"/> on the Mac, with module-o-mat's names (<c>NEXTCLOUD_WEBDAV_URL</c>, <c>NEXTCLOUD_USERNAME</c>,
/// <c>NEXTCLOUD_APP_PASSWORD</c>). Environment variables of those names win over the file, the file over the
/// settings. Like module-o-mat's backup, the password is an app password (Nextcloud: Settings → Security → Devices
/// &amp; sessions), which can be revoked on its own and does not open the account's web login.
/// </remarks>
public sealed class BackupOptions
{
    public const string Section = "Backup";

    public const string UrlVariable = "NEXTCLOUD_WEBDAV_URL";
    public const string UserVariable = "NEXTCLOUD_USERNAME";
    public const string PasswordVariable = "NEXTCLOUD_APP_PASSWORD";

    /// <summary>
    /// A file of <c>KEY=value</c> lines (<c>#</c> comments, optional quotes and <c>export</c>) with the Nextcloud's
    /// address, user and app password; <c>~/</c> is the home folder. Read on every use, so an edit needs no restart.
    /// </summary>
    public string? EnvFile { get; set; } = "~/.config/tonwerk/nextcloud.env";

    /// <summary>
    /// Fallback for <c>NEXTCLOUD_WEBDAV_URL</c>: the WebDAV folder the backup goes into, e.g.
    /// <c>https://cloud.example.de/remote.php/dav/files/marcel/Tonwerk</c>. The folder itself is created if missing,
    /// the one above it has to exist. A LAN address does not work from the LaunchAgent (macOS's local network
    /// permission); a Tailscale or public address does.
    /// </summary>
    public string? WebDavUrl { get; set; }

    /// <summary>Fallback for <c>NEXTCLOUD_USERNAME</c>.</summary>
    public string? Username { get; set; }

    /// <summary>Fallback for <c>NEXTCLOUD_APP_PASSWORD</c>.</summary>
    public string? AppPassword { get; set; }

    /// <summary>When the nightly backup starts, local time in <see cref="TimeZone"/>; empty for manual backups only.</summary>
    public string? At { get; set; } = "03:00";

    public string TimeZone { get; set; } = "Europe/Berlin";

    /// <summary>
    /// Whether the songs, versions, stems, covers, recorded voices, takes and transcriptions go along as files, or only
    /// the database. The first backup uploads all of them; later ones only what is new or changed.
    /// </summary>
    public bool Files { get; set; } = true;

    /// <summary>How long one upload may take; a stem set's FLAC over a slow upload takes minutes.</summary>
    public TimeSpan UploadTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>A failed nightly backup is tried again after this long, not every minute.</summary>
    public TimeSpan RetryAfter { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long to wait before trying a file again that the Nextcloud refused for the moment (locked, busy); the wait
    /// grows with each of the three tries. A lock left by a cut-off upload lasts until Nextcloud's lock TTL (an hour by
    /// default), longer than is worth waiting, so such a file is left for the next backup.
    /// </summary>
    public TimeSpan TransientRetryDelay { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Files written more recently than this may still be in the works and wait for the next backup.</summary>
    public TimeSpan Settle { get; set; } = TimeSpan.FromMinutes(2);

    public Uri? BaseUri =>
        Uri.TryCreate(Value(UrlVariable, WebDavUrl) is { } url ? url.TrimEnd('/') + "/" : null, UriKind.Absolute, out var uri)
        && uri.Scheme is "https" or "http" ? uri : null;

    public string? ResolvedUsername => Value(UserVariable, Username);

    public string? ResolvedPassword => Value(PasswordVariable, AppPassword);

    public bool Configured => BaseUri is not null && ResolvedUsername is not null && ResolvedPassword is not null;

    private string? Value(string name, string? setting) => EnvValues.Resolve(name, EnvFile, setting);

    /// <summary>The daily start, or null for manual backups only (or a time that does not parse).</summary>
    public TimeOnly? DailyAt => TimeOnly.TryParse(At, System.Globalization.CultureInfo.InvariantCulture, out var at) ? at : null;

    public TimeZoneInfo Zone
    {
        get
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
            }
            catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                return TimeZoneInfo.Local;
            }
        }
    }
}
