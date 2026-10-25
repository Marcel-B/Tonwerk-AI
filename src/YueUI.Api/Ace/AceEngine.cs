using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace YueUI.Api.Ace;

/// <param name="WorkDirectory">An empty folder of the job's own, where the request files and the WAVs go.</param>
/// <param name="Request">acestep.cpp's request (<see cref="AceRequestJson.Build"/>).</param>
/// <param name="UsesLm">Run <c>ace-lm</c> first (text2music); cover and repaint go to <c>ace-synth</c> directly.</param>
/// <param name="SourceAudio">A WAV of the library song a cover or repaint starts from.</param>
public sealed record AceRun(string WorkDirectory, JsonObject Request, bool UsesLm, string? SourceAudio);

/// <param name="Wave">The song as a 48 kHz stereo WAV.</param>
/// <param name="Request">The request the song was synthesized from: for text2music the LM's, with its lyrics, tempo, key and so on filled in.</param>
public sealed record AceOutput(string Wave, JsonObject Request);

public sealed class AceException(string message, int status = StatusCodes.Status500InternalServerError) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>Makes songs with ACE-Step. Tests replace it; the real one runs acestep.cpp's command line tools.</summary>
public interface IAceEngine
{
    /// <summary>The GGUF files in the models folder by kind, and the adapters.</summary>
    AceModelFiles Files();

    bool Installed { get; }

    /// <param name="progress">Hears the stage (loading, writing, composing, synthesizing, decoding) and the share of the job done.</param>
    Task<IReadOnlyList<AceOutput>> GenerateAsync(AceRun run, Action<string, double> progress, CancellationToken cancellationToken);

    /// <summary>Whether <see cref="UnderstandAsync"/> can run: acestep.cpp's <c>ace-understand</c> is built.</summary>
    bool Understands { get; }

    /// <summary>
    /// Has ACE-Step's LM hear a recording (VAE, tokenizer, LM backwards) and say what it is: caption, lyrics, tempo,
    /// key, metre, language, length, as it would write them for a song of its own.
    /// </summary>
    /// <param name="audio">A WAV of the recording.</param>
    /// <param name="workDirectory">An empty folder for the request and the answer.</param>
    /// <param name="lmModel">The LM file to hear with; it must be in <see cref="Files"/>.</param>
    Task<AceHeard> UnderstandAsync(string audio, string workDirectory, string lmModel, string ditModel, CancellationToken cancellationToken);
}

/// <summary>What ACE-Step heard in a recording, in the page's terms; null where it could not tell.</summary>
public sealed record AceHeard(
    string? Caption,
    string? Lyrics,
    int? Bpm,
    string? Key,
    int? TimeSignature,
    string? Language,
    double? Duration,
    bool Instrumental)
{
    /// <summary>acestep.cpp's request JSON as <c>ace-understand</c> writes it.</summary>
    public static AceHeard From(JsonObject written)
    {
        static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text)
            && text.Trim() is { Length: > 0 } trimmed ? trimmed : null;
        static double? Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<double>(out var number)
            && double.IsFinite(number) && number > 0 ? number : null;

        var lyrics = Text(written["lyrics"]);
        var language = Text(written["vocal_language"]);
        var beats = Text(written["timesignature"]) is { } metre
            && int.TryParse(metre.Split('/')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) ? b : (int?)null;
        return new AceHeard(
            Text(written["caption"]),
            // The LM says "[Instrumental]" for music without a voice, which the form has a switch for.
            lyrics == AceRequestJson.Instrumental ? null : lyrics,
            Number(written["bpm"]) is { } bpm ? (int)Math.Round(bpm) : null,
            Text(written["keyscale"]),
            beats is { } n && AceSongRequest.TimeSignatures.Contains(n) ? n : null,
            language is not null && AceSongRequest.Languages.Contains(language) ? language : null,
            Number(written["duration"]) is { } seconds ? Math.Round(seconds) : null,
            lyrics == AceRequestJson.Instrumental);
    }
}

