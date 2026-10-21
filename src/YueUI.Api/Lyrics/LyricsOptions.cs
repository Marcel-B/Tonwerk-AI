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

    public string LmsPath => string.IsNullOrWhiteSpace(Lms)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lmstudio", "bin", "lms")
        : Lms;
}
