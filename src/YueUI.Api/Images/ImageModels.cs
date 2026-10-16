namespace YueUI.Api.Images;

/// <summary>
/// A text-to-image model mflux can run. A class with setters, since the list can be replaced in the configuration
/// (<see cref="ImageOptions.Models"/>).
/// </summary>
public sealed class ImageModel
{
    /// <summary>Stable key, stored with each picture.</summary>
    public string Id { get; set; } = "";

    public string Label { get; set; } = "";

    /// <summary>mflux's name for it (<c>flux2-klein-4b</c>), which picks its configuration and its repository.</summary>
    public string Model { get; set; } = "";

    /// <summary>The Hugging Face repository mflux downloads, to tell whether it is in the cache already.</summary>
    public string Repo { get; set; } = "";

    /// <summary>
    /// Bits the weights are quantized to when loaded (3, 4, 5, 6 or 8), null for the full bfloat16 weights. The download
    /// is the full one either way; what shrinks is the memory while it paints.
    /// </summary>
    public int? Quantize { get; set; }

    /// <summary>Klein is distilled to four steps; more make it slower, not better.</summary>
    public int Steps { get; set; } = 4;

    /// <summary>Roughly what it downloads, in GB, so the dialog can say what the first picture costs.</summary>
    public double DownloadGb { get; set; }

    /// <summary>Roughly what it holds while painting, in GB, as quantized.</summary>
    public double MemoryGb { get; set; }

    /// <summary>The licence as the model card states it.</summary>
    public string License { get; set; } = "";

    /// <summary>Whether its pictures may be used commercially, e.g. as the cover of a released song.</summary>
    public bool Commercial { get; set; }

    /// <summary>The repository is gated: a Hugging Face token of an account that accepted the licence is needed.</summary>
    public bool Gated { get; set; }

    /// <summary>
    /// Both distilled Klein models of Black Forest Labs' FLUX.2 (January 2026), the ones mflux 0.21 runs. Sizes are the
    /// downloads mflux's README names; the memory is an estimate (transformer plus Qwen3 text encoder at the bits
    /// given), not measured on the Mac. 4B is the default because its pictures may go on a released song.
    /// </summary>
    public static IReadOnlyList<ImageModel> Defaults { get; } =
    [
        new()
        {
            Id = "klein-4b",
            Label = "FLUX.2 Klein 4B",
            Model = "flux2-klein-4b",
            Repo = "black-forest-labs/FLUX.2-klein-4B",
            Quantize = 8,
            DownloadGb = 15,
            MemoryGb = 9,
            License = "Apache 2.0",
            Commercial = true,
        },
        new()
        {
            Id = "klein-9b",
            Label = "FLUX.2 Klein 9B",
            Model = "flux2-klein-9b",
            Repo = "black-forest-labs/FLUX.2-klein-9B",
            // At 8 bits it would hold about 18 GB, too much beside macOS and the open apps on 24 GB.
            Quantize = 4,
            DownloadGb = 32,
            MemoryGb = 10,
            License = "FLUX Non-Commercial",
            Commercial = false,
            Gated = true,
        },
    ];
}

/// <summary>
/// A picture painted for a song: a candidate for its cover, which the dialog lists until it is taken or deleted. Kept
/// as JSON beside its image in <c>images/</c> next to the database, so a restart (a deploy) does not lose it.
/// </summary>
public sealed record ImageState
{
    public required string Id { get; init; }

    /// <summary><c>run/songN</c></summary>
    public required string SongId { get; init; }

    /// <summary>The song's title when it was asked for, for the queue page.</summary>
    public string? Title { get; init; }

    public required string ModelId { get; init; }

    public required string ModelLabel { get; init; }

    public required string Prompt { get; init; }

    /// <summary>
    /// The English the model was given, where Claude translated <see cref="Prompt"/>; null while it waits for that, when
    /// the prompt went as written, or for pictures from before.
    /// </summary>
    public string? PaintedPrompt { get; init; }

    /// <summary>The same prompt, model and seed paint the same picture again.</summary>
    public long Seed { get; init; }

    /// <summary>queued, loading (the model, including a download), painting, done, failed or cancelled.</summary>
    public string Stage { get; init; } = "queued";

    public string? Message { get; init; }

    /// <summary>Of the painting steps, while it paints.</summary>
    public double Fraction { get; init; }

    public double? LoadSeconds { get; init; }

    public double? PaintSeconds { get; init; }

    public double? PeakMemoryGb { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public bool Finished => Stage is "done" or "failed" or "cancelled";
}

/// <param name="Prompt">What to paint, up to <see cref="MaxPromptLength"/> characters.</param>
/// <param name="Model">An <see cref="ImageModel.Id"/>; null for the first one offered.</param>
/// <param name="Seed">Null for a new one each time.</param>
public sealed record ImageRequest(string? Prompt, string? Model, long? Seed)
{
    /// <summary>Klein reads its prompt through Qwen3 with 512 tokens; this stays well inside.</summary>
    public const int MaxPromptLength = 2000;
}

/// <param name="Translates">Whether a description is translated into English before painting (a Claude key is there).</param>
public sealed record ImageInfo(bool Installed, string Python, IReadOnlyList<ImageModelInfo> Models, bool Translates);

/// <param name="TokenFound">For a gated model: whether a Hugging Face token is there to download it with.</param>
public sealed record ImageModelInfo(
    string Id,
    string Label,
    double DownloadGb,
    double MemoryGb,
    string License,
    bool Commercial,
    bool Gated,
    bool Downloaded,
    bool TokenFound);
