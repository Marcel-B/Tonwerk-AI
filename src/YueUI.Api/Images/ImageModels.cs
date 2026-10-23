namespace YueUI.Api.Images;

/// <summary>
/// A text-to-image model one of the runners can paint with (<see cref="ImageRunners"/>). A class with setters, since the
/// list can be replaced in the configuration (<see cref="ImageOptions.Models"/>).
/// </summary>
public sealed class ImageModel
{
    /// <summary>Stable key, stored with each picture.</summary>
    public string Id { get; set; } = "";

    public string Label { get; set; } = "";

    /// <summary>Which runner paints it: <c>mflux</c> (the default) or <c>flux3-mlx</c> (<see cref="ImageRunners"/>).</summary>
    public string Runner { get; set; } = ImageRunners.Mflux;

    /// <summary>
    /// The runner's name for it: mflux's model name (<c>flux2-klein-4b</c>, <c>z-image-turbo</c>, <c>krea-2</c>), which
    /// picks its configuration and its repository, or flux3-mlx's build (<c>8bit</c>).
    /// </summary>
    public string Model { get; set; } = "";

    /// <summary>The Hugging Face repository downloaded, to tell whether it is in the cache already.</summary>
    public string Repo { get; set; } = "";

    /// <summary>Further repositories its first picture downloads (FLUX 3's prompt upsampler), for the same question.</summary>
    public List<string> MoreRepos { get; set; } = [];

    /// <summary>
    /// Bits mflux quantizes the weights to when it loads them (3, 4, 5, 6 or 8), null for the full bfloat16 weights. The
    /// download is the full one either way; what shrinks is the memory while it paints. flux3-mlx's builds come quantized.
    /// </summary>
    public int? Quantize { get; set; }

    /// <summary>The steps the model is distilled to; more make it slower, not better.</summary>
    public int Steps { get; set; } = 4;

    /// <summary>
    /// The prompt upsampler flux3-mlx expands the description with (<c>4bit</c>), null for none. FLUX 3 Klein was trained
    /// on the upsampler's long captions, and Black Forest Labs say never to use it without.
    /// </summary>
    public string? Upsampler { get; set; }

    /// <summary>
    /// Every picture passes an NSFW classifier before it is kept, and a flagged one is not: Krea 2's licence (§4.2)
    /// requires a content filter wherever the model is used, FLUX 3's and Klein 9B's (FLUX Kommunity and FLUX Non-Commercial v2.1, both §2.e) a filter or a review of
    /// every picture before it is published, and the filter does not depend on someone looking.
    /// </summary>
    public bool ContentFilter { get; set; }

    /// <summary>
    /// The least memory a Mac needs to be offered it, in GB; null for any. A Mac with less never paints it, here or as a
    /// helper, so automatic placement passes it over.
    /// </summary>
    public double? MinSystemGb { get; set; }

    /// <summary>It can write short words into the picture (a title on a wall), which Klein 4B cannot.</summary>
    public bool Lettering { get; set; }

    /// <summary>
    /// Whether its runner applies LoRAs (<see cref="ImageLoraLibrary"/>): mflux does for Klein, Z-Image and Krea 2;
    /// flux3-mlx has no LoRA loader.
    /// </summary>
    public bool TakesLoras => Runner == ImageRunners.Mflux;

    /// <summary>
    /// How many reference pictures it looks at (style, figure, place): FLUX.2 Klein through mflux's edit pipeline and
    /// FLUX 3 through flux3-mlx's <c>images</c>; Z-Image and Krea 2 have no such input and take none.
    /// </summary>
    public int MaxReferences => Runner == ImageRunners.Flux3 || Model.StartsWith("flux2-klein", StringComparison.Ordinal)
        ? CoverStyleLibrary.MaxImages
        : 0;

