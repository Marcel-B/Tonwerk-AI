using System.Text.Json;

namespace YueUI.Api.Speech;

/// <summary>
/// A text-to-speech model the lab offers: a Hugging Face repository mlx-audio can load, with what it needs to speak
/// German. A class with setters, since the list can be replaced in the configuration (<see cref="SpeechOptions.Models"/>).
/// </summary>
public sealed class SpeechModel
{
    /// <summary>Stable key, stored with each take.</summary>
    public string Id { get; set; } = "";

    public string Label { get; set; } = "";

    /// <summary>The repository mlx-audio loads, e.g. <c>mlx-community/chatterbox-multilingual-v3</c>.</summary>
    public string Repo { get; set; } = "";

    /// <summary>
    /// What mlx-audio's <c>lang_code</c> means for this model differs: a code (<c>de</c>) for Chatterbox, a name
    /// (<c>german</c>) for Qwen3-TTS; null where the model reads the language off the text.
    /// </summary>
    public string? LangCode { get; set; }

    /// <summary>
    /// Further keyword arguments for the model's <c>generate</c>, e.g. MOSS-TTS's <c>language</c>. <c>temperature</c> and
    /// <c>max_tokens</c> reach the model only when set here; otherwise it samples with its own defaults.
    /// </summary>
    public Dictionary<string, JsonElement>? Options { get; set; }

    /// <summary>
    /// For a model that speaks only about 40 s per generation and splits nothing itself: the longest piece of text it is
    /// given at once, cut at sentence ends, the pieces joined with a short pause (<c>yueui_speech.py</c>,
    /// <c>pieces</c>). Null gives it the whole text.
    /// </summary>
    public int? ChunkCharacters { get; set; }

    /// <summary>
    /// For a model spoken in pieces that takes several reference clips: how many of the pieces just spoken go with the
    /// recording into the next one, so that it continues their voice instead of starting afresh from the recording.
    /// Null clones only the recording (or, without one, the first piece).
    /// </summary>
    public int? ContextPieces { get; set; }

    /// <summary>Roughly what it downloads, in GB, so the page can say what the first take costs.</summary>
    public double DownloadGb { get; set; }

    /// <summary>Whether it clones a recorded voice; without one it speaks with a voice of its own.</summary>
    public bool Clones { get; set; } = true;

    /// <summary>The model's licence as its card states it; some allow no commercial use.</summary>
    public string License { get; set; } = "";

    /// <summary>A line for the page: what it is good at, what to watch for.</summary>
    public string Note { get; set; } = "";

    /// <summary>
    /// For a model that cleans a recording instead of speaking (<see cref="CleanDefaults"/>): the subfolder of the
    /// repository that holds the weights, as DeepFilterNet keeps its versions; null for the repository's root.
    /// </summary>
    public string? Subfolder { get; set; }

    /// <summary>
    /// The models that take a speech recording (a talk, an interview) and give back the voice without noise:
    /// enhancement models mlx-audio 0.5.9 has under <c>mlx_audio.sts</c>, found by web research on 2026-10-14 and
    /// not yet heard on the Mac. Both work at 48 kHz on one channel, so the recording is decoded to that first.
    /// </summary>
    /// <remarks>
    /// Left out: Resemble Enhance (has an MLX port, but rebuilds speech generatively and invents words: a study of
    /// 2026, arXiv 2603.02641, measured 47 % character accuracy after it), NVIDIA's RE-USE (non-commercial, needs
    /// mamba_ssm on CUDA) and Sidon (dereverberates too, but only as CoreML in a Swift toolkit). The stem separator's
    /// vocal models are trained on music; they keep a voice but not as clean as a model trained on speech.
    /// </remarks>
    public static IReadOnlyList<SpeechModel> CleanDefaults { get; } =
    [
        new()
        {
            Id = "mossformer2-se",
            Label = "MossFormer2 SE 48K",
            // Alibaba's ClearerVoice-Studio model converted to MLX.
            Repo = "starkdmi/MossFormer2_SE_48K_MLX",
            DownloadGb = 0.25,
            Clones = false,
            License = "Apache-2.0",
            Note = "Removes noise and much of the room thoroughly; the stronger of the two.",
        },
        new()
        {
            Id = "deepfilternet3",
            Label = "DeepFilterNet 3",
            Repo = "mlx-community/DeepFilterNet-mlx",
            Subfolder = "v3",
            DownloadGb = 0.01,
            Clones = false,
            License = "MIT / Apache-2.0",
            Note = "Small and fast, gentle: takes steady noise away and leaves the voice as it was, reverb included.",
        },
    ];

