using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using YueUI.Api.Data;
using YueUI.Api.Share;

namespace YueUI.Api.Voices;

/// <summary>A reference voice's recording.</summary>
public sealed record VoiceAudio(byte[] Audio, string ContentType);

/// <summary>How far a conversion is: the fraction of the expected time gone (1 when it ran out) and that time in seconds.</summary>
public delegate void VoiceProgress(double fraction, double estimatedSeconds);

/// <summary>What keeps the reference voices and sings with them: this server (<see cref="SeedVcVoices"/>) or ChangeMyVoice (<see cref="VoiceClient"/>).</summary>
public interface IVoiceBackend
{
    Task<IReadOnlyList<ReferenceVoice>> ListVoicesAsync(CancellationToken cancellationToken);

    /// <param name="startSeconds">Where the part to keep begins, null for the beginning.</param>
    /// <param name="endSeconds">Where it ends, null for the end; at most 25 seconds from the start are kept.</param>
    Task<ReferenceVoice> AddVoiceAsync(string label, Stream audio, string fileName, double? startSeconds, double? endSeconds, CancellationToken cancellationToken);

    Task<VoiceAudio> VoiceAudioAsync(string id, CancellationToken cancellationToken);

    Task DeleteVoiceAsync(string id, CancellationToken cancellationToken);

    /// <summary>Sings <paramref name="vocals"/> with the conversion's voice and writes the result as a 48 kHz WAV to <paramref name="output"/>.</summary>
    Task ConvertAsync(VoiceConversion version, string vocals, string output, VoiceProgress progress, CancellationToken cancellationToken);
}

/// <summary>
/// Converts with this server's own Seed-VC where it is installed (<see cref="VoiceOptions.LocalVoices"/>), else
/// through ChangeMyVoice's API. On the first use of its own, it takes ChangeMyVoice's voices over with their ids, so
/// versions keep naming the voice they were sung with.
/// </summary>
public sealed class VoiceEngine(
    SeedVcVoices local,
    VoiceClient service,
    SqliteReferenceVoiceStore store,
    IOptions<VoiceOptions> options,
    ILogger<VoiceEngine> logger) : IVoiceBackend
{
    private readonly SemaphoreSlim _import = new(1, 1);

    private IVoiceBackend Backend => options.Value.LocalVoices ? local : service;

    public async Task<IReadOnlyList<ReferenceVoice>> ListVoicesAsync(CancellationToken cancellationToken)
    {
        if (options.Value.LocalVoices)
        {
            await ImportAsync(cancellationToken);
        }
        return await Backend.ListVoicesAsync(cancellationToken);
    }

    public Task<ReferenceVoice> AddVoiceAsync(string label, Stream audio, string fileName, double? startSeconds, double? endSeconds, CancellationToken cancellationToken) =>
        Backend.AddVoiceAsync(label, audio, fileName, startSeconds, endSeconds, cancellationToken);

    public Task<VoiceAudio> VoiceAudioAsync(string id, CancellationToken cancellationToken) => Backend.VoiceAudioAsync(id, cancellationToken);

    public Task DeleteVoiceAsync(string id, CancellationToken cancellationToken) => Backend.DeleteVoiceAsync(id, cancellationToken);

    public async Task ConvertAsync(VoiceConversion version, string vocals, string output, VoiceProgress progress, CancellationToken cancellationToken)
    {
        if (options.Value.LocalVoices)
        {
            await ImportAsync(cancellationToken);
        }
        await Backend.ConvertAsync(version, vocals, output, progress, cancellationToken);
    }

    /// <summary>
    /// Copies ChangeMyVoice's voices once, if it is still configured. A marker file says it is done, so voices deleted
    /// here afterwards do not come back; if ChangeMyVoice does not answer, the next listing tries again.
    /// </summary>
    private async Task ImportAsync(CancellationToken cancellationToken)
    {
        var marker = Path.Combine(Path.GetDirectoryName(store.FilePath("x"))!, ".imported");
        if (!options.Value.VoiceServiceConfigured || File.Exists(marker))
        {
            return;
        }
        await _import.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(marker))
            {
                return;
            }
            var known = store.All().Select(v => v.Id).ToHashSet();
            var voices = await service.ListVoicesAsync(cancellationToken);
            foreach (var voice in voices.Where(v => v.Id.Length > 0 && !known.Contains(v.Id)))
            {
                // Ids name files here; ChangeMyVoice's are GUIDs, anything else stays behind.
                if (voice.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
                {
                    continue;
                }
                var audio = await service.VoiceAudioAsync(voice.Id, cancellationToken);
                var path = store.FilePath(voice.Id);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, audio.Audio, cancellationToken);
                store.Add(voice);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            await File.WriteAllTextAsync(marker, $"{voices.Count} voices from ChangeMyVoice, {DateTimeOffset.UtcNow:O}\n", cancellationToken);
            logger.LogInformation("Took {Count} reference voices over from ChangeMyVoice", voices.Count);
        }
        catch (VoiceServiceException exception)
        {
            logger.LogWarning("Could not take the voices over from ChangeMyVoice yet: {Message}", exception.Message);
        }
        finally
        {
            _import.Release();
        }
    }
}

