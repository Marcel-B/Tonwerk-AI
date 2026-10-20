namespace YueUI.Api.Push;

/// <summary>Web Push, configured under <c>Push</c>.</summary>
public sealed class PushOptions
{
    public const string Section = "Push";

    /// <summary>
    /// The file holding this server's VAPID key pair and the browsers' subscriptions. Default:
    /// <c>~/Library/Application Support/YuE UI/push.json</c> (on macOS; the local application data folder elsewhere).
    /// </summary>
    public string? DataPath { get; set; }

    /// <summary>
    /// The VAPID subject: a <c>mailto:</c> or <c>https:</c> address where push services can reach whoever runs the
    /// server. Apple refuses tokens whose subject is not one, e.g. <c>mailto:me@localhost</c>.
    /// </summary>
    public string Subject { get; set; } = "https://github.com/Marcel-B/Tonwerk-AI";

    public string ResolvedDataPath => string.IsNullOrWhiteSpace(DataPath)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YuE UI", "push.json")
        : Path.GetFullPath(DataPath);
}
