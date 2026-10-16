namespace YueUI.Api.Images;

/// <summary>
/// Covers painted by a local text-to-image model (FLUX.2 Klein through mflux), configured under <c>Images</c>. It needs
/// its own Python environment, which <c>deploy/install-images.sh</c> makes; without it the dialog explains how and the
/// endpoint that paints answers 501.
/// </summary>
public sealed class ImageOptions
{
    public const string Section = "Images";

    /// <summary>
    /// Where the environment and the downloaded models live. Default: <c>images</c> in the app's data folder
    /// (<c>~/Library/Application Support/YuE UI/images</c> on macOS), where the install script puts them too.
    /// </summary>
    public string? Root { get; set; }

    /// <summary>The Python with mflux; default <c>env/bin/python</c> under <see cref="Root"/>.</summary>
    public string? Python { get; set; }

    /// <summary>The Hugging Face cache the models are downloaded to (<c>HF_HOME</c>); default <c>models</c> under <see cref="Root"/>.</summary>
    public string? ModelCache { get; set; }

    /// <summary>
    /// A file holding a Hugging Face access token, read for every picture; default
    /// <c>~/.config/tonwerk/huggingface.token</c>. Only Klein 9B needs it: its repository is gated behind the
    /// non-commercial licence, which has to be accepted on huggingface.co with that account first.
    /// </summary>
    public string? TokenFile { get; set; }

    /// <summary>A picture gives up after this long; the first one of a model includes downloading it (15 to 32 GB).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(90);

    /// <summary>How often a waiting picture looks whether YuE2, the lyrics model, a voice or the speech lab left the memory.</summary>
    public TimeSpan WaitInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Whether a description goes through Claude into English first (<see cref="ClaudePromptTranslator"/>); it needs
    /// the sketches' Anthropic key and is skipped without one.
    /// </summary>
    public bool Translate { get; set; } = true;

    /// <summary>The Claude model that translates; a small one, since the task is short and plain.</summary>
    public string TranslateModel { get; set; } = "claude-haiku-5-5";

    /// <summary>A translation that takes longer is given up and the description painted as written.</summary>
    public TimeSpan TranslateTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>The models offered; replaces <see cref="ImageModel.Defaults"/> when set.</summary>
    public List<ImageModel>? Models { get; set; }

    public string ResolvedRoot => string.IsNullOrWhiteSpace(Root)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YuE UI", "images")
        : Expand(Root);

    public string ResolvedPython => string.IsNullOrWhiteSpace(Python) ? Path.Combine(ResolvedRoot, "env", "bin", "python") : Expand(Python);

    public string ResolvedModelCache => string.IsNullOrWhiteSpace(ModelCache) ? Path.Combine(ResolvedRoot, "models") : Expand(ModelCache);

    public string ResolvedTokenFile => Expand(string.IsNullOrWhiteSpace(TokenFile) ? "~/.config/tonwerk/huggingface.token" : TokenFile);

    public IReadOnlyList<ImageModel> ResolvedModels => Models is { Count: > 0 } models ? models : ImageModel.Defaults;

    private static string Expand(string path) => Path.GetFullPath(path.StartsWith("~/", StringComparison.Ordinal)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
        : path);
}
