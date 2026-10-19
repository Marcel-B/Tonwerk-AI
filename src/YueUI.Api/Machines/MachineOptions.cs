namespace YueUI.Api.Machines;

/// <summary>
/// Other Macs that compute for this one, and whether this one computes for others. Configured under <c>Machines</c>.
/// A helper is another Tonwerk installation with <see cref="Serve"/> on, reached through Tailscale (the LaunchAgent's
/// dotnet cannot reach LAN addresses), with the same key on both sides.
/// </summary>
public sealed class MachineOptions
{
    public const string Section = "Machines";

    /// <summary>What this Mac is called in the pickers.</summary>
    public string Name { get; set; } = "Dieser Mac";

    /// <summary>The Macs this one may hand jobs to.</summary>
    public List<HelperMachine> Helpers { get; set; } = [];

    /// <summary>Answer <c>/api/helper</c>: run jobs for another Tonwerk, which sends them with the key.</summary>
    public bool Serve { get; set; }

    /// <summary>The shared key; else the first line of <see cref="KeyFile"/>, read on every use.</summary>
    public string? Key { get; set; }

    /// <summary>Default <c>~/.config/tonwerk/helper.key</c>.</summary>
    public string? KeyFile { get; set; }

    /// <summary>How often the helpers are asked whether they are free.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    public string ResolvedKeyFile => Sketches.SketchOptions.Expand(string.IsNullOrWhiteSpace(KeyFile) ? "~/.config/tonwerk/helper.key" : KeyFile);

    /// <summary>The key from the settings or the key file; null when there is none.</summary>
    public string? ResolveKey()
    {
        if (!string.IsNullOrWhiteSpace(Key))
        {
            return Key.Trim();
        }
        try
        {
            return File.Exists(ResolvedKeyFile)
                ? File.ReadLines(ResolvedKeyFile).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'))
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>Another Mac running Tonwerk with <see cref="MachineOptions.Serve"/> on.</summary>
public sealed class HelperMachine
{
    /// <summary>What jobs name it by (<c>machine</c>); not <see cref="Machines.Local"/> or <see cref="Machines.Auto"/>.</summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>Its Tonwerk, e.g. <c>https://mac-alt.tailxxxx.ts.net:8443/</c>.</summary>
    public string Url { get; set; } = "";

    /// <summary>Its key where it differs from <see cref="MachineOptions.Key"/>.</summary>
    public string? Key { get; set; }

    public string Label => string.IsNullOrWhiteSpace(Name) ? Id : Name;
}
