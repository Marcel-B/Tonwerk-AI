using System.Text.Json;
using Microsoft.Extensions.Options;
using YueUI.Api.Share;

namespace YueUI.Api.Voices;

/// <summary>What separates a song: this server itself (<see cref="MlxStemSeparator"/>) or StemMyWav's API (<see cref="StemClient"/>).</summary>
public interface IStemBackend
{
    /// <summary>
    /// Separates <paramref name="flac"/> into <paramref name="directory"/> and answers the 48 kHz WAVs the model made,
    /// named after its stems (<c>vocals.wav</c>, <c>instrumental.wav</c>, …, with <paramref name="dereverb"/> also
    /// <c>vocals_dry.wav</c> and <c>vocals_reverb.wav</c>).
    /// </summary>
    Task<IReadOnlyList<string>> ExtractAsync(string flac, string model, bool dereverb, string directory, CancellationToken cancellationToken);

    /// <returns>Null when the models cannot be listed; the caller then offers only <see cref="VoiceOptions.StemModel"/>.</returns>
    Task<IReadOnlyList<StemModel>?> ListModelsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Separates with this server's own <c>mlx-audio-separator</c> where it is installed (<see cref="VoiceOptions.LocalStems"/>),
/// else through StemMyWav's API, so a Mac set up before the separator moved in here keeps working.
/// </summary>
public sealed class StemSeparator(MlxStemSeparator local, StemClient service, IOptions<VoiceOptions> options)
{
    private IStemBackend Backend => options.Value.LocalStems ? local : service;

    /// <param name="model">The model the version was asked with; the configured one where it names none.</param>
    /// <param name="backend">Another Mac's separator (Machines/RemoteEngines.cs); this one's where null.</param>
    public async Task<Stems> SeparateAsync(string flac, string? model, string directory, CancellationToken cancellationToken, IStemBackend? backend = null)
    {
        model ??= options.Value.StemModel;
        // Dry vocals: Seed-VC would copy the reverb into the new voice, and it is mixed back in untouched instead.
        await (backend ?? Backend).ExtractAsync(flac, model, dereverb: true, directory, cancellationToken);
        string? Stem(string name) => File.Exists(Path.Combine(directory, name)) ? Path.Combine(directory, name) : null;
        var vocals = Stem("vocals_dry.wav") ?? Stem("vocals.wav");
        var instrumental = Stem("instrumental.wav");
        if (vocals is null || instrumental is null)
        {
            throw new VoiceServiceException($"The separation model {model} gave no vocals and instrumental.");
        }
        return new Stems(vocals, instrumental, Stem("vocals_reverb.wav"));
    }

    /// <inheritdoc cref="IStemBackend.ExtractAsync"/>
    public Task<IReadOnlyList<string>> ExtractAsync(
        string flac, string model, bool dereverb, string directory, CancellationToken cancellationToken, IStemBackend? backend = null) =>
        (backend ?? Backend).ExtractAsync(flac, model, dereverb, directory, cancellationToken);

    /// <inheritdoc cref="IStemBackend.ListModelsAsync"/>
    public Task<IReadOnlyList<StemModel>?> ListModelsAsync(CancellationToken cancellationToken) => Backend.ListModelsAsync(cancellationToken);
}

/// <summary>A model of <see cref="StemCatalog"/>.</summary>
/// <param name="Filename">What mlx-audio-separator loads.</param>
/// <param name="Stems">The names its files get, without .wav.</param>
public sealed record CatalogModel(string Id, string Name, string Filename, string Task, IReadOnlyList<string> Stems, double? RealtimeFactor);

/// <summary>The separation models offered, StemMyWav's catalog (embedded <c>stem-models.json</c>).</summary>
public static class StemCatalog
{
    public static IReadOnlyList<CatalogModel> Models { get; } = Load();

    public static CatalogModel? Find(string id) => Models.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    private static List<CatalogModel> Load()
    {
        using var stream = typeof(StemCatalog).Assembly.GetManifestResourceStream("stem-models.json")
            ?? throw new InvalidOperationException("stem-models.json is not embedded.");
        using var document = JsonDocument.Parse(stream);
        return
        [
            .. document.RootElement.GetProperty("models").EnumerateArray().Select(m => new CatalogModel(
                m.GetProperty("id").GetString()!,
                m.GetProperty("name").GetString()!,
                m.GetProperty("filename").GetString()!,
                m.GetProperty("task").GetString()!,
                [.. m.GetProperty("stems").EnumerateArray().Select(s => s.GetString()!)],
                m.TryGetProperty("realtimeFactor", out var factor) && factor.ValueKind == JsonValueKind.Number ? factor.GetDouble() : null)),
        ];
    }
}

/// <summary>
/// Separates with <c>mlx-audio-separator</c> (MLX on the Mac's GPU) as StemMyWav's Mac API did, which this replaces:
/// the song as 44.1 kHz stereo into the model, optionally the vocals through the dereverb model, every stem out as
/// 48 kHz 16-bit WAV, the rate the Logic template and the mixer work at.
/// </summary>
public sealed class MlxStemSeparator(IOptions<VoiceOptions> options, ILogger<MlxStemSeparator> logger) : IStemBackend
{
    /// <summary>The slowest models take three times the song's length, and the first run of one downloads it.</summary>
    private static readonly TimeSpan SeparationTimeout = TimeSpan.FromHours(2);

    private static readonly TimeSpan FfmpegTimeout = TimeSpan.FromMinutes(5);

    public Task<IReadOnlyList<StemModel>?> ListModelsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<StemModel>?>(
            [.. StemCatalog.Models.Select(m => new StemModel(m.Id, m.Name, m.Task, m.Stems, m.RealtimeFactor))]);