/// <param name="Lm">The 5 Hz language models (<c>acestep-5Hz-lm-*.gguf</c>), the largest first.</param>
/// <param name="Dit">The diffusion transformers (<c>acestep-v15-*.gguf</c>), turbo first.</param>
public sealed record AceModelFiles(
    IReadOnlyList<string> Lm,
    IReadOnlyList<string> Dit,
    bool TextEncoder,
    bool Vae,
    IReadOnlyList<string> Adapters)
{
    public bool Complete => Lm.Count > 0 && Dit.Count > 0 && TextEncoder && Vae;

    /// <summary>
    /// Sorts the folder's GGUF files by their names as Serveurperso/ACE-Step-1.5-GGUF publishes them. acestep.cpp itself
    /// reads each header's architecture; going by name needs no GGUF reader here and only misses renamed files.
    /// </summary>
    public static AceModelFiles Scan(string models, string adapters)
    {
        var files = Directory.Exists(models)
            ? Directory.EnumerateFiles(models, "*.gguf").Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal).ToList()
            : [];
        var lm = files.Where(f => f.StartsWith("acestep-5Hz-lm-", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(LmSize).ThenBy(f => f, StringComparer.Ordinal).ToList();
        var dit = files.Where(f => f.StartsWith("acestep-v15-", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Contains("turbo", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(f => f.Contains("-xl-", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(f => f, StringComparer.Ordinal).ToList();
        List<string> loras = [];
        if (Directory.Exists(adapters))
        {
            loras.AddRange(Directory.EnumerateFiles(adapters, "*.safetensors").Select(f => Path.GetFileName(f)));
            // A PEFT adapter is a folder with its weights and config.
            loras.AddRange(Directory.EnumerateDirectories(adapters)
                .Where(d => File.Exists(Path.Combine(d, "adapter_model.safetensors")))
                .Select(d => Path.GetFileName(d)));
            loras.Sort(StringComparer.Ordinal);
        }
        return new AceModelFiles(
            lm,
            dit,
            files.Any(f => f.StartsWith("Qwen3-Embedding", StringComparison.OrdinalIgnoreCase)),
            files.Any(f => f.StartsWith("vae", StringComparison.OrdinalIgnoreCase)),
            loras);
    }

    /// <summary>"acestep-5Hz-lm-4B-Q8_0.gguf" → 4, so the 4B is the default: the 0.6B and 1.7B write weaker songs.</summary>
    private static double LmSize(string file) =>
        Regex.Match(file, @"-lm-(\d+(?:\.\d+)?)B", RegexOptions.IgnoreCase) is { Success: true } match
            ? double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)
            : 0;
}

/// <summary>
/// Runs acestep.cpp's <c>ace-lm</c> (lyrics, metadata and audio codes) and <c>ace-synth</c> (DiT and VAE), one process
/// each per job, like the speech lab: the models leave the memory with the process, since on 24 GB they must not stay
/// beside YuE2. acestep.cpp also has a server; its jobs and model swapping would be a second queue beside ours.
/// </summary>
/// <remarks>
/// Progress is read from what the tools print on stderr: <c>[LM-Phase1] Step n</c> while the LM writes lyrics and
/// metadata, <c>[LM-Phase2] max_tokens: m</c> and every 50 steps <c>… k total codes</c> while it writes the 5 Hz audio
/// codes, <c>[DiT] Step i/n</c> per diffusion step, <c>[VAE-Decode BatchN] Decode</c> per finished song. A line with
/// <c>FATAL</c> or <c>ERROR</c> becomes the job's message.
/// </remarks>
public sealed partial class AceCppEngine(IOptions<AceOptions> options, ILogger<AceCppEngine> logger) : IAceEngine
{
    private const int TailLines = 40;

    /// <summary>Of the job, what the LM takes when it runs; the DiT and the VAE share the rest.</summary>
    private const double LmShare = 0.55;

    public bool Installed => File.Exists(Tool("ace-lm")) && File.Exists(Tool("ace-synth"));

    public bool Understands => File.Exists(Tool("ace-understand"));

    public async Task<AceHeard> UnderstandAsync(string audio, string workDirectory, string lmModel, string ditModel, CancellationToken cancellationToken)
    {
        if (!Understands)
        {
            throw new AceException(
                $"acestep.cpp's ace-understand is not built ({options.Value.ResolvedBinaries}); run deploy/install-ace.sh.",
                StatusCodes.Status501NotImplemented);
        }
        var request = Path.Combine(workDirectory, "understand.json");
        var answer = Path.Combine(workDirectory, "heard.json");
        // Only the models: without sampling values ace-understand keeps its own for hearing (temperature 0.3, no top-p).
        await File.WriteAllTextAsync(request, new JsonObject { ["lm_model"] = lmModel, ["synth_model"] = ditModel }.ToJsonString(), cancellationToken);
        await RunAsync("ace-understand", ["--models", options.Value.ResolvedModels, "--src-audio", audio, "--request", request, "-o", answer], _ => { }, cancellationToken);
        if (!File.Exists(answer) || JsonNode.Parse(await File.ReadAllTextAsync(answer, cancellationToken)) is not JsonObject written)
        {
            throw new AceException("ace-understand wrote no answer.");
        }
        return AceHeard.From(written);
    }

    public AceModelFiles Files() => AceModelFiles.Scan(options.Value.ResolvedModels, options.Value.ResolvedAdapters);

    private string Tool(string name) => Path.Combine(options.Value.ResolvedBinaries, name);

    public async Task<IReadOnlyList<AceOutput>> GenerateAsync(AceRun run, Action<string, double> progress, CancellationToken cancellationToken)
    {
        if (!Installed)
        {
            throw new AceException(
                $"acestep.cpp is not built ({options.Value.ResolvedBinaries}); run deploy/install-ace.sh.",
                StatusCodes.Status501NotImplemented);
        }
        var batch = (int?)run.Request["lm_batch_size"] ?? (int?)run.Request["synth_batch_size"] ?? 1;
        var request = Path.Combine(run.WorkDirectory, "request.json");
        await File.WriteAllTextAsync(request, run.Request.ToJsonString(), cancellationToken);

        List<string> synthRequests;
        if (run.UsesLm)
        {
            var lm = new LmProgress(batch);
            progress("loading", 0);
            await RunAsync("ace-lm", ["--models", options.Value.ResolvedModels, "--request", request], line =>
            {
                if (lm.Read(line) is { } read)
                {
                    progress(read.Stage, read.Fraction * LmShare);
                }
            }, cancellationToken);
            // ace-lm writes request0.json … requestN-1.json beside its input, one per song.
            synthRequests = [.. Enumerable.Range(0, batch).Select(i => Path.Combine(run.WorkDirectory, $"request{i}.json"))];
            if (synthRequests.FirstOrDefault(r => !File.Exists(r)) is { } missing)
            {
                throw new AceException($"ace-lm wrote no {Path.GetFileName(missing)}.");
            }
            // acestep.cpp before Tonwerk's patch (deploy/ace/request-utf8.patch) left an empty file when the LM's text
            // held invalid UTF-8, exited 0 and let ace-synth fail on it with a message that points nowhere.
            if (synthRequests.FirstOrDefault(r => new FileInfo(r).Length == 0) is { } empty)
            {
                throw new AceException($"ace-lm wrote an empty {Path.GetFileName(empty)} (invalid UTF-8 in the LM's text?); run deploy/install-ace.sh again.");
            }
        }
        else
        {
            synthRequests = [request];
        }

        var start = run.UsesLm ? LmShare : 0;
        var synth = new SynthProgress(batch);
        progress(run.UsesLm ? "synthesizing" : "loading", start);
        List<string> arguments = ["--models", options.Value.ResolvedModels, "--adapters", options.Value.ResolvedAdapters];
        if (run.SourceAudio is { } source)
        {
            arguments.AddRange(["--src-audio", source]);
        }
        arguments.Add("--request");
        arguments.AddRange(synthRequests);
        await RunAsync("ace-synth", arguments, line =>
        {
            if (synth.Read(line) is { } read)
            {
                progress(read.Stage, start + read.Fraction * (1 - start));
            }
        }, cancellationToken);

        // ace-synth names each song after its request and its DiT variation: request0.json → request00.wav.
        List<AceOutput> outputs = [];
        foreach (var file in synthRequests)
        {
            var written = JsonNode.Parse(await File.ReadAllTextAsync(file, cancellationToken)) as JsonObject ?? [];
            var stem = Path.Combine(run.WorkDirectory, Path.GetFileNameWithoutExtension(file));
            var variations = run.UsesLm ? 1 : batch;
            var seed = (long?)written["seed"];
            for (var i = 0; i < variations; i++)
            {
                var wave = $"{stem}{i}.wav";
                if (!File.Exists(wave))
                {
                    continue;
                }
                var song = written.DeepClone().AsObject();
                if (seed is { } first)
                {
                    // acestep.cpp gives a DiT variation the next seed (its batching rule 3).
                    song["seed"] = first + i;
                }
                outputs.Add(new AceOutput(wave, song));
            }
        }
        if (outputs.Count == 0)
        {
            throw new AceException("ace-synth wrote no audio.");
        }
        return outputs;
    }

    private async Task RunAsync(string tool, IReadOnlyList<string> arguments, Action<string> line, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(Tool(tool))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            WorkingDirectory = options.Value.ResolvedBinaries,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start) ?? throw new AceException($"Could not start {Tool(tool)}.");
        logger.LogInformation("Running {Tool} (pid {Pid})", tool, process.Id);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Value.Timeout);
        var tail = new Queue<string>();
        try
        {
            void Read(string read)
            {
                Keep(tail, read);
                line(read);
            }
            var output = PumpAsync(process.StandardOutput, Read, timeout.Token);
            await PumpAsync(process.StandardError, Read, timeout.Token);
            await output;
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            if (!cancellationToken.IsCancellationRequested)
            {
                throw new AceException($"{tool} took longer than {options.Value.Timeout.TotalMinutes:0} minutes.");
            }
            throw;
        }
        if (process.ExitCode != 0)
        {
            lock (tail)
            {
                logger.LogWarning("{Tool} failed with {Code}:\n{Output}", tool, process.ExitCode, string.Join('\n', tail));
                throw new AceException(FailureMessage(tool, tail));
            }
        }
    }

    /// <summary>acestep.cpp says what broke on a line with FATAL or ERROR; without one the last line is the best guess.</summary>
    public static string FailureMessage(string tool, IEnumerable<string> output)
    {
        var lines = output.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var line = lines.LastOrDefault(l => l.Contains("FATAL", StringComparison.Ordinal) || l.Contains("ERROR", StringComparison.Ordinal))
            ?? lines.LastOrDefault();
        return line is null ? $"{tool} stopped without a word." : line;
    }

    private static void Keep(Queue<string> tail, string line)
    {
        lock (tail)
        {
            tail.Enqueue(line);
            while (tail.Count > TailLines)
            {
                tail.Dequeue();
            }
        }
    }

    private static async Task PumpAsync(StreamReader reader, Action<string> line, CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } read)
        {
            line(read);
        }
    }

    /// <summary>
    /// The LM's progress. Phase 1 (lyrics and metadata, only when something is missing) prints steps without a total, so
    /// it counts as a tenth; phase 2 counts its audio codes against <c>max_tokens</c> per song.
    /// </summary>
    public sealed partial class LmProgress(int batch)
    {
        private int _maxTokens;

        public (string Stage, double Fraction)? Read(string line)
        {
            if (line.StartsWith("[LM-Phase1]", StringComparison.Ordinal))
            {
                return ("writing", 0.05);
            }
            if (MaxTokens().Match(line) is { Success: true } max)
            {
                _maxTokens = int.Parse(max.Groups[1].Value, CultureInfo.InvariantCulture);
                return ("composing", 0.1);
            }
            if (Codes().Match(line) is { Success: true } codes && _maxTokens > 0)
            {
                var done = int.Parse(codes.Groups[1].Value, CultureInfo.InvariantCulture) / (double)(_maxTokens * Math.Max(batch, 1));
                return ("composing", 0.1 + 0.9 * Math.Clamp(done, 0, 1));
            }
            return null;
        }

        [GeneratedRegex(@"^\[LM-Phase2\] max_tokens: (\d+)")]
        private static partial Regex MaxTokens();

        [GeneratedRegex(@"^\[LM-Phase2\] Step \d+, \d+ active, (\d+) total codes")]
        private static partial Regex Codes();
    }

    /// <summary>The DiT's steps make most of the synthesis, the VAE the rest, one decode per song.</summary>
    public sealed partial class SynthProgress(int batch)
    {
        private const double DitShare = 0.7;
        private int _decoded;

        public (string Stage, double Fraction)? Read(string line)
        {
            if (DitStep().Match(line) is { Success: true } step)
            {
                var done = int.Parse(step.Groups[1].Value, CultureInfo.InvariantCulture) / double.Parse(step.Groups[2].Value, CultureInfo.InvariantCulture);
                return ("synthesizing", DitShare * Math.Clamp(done, 0, 1));
            }
            if (line.StartsWith("[DiT-Generate] Total", StringComparison.Ordinal))
            {
                return ("decoding", DitShare);
            }
            if (Decoded().IsMatch(line))
            {
                _decoded++;
                return ("decoding", DitShare + (1 - DitShare) * Math.Min(1, _decoded / (double)Math.Max(batch, 1)));
            }
            return null;
        }

        [GeneratedRegex(@"^\[DiT\] Step (\d+)/(\d+)")]
        private static partial Regex DitStep();

        [GeneratedRegex(@"^\[VAE-Decode Batch\d+\] Decode")]
        private static partial Regex Decoded();
    }
}