    /// <summary>
    /// Found by web research on 2026-09-28 and not yet heard on the Mac; the repositories are the ones mlx-audio 0.5.6
    /// names. Each is loaded for its take and freed after, so the largest one decides what has to leave the memory.
    /// </summary>
    public static IReadOnlyList<SpeechModel> Defaults { get; } =
    [
        new()
        {
            Id = "chatterbox",
            Label = "Chatterbox Multilingual v3",
            Repo = "mlx-community/chatterbox-multilingual-v3",
            LangCode = "de",
            // 1000 tokens at 25 per second; 300 characters are about 20 s of German.
            ChunkCharacters = 300,
            DownloadGb = 3,
            License = "MIT",
            Note = "23 languages including German; clones from a few seconds.",
        },
        new()
        {
            Id = "qwen3-tts",
            Label = "Qwen3-TTS 1.7B",
            Repo = "mlx-community/Qwen3-TTS-12Hz-1.7B-Base-8bit",
            LangCode = "german",
            DownloadGb = 3.1,
            License = "Apache-2.0",
            Note = "Clones from 5 to 10 seconds; wants the words of the recording.",
        },
        new()
        {
            Id = "higgs-v2",
            Label = "Higgs Audio v2 3B",
            Repo = "mlx-community/higgs-audio-v2-3B-mlx-q8",
            // 1200 frames at 25 per second, 48 s.
            ChunkCharacters = 300,
            DownloadGb = 6.5,
            License = "Apache-2.0",
            Note = "Expressive, reads the language off the text; wants the words of the recording.",
        },
        new()
        {
            Id = "higgs-v3",
            Label = "Higgs Audio v3 4B",
            Repo = "bosonai/higgs-audio-v3-tts-4b",
            // What its model card recommends for cloning; mlx-audio's own defaults are 1.0 without top_k and 2048 tokens.
            Options = new()
            {
                ["temperature"] = JsonSerializer.SerializeToElement(0.8),
                ["top_k"] = JsonSerializer.SerializeToElement(50),
                ["max_tokens"] = JsonSerializer.SerializeToElement(1024),
            },
            ChunkCharacters = 300,
            DownloadGb = 9.3,
            License = "Research and non-commercial",
            Note = "Conversational, tags such as <|emotion:amusement|> in the text. Podcasts are allowed when Boson AI is credited.",
        },
        new()
        {
            // The same model made steadier, to compare by ear: with the recommended values, even cloning a recording, the
            // voice shifts a little from piece to piece and some pieces chirp. Upstream knows such glitches without a fix;
            // a user in the model's discussion "Glitches and Silence" had fewer with temperature 0.7 and still fewer with
            // a lower top_k. The piece just spoken goes along as a second reference (an experiment, see LabModel).
            Id = "higgs-v3-calm",
            Label = "Higgs Audio v3 4B (0.7 / top-k 20)",
            Repo = "bosonai/higgs-audio-v3-tts-4b",
            Options = new()
            {
                ["temperature"] = JsonSerializer.SerializeToElement(0.7),
                ["top_k"] = JsonSerializer.SerializeToElement(20),
                ["max_tokens"] = JsonSerializer.SerializeToElement(1024),
            },
            ChunkCharacters = 300,
            ContextPieces = 1,
            DownloadGb = 9.3,
            License = "Research and non-commercial",
            Note = "Higgs Audio v3 sampled more carefully (temperature 0.7, top-k 20 instead of 0.8 and 50), and each piece hears the one before beside the recording: meant to chirp less and keep the voice steadier between pieces, perhaps a little flatter. Speak both to compare.",
        },
        new()
        {
            Id = "moss-tts",
            Label = "MOSS-TTS Local v1.5",
            // The 8-bit MLX build of the 4.5B local-transformer model; the 8B MOSS-TTS-v1.5 is 17 GB in bf16, too much
            // for 24 GB with anything else open.
            Repo = "mlx-community/MOSS-TTS-Local-Transformer-v1.5-8bit",
            Options = new() { ["language"] = JsonSerializer.SerializeToElement("German") },
            DownloadGb = 6,
            License = "Apache-2.0",
            Note = "48 kHz, pauses as [pause 1.5s] in the text.",
        },
    ];
}

