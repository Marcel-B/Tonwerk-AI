namespace YueUI.Api.Speech;

/// <summary>
/// The speech lab, configured under <c>Speech</c>: text spoken by local text-to-speech models (mlx-audio), to hear
/// which one would carry a podcast. It needs its own Python environment, which <c>deploy/install-speech.sh</c> makes;
/// without it the page explains how and the endpoints that speak answer 501.
/// </summary>
public sealed class SpeechOptions
{
    public const string Section = "Speech";

    /// <summary>
    /// Where the environment and the downloaded models live. Default: <c>speech</c> in the app's data folder
    /// (<c>~/Library/Application Support/YuE UI/speech</c> on macOS), where the install script puts them too.
    /// </summary>
    public string? Root { get; set; }

    /// <summary>The Python with mlx-audio; default <c>env/bin/python</c> under <see cref="Root"/>.</summary>
    public string? Python { get; set; }

    /// <summary>The Hugging Face cache the models are downloaded to (<c>HF_HOME</c>); default <c>models</c> under <see cref="Root"/>.</summary>
    public string? ModelCache { get; set; }

    /// <summary>A take gives up after this long; the first one of a model includes downloading it (up to 16 GB).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(60);

    /// <summary>How often a waiting take looks whether YuE2, the lyrics model or a voice conversion has left the memory.</summary>
    public TimeSpan WaitInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The models the lab offers; replaces <see cref="SpeechModel.Defaults"/> when set.</summary>
    public List<SpeechModel>? Models { get; set; }

    /// <summary>The models that clean a speech recording; replaces <see cref="SpeechModel.CleanDefaults"/> when set.</summary>
    public List<SpeechModel>? CleanModels { get; set; }

    public string ResolvedRoot => string.IsNullOrWhiteSpace(Root)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YuE UI", "speech")
        : Expand(Root);

    public string ResolvedPython => string.IsNullOrWhiteSpace(Python) ? Path.Combine(ResolvedRoot, "env", "bin", "python") : Expand(Python);

    public string ResolvedModelCache => string.IsNullOrWhiteSpace(ModelCache) ? Path.Combine(ResolvedRoot, "models") : Expand(ModelCache);

    public IReadOnlyList<SpeechModel> ResolvedModels => Models is { Count: > 0 } models ? models : SpeechModel.Defaults;

    public IReadOnlyList<SpeechModel> ResolvedCleanModels => CleanModels is { Count: > 0 } models ? models : SpeechModel.CleanDefaults;

    private static string Expand(string path) => Path.GetFullPath(path.StartsWith("~/", StringComparison.Ordinal)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
        : path);
}
