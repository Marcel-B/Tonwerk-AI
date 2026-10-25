using System.Text.Json;
using System.Text.Json.Nodes;
using YueUI.Api.Ace;
using YueUI.Api.Images;
using YueUI.Api.Lyrics;
using YueUI.Api.Singing;
using YueUI.Api.Speech;
using YueUI.Api.Voices;

namespace YueUI.Api.Machines;

/// <summary>
/// The engines of a helper, behind the same interfaces as this Mac's own, so a job runs the same code whichever Mac
/// it was placed on (<see cref="Machines.PlaceAsync"/>). Only the running is remote: what the pages ask (installed,
/// downloaded) is this Mac's own business and answered by its own engines.
/// </summary>
public sealed class RemoteEngines(HelperClient client, Machines machines)
{
    public ISpeechEngine Speech(HelperMachine helper) => new RemoteSpeech(client, helper);

    public IImageEngine Images(HelperMachine helper) => new RemoteImages(client, helper);

    public ISingingEngine Singing(HelperMachine helper) => new RemoteSinging(client, helper);

    public IAceEngine Ace(HelperMachine helper) => new RemoteAce(client, helper, machines);

    public IStemBackend Stems(HelperMachine helper) => new RemoteStems(client, helper);

    /// <summary>Seed-VC on the helper with the reference voice sent along, since the helper keeps no voices of this Mac.</summary>
    public async Task ConvertAsync(
        HelperMachine helper,
        VoiceConversion conversion,
        byte[] reference,
        string vocals,
        string output,
        VoiceProgress progress,
        CancellationToken cancellationToken)
    {
        var referenceFile = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, $"reference-{Guid.NewGuid():N}.wav");
        await File.WriteAllBytesAsync(referenceFile, reference, cancellationToken);
        try
        {
            await client.RunAsync(
                helper,
                Engines.Voices,
                new VoiceHelperJob(conversion),
                new Dictionary<string, string> { ["vocals"] = vocals, ["reference"] = referenceFile },
                new Dictionary<string, string> { ["converted.wav"] = output },
                null,
                read =>
                {
                    if (read.Type == "progress")
                    {
                        progress(read.Share ?? 0, read.Data?.GetValue<double>() ?? 0);
                    }
                },
                cancellationToken);
        }
        finally
        {
            File.Delete(referenceFile);
        }
    }

    /// <summary>A draft by the helper's LM Studio.</summary>
    public async Task<string> WriteLyricsAsync(HelperMachine helper, LyricsHelperJob job, CancellationToken cancellationToken)
    {
        var outcome = await client.RunAsync(helper, Engines.Lyrics, job, new Dictionary<string, string>(), null, null, null, cancellationToken);
        return outcome.Result?["lyrics"]?.GetValue<string>() is { Length: > 0 } lyrics
            ? lyrics
            : throw new HelperException($"{helper.Label} returned no lyrics.");
    }

    /// <summary>The helper's LM Studio models (no Claude: that runs here whichever Mac is chosen).</summary>
    public async Task<LyricsModels> LyricsModelsAsync(HelperMachine helper, CancellationToken cancellationToken) =>
        await client.GetJsonAsync<LyricsModels>(helper, "api/helper/lyrics/models", cancellationToken)
            ?? throw new HelperException($"{helper.Label} listed no lyrics models.");

    private static T? Read<T>(JsonNode? node) => node is null ? default : node.Deserialize<T>(HelperClient.Json);

    private sealed class RemoteSpeech(HelperClient client, HelperMachine helper) : ISpeechEngine
    {
        public bool Installed => true;

        public bool CanPrepareVoices => false;

        public bool Downloaded(SpeechModel model) => true;

        public Task PrepareVoiceAsync(string input, string output, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task PrepareRecordingAsync(string input, string output, CancellationToken cancellationToken) => throw new NotSupportedException();

        public async Task<SpeechResult> SpeakAsync(SpeechJob job, Action<string> stage, CancellationToken cancellationToken)
        {
            Dictionary<string, string> inputs = [];
            if (job.RefAudio is not null)
            {
                inputs["voice"] = job.RefAudio;
            }
            var outcome = await client.RunAsync(
                helper,
                Engines.Speech,
                new SpeechHelperJob(job.Model.Id, Clean: false, job.Text, job.RefText),
                inputs,
                new Dictionary<string, string> { ["take.wav"] = job.Output },
                null,
                read => Staged(read, stage),
                cancellationToken);
            return Read<SpeechResult>(outcome.Result) ?? new SpeechResult(null, null, null);
        }

        public async Task<SpeechResult> CleanAsync(CleanJob job, Action<string> stage, Action<double> progress, CancellationToken cancellationToken)
        {
            var outcome = await client.RunAsync(
                helper,
                Engines.Speech,
                new SpeechHelperJob(job.Model.Id, Clean: true, Seconds: job.Seconds),
                new Dictionary<string, string> { ["source"] = job.Input },
                new Dictionary<string, string> { ["take.wav"] = job.Output },
                null,
                read =>
                {
                    Staged(read, stage);
                    if (read is { Type: "progress", Share: { } share })
                    {
                        progress(share);
                    }
                },
                cancellationToken);
            return Read<SpeechResult>(outcome.Result) ?? new SpeechResult(null, null, null);
        }
    }

    private sealed class RemoteImages(HelperClient client, HelperMachine helper) : IImageEngine
    {
        public bool Installed => true;

        public bool TokenFound => true;

        public bool Downloaded(ImageModel model) => true;

        /// <summary>The helper says so itself when it refuses the run.</summary>
        public string? Unavailable(ImageModel model) => null;

        public async Task<ImageResult> PaintAsync(ImageJob job, Action<string, double> progress, CancellationToken cancellationToken)
        {
            var outcome = await client.RunAsync(
                helper,
                Engines.Images,
                new ImageHelperJob(
                    job.Model.Id,
                    job.Prompt,
                    job.Seed,
                    job.Width,
                    job.Height,
                    [.. (job.Loras ?? []).Select(l => l.Strength)],
                    [.. (job.References ?? []).Select(r => new ImageHelperReference(r.Role, r.Strength))]),
                // The helper has its own LoRA folder; the files go along so a LoRA added here paints there too.
                (job.Loras ?? []).Select((l, index) => (Key: $"lora{index}", l.Path))
                    .Concat((job.References ?? []).Select((r, index) => (Key: $"ref{index}", r.Path)))
                    .ToDictionary(p => p.Key, p => p.Path),
                new Dictionary<string, string> { ["image.jpg"] = job.Output },
                null,
                read =>
                {
                    if (read is { Type: "stage" or "progress" })
                    {
                        progress(read.Stage ?? "painting", read.Share ?? 0);
                    }
                },
                cancellationToken);
            return Read<ImageResult>(outcome.Result) ?? new ImageResult(null, null, null);
        }
    }

    private sealed class RemoteSinging(HelperClient client, HelperMachine helper) : ISingingEngine
    {
        public bool Installed => true;

        public bool ModelDownloaded => true;

        public async Task<SingingResult> SingAsync(
            SingingJob job,
            Action<string> stage,
            Action<double> progress,
            Action<SingingAlignment> aligned,
            CancellationToken cancellationToken)
        {
            var outcome = await client.RunAsync(
                helper,
                Engines.Singing,
                new SingingHelperJob(job.Notes, job.Lyrics, job.Voice.Id, job.PitchShift, job.AutoShift),
                // A voice of one's own lives only here; it goes along, the helper's configured ones are found by id.
                job.Voice.Own
                    ? new Dictionary<string, string> { ["voiceAudio"] = job.Voice.Audio, ["voiceMetadata"] = job.Voice.Metadata }
                    : new Dictionary<string, string>(),
                new Dictionary<string, string> { ["take.wav"] = job.Output },
                null,
                read =>
                {
                    Staged(read, stage);
                    if (read is { Type: "progress", Share: { } share })
                    {
                        progress(share);
                    }
                    if (read is { Type: "event", Name: "aligned" } && Read<SingingAlignment>(read.Data) is { } alignment)
                    {
                        aligned(alignment);
                    }
                },
                cancellationToken);
            return Read<SingingResult>(outcome.Result) ?? new SingingResult(null, null, null);
        }

        /// <summary>The editor's alignment loads only g2p_en and runs on this Mac (<c>POST /api/singing/align</c>), never here.</summary>
        public Task<SingingAligned> AlignAsync(IReadOnlyList<SungNote> notes, string? lyrics, CancellationToken cancellationToken) =>
            throw new SingingException("Alignment runs on this Mac.", StatusCodes.Status501NotImplemented);

        /// <summary>Like the alignment, a voice is made on this Mac (<c>POST /api/singing/voices</c>).</summary>
        public Task<SingingVoiceMade> MakeVoiceAsync(string recording, string lyrics, string audio, string metadata, CancellationToken cancellationToken) =>
            throw new SingingException("Voices are made on this Mac.", StatusCodes.Status501NotImplemented);
    }

    private sealed class RemoteAce(HelperClient client, HelperMachine helper, Machines machines) : IAceEngine
    {
        public bool Installed => true;

        /// <summary>The helper's files as last polled: the job names the models it has.</summary>
        public AceModelFiles Files() => machines.StatusOf(helper)?.Ace ?? new AceModelFiles([], [], false, false, []);

        public bool Understands => false;

        public Task<AceHeard> UnderstandAsync(string audio, string workDirectory, string lmModel, string ditModel, CancellationToken cancellationToken) =>
            throw new AceException("Recordings are heard on this Mac.", StatusCodes.Status501NotImplemented);

        public async Task<IReadOnlyList<AceOutput>> GenerateAsync(AceRun run, Action<string, double> progress, CancellationToken cancellationToken)
        {
            Dictionary<string, string> inputs = [];
            if (run.SourceAudio is not null)
            {
                inputs["source"] = run.SourceAudio;
            }
            var directory = Path.Combine(run.WorkDirectory, "remote");
            var outcome = await client.RunAsync(
                helper,
                Engines.Ace,
                new AceHelperJob(run.Request, run.UsesLm),
                inputs,
                null,
                directory,
                read =>
                {
                    if (read is { Type: "stage" or "progress" })
                    {
                        progress(read.Stage ?? "synthesizing", read.Share ?? 0);
                    }
                },
                cancellationToken);
            var requests = outcome.Result?["requests"]?.AsArray() ?? [];
            if (requests.Count != outcome.Files.Count)
            {
                throw new HelperException($"{helper.Label} sent {outcome.Files.Count} songs for {requests.Count} requests.");
            }
            return [.. outcome.Files.Select((wave, index) => new AceOutput(wave, requests[index]!.DeepClone().AsObject()))];
        }
    }

    private sealed class RemoteStems(HelperClient client, HelperMachine helper) : IStemBackend
    {
        public async Task<IReadOnlyList<string>> ExtractAsync(string flac, string model, bool dereverb, string directory, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(directory);
            var outcome = await client.RunAsync(
                helper,
                Engines.Stems,
                new StemsHelperJob(model, dereverb),
                new Dictionary<string, string> { ["audio"] = flac },
                null,
                directory,
                null,
                cancellationToken);
            return outcome.Files;
        }

        public Task<IReadOnlyList<StemModel>?> ListModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<StemModel>?>(null);
    }

    private static void Staged(HelperEvent read, Action<string> stage)
    {
        if (read is { Type: "stage", Stage: { } name })
        {
            stage(name);
        }
    }
}
