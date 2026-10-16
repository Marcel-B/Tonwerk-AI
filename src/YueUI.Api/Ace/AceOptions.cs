namespace YueUI.Api.Ace;

/// <summary>
/// Songs made with ACE-Step 1.5 through acestep.cpp (a C++ port on GGML, Metal on the Mac), configured under <c>Ace</c>.
/// <c>deploy/install-ace.sh</c> builds it and downloads the GGUF models into <see cref="ResolvedRoot"/>; without the
/// binaries the page explains how and the endpoint that makes songs answers 501.
/// </summary>
public sealed class AceOptions
{
    public const string Section = "Ace";

    /// <summary>
    /// Where the build and the models live. Default: <c>ace</c> in the app's data folder
    /// (<c>~/Library/Application Support/YuE UI/ace</c> on macOS), where the install script puts them too.
    /// </summary>
    public string? Root { get; set; }

    /// <summary>The folder with <c>ace-lm</c> and <c>ace-synth</c>; default <c>acestep.cpp/build</c> under <see cref="Root"/>.</summary>
    public string? Binaries { get; set; }

    /// <summary>The GGUF files (LM, DiT, text encoder, VAE); default <c>models</c> under <see cref="Root"/>.</summary>
    public string? Models { get; set; }

    /// <summary>LoRAs for the DiT (a ComfyUI <c>.safetensors</c> or a PEFT folder); default <c>adapters</c> under <see cref="Root"/>.</summary>
    public string? Adapters { get; set; }

    /// <summary>
    /// Where the songs go, one run folder each like YuE Studio's; default <c>ace-songs</c> next to the database. Not YuE
    /// Studio's output folder, which its own app reads and would not understand.
    /// </summary>
    public string? Songs { get; set; }

    /// <summary>A song gives up after this long; ten minutes of music with the 4B LM take a while on the CPU.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(60);

    /// <summary>How often a waiting song looks whether YuE2 or another model left the memory.</summary>
    public TimeSpan WaitInterval { get; set; } = TimeSpan.FromSeconds(10);

    public string ResolvedRoot => string.IsNullOrWhiteSpace(Root)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YuE UI", "ace")
        : Expand(Root);

    public string ResolvedBinaries => string.IsNullOrWhiteSpace(Binaries) ? Path.Combine(ResolvedRoot, "acestep.cpp", "build") : Expand(Binaries);

    public string ResolvedModels => string.IsNullOrWhiteSpace(Models) ? Path.Combine(ResolvedRoot, "models") : Expand(Models);

    public string ResolvedAdapters => string.IsNullOrWhiteSpace(Adapters) ? Path.Combine(ResolvedRoot, "adapters") : Expand(Adapters);

    /// <param name="dataDirectory">The folder the database is in.</param>
    public string ResolvedSongs(string dataDirectory) =>
        string.IsNullOrWhiteSpace(Songs) ? Path.Combine(dataDirectory, "ace-songs") : Expand(Songs);

    private static string Expand(string path) => Path.GetFullPath(path.StartsWith("~/", StringComparison.Ordinal)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
        : path);
}
