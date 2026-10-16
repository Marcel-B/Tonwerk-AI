using System.Text.Json.Nodes;

namespace YueUI.Api.Ace;

/// <summary>
/// A song asked for on the ACE-Step page, in the page's terms; <see cref="AceRequestJson"/> turns it into acestep.cpp's
/// request JSON. Null (or zero for the numbers ACE-Step itself treats so) leaves a value to the model: the LM then
/// writes lyrics, picks tempo, key, metre, length and language, the DiT takes its preset's steps, guidance and shift.
/// </summary>
/// <param name="Title">Names the run folder and the song in the library; else the caption's first words.</param>
/// <param name="Caption">The style as prose or tags ("melancholic synth-pop, female vocals, 80s drum machine").</param>
/// <param name="Lyrics">Sung as written, with section tags like [Verse]; empty lets the LM write them.</param>
/// <param name="Instrumental">No vocals: acestep.cpp's <c>[Instrumental]</c> lyrics, which the DiT was trained with.</param>
/// <param name="Language">The lyrics' language as ACE-Step's code (<c>de</c>, <c>en</c>, …); null lets the LM tell.</param>
/// <param name="Duration">Seconds, 10 to 600; null lets the LM pick.</param>
/// <param name="Key">"C major", "F# minor"; null lets the LM pick.</param>
/// <param name="TimeSignature">Beats per bar: 2, 3, 4 or 6.</param>
/// <param name="Seed">The DiT's noise; the songs of a batch take the following ones. Null for a new one.</param>
/// <param name="Batch">Songs made at once, 1 to 4: different songs from the LM, or variations of the source.</param>
/// <param name="LmModel">A GGUF file name from <see cref="AceInfo.LmModels"/>; null for the largest there.</param>
/// <param name="DitModel">A GGUF file name from <see cref="AceInfo.DitModels"/>; null for the first turbo there.</param>
/// <param name="EnrichCaption">The LM rewrites the caption into a fuller one before composing (ACE-Step's CoT caption).</param>
/// <param name="Task"><c>text2music</c>, <c>cover</c> (a new style over a song) or <c>repaint</c> (a stretch of it made again).</param>
/// <param name="Source">The library song (<c>run/songN</c>) a cover or repaint starts from.</param>
/// <param name="CoverStrength">Share of the DiT steps that see the source, 0 to 1.</param>
/// <param name="RepaintStart">Seconds; negative adds music before the song.</param>
/// <param name="RepaintEnd">Seconds; beyond the song's end adds music after it, null for its end.</param>
public sealed record AceSongRequest(
    string? Title = null,
    string? Caption = null,
    string? Lyrics = null,
    bool? Instrumental = null,
    string? Language = null,
    double? Duration = null,
    int? Bpm = null,
    string? Key = null,
    int? TimeSignature = null,
    long? Seed = null,
    int? Batch = null,
    string? LmModel = null,
    string? DitModel = null,
    int? Steps = null,
    double? Guidance = null,
    double? Shift = null,
    string? Solver = null,
    double? LmTemperature = null,
    double? LmCfgScale = null,
    double? LmTopP = null,
    int? LmTopK = null,
    string? NegativePrompt = null,
    bool? EnrichCaption = null,
    string? Adapter = null,
    double? AdapterScale = null,
    string? Task = null,
    string? Source = null,
    double? CoverStrength = null,
    double? RepaintStart = null,
    double? RepaintEnd = null)
{
    public const int MaxCaptionLength = 2000;

    public const int MaxLyricsLength = 8000;

    public const int MaxBatch = 4;

    public static readonly string[] Tasks = ["text2music", "cover", "repaint"];

    public static readonly string[] Solvers = ["euler", "sde", "dpm3m", "stork4"];

    public static readonly int[] TimeSignatures = [2, 3, 4, 6];

    /// <summary>The 50 languages ACE-Step's LM is constrained to (acestep.cpp's <c>metadata-fsm.h</c>), <c>unknown</c> left out.</summary>
    public static readonly string[] Languages =
    [
        "ar", "az", "bg", "bn", "ca", "cs", "da", "de", "el", "en", "es", "fa", "fi", "fr", "he", "hi", "hr", "ht", "hu",
        "id", "is", "it", "ja", "ko", "la", "lt", "ms", "ne", "nl", "no", "pa", "pl", "pt", "ro", "ru", "sa", "sk", "sr",
        "sv", "sw", "ta", "te", "th", "tl", "tr", "uk", "ur", "vi", "yue", "zh",
    ];

    /// <summary>Whether the LM runs: only text2music composes; cover and repaint hand the caption to the DiT directly.</summary>
    public bool UsesLm => (Task ?? "text2music") == "text2music";
}

/// <summary>
/// A song (or a batch) being made by ACE-Step, from queued to done; kept in the database (<c>ace_jobs</c>), so that a
/// restart takes waiting ones up again and marks a half-made one failed.
/// </summary>
public sealed record AceJob
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required AceSongRequest Request { get; init; }

    /// <summary>The seed the first song gets, chosen when it was asked for so that it is known before anything runs.</summary>
    public long Seed { get; init; }

    /// <summary>queued, loading, writing (the LM's lyrics and metadata), composing (the LM's audio codes), synthesizing (the DiT), decoding (the VAE), saving, done, failed or cancelled.</summary>
    public string Stage { get; init; } = "queued";

    /// <summary>Of the whole job, roughly: the LM's share and the DiT's are guessed, not measured.</summary>
    public double Fraction { get; init; }

    public string? Message { get; init; }

    /// <summary>The run folder the songs went into, once done; the library lists them from then on.</summary>
    public string? Run { get; init; }

    /// <summary>How long it took from the memory being free to the songs being stored.</summary>
    public double? Seconds { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public bool Finished => Stage is "done" or "failed" or "cancelled";
}

/// <summary>What the page needs to offer: whether acestep.cpp is there and which GGUF files it can choose from.</summary>
/// <param name="Binaries">Where <c>ace-lm</c> and <c>ace-synth</c> are looked for, for the hint when they are missing.</param>
/// <param name="Ready">Binaries, an LM, a DiT, the text encoder and the VAE are all there.</param>
public sealed record AceInfo(
    bool Installed,
    bool Ready,
    string Binaries,
    string Models,
    IReadOnlyList<string> LmModels,
    IReadOnlyList<string> DitModels,
    IReadOnlyList<string> Adapters,
    IReadOnlyList<AceJob> Jobs);

/// <summary>A song as ACE-Step was asked for it, stored beside it (<c>ace-request.json</c>) so the page can take it over.</summary>
public sealed record AceSongSettings(AceSongRequest Request, JsonObject? Written);