/// <summary>What the lab page needs to draw itself.</summary>
/// <param name="Installed">The Python environment with mlx-audio exists; otherwise the page shows how to make it.</param>
/// <param name="Python">Where the server looks for it.</param>
/// <param name="FfmpegInstalled">Recordings are converted with ffmpeg; without it none can be stored or cleaned.</param>
/// <param name="CleanModels">The models that clean a speech recording (<see cref="SpeechModel.CleanDefaults"/>).</param>
public sealed record SpeechInfo(
    bool Installed,
    string Python,
    bool FfmpegInstalled,
    IReadOnlyList<SpeechModelInfo> Models,
    IReadOnlyList<SpeechModelInfo> CleanModels,
    IReadOnlyList<SpeechVoice> Voices,
    IReadOnlyList<SpeechTake> Takes);

/// <param name="Downloaded">Its repository is in the model cache, so the first take does not download.</param>
public sealed record SpeechModelInfo(
    string Id,
    string Label,
    string Repo,
    double DownloadGb,
    bool Clones,
    string License,
    string Note,
    bool Downloaded);

/// <summary>
/// A recorded voice to clone: a few seconds of speech, stored as 24 kHz mono WAV, and what was said in it, which
/// several models need to line the voice up with the text.
/// </summary>
public sealed record SpeechVoice(string Id, string Label, string Transcript, double Seconds, DateTimeOffset CreatedAt);

/// <summary>
/// One text spoken by one model, or one recording cleaned by one (<see cref="Source"/> set). <see cref="Stage"/>:
/// queued, loading (includes a first download), speaking or cleaning, done, failed or cancelled. The times are the
/// script's own measurements, to compare the models.
/// </summary>
public sealed record SpeechTake
{
    public required string Id { get; init; }

    public required string ModelId { get; init; }

    public required string ModelLabel { get; init; }

    /// <summary>What is spoken; empty for a cleaned recording.</summary>
    public required string Text { get; init; }

    /// <summary>
    /// The name of the uploaded recording this take cleans (<c>POST /api/speech/cleanups</c>); null for a spoken take.
    /// The recording itself, decoded to 48 kHz mono WAV, lies beside the takes until the take is deleted.
    /// </summary>
    public string? Source { get; init; }

    /// <summary>How much of the recording is cleaned, 0 to 1; null while it is not known (and for spoken takes).</summary>
    public double? Progress { get; init; }

    /// <summary>The recorded voice, null for the model's own.</summary>
    public string? VoiceId { get; init; }

    /// <summary>Kept, so that a take still says whose voice it cloned after the recording is deleted.</summary>
    public string? VoiceLabel { get; init; }

    public string Stage { get; init; } = "queued";

    public string? Message { get; init; }

    /// <summary>How long the spoken audio is.</summary>
    public double? Seconds { get; init; }

    /// <summary>Loading the model, including a download.</summary>
    public double? LoadSeconds { get; init; }

    /// <summary>Speaking the text (or cleaning the recording), without loading.</summary>
    public double? SpeakSeconds { get; init; }

    public double? PeakMemoryGb { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public bool Finished => Stage is "done" or "failed" or "cancelled";
}

/// <param name="Text">What to say, up to <see cref="MaxTextLength"/> characters.</param>
/// <param name="VoiceId">A recorded voice to clone, or null for each model's own.</param>
/// <param name="Models">One take per model, spoken one after the other.</param>
public sealed record SpeechRequest(string? Text, string? VoiceId, IReadOnlyList<string>? Models)
{
    /// <summary>A few paragraphs: enough to hear a model, short enough that a take is done in minutes.</summary>
    public const int MaxTextLength = 3000;

    public Dictionary<string, string[]> Validate(IReadOnlyList<SpeechModel> offered)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(Text) || Text.Trim().Length > MaxTextLength)
        {
            errors["text"] = [$"A text of up to {MaxTextLength} characters."];
        }
        if (Models is not { Count: > 0 } || Models.Any(id => offered.All(m => m.Id != id)))
        {
            errors["models"] = ["One or more of the models GET /api/speech lists."];
        }
        return errors;
    }
}