    /// <summary>
    /// Whether it paints over a start picture instead (img2img, <see cref="ReferenceRoles.Start"/>): Z-Image and Krea 2
    /// keep its layout and colours by a strength, which is all they can take of a picture.
    /// </summary>
    public bool TakesStartImage => Runner == ImageRunners.Mflux
        && (Model.StartsWith("z-image", StringComparison.Ordinal) || Model.StartsWith("krea-2", StringComparison.Ordinal));

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
    /// Both distilled Klein models of Black Forest Labs' FLUX.2 (January 2026), Z-Image Turbo (Tongyi, the baseline of
    /// Marcel's tests on oi-o-mat), Krea 2 Turbo (June 2026) and FLUX 3 Image Klein (October 2026). mflux 0.23 runs the
    /// first four, flux3-mlx the last with its upsampler. Sizes are the downloads the model cards and READMEs name; the
    /// memory is an estimate (transformer plus text encoder at the bits given), not measured on the Mac. 4B stays the
    /// default because it is the smallest whose pictures may go on a released song. Krea 2 and FLUX 3 are offered only
    /// on Macs with 32 GB (Marcel's wish, 2026-10-21): on 24 GB they would leave too little beside macOS.
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
            ContentFilter = true,
            Gated = true,
        },
        new()
        {
            Id = "z-image-turbo",
            Label = "Z-Image Turbo",
            Model = "z-image-turbo",
            Repo = "Tongyi-MAI/Z-Image-Turbo",
            Quantize = 8,
            // Distilled to eight denoising steps, which take nine scheduler steps; mflux's README uses nine.
            Steps = 9,
            Lettering = true,
            DownloadGb = 21,
            MemoryGb = 11,
            License = "Apache 2.0",
            Commercial = true,
        },
        new()
        {
            Id = "krea-2-turbo",
            Label = "Krea 2 Turbo",
            Model = "krea-2",
            Repo = "krea/Krea-2-Turbo",
            Quantize = 8,
            Steps = 8,
            ContentFilter = true,
            MinSystemGb = 32,
            Lettering = true,
            DownloadGb = 35,
            MemoryGb = 22,
            License = "Krea 2 Community (kommerziell bis 1 Mio. $ Umsatz)",
            Commercial = true,
            Gated = true,
        },
        new()
        {
            Id = "flux3-klein",
            Label = "FLUX 3 Klein",
            Runner = ImageRunners.Flux3,
            Model = "8bit",
            Repo = "VisualInference/FLUX-3-Image-Klein-MLX-8bit",
            MoreRepos = ["VisualInference/FLUX-3-Image-Klein-pup-MLX-4bit"],
            Steps = 16,
            Upsampler = "4bit",
            ContentFilter = true,
            MinSystemGb = 32,
            Lettering = true,
            DownloadGb = 15,
            MemoryGb = 12,
            License = "FLUX Kommunity (kommerziell bis 5 Mio. $ Umsatz)",
            Commercial = true,
        },
    ];
}

/// <summary>The programs that paint, each in a Python environment of its own (<c>deploy/install-images.sh</c>).</summary>
public static class ImageRunners
{
    /// <summary>mflux: FLUX.2 Klein, Z-Image Turbo and Krea 2.</summary>
    public const string Mflux = "mflux";

    /// <summary>flux3-mlx: FLUX 3 Image Klein with Black Forest Labs' prompt upsampler, which mflux does not have.</summary>
    public const string Flux3 = "flux3-mlx";
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
    /// The English the model was given, where Claude translated <see cref="Prompt"/> or FLUX 3's upsampler wrote it out;
    /// null while it waits for that, when the prompt went as written, or for pictures from before.
    /// </summary>
    public string? PaintedPrompt { get; init; }

    /// <summary>
    /// The whole text sent to the model: <see cref="PaintedPrompt"/> (or <see cref="Prompt"/>) with the LoRAs' trigger words and
    /// the reference pictures' sentences after it; null until it is sent, and for pictures from before (see
    /// <see cref="ImageMaker.SentPromptOf"/>). FLUX 3's upsampler writes it out again; what it made is <see cref="PaintedPrompt"/>.
    /// </summary>
    public string? SentPrompt { get; init; }

    /// <summary>The same prompt, model and seed paint the same picture again (FLUX 3's upsampler decodes greedily, so it too).</summary>
    public long Seed { get; init; }

    /// <summary>queued, loading (the model, including a download), painting, done, failed or cancelled.</summary>
    public string Stage { get; init; } = "queued";

    public string? Message { get; init; }

    /// <summary>Of the painting steps, while it paints.</summary>
    public double Fraction { get; init; }

    public double? LoadSeconds { get; init; }

    public double? PaintSeconds { get; init; }

    public double? PeakMemoryGb { get; init; }

    /// <summary>The Mac it was asked for (<c>local</c> or a helper's id, Machines/); null for whichever is free.</summary>
    public string? Machine { get; init; }

    /// <summary>The LoRAs it is painted with, as they were when it was asked for; null or empty for none.</summary>
    public IReadOnlyList<ImageLoraUsed>? Loras { get; init; }

    /// <summary>
    /// The reference pictures it is painted with, in their order (image 1, 2, …); their files lie beside it as
    /// <c>&lt;id&gt;.ref&lt;n&gt;.*</c>, copies, so that deleting a style or a candidate leaves the picture alone.
    /// </summary>
    public IReadOnlyList<ImageReferenceUsed>? References { get; init; }