/// <summary>
/// Reference voices kept by this server and songs sung with them through Seed-VC (github.com/Plachtaa/seed-vc) in its
/// own Python environment, run by <c>yueui_seedvc.py</c> (ChangeMyVoice's script) once per conversion, so the model
/// leaves the memory with the process. The singing path: F0 conditioning, 44.1 kHz in and out, then 48 kHz for the mix.
/// </summary>
public sealed class SeedVcVoices(
    SqliteReferenceVoiceStore store,
    IOptions<VoiceOptions> options,
    TimeProvider time,
    ILogger<SeedVcVoices> logger) : IVoiceBackend
{
    /// <summary>ChangeMyVoice's limits: shorter says too little about the voice, Seed-VC uses no more than 25 seconds.</summary>
    public const double MinVoiceSeconds = 3;

    public const double MaxVoiceSeconds = 25;

    private static readonly TimeSpan FfmpegTimeout = TimeSpan.FromMinutes(5);

    public static string Script => Path.Combine(AppContext.BaseDirectory, "Voices", "yueui_seedvc.py");

    public Task<IReadOnlyList<ReferenceVoice>> ListVoicesAsync(CancellationToken cancellationToken) => Task.FromResult(store.All());

    public async Task<ReferenceVoice> AddVoiceAsync(
        string label, Stream audio, string fileName, double? startSeconds, double? endSeconds, CancellationToken cancellationToken)
    {
        var ffmpeg = Ffmpeg();
        var id = Guid.NewGuid().ToString();
        var upload = Path.Combine(Path.GetTempPath(), $"yueui-voice-upload-{id}{Path.GetExtension(fileName)}");
        var target = store.FilePath(id);
        try
        {
            await using (var file = File.Create(upload))
            {
                await audio.CopyToAsync(file, cancellationToken);
            }
            var start = startSeconds ?? 0;
            var length = Math.Min(MaxVoiceSeconds, (endSeconds ?? double.MaxValue) - start);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var result = await ToolProcess.RunAsync(
                ffmpeg,
                ["-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-ss", Seconds(start), "-i", upload, "-t", Seconds(length),
                    "-map", "a:0", "-vn", "-ac", "1", "-ar", "44100", "-c:a", "pcm_s16le", target],
                FfmpegTimeout,
                cancellationToken);
            if (result.ExitCode != 0 || !File.Exists(target))
            {
                throw new VoiceServiceException($"The recording could not be read: {ToolProcess.Reason(result, "no audio")}", System.Net.HttpStatusCode.UnprocessableEntity);
            }
            var seconds = Speech.WavInfo.Seconds(target) ?? 0;
            if (seconds < MinVoiceSeconds)
            {
                File.Delete(target);
                throw new VoiceServiceException($"The voice needs at least {MinVoiceSeconds:0} seconds of singing; this has {seconds:0.#}.", System.Net.HttpStatusCode.UnprocessableEntity);
            }
            var voice = new ReferenceVoice(id, label, Math.Round(seconds, 2), time.GetUtcNow());
            store.Add(voice);
            return voice;
        }
        finally
        {
            File.Delete(upload);
        }
    }

    public async Task<VoiceAudio> VoiceAudioAsync(string id, CancellationToken cancellationToken)
    {
        if (store.Get(id) is null || !File.Exists(store.FilePath(id)))
        {
            throw new VoiceServiceException("There is no such reference voice.", System.Net.HttpStatusCode.NotFound);
        }
        return new VoiceAudio(await File.ReadAllBytesAsync(store.FilePath(id), cancellationToken), "audio/wav");
    }

    public Task DeleteVoiceAsync(string id, CancellationToken cancellationToken)
    {
        if (!store.Remove(id))
        {
            throw new VoiceServiceException("There is no such reference voice.", System.Net.HttpStatusCode.NotFound);
        }
        return Task.CompletedTask;
    }

    public Task ConvertAsync(VoiceConversion version, string vocals, string output, VoiceProgress progress, CancellationToken cancellationToken)
    {
        var reference = store.FilePath(version.VoiceId);
        if (store.Get(version.VoiceId) is null || !File.Exists(reference))
        {
            throw new VoiceServiceException("The reference voice is gone.");
        }
        return ConvertAsync(version, reference, vocals, output, progress, cancellationToken);
    }

    /// <summary>With a reference voice that is not in this server's collection: another Mac's, sent with a job (Machines/).</summary>
    public async Task ConvertAsync(VoiceConversion version, string reference, string vocals, string output, VoiceProgress progress, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var ffmpeg = Ffmpeg();
        var work = Path.Combine(Path.GetDirectoryName(output)!, "seed-vc");
        Directory.CreateDirectory(work);
        var source = Path.Combine(work, "source.wav");
        var converted = Path.Combine(work, "converted.wav");
        // Seed-VC's singing path reads 44.1 kHz mono; converting here means it resamples nothing itself.
        await FfmpegAsync(ffmpeg, ["-i", vocals, "-map", "a:0", "-ac", "1", "-ar", "44100", "-c:a", "pcm_s16le", source], cancellationToken);

        List<string> arguments =
        [
            "-u", Script, "--source", source, "--reference", reference, "--output", converted,
            "--diffusion-steps", version.DiffusionSteps.ToString(CultureInfo.InvariantCulture),
            "--inference-cfg-rate", version.Strength.ToString("0.###", CultureInfo.InvariantCulture),
            "--f0-condition",
        ];
        if (version.SemiToneShift != 0)
        {
            arguments.AddRange(["--semi-tone-shift", version.SemiToneShift.ToString(CultureInfo.InvariantCulture)]);
        }
        var estimate = EstimatedSeconds(Speech.WavInfo.Seconds(source) ?? 0, version.DiffusionSteps);
        var started = time.GetUtcNow();
        using var ticking = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ticker = TickAsync();
        logger.LogInformation("Converting {Subject} with the voice {Voice}, expected {Seconds:0} s", version.Subject, version.VoiceLabel, estimate);
        ToolResult result;
        try
        {
            result = await ToolProcess.RunAsync(
                settings.ResolvedSeedVcPython,
                arguments,
                settings.ConversionTimeout,
                cancellationToken,
                new Dictionary<string, string>
                {
                    ["SEED_VC_PATH"] = settings.ResolvedSeedVcPath,
                    ["HF_HUB_CACHE"] = settings.ResolvedSeedVcModels,
                    ["HF_HUB_DISABLE_TELEMETRY"] = "1",
                    ["PYTHONUNBUFFERED"] = "1",
                    ["PATH"] = "/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin",
                },
                // Seed-VC's inference.py loads its configs by relative path.
                settings.ResolvedSeedVcPath);
        }
        finally
        {
            await ticking.CancelAsync();
            await ticker;
        }
        // The script answers one JSON line: {"status": "ok", …} or {"status": "error", "code", "message"}.
        var answer = result.Output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('{'));
        var parsed = answer is null ? null : JsonNode.Parse(answer);
        if ((string?)parsed?["status"] != "ok" || !File.Exists(converted))
        {
            logger.LogWarning("Seed-VC failed:\n{Output}", string.Join('\n', result.Tail));
            throw new VoiceServiceException((string?)parsed?["message"] ?? ToolProcess.Reason(result, "Seed-VC wrote no audio."));
        }
        logger.LogInformation("Seed-VC loaded in {Load} ms and converted in {Inference} ms", (long?)parsed?["modelLoadMs"], (long?)parsed?["inferenceMs"]);
        await FfmpegAsync(ffmpeg, ["-i", converted, "-ar", "48000", "-c:a", "pcm_s16le", output], cancellationToken);
        Directory.Delete(work, recursive: true);

        async Task TickAsync()
        {
            try
            {
                while (true)
                {
                    progress(estimate > 0 ? (time.GetUtcNow() - started).TotalSeconds / estimate : 0, estimate);
                    await Task.Delay(options.Value.PollInterval, time, ticking.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>
    /// ChangeMyVoice's estimate on Apple Silicon (its <c>ConversionDurationEstimate</c>): 4.55 s per second of
    /// material plus a quadratic part, measured at 50 steps, which go in about linearly.
    /// </summary>
    public static double EstimatedSeconds(double materialSeconds, int diffusionSteps) =>
        materialSeconds <= 0 ? 0 : Math.Round(((4.55 * materialSeconds) + (0.0211 * materialSeconds * materialSeconds)) * diffusionSteps / 50.0);

    private static string Ffmpeg() =>
        AacEncoder.FindFfmpeg() ?? throw new VoiceServiceException("ffmpeg is not installed.", System.Net.HttpStatusCode.NotImplemented);

    private static string Seconds(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static async Task FfmpegAsync(string ffmpeg, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var result = await ToolProcess.RunAsync(ffmpeg, ["-nostdin", "-hide_banner", "-loglevel", "error", "-y", .. arguments], FfmpegTimeout, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new VoiceServiceException($"ffmpeg failed: {ToolProcess.Reason(result, "no output")}");
        }
    }
}
