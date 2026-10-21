namespace YueUI.Api.Lyrics;

/// <summary>
/// Where lyrics are drafted: an OpenAI-compatible server, by default LM Studio on this Mac. Configured under
/// <c>Lyrics</c> (<c>Lyrics__Model</c> etc. as environment variables).
/// </summary>
public sealed class LyricsOptions
{
    public const string Section = "Lyrics";

    /// <summary>The server without <c>/v1</c>; LM Studio listens on port 1234.</summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:1234";

    /// <summary>
    /// The model's id as <c>GET /v1/models</c> lists it. Gemma 4 26B A4B QAT by default. It takes about 15 GB:
    /// together with the apps usually open it ran a 24 GB Mac deep into swap until it froze, so close what you can
    /// before a draft, or switch to the small <c>google/gemma-4-e4b</c>.
    /// </summary>
    public string Model { get; set; } = "google/gemma-4-26b-a4b-qat";

    /// <summary>
    /// Tokens of context the model is loaded with, and so what prompt, reasoning and lyrics share. Prompt and lyrics
    /// need about 1500, a thinking model's reasoning several thousand more. LM Studio's own default can be the
    /// model's maximum (262144 for Gemma 4), whose cache alone would not fit into memory.
    /// </summary>
    public int ContextLength { get; set; } = 30000;

    /// <summary>
    /// If LM Studio's guardrails refuse the model for lack of memory, the context is halved down to this before the
    /// draft fails. Below about 8000 tokens a thinking model rarely gets past its reasoning.
    /// </summary>
    public int MinContextLength { get; set; } = 8192;

    /// <summary>
    /// How long a request to the server may take, a draft's whole answer included, since LM Studio answers only once
    /// the lyrics are written. A thinking model at a high reasoning effort (Qwen 3.8 at xhigh) thought for over ten
    /// minutes; lowering the effort in LM Studio's model settings is the quicker way out.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>Only if "Require Authentication" is switched on in LM Studio's server settings.</summary>
    public string? ApiToken { get; set; }

    /// <summary>
    /// LM Studio's command line tool, used to start its server when nothing answers; default
    /// <c>~/.lmstudio/bin/lms</c>. Empty, or a missing file, leaves starting the server to the user.
    /// </summary>
    public string? Lms { get; set; }

    /// <summary>
    /// LM Studio unloads the model after this many idle seconds. The model is unloaded right after each draft
    /// anyway; this only covers a failed unload, so that it does not hold its memory for LM Studio's default hour.
    /// </summary>
    public int IdleTtlSeconds { get; set; } = 60;

    public double Temperature { get; set; } = 0.9;

    /// <summary>
    /// Claude models the picker offers beside LM Studio's, while an Anthropic key is set (the sketches' key,
    /// <c>Sketch:ApiKey</c>, <c>ANTHROPIC_API_KEY</c> or <c>~/.config/tonwerk/anthropic.key</c>). Empty offers none.
    /// Lyrics are short, so even Fable costs little; Haiku is the quick and cheap one.
    /// </summary>
    public string[] ClaudeModels { get; set; } = ["claude-opus-5-5", "claude-fable-5-1", "claude-sonnet-5-5", "claude-haiku-5-5"];

    /// <summary>How hard Claude thinks over lyrics: low, medium, high, xhigh or max. Lyrics are short; medium suffices.</summary>
    public string ClaudeEffort { get; set; } = "medium";

    /// <summary>Upper limit of Claude's answer, thinking included.</summary>
    public int ClaudeMaxTokens { get; set; } = 16000;

    /// <summary>
    /// Local models the picker marks with what they are good for, matched by a part of their LM Studio key, so any
    /// quantisation counts. Both need a Mac with 32 GB: Gemma 4 31B is dense, about 20 GB at 4 bits, and writes slowly
    /// but well, German included; Qwen 3.6 35B A3B is larger (about 23 GB) but only 3B of it work per token, so it
    /// answers several times as fast, for ideas, rhymes and variants.
    /// </summary>
    public LyricsModelRole[] Roles { get; set; } =
    [
        new() { Match = "gemma-4-31b", Role = "quality", MinSystemGb = 32 },
        new() { Match = "qwen3.6-35b-a3b", Role = "ideas", MinSystemGb = 32 },
    ];

    /// <summary>
    /// The Mac's memory in GB, which decides which of <see cref="Roles"/>' models it offers; null reads it from the
    /// system as the painted covers do.
    /// </summary>
    public double? SystemMemoryGb { get; set; }

    public double? ResolvedSystemMemoryGb => SystemMemoryGb ?? (GC.GetGCMemoryInfo().TotalAvailableMemoryBytes is > 0 and var bytes
        ? bytes / (double)(1L << 30)
        : null);

    /// <summary>The role entry whose <see cref="LyricsModelRole.Match"/> the model's key contains, if any.</summary>
    public LyricsModelRole? RoleOf(string model) => Roles.FirstOrDefault(r =>
        !string.IsNullOrWhiteSpace(r.Match) && model.Contains(r.Match.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The memory <paramref name="model"/> needs and this Mac lacks; null when it fits or nobody knows.</summary>
    public double? MissingMemoryFor(string model) =>
        RoleOf(model)?.MinSystemGb is { } needed && ResolvedSystemMemoryGb is { } has && has < needed - 0.5 ? needed : null;

    public string LmsPath => string.IsNullOrWhiteSpace(Lms)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lmstudio", "bin", "lms")
        : Lms;
}

/// <summary>What a local model is suggested for (<see cref="LyricsOptions.Roles"/>).</summary>
public sealed class LyricsModelRole
{
    /// <summary>A part of the model's LM Studio key, e.g. <c>gemma-4-31b</c>.</summary>
    public string Match { get; set; } = "";

    /// <summary><c>quality</c> (final lyrics, German) or <c>ideas</c> (fast brainstorming); the apps name it.</summary>
    public string Role { get; set; } = "";

    /// <summary>The Mac's memory it needs in GB; smaller Macs do not offer it.</summary>
    public double? MinSystemGb { get; set; }
}