    /// <summary>The name of the Mac that runs it, once one does.</summary>
    public string? RanOn { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public bool Finished => Stage is "done" or "failed" or "cancelled";
}

/// <param name="Prompt">What to paint, up to <see cref="MaxPromptLength"/> characters.</param>
/// <param name="Model">An <see cref="ImageModel.Id"/>; null for the first one offered.</param>
/// <param name="Seed">Null for a new one each time.</param>
/// <param name="Machine">The Mac to paint on: <c>local</c>, a helper's id, or null for whichever is free.</param>
/// <param name="Loras">Up to <see cref="ImageLoraLibrary.MaxPerPicture"/> LoRAs made for the model.</param>
/// <param name="References">Pictures to look at (<see cref="ImageModel.MaxReferences"/>) or one to paint over
/// (<see cref="ImageModel.TakesStartImage"/>).</param>
public sealed record ImageRequest(
    string? Prompt,
    string? Model,
    long? Seed,
    string? Machine = null,
    IReadOnlyList<ImageLoraChoice>? Loras = null,
    IReadOnlyList<ImageReferenceChoice>? References = null)
{
    /// <summary>Klein reads its prompt through Qwen3 with 512 tokens; this stays well inside.</summary>
    public const int MaxPromptLength = 2000;
}

/// <param name="Installed">Some runner is installed on this Mac.</param>
/// <param name="Python">mflux's Python, where <c>deploy/install-images.sh</c> puts it.</param>
/// <param name="Translates">Whether a description is translated into English before painting (a Claude key is there).</param>
/// <param name="Flux3Python">flux3-mlx's Python, the same.</param>
public sealed record ImageInfo(bool Installed, string Python, IReadOnlyList<ImageModelInfo> Models, bool Translates, string Flux3Python = "");

/// <param name="TokenFound">For a gated model: whether a Hugging Face token is there to download it with.</param>
/// <param name="Available">Whether a Mac here can paint it: this one, or a helper that says so.</param>
/// <param name="Unavailable">Why this Mac cannot (its runner missing, too little memory), even where a helper can.</param>
/// <param name="Lettering">It can write a short title into the picture.</param>
/// <param name="Loras">It can be painted with LoRAs made for it.</param>
/// <param name="References">How many reference pictures it looks at; 0 for none.</param>
/// <param name="StartImage">It paints over a start picture instead.</param>
public sealed record ImageModelInfo(
    string Id,
    string Label,
    double DownloadGb,
    double MemoryGb,
    string License,
    bool Commercial,
    bool Gated,
    bool Downloaded,
    bool TokenFound,
    bool Available = true,
    string? Unavailable = null,
    bool Lettering = false,
    double? MinSystemGb = null,
    bool Loras = false,
    int References = 0,
    bool StartImage = false);

/// <summary>A reference picture a new one asks for.</summary>
/// <param name="Source">
/// Where it comes from: <c>style</c> (<paramref name="Id"/> <c>&lt;style id&gt;/&lt;picture id&gt;</c>), <c>image</c> (an
/// earlier candidate's id), <c>cover</c> (the song's own cover) or <c>upload</c> (an id from <c>POST /api/image-references</c>).
/// </param>
/// <param name="Role">One of <see cref="ReferenceRoles"/>; null for a style picture's own role, else style.</param>
/// <param name="Strength">For <see cref="ReferenceRoles.Start"/>: how much of the start picture stays, 0.1 to 0.9.</param>
public sealed record ImageReferenceChoice(string Source, string? Id = null, string? Role = null, double? Strength = null);

/// <summary>
/// How a song's cover was painted, kept with the cover in the database when a candidate becomes it (the candidate itself
/// lives only in <c>images/</c>, which a cleanup empties and the backup leaves out), so the text can be read and copied
/// later. A photo taken as cover has none. Field names as <see cref="ImageState"/>'s, so the viewers take either.
/// </summary>
public sealed record CoverPainting(
    string ImageId,
    string ModelId,
    string ModelLabel,
    string Prompt,
    string? PaintedPrompt,
    string SentPrompt,
    long Seed,
    IReadOnlyList<ImageLoraUsed>? Loras,
    IReadOnlyList<ImageReferenceUsed>? References,
    DateTimeOffset CreatedAt)
{
    public static CoverPainting Of(ImageState image) => new(
        image.Id,
        image.ModelId,
        image.ModelLabel,
        image.Prompt,
        image.PaintedPrompt,
        ImageMaker.SentPromptOf(image),
        image.Seed,
        image.Loras,
        image.References,
        image.CreatedAt);
}

/// <summary>A reference a picture was painted with, as the dialog shows it under the picture.</summary>
public sealed record ImageReferenceUsed(string Role, string Label, double? Strength = null);

/// <summary>A reference picture's file for the runner.</summary>
public sealed record ImageReferenceFile(string Path, string Role, double? Strength = null);
