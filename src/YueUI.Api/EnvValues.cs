namespace YueUI.Api;

/// <summary>
/// Secrets that stay out of the repository and out of <c>appsettings.json</c>: an environment variable first, then a
/// line of an env file on the Mac (<c>KEY=value</c>, <c>#</c> comments, optional quotes and <c>export</c>, <c>~/</c> the
/// home folder, read on every use so an edit needs no restart), then the setting as a fallback the tests use.
/// </summary>
public static class EnvValues
{
    /// <summary>The environment variable, else the env file's line, else the setting; null when none has a value.</summary>
    public static string? Resolve(string name, string? envFile, string? setting) =>
        Clean(Environment.GetEnvironmentVariable(name)) ?? Clean(Read(envFile).GetValueOrDefault(name)) ?? Clean(setting);

    private static Dictionary<string, string> Read(string? envFile)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(envFile))
        {
            return values;
        }
        var path = envFile.StartsWith("~/", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), envFile[2..])
            : envFile;
        try
        {
            if (!File.Exists(path))
            {
                return values;
            }
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.StartsWith("export ", StringComparison.Ordinal))
                {
                    line = line[7..].TrimStart();
                }
                if (line.Length == 0 || line[0] == '#' || line.IndexOf('=') is not (> 0 and var equals))
                {
                    continue;
                }
                values[line[..equals].Trim()] = line[(equals + 1)..];
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
        return values;
    }

    /// <summary>Trimmed and without surrounding quotes, as module-o-mat's <c>Normalize</c> reads them; null when empty.</summary>
    private static string? Clean(string? value)
    {
        var trimmed = value?.Trim() ?? "";
        if (trimmed.Length >= 2 && (trimmed[0], trimmed[^1]) is ('"', '"') or ('\'', '\''))
        {
            trimmed = trimmed[1..^1].Trim();
        }
        return trimmed.Length > 0 ? trimmed : null;
    }
}
