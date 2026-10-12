namespace YueUI.Api.Video;

/// <summary>
/// Photo videos (<see cref="MontageBuilder"/>), configured under <c>Montage</c>. The planner needs its own Python
/// environment, which <c>deploy/install-montage.sh</c> makes; without it the dialog explains how and the endpoint
/// refuses photo videos with 501.
/// </summary>
public sealed class MontageOptions
{
    public const string Section = "Montage";

    /// <summary>
    /// Where the environment lives. Default: <c>montage</c> in the app's data folder
    /// (<c>~/Library/Application Support/YuE UI/montage</c> on macOS), where the install script puts it too.
    /// </summary>
    public string? Root { get; set; }

    /// <summary>The Python with librosa, Pillow and open_clip; default <c>env/bin/python</c> under <see cref="Root"/>.</summary>
    public string? Python { get; set; }

    /// <summary>
    /// The folder whose subfolders the dialog offers, one per video's pictures. Default <c>~/Pictures/Tonwerk</c>:
    /// easy to fill from Finder, from the Photos app's export or by AirDrop, and not one of the folders macOS asks
    /// permission for (Desktop, Documents, Downloads), which a LaunchAgent could not answer.
    /// </summary>
    public string? MediaRoot { get; set; }

    /// <summary>
    /// The CLIP model that ranks pictures by the motif, as open_clip names it. The default reads German and English
    /// alike (its text half is XLM-RoBERTa), about 1.5 GB, downloaded into <see cref="ModelCache"/> on first use.
    /// </summary>
    public string ClipModel { get; set; } = "xlm-roberta-base-ViT-B-32";

    public string ClipPretrained { get; set; } = "laion5b_s13b_b90k";

    /// <summary>Where Hugging Face downloads go (<c>HF_HOME</c>); default <c>models</c> under <see cref="Root"/>.</summary>
    public string? ModelCache { get; set; }

    /// <summary>A few hundred photos and a dozen clips take minutes; anything beyond this is hanging.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(45);

    public string ResolvedRoot => string.IsNullOrWhiteSpace(Root)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YuE UI", "montage")
        : Expand(Root);

    public string ResolvedPython => string.IsNullOrWhiteSpace(Python) ? Path.Combine(ResolvedRoot, "env", "bin", "python") : Expand(Python);

    public string ResolvedMediaRoot => string.IsNullOrWhiteSpace(MediaRoot)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures", "Tonwerk")
        : Expand(MediaRoot);

    public string ResolvedModelCache => string.IsNullOrWhiteSpace(ModelCache) ? Path.Combine(ResolvedRoot, "models") : Expand(ModelCache);

    private static string Expand(string path) => Path.GetFullPath(path.StartsWith("~/", StringComparison.Ordinal)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
        : path);
}
