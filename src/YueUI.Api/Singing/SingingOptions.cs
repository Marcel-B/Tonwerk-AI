namespace YueUI.Api.Singing;

/// <summary>
/// The singing page, configured under <c>Singing</c>: a melody with words sung by SoulX-Singer (Soul AI Lab, Apache
/// 2.0 for code and weights). It needs a checkout of SoulX-Singer, a Python environment and the weights, which
/// <c>deploy/install-singing.sh</c> puts under <see cref="Root"/>; without them the endpoint that sings answers 501.
/// </summary>
public sealed class SingingOptions
{
    public const string Section = "Singing";

    /// <summary>Default: <c>singing</c> in the app's data folder (<c>~/Library/Application Support/YuE UI/singing</c> on macOS).</summary>
    public string? Root { get; set; }

    /// <summary>The Python with PyTorch and SoulX-Singer's packages; default <c>env/bin/python</c> under <see cref="Root"/>.</summary>
    public string? Python { get; set; }

    /// <summary>SoulX-Singer's git checkout (its code, config and example voices); default <c>src</c> under <see cref="Root"/>.</summary>
    public string? Source { get; set; }

    /// <summary>The weights (2.8 GB); default <c>models/SoulX-Singer/model.pt</c> under <see cref="Root"/>.</summary>
    public string? Model { get; set; }

    /// <summary>CMUdict and the tagger g2p_en reads (<c>NLTK_DATA</c>); default <c>nltk_data</c> under <see cref="Root"/>.</summary>
    public string? NltkData { get; set; }

    /// <summary>PyTorch's device; null picks Metal (mps) where there is one, else the CPU.</summary>
    public string? Device { get; set; }

    /// <summary>Flow-matching steps per segment; the model's default is 32.</summary>
    public int Steps { get; set; } = 32;

    /// <summary>Classifier-free guidance; the model's default is 3.</summary>
    public double Cfg { get; set; } = 3;

    /// <summary>A take gives up after this long; loading the model is part of it.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>How often a waiting take looks whether YuE2 or another large model has left the memory.</summary>
    public TimeSpan WaitInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The longest melody sung at once; ten minutes are longer than any song.</summary>
    public double MaxSeconds { get; set; } = 600;

    /// <summary>The voices offered; replaces <see cref="SingingVoice.Defaults"/> when set.</summary>
    public List<SingingVoice>? Voices { get; set; }

    public string ResolvedRoot => string.IsNullOrWhiteSpace(Root)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YuE UI", "singing")
        : Expand(Root);

    public string ResolvedPython => string.IsNullOrWhiteSpace(Python) ? Path.Combine(ResolvedRoot, "env", "bin", "python") : Expand(Python);

    public string ResolvedSource => string.IsNullOrWhiteSpace(Source) ? Path.Combine(ResolvedRoot, "src") : Expand(Source);

    public string ResolvedModel => string.IsNullOrWhiteSpace(Model)
        ? Path.Combine(ResolvedRoot, "models", "SoulX-Singer", "model.pt")
        : Expand(Model);

    public string ResolvedNltkData => string.IsNullOrWhiteSpace(NltkData) ? Path.Combine(ResolvedRoot, "nltk_data") : Expand(NltkData);

    public IReadOnlyList<SingingVoice> ResolvedVoices => Voices is { Count: > 0 } voices ? voices : SingingVoice.Defaults;

    /// <summary>A voice's file: absolute as configured, else inside SoulX-Singer's checkout.</summary>
    public string VoiceFile(string path) => Path.IsPathRooted(path) || path.StartsWith("~/", StringComparison.Ordinal)
        ? Expand(path)
        : Path.Combine(ResolvedSource, path);

    private static string Expand(string path) => Path.GetFullPath(path.StartsWith("~/", StringComparison.Ordinal)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
        : path);
}
