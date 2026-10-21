namespace YueUI.Api.Lyrics;

/// <summary>
/// How much a thinking model thinks before the lyrics, LM Studio's <c>reasoning</c> of its native chat API. Left out,
/// the model thinks as set in LM Studio, which may be far longer than a song needs (Qwen 3.8 at xhigh took over ten
/// minutes). A model that cannot be steered refuses the value; the draft is then written without it.
/// </summary>
public enum LyricsReasoning
{
    Off,
    Low,
    Medium,
    High,
    On,
}
