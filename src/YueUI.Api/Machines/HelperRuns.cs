using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using YueUI.Api.Ace;
using YueUI.Api.Images;
using YueUI.Api.Lyrics;
using YueUI.Api.Memory;
using YueUI.Api.Singing;
using YueUI.Api.Speech;
using YueUI.Api.Voices;
using YueUI.Api.Worker;

namespace YueUI.Api.Machines;

/// <summary>What this Mac can run, for its own pickers and for a Mac it computes for.</summary>
public sealed class EngineInventory(IServiceProvider services)
{
    public IReadOnlyList<string> Installed()
    {
        List<string> engines = [Engines.Lyrics];
        if (services.GetRequiredService<ISpeechEngine>().Installed)
        {
            engines.Add(Engines.Speech);
        }
        var images = services.GetRequiredService<IImageEngine>();
        if (images.Installed)
        {
            engines.Add(Engines.Images);
            engines.AddRange(services.GetRequiredService<IOptions<ImageOptions>>().Value.ResolvedModels
                .Where(m => images.Unavailable(m) is null)
                .Select(m => Engines.ImageModel(m.Id)));
        }
        var ace = services.GetRequiredService<IAceEngine>();
        if (ace.Installed && ace.Files().Complete)
        {
            engines.Add(Engines.Ace);
        }
        var voices = services.GetRequiredService<IOptions<VoiceOptions>>().Value;
        if (voices.LocalStems)
        {
            engines.Add(Engines.Stems);
        }
        // A version is separated and converted; a helper takes both or neither.
        if (voices.LocalStems && voices.LocalVoices)
        {
            engines.Add(Engines.Voices);
        }
        var singing = services.GetRequiredService<ISingingEngine>();
        if (singing.Installed && singing.ModelDownloaded)
        {
            engines.Add(Engines.Singing);
        }
        return engines;
    }
}

