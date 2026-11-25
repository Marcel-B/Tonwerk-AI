using Microsoft.Extensions.Options;
using YueUI.Api.Ace;
using YueUI.Api.Singing;
using YueUI.Api.Speech;
using YueUI.Api.Storage;
using YueUI.Api.Timing;
using YueUI.Api.Video;
using YueUI.Api.Voices;
using YueUI.Api.Worker;

namespace YueUI.Api.Lyrics;

/// <summary>A model Tonwerk loads itself, past the gate, as <c>PUT /gate/apps/{app}</c> lists it in <c>ownModels</c>.</summary>
/// <param name="Name">Unique among Tonwerk's: the kind and the Hugging Face repository, or the kind and a short name.</param>
/// <param name="Path">The folder on this Mac: a Hugging Face cache of one repository (<c>models--user--repo</c>) or a plain folder of its files.</param>
/// <param name="Source">The Hugging Face repository the gate checks for updates; null for models from elsewhere.</param>
public sealed record LlmGateOwnModel(string Name, string Path, string? Label = null, string? Kind = null, string? Source = null, double? SizeGb = null, double? RamGb = null);

/// <summary>
/// Tonwerk's models outside LM Studio, so the gate knows every model on the Mac: YuE2 and what it needs, ACE-Step, the
/// speech lab's, SoulX-Singer, the separator's, Seed-VC's, Whisper for lyrics timing and the photo videos' CLIP. The
/// gate shows them under Tonwerk, never loads or unloads them and checks those with a Hugging Face repository for
/// updates. Pictures are not here: img-o-mat lists its own.
/// </summary>
public sealed class LlmGateOwnModels(
    YuePaths paths,
    IOptions<SpeechOptions> speech,
    IOptions<AceOptions> ace,
    IOptions<SingingOptions> singing,
    IOptions<VoiceOptions> voices,
    IOptions<TimingOptions> timing,
    IOptions<MontageOptions> montage)
{
    /// <summary>Where acestep.cpp's quantized models come from (<c>deploy/install-ace.sh</c>).</summary>
    public const string AceRepository = "Serveurperso/ACE-Step-1.5-GGUF";

    /// <summary>Where SoulX-Singer's weights come from (<c>deploy/install-singing.sh</c>).</summary>
    public const string SingingRepository = "Soul-AILab/SoulX-Singer";

    /// <summary>The models that are there, each with its size; reads only the file system.</summary>
    public IReadOnlyList<LlmGateOwnModel> List()
    {
        var models = new List<LlmGateOwnModel>();
        models.AddRange(Cached("music", paths.ModelCache));
        if (Plain("music", "ace-step", "ACE-Step 1.5", ace.Value.ResolvedModels, AceRepository) is { } aceModel)
        {
            models.Add(aceModel);
        }
        models.AddRange(Cached("speech", speech.Value.ResolvedModelCache));
        var singingFolder = System.IO.Path.GetDirectoryName(singing.Value.ResolvedModel);
        if (singingFolder is not null && Plain("singing", "soulx-singer", "SoulX-Singer", singingFolder, SingingRepository) is { } singer)
        {
            models.Add(singer);
        }
        if (Plain("stems", "separator", "Roformer separation", voices.Value.ResolvedSeparatorModels, null) is { } separator)
        {
            models.Add(separator);
        }
        models.AddRange(Cached("voices", voices.Value.ResolvedSeedVcModels));
        models.AddRange(Cached("transcription", timing.Value.ResolvedModelCache));
        models.AddRange(Cached("video", montage.Value.ResolvedModelCache));
        // One name per model: the same repository may lie in two caches.
        return [.. models.GroupBy(model => model.Name, StringComparer.Ordinal).Select(group => group.First())];
    }

    /// <summary>
    /// Every complete repository in a Hugging Face cache (<c>HF_HOME</c>: the repositories lie in its <c>hub</c>, or
    /// <paramref name="root"/> is the hub itself).
    /// </summary>
    private static IEnumerable<LlmGateOwnModel> Cached(string kind, string root)
    {
        var hub = Directory.Exists(System.IO.Path.Combine(root, "hub")) ? System.IO.Path.Combine(root, "hub") : root;
        if (!Directory.Exists(hub))
        {
            yield break;
        }
        IEnumerable<string> folders;
        try
        {
            folders = [.. Directory.EnumerateDirectories(hub, "models--*").Order(StringComparer.Ordinal)];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (var folder in folders)
        {
            // models--user--repo; Hugging Face writes the slash as "--", user names have single hyphens at most.
            var parts = System.IO.Path.GetFileName(folder)["models--".Length..].Split("--", 2);
            if (parts.Length != 2 || !File.Exists(System.IO.Path.Combine(folder, "refs", "main")))
            {
                continue;
            }
            var repository = $"{parts[0]}/{parts[1]}";
            yield return new LlmGateOwnModel($"{kind}/{repository}", System.IO.Path.GetFullPath(folder), parts[1], kind, repository, SizeOf(folder));
        }
    }

    /// <summary>A folder of model files, if any are there.</summary>
    private static LlmGateOwnModel? Plain(string kind, string name, string label, string folder, string? source)
    {
        var size = SizeOf(folder);
        return size is null ? null : new LlmGateOwnModel($"{kind}/{name}", System.IO.Path.GetFullPath(folder), label, kind, source, size);
    }

    private static double? SizeOf(string folder)
    {
        var (bytes, files) = StorageScanner.Measure(folder);
        return files == 0 ? null : Math.Round(bytes / 1e9, 2);
    }
}
