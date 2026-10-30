namespace YueUI.Api.Monitoring;

/// <summary>Metrics for Prometheus and logs for Loki, configured under <c>Monitoring</c>.</summary>
/// <remarks>
/// The monitoring stack itself (Prometheus, Loki, Grafana; <c>deploy/monitoring/</c>) runs on another machine reached
/// over Tailscale, never on the Mac: its memory belongs to the models.
/// </remarks>
public sealed class MonitoringOptions
{
    public const string Section = "Monitoring";

    /// <summary>Whether <c>GET /metrics</c> answers. It costs nothing until something scrapes it.</summary>
    public bool Metrics { get; set; } = true;

    public LokiOptions Loki { get; set; } = new();
}

/// <summary>Shipping the collected log (<see cref="Logs.LogStore"/>) to Loki; off while <see cref="Url"/> is empty.</summary>
public sealed class LokiOptions
{
    /// <summary>
    /// Loki's base address, e.g. <c>http://monitoring:3100</c> (a Tailscale name or 100.x address: the LaunchAgent
    /// cannot reach LAN addresses). Empty keeps the log on the Mac only.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>Basic auth, for a Loki behind a proxy that asks for it; empty for none.</summary>
    public string? Username { get; set; }

    public string? Password { get; set; }

    /// <summary>The <c>host</c> label, to tell this Mac from other machines in the same Loki. Default: the machine's name.</summary>
    public string? Host { get; set; }

    /// <summary>Lines below this level stay on the Mac: debug, info, warning or error.</summary>
    public string MinLevel { get; set; } = Logs.LogLevels.Info;

    /// <summary>How long lines are gathered before they go out together.</summary>
    public TimeSpan BatchDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Lines kept while Loki does not answer; beyond this the oldest are dropped.</summary>
    public int Capacity { get; set; } = 20_000;

    /// <summary>How often <c>deploy/update.sh</c>'s log is read for new lines to send (<see cref="UpdateLogShipper"/>).</summary>
    public TimeSpan UpdateLogInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long the update log must be still before its newest entry goes out, since it may still grow.</summary>
    public TimeSpan UpdateLogSettle { get; set; } = TimeSpan.FromMinutes(1);

    public bool Enabled => !string.IsNullOrWhiteSpace(Url);
}