/// <summary>
/// Runs jobs another Tonwerk hands this Mac (<see cref="HelperClient"/>), with this Mac's own engines and under its own
/// <see cref="ModelMemory"/>: a run waits for the memory like a job of this Mac's own, so the two never load two large
/// models here at once. The answer streams the job's progress as JSON lines (<see cref="HelperEvent"/>); the files it
/// wrote wait in a folder of their own until they are fetched and the run forgotten, or a few hours have passed.
/// </summary>
public sealed class HelperRuns(
    IServiceProvider services,
    ModelMemory memory,
    WorkerHost host,
    TimeProvider time,
    ILogger<HelperRuns> logger)
{
    /// <summary>A run whose files were never fetched is deleted after this long.</summary>
    private static readonly TimeSpan Forgotten = TimeSpan.FromHours(6);

    /// <summary>Often enough that no proxy on the way (tailscale serve) takes a silent answer for a dead one.</summary>
    private static readonly TimeSpan PingEvery = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan WaitInterval = TimeSpan.FromSeconds(2);

    public static string Root => Path.Combine(Path.GetTempPath(), "yueui-helper");

    public static string OutputFolder(string run) => Path.Combine(Root, run, "out");

    /// <summary>A run's id as this server makes them; anything else is not looked up on disk.</summary>
    public static bool IsRunId(string id) => id.Length == 32 && id.All(char.IsAsciiHexDigitLower);

    public static bool Known(string kind) => kind is Engines.Lyrics or Engines.Speech or Engines.Images or Engines.Ace
        or Engines.Stems or Engines.Voices or Engines.Singing;

    public async Task RunAsync(string kind, HttpContext context)
    {
        var cancellationToken = context.RequestAborted;
        if (!Known(kind))
        {
            await RefuseAsync(context, StatusCodes.Status404NotFound, $"Unknown kind of job: {kind}.");
            return;
        }
        if (!services.GetRequiredService<EngineInventory>().Installed().Contains(kind))
        {
            await RefuseAsync(context, StatusCodes.Status501NotImplemented, $"{kind} is not installed on this Mac.");
            return;
        }
        if (!context.Request.HasFormContentType)
        {
            await RefuseAsync(context, StatusCodes.Status400BadRequest, "A job comes as multipart form data.");
            return;
        }
        Sweep();
        var run = Guid.NewGuid().ToString("N");
        var folder = Path.Combine(Root, run);
        var inputs = Path.Combine(folder, "in");
        var outputs = OutputFolder(run);
        Directory.CreateDirectory(inputs);
        Directory.CreateDirectory(outputs);
        var keep = false;
        try
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            var job = JsonNode.Parse(form["job"].ToString()) ?? throw new HelperException("The job is missing.", StatusCodes.Status400BadRequest);
            Dictionary<string, string> files = [];
            foreach (var file in form.Files)
            {
                // The part's name is what the job reads it as; the extension keeps ffmpeg and the scripts happy.
                if (file.Name.Any(c => !char.IsAsciiLetterOrDigit(c)))
                {
                    continue;
                }
                var path = Path.Combine(inputs, file.Name + Path.GetExtension(Path.GetFileName(file.FileName)));
                await using (var stream = File.Create(path))
                {
                    await file.CopyToAsync(stream, cancellationToken);
                }
                files[file.Name] = path;
            }

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/x-ndjson";
            var writer = new LineWriter(context.Response);
            await writer.WriteAsync(new HelperEvent("started", Run: run), cancellationToken);
            using var pinging = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var pinger = PingAsync(writer, pinging.Token);
            try
            {
                logger.LogInformation("Running a {Kind} job for another Mac", kind);
                var result = await HandleAsync(kind, job, files, outputs, writer, cancellationToken);
                var written = Directory.EnumerateFiles(outputs).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
                keep = true;
                await pinging.CancelAsync();
                await pinger;
                await writer.WriteAsync(new HelperEvent("done", Run: run, Result: result, Files: written!), cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await pinging.CancelAsync();
                await pinger;
                logger.LogWarning(exception, "A {Kind} job for another Mac failed", kind);
                await writer.WriteAsync(new HelperEvent("failed", Message: exception.Message, Status: StatusOf(exception)), CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("The other Mac cancelled its {Kind} job", kind);
        }
        catch (Exception exception) when (exception is HelperException or JsonException && !context.Response.HasStarted)
        {
            await RefuseAsync(context, StatusOf(exception), exception.Message);
        }
        finally
        {
            if (!keep)
            {
                Delete(folder);
            }
        }
    }

    public static void Forget(string run)
    {
        if (IsRunId(run))
        {
            Delete(Path.Combine(Root, run));
        }
    }

    private async Task<JsonNode?> HandleAsync(
        string kind,
        JsonNode job,
        IReadOnlyDictionary<string, string> files,
        string outputs,
        LineWriter writer,
        CancellationToken cancellationToken)
    {
        var model = kind switch
        {
            Engines.Lyrics => LargeModel.Lyrics,
            Engines.Speech => LargeModel.Speech,
            Engines.Images => LargeModel.Images,
            Engines.Ace => LargeModel.Ace,
            Engines.Singing => LargeModel.Singing,
            _ => LargeModel.Voices,
        };
        void Send(HelperEvent line) => writer.Post(line);
        if (!memory.TryClaim(model))
        {
            Send(new HelperEvent("waiting"));
            await memory.ClaimAsync(model, WaitInterval, time, cancellationToken);
        }
        try
        {
            if (kind != Engines.Lyrics)
            {
                // YuE2 keeps its model for ten idle minutes; the job's model needs the memory now.
                await host.ShutdownWorkerAsync();
            }
            Send(new HelperEvent("stage", Stage: "loading"));
            return kind switch
            {
                Engines.Lyrics => await LyricsAsync(Parse<LyricsHelperJob>(job), cancellationToken),
                Engines.Speech => await SpeechAsync(Parse<SpeechHelperJob>(job), files, outputs, Send, cancellationToken),
                Engines.Images => await ImagesAsync(Parse<ImageHelperJob>(job), outputs, Send, cancellationToken),
                Engines.Ace => await AceAsync(Parse<AceHelperJob>(job), files, outputs, Send, cancellationToken),
                Engines.Singing => await SingingAsync(Parse<SingingHelperJob>(job), files, outputs, Send, cancellationToken),
                Engines.Stems => await StemsAsync(Parse<StemsHelperJob>(job), files, outputs, cancellationToken),
                _ => await VoicesAsync(Parse<VoiceHelperJob>(job), files, outputs, Send, cancellationToken),
            };
        }
        finally
        {
            memory.Release(model);
        }
    }

    private async Task<JsonNode?> LyricsAsync(LyricsHelperJob job, CancellationToken cancellationToken)
    {
        var lyrics = await services.GetRequiredService<LyricsWriter>().WriteForAsync(job, cancellationToken);
        return new JsonObject { ["lyrics"] = lyrics };
    }

    private async Task<JsonNode?> SpeechAsync(
        SpeechHelperJob job,
        IReadOnlyDictionary<string, string> files,
        string outputs,
        Action<HelperEvent> send,
        CancellationToken cancellationToken)
    {
        var settings = services.GetRequiredService<IOptions<SpeechOptions>>().Value;
        var engine = services.GetRequiredService<ISpeechEngine>();
        var output = Path.Combine(outputs, "take.wav");
        void Staged(string stage) => send(new HelperEvent("stage", Stage: stage));
        SpeechResult result;
        if (job.Clean)
        {
            var model = settings.ResolvedCleanModels.FirstOrDefault(m => m.Id == job.Model)
                ?? throw new HelperException($"The cleaning model {job.Model} is not offered on this Mac.", StatusCodes.Status400BadRequest);
            var source = files.GetValueOrDefault("source") ?? throw new HelperException("The recording is missing.", StatusCodes.Status400BadRequest);
            result = await engine.CleanAsync(new CleanJob(model, source, output, job.Seconds), Staged,
                share => send(new HelperEvent("progress", Share: Math.Round(share, 3))), cancellationToken);
        }
        else
        {
            var model = settings.ResolvedModels.FirstOrDefault(m => m.Id == job.Model)
                ?? throw new HelperException($"The speech model {job.Model} is not offered on this Mac.", StatusCodes.Status400BadRequest);
            result = await engine.SpeakAsync(new SpeechJob(model, job.Text ?? "", files.GetValueOrDefault("voice"), job.RefText, output), Staged, cancellationToken);
        }
        return JsonSerializer.SerializeToNode(result, HelperClient.Json);
    }

    private async Task<JsonNode?> ImagesAsync(ImageHelperJob job, string outputs, Action<HelperEvent> send, CancellationToken cancellationToken)
    {
        var model = services.GetRequiredService<IOptions<ImageOptions>>().Value.ResolvedModels.FirstOrDefault(m => m.Id == job.Model)
            ?? throw new HelperException($"The image model {job.Model} is not offered on this Mac.", StatusCodes.Status400BadRequest);
        var result = await services.GetRequiredService<IImageEngine>().PaintAsync(
            new ImageJob(model, job.Prompt, job.Seed, job.Width, job.Height, Path.Combine(outputs, "image.jpg")),
            (stage, share) => send(new HelperEvent("progress", Stage: stage, Share: Math.Round(share, 3))),
            cancellationToken);
        return JsonSerializer.SerializeToNode(result, HelperClient.Json);
    }

    private async Task<JsonNode?> AceAsync(
        AceHelperJob job,
        IReadOnlyDictionary<string, string> files,
        string outputs,
        Action<HelperEvent> send,
        CancellationToken cancellationToken)
    {
        var work = Path.Combine(Path.GetDirectoryName(outputs)!, "ace");
        Directory.CreateDirectory(work);
        var songs = await services.GetRequiredService<IAceEngine>().GenerateAsync(
            new AceRun(work, job.Request, job.UsesLm, files.GetValueOrDefault("source")),
            (stage, share) => send(new HelperEvent("progress", Stage: stage, Share: Math.Round(share, 3))),
            cancellationToken);
        var requests = new JsonArray();
        for (var index = 0; index < songs.Count; index++)
        {
            // Zero-padded, so that the order the other Mac reads them in is the order of the requests.
            File.Move(songs[index].Wave, Path.Combine(outputs, $"song{index:D3}.wav"));
            requests.Add(songs[index].Request.DeepClone());
        }
        return new JsonObject { ["requests"] = requests };
    }

    private async Task<JsonNode?> SingingAsync(
        SingingHelperJob job,
        IReadOnlyDictionary<string, string> files,
        string outputs,
        Action<HelperEvent> send,
        CancellationToken cancellationToken)
    {
        // A voice of one's own comes with the job; a configured one is this Mac's.
        var voice = files.GetValueOrDefault("voiceAudio") is { } audio && files.GetValueOrDefault("voiceMetadata") is { } metadata
            ? new SingingVoice { Id = job.Voice, Label = job.Voice, Audio = audio, Metadata = metadata, Language = "English", Own = true }
            : services.GetRequiredService<IOptions<SingingOptions>>().Value.ResolvedVoices.FirstOrDefault(v => v.Id == job.Voice)
                ?? throw new HelperException($"The voice {job.Voice} is not offered on this Mac.", StatusCodes.Status400BadRequest);
        var result = await services.GetRequiredService<ISingingEngine>().SingAsync(
            new SingingJob(job.Notes, job.Lyrics, voice, job.PitchShift, job.AutoShift, Path.Combine(outputs, "take.wav")),
            stage => send(new HelperEvent("stage", Stage: stage)),
            share => send(new HelperEvent("progress", Share: Math.Round(share, 3))),
            aligned => send(new HelperEvent("event", Name: "aligned", Data: JsonSerializer.SerializeToNode(aligned, HelperClient.Json))),
            cancellationToken);
        return JsonSerializer.SerializeToNode(result, HelperClient.Json);
    }

    private async Task<JsonNode?> StemsAsync(StemsHelperJob job, IReadOnlyDictionary<string, string> files, string outputs, CancellationToken cancellationToken)
    {
        var audio = files.GetValueOrDefault("audio") ?? throw new HelperException("The song is missing.", StatusCodes.Status400BadRequest);
        await services.GetRequiredService<StemSeparator>().ExtractAsync(audio, job.Model, job.Dereverb, outputs, cancellationToken);
        return null;
    }

    private async Task<JsonNode?> VoicesAsync(
        VoiceHelperJob job,
        IReadOnlyDictionary<string, string> files,
        string outputs,
        Action<HelperEvent> send,
        CancellationToken cancellationToken)
    {
        var vocals = files.GetValueOrDefault("vocals") ?? throw new HelperException("The vocals are missing.", StatusCodes.Status400BadRequest);
        var reference = files.GetValueOrDefault("reference") ?? throw new HelperException("The reference voice is missing.", StatusCodes.Status400BadRequest);
        // Seed-VC's own work folder lands beside the output; out of the folder whose files are sent back.
        var work = Path.Combine(Path.GetDirectoryName(outputs)!, "voice");
        Directory.CreateDirectory(work);
        var converted = Path.Combine(work, "converted.wav");
        var shown = -1.0;
        await services.GetRequiredService<SeedVcVoices>().ConvertAsync(job.Conversion, reference, vocals, converted, (fraction, estimated) =>
        {
            if (fraction - shown >= 0.01)
            {
                shown = fraction;
                send(new HelperEvent("progress", Share: Math.Round(fraction, 3), Data: JsonValue.Create(estimated)));
            }
        }, cancellationToken);
        File.Move(converted, Path.Combine(outputs, "converted.wav"));
        return null;
    }

    private static T Parse<T>(JsonNode job) =>
        job.Deserialize<T>(HelperClient.Json) ?? throw new HelperException("The job could not be read.", StatusCodes.Status400BadRequest);

    private static int StatusOf(Exception exception) => exception switch
    {
        HelperException helper => helper.Status,
        SpeechException speech => speech.Status,
        ImageException image => image.Status,
        AceException ace => ace.Status,
        SingingException singing => singing.Status,
        JsonException => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status500InternalServerError,
    };

    private static async Task RefuseAsync(HttpContext context, int status, string detail)
    {
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { status, detail }, context.RequestAborted);
    }

    private static async Task PingAsync(LineWriter writer, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(PingEvery, cancellationToken);
                await writer.WriteAsync(new HelperEvent("ping"), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
            // The other Mac went away; the job hears it through the request's own token.
        }
    }

    private void Sweep()
    {
        try
        {
            if (!Directory.Exists(Root))
            {
                return;
            }
            var before = time.GetUtcNow() - Forgotten;
            foreach (var folder in Directory.EnumerateDirectories(Root))
            {
                if (Directory.GetLastWriteTimeUtc(folder) < before.UtcDateTime)
                {
                    Delete(folder);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not clear old runs in {Folder}", Root);
        }
    }

    private static void Delete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Writes one JSON line at a time, from the job's callbacks (any thread, synchronous) and the pinger alike.
    /// Callbacks only queue their line, so a slow connection never holds up the engine.
    /// </summary>
    private sealed class LineWriter(HttpResponse response)
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private Task _posted = Task.CompletedTask;
        private readonly Lock _chain = new();

        public void Post(HelperEvent line)
        {
            lock (_chain)
            {
                _posted = _posted.ContinueWith(_ => WriteAsync(line, CancellationToken.None), TaskScheduler.Default).Unwrap();
            }
        }

        public async Task WriteAsync(HelperEvent line, CancellationToken cancellationToken)
        {
            Task posted;
            lock (_chain)
            {
                posted = _posted;
            }
            if (line.Type is "done" or "failed")
            {
                // Everything the job said goes out before its end.
                try
                {
                    await posted;
                }
                catch (Exception exception) when (exception is IOException or OperationCanceledException)
                {
                }
            }
            await _gate.WaitAsync(cancellationToken);
            try
            {
                await response.WriteAsync(JsonSerializer.Serialize(line, HelperClient.Json) + "\n", cancellationToken);
                await response.Body.FlushAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException && line.Type is not ("done" or "failed"))
            {
                // A line lost to a closed connection; the job ends on the request's token.
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
