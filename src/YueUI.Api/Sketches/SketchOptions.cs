namespace YueUI.Api.Sketches;

/// <summary>
/// Turning a written song sketch (structure, chords, lyrics, ideas for the melody) into a song plan with Claude
/// (<see cref="ClaudeSketchModel"/>). Configured under <c>Sketch</c>. The local lyrics models cannot do this reliably:
/// a plan is a whole song's melody, bar by bar, in a strict format, so it goes to Anthropic's API.
/// </summary>
public sealed class SketchOptions
{
    public const string Section = "Sketch";

    /// <summary>Claude's model id, the default of the page's choice.</summary>
    public string Model { get; set; } = "claude-opus-5-5";

    /// <summary>
    /// The models the page offers besides <see cref="Model"/>. Fable 5.1 thinks deeper and costs about two and a half
    /// times as much; Sonnet 5.5 half. Haiku is left out: a whole melody bar by bar asks too much of it.
    /// </summary>
    public string[] Models { get; set; } = ["claude-opus-5-5", "claude-fable-5-1", "claude-sonnet-5-5"];

    /// <summary><see cref="Model"/> first, then the others, each once.</summary>
    public IReadOnlyList<string> Offered => [.. new[] { Model }.Concat(Models).Where(m => !string.IsNullOrWhiteSpace(m)).Distinct()];

    /// <summary>How hard the model thinks: low, medium, high, xhigh or max. A whole melody repays more than the default.</summary>
    public string Effort { get; set; } = "high";

    /// <summary>
    /// The API key; else <c>ANTHROPIC_API_KEY</c>, else the first line of <see cref="KeyFile"/> (default
    /// <c>~/.config/tonwerk/anthropic.key</c>), read on every use so a key can be added without a restart.
    /// </summary>
    public string? ApiKey { get; set; }

    public string? KeyFile { get; set; }

    /// <summary>Upper limit of the answer, thinking included; a song of a hundred bars takes about 20000 tokens.</summary>
    public int MaxTokens { get; set; } = 64000;

    /// <summary>A plan gives up after this long.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>Sketches longer than this are refused before they cost anything.</summary>
    public int MaxSketchCharacters { get; set; } = 60000;

    public string ResolvedKeyFile => Expand(string.IsNullOrWhiteSpace(KeyFile) ? "~/.config/tonwerk/anthropic.key" : KeyFile);

    /// <summary>The key from the settings, the environment or the key file; null when there is none.</summary>
    public string? ResolveApiKey()
    {
        if (!string.IsNullOrWhiteSpace(ApiKey))
        {
            return ApiKey.Trim();
        }
        if (Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") is { Length: > 0 } fromEnvironment)
        {
            return fromEnvironment.Trim();
        }
        try
        {
            return File.Exists(ResolvedKeyFile)
                ? File.ReadLines(ResolvedKeyFile).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'))
                : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Expand(string path) => Path.GetFullPath(path.StartsWith("~/", StringComparison.Ordinal)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
        : path);
}