    public async Task<IReadOnlyList<string>> ExtractAsync(string flac, string model, bool dereverb, string directory, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var entry = StemCatalog.Find(model) ?? throw new VoiceServiceException($"There is no separation model {model}.", System.Net.HttpStatusCode.BadRequest);
        var ffmpeg = AacEncoder.FindFfmpeg() ?? throw new VoiceServiceException("ffmpeg is not installed.", System.Net.HttpStatusCode.NotImplemented);
        Directory.CreateDirectory(directory);
        var work = Path.Combine(directory, "work");
        Directory.CreateDirectory(work);
        try
        {
            var input = Path.Combine(work, "input.flac");
            await FfmpegAsync(ffmpeg, ["-i", flac, "-vn", "-ac", "2", "-ar", "44100", "-c:a", "flac", input], cancellationToken);

            var separated = Path.Combine(work, "stems");
            await SeparateAsync(settings, input, entry.Filename, separated, cancellationToken);
            var stems = MapStems(entry.Stems, Directory.GetFiles(separated, "*.wav"));

            List<(string Source, string Name)> outputs = [.. entry.Stems.Select(stem => (stems[stem], stem))];
            if (dereverb && stems.TryGetValue("vocals", out var vocals))
            {
                var dry = Path.Combine(work, "dereverb");
                await SeparateAsync(settings, vocals, settings.DereverbModel, dry, cancellationToken);
                var produced = Directory.GetFiles(dry, "*.wav");
                var noReverb = Find(produced, "noreverb") ?? throw new VoiceServiceException("The dereverb model gave no dry vocals.");
                outputs.Add((noReverb, "vocals_dry"));
                if (Find(produced, "reverb") is { } reverb)
                {
                    outputs.Add((reverb, "vocals_reverb"));
                }
            }

            List<string> files = [];
            foreach (var (source, name) in outputs)
            {
                var target = Path.Combine(directory, name + ".wav");
                await FfmpegAsync(ffmpeg, ["-i", source, "-map", "0:a:0", "-ac", "2", "-ar", "48000", "-c:a", "pcm_s16le", target], cancellationToken);
                files.Add(target);
            }
            return files;
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException exception)
            {
                logger.LogWarning(exception, "Could not delete {Directory}", work);
            }
        }
    }

    private async Task SeparateAsync(VoiceOptions settings, string input, string modelFile, string output, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(settings.ResolvedSeparatorModels);
        logger.LogInformation("Separating {Input} with {Model}", Path.GetFileName(input), modelFile);
        var result = await ToolProcess.RunAsync(
            settings.ResolvedSeparator,
            [input, "--model_filename", modelFile, "--output_dir", output, "--output_format", "WAV", "--model_file_dir", settings.ResolvedSeparatorModels],
            SeparationTimeout,
            cancellationToken,
            // The launchd agent's PATH lacks Homebrew, where the separator looks for ffmpeg.
            new Dictionary<string, string> { ["PATH"] = $"{Path.GetDirectoryName(settings.ResolvedSeparator)}:/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin" });
        if (result.ExitCode != 0)
        {
            logger.LogWarning("mlx-audio-separator failed:\n{Output}", string.Join('\n', result.Tail));
            throw new VoiceServiceException($"The separation failed: {ToolProcess.Reason(result, "mlx-audio-separator wrote no stems.")}");
        }
        if (Directory.GetFiles(output, "*.wav").Length == 0)
        {
            throw new VoiceServiceException("mlx-audio-separator wrote no stems.");
        }
    }

    private static async Task FfmpegAsync(string ffmpeg, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var result = await ToolProcess.RunAsync(ffmpeg, ["-nostdin", "-hide_banner", "-loglevel", "error", "-y", .. arguments], FfmpegTimeout, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new VoiceServiceException($"ffmpeg failed: {ToolProcess.Reason(result, "no output")}");
        }
    }

    /// <summary>
    /// Gives each stem of the catalog its file. mlx-audio-separator puts the stem's name in brackets at the end of the
    /// file name, but the model's configuration decides which: the same counterpart is "(Instrumental)" in one and
    /// "(other)" in another. So names match first, and only a single stem left over with a single file is paired;
    /// anything less clear fails rather than deliver a wrongly named stem (StemMyWav's rule).
    /// </summary>
    public static Dictionary<string, string> MapStems(IReadOnlyList<string> expected, string[] produced)
    {
        var stems = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var spare = new List<string>(produced);
        foreach (var stem in expected)
        {
            var hit = spare.FirstOrDefault(path => string.Equals(Label(path), stem, StringComparison.OrdinalIgnoreCase));
            if (hit is null)
            {
                continue;
            }
            stems[stem] = hit;
            spare.Remove(hit);
        }
        var missing = expected.Where(stem => !stems.ContainsKey(stem)).ToList();
        if (missing.Count == 1 && spare.Count == 1)
        {
            stems[missing[0]] = spare[0];
            missing.Clear();
        }
        if (missing.Count > 0)
        {
            throw new VoiceServiceException(
                $"The model made no {string.Join(", ", missing)}; it made {string.Join(", ", produced.Select(p => Label(p) ?? Path.GetFileName(p)))}.");
        }
        return stems;
    }

    /// <summary>The last bracket of the file name; for dereverbed vocals "(noreverb)", after the first run's "(Vocals)".</summary>
    public static string? Label(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var close = name.LastIndexOf(')');
        if (close < 0)
        {
            return null;
        }
        var open = name.LastIndexOf('(', close);
        return open < 0 ? null : name[(open + 1)..close];
    }

    private static string? Find(string[] files, string label) =>
        files.SingleOrDefault(path => string.Equals(Label(path), label, StringComparison.OrdinalIgnoreCase));
}
