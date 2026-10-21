using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using YueUI.Api.Backup;
using YueUI.Api.Data;
using YueUI.Api.Export;
using YueUI.Api.Lyrics;
using YueUI.Api.Push;
using YueUI.Api.Share;
using YueUI.Api.Speech;
using YueUI.Api.Voices;
using YueUI.Api.Worker;

namespace YueUI.Api.Tests;

/// <summary>
/// The app with a temporary song library and a scripted worker: tests read the commands the server sent and
/// answer with the events <c>yue2_worker.py</c> would write.
/// </summary>
public sealed class TestApp : WebApplicationFactory<Program>
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly bool _fakeWorker;

    public TestApp(bool fakeWorker = true)
    {
        _fakeWorker = fakeWorker;
        // A job waiting for the memory starts within moments of it being free, not a second later.
        Queue.JobQueue.CheckInterval = TimeSpan.FromMilliseconds(20);
        Root = Directory.CreateTempSubdirectory("yueui-").FullName;
        OutputDir = Path.Combine(Root, "songs");
        Directory.CreateDirectory(OutputDir);
    }

    public string Root { get; }

    public string OutputDir { get; }

    public FakeLauncher Launcher { get; } = new();

    public FakeStudio Studio { get; } = new();

    public FakeLmStudio LmStudio { get; } = new();

    public FakePushSender Push { get; } = new();

    public FakeEncoder Encoder { get; } = new();

    public FakeTagger Tagger { get; } = new();

    public FakeVoiceService Voice { get; } = new();

    public FakeStems Stems { get; } = new();

    public FakeMixer Mixer { get; } = new();

    public FakeSpeech Speech { get; } = new();

    public FakeSinging Singing { get; } = new();

    public FakeVideoRenderer Videos { get; } = new();

    public FakeMontage Montage { get; } = new();

    /// <summary>Where photo videos look for their folders (<c>Montage:MediaRoot</c>); empty until a test fills it.</summary>
    public string MediaRoot => Path.Combine(Root, "media");

    public FakeImages Images { get; } = new();

    public FakeAudioMidi AudioMidi { get; } = new();

    public FakeAce Ace { get; } = new();

    public FakeSketchModel Sketches { get; } = new();

    public FakeCloudLyrics CloudLyrics { get; } = new();

    public FakeTranslator Translator { get; } = new();

    /// <summary>The Anthropic key the sketches see; null leaves them unconfigured.</summary>
    public string? SketchKey { get; set; } = "sketch-key";

    public FakeListener Listener { get; } = new();

    public FakeSongListener SongListener { get; } = new();

    /// <summary>Whether the songs are heard for tempo and key in the background; off unless a test is about it.</summary>
    public bool AnalyseSongs { get; set; }

    public FakeNextcloud Nextcloud { get; } = new();

    public FakeLoki Loki { get; } = new();

    /// <summary>Where the fake Loki is expected; null (the default) leaves shipping the log off.</summary>
    public string? LokiUrl { get; set; }

    /// <summary>The backup folder in the fake Nextcloud; null (the default) leaves the backup off.</summary>
    public string? NextcloudUrl { get; set; }

    /// <summary>The env file with the Nextcloud's address, user and password; null (the default) reads none.</summary>
    public string? BackupEnvFile { get; set; }

    /// <summary>When the nightly backup starts; null for manual backups only.</summary>
    public string? BackupAt { get; set; }

    /// <summary>Where the fake ChangeMyVoice is expected; set to null before the first request to switch voices off.</summary>
    public string? VoiceBaseUrl { get; set; } = "http://voice.test";

    /// <summary>Where the fake StemMyWav is expected; set to null before the first request to switch stems off.</summary>
    public string? StemsBaseUrl { get; set; } = "http://stems.test";

    /// <summary>How long a draft or version may be passed by songs; set before the first request.</summary>
    public TimeSpan BundleWindow { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>Settings of <c>Machines</c> on top of the test's own (no helpers, no serving, no key file).</summary>
    public Action<Api.Machines.MachineOptions>? Machines { get; set; }

    /// <summary>What the helper client talks to, typically another <see cref="TestApp"/>'s server; null for none.</summary>
    public Func<HttpMessageHandler>? HelperHandler { get; set; }

    public FakeWorker Worker => Launcher.Current ?? throw new InvalidOperationException("No worker was started.");

    /// <summary>The worker a job from the queue starts on the queue's own thread.</summary>
    public async Task<FakeWorker> StartedWorker()
    {
        await WaitUntil(() => Launcher.Current is { Disposed: false });
        return Worker;
    }

    /// <summary>Creates <c>run/songN</c> with the files the worker writes; <paramref name="files"/> overrides or adds.</summary>
    public string AddSong(string run, string song, string title = "Neon Night", string quality = "draft", params string[] files)
    {
        var directory = Path.Combine(OutputDir, run, song);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "request.json"), """{"style": "Dark synthwave", "lyrics": "[verse]\nLa la", "cot": "full", "seed": 42, "id": "song1"}""");
        File.WriteAllText(Path.Combine(directory, "result.json"), $$"""{"status": "complete", "audio_seconds": 187.5, "quality": "{{quality}}", "title": "{{title}}"}""");
        File.WriteAllBytes(Path.Combine(directory, "audio.flac"), [.. "fLaC"u8, .. new byte[96]]);
        File.WriteAllText(Path.Combine(directory, "score.abc"), "X:1\n");
        foreach (var file in files)
        {
            File.WriteAllText(Path.Combine(directory, file), "");
        }
        return directory;
    }

    /// <summary>Where YuE Studio's installer puts SheetSage2's environment; an empty file is enough for the server.</summary>
    public void InstallSheetSage()
    {
        var python = Path.Combine(Root, "install", "sheetsage-env", "bin", "python");
        Directory.CreateDirectory(Path.GetDirectoryName(python)!);
        File.WriteAllText(python, "");
    }

    /// <summary>A transcription folder as the worker leaves it; without a score it stands for a failed one.</summary>
    public string AddTranscription(string name, string source = "My Song.mp3", string? score = "X:1\nV:Vocal\nE2G2|\n")
    {
        var directory = Path.Combine(OutputDir, "transcriptions", name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "input.json"), $$"""{"source_name": "{{source}}", "task": "melody-vocal"}""");
        if (score is not null)
        {
            File.WriteAllText(Path.Combine(directory, "score.abc"), score);
            File.WriteAllText(Path.Combine(directory, "transcription.mid"), "MThd");
            File.WriteAllText(Path.Combine(directory, "transcription_manifest.json"), """{"status": "complete", "warnings": ["key uncertain"]}""");
        }
        else
        {
            File.WriteAllText(Path.Combine(directory, "failure.json"), """{"status": "failed"}""");
        }
        return directory;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(new YuePaths(Path.Combine(Root, "install"), OutputDir));
            services.AddSingleton<IStudioDetector>(Studio);
            services.AddSingleton<ILmStudioStarter>(LmStudio);
            // The model FakeLmStudio lists and a fixed context, whatever appsettings.json picks for the Mac.
            services.Configure<LyricsOptions>(options =>
            {
                options.Model = "google/gemma-4-e4b";
                options.ContextLength = 8192;
                options.MinContextLength = 2048;
            });
            // A new handler each time: the factory disposes the ones it rotates out.
            services.AddHttpClient(LyricsWriter.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new FakeLmStudio.Handler(LmStudio));

            services.Configure<VoiceOptions>(options =>
            {
                options.BaseUrl = VoiceBaseUrl;
                options.ApiKey = "voice-key";
                options.ApiKeyFile = null;
                options.StemsBaseUrl = StemsBaseUrl;
                options.StemsApiKey = "stems-key";
                options.StemsApiKeyFile = null;
                options.PollInterval = TimeSpan.FromMilliseconds(10);
                options.WaitInterval = TimeSpan.FromMilliseconds(10);
                // Empty unless a test installs fake engines there (LocalEngines).
                options.EngineRoot = Path.Combine(Root, "engines");
            });
            services.AddHttpClient(VoiceClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new FakeVoiceService.Handler(Voice));
            services.AddHttpClient(StemClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new FakeStems.Handler(Stems));
            services.AddSingleton<IAudioMixer>(Mixer);

            services.AddSingleton<ISpeechEngine>(Speech);
            services.AddSingleton<Api.Singing.ISingingEngine>(Singing);
            services.Configure<Api.Singing.SingingOptions>(options =>
            {
                options.Root = Path.Combine(Root, "singing");
                options.WaitInterval = TimeSpan.FromMilliseconds(10);
            });
            services.AddSingleton<Video.IVideoRenderer>(Videos);
            services.AddSingleton<Video.IMontageBuilder>(Montage);
            services.Configure<Video.MontageOptions>(options => options.MediaRoot = MediaRoot);
            services.AddSingleton<Images.IImageEngine>(Images);
            services.AddSingleton<Images.IPromptTranslator>(Translator);
            services.AddSingleton<AudioMidi.IAudioMidiEngine>(AudioMidi);
            services.AddSingleton<Ace.IAceEngine>(Ace);
            services.AddSingleton<Sketches.ISketchModel>(Sketches);
            services.AddSingleton<ICloudLyricsModel>(CloudLyrics);
            services.Configure<Sketches.SketchOptions>(options =>
            {
                options.ApiKey = SketchKey;
                options.KeyFile = Path.Combine(Root, "anthropic.key");
            });
            services.AddSingleton<Timing.IWordListener>(Listener);
            services.AddSingleton<Analysis.ISongListener>(SongListener);
            services.Configure<Analysis.AnalysisOptions>(options =>
            {
                options.Auto = AnalyseSongs;
                options.StartDelay = TimeSpan.Zero;
                options.IdlePoll = TimeSpan.FromMilliseconds(10);
            });
            services.Configure<Ace.AceOptions>(options =>
            {
                options.Root = Path.Combine(Root, "ace");
                options.WaitInterval = TimeSpan.FromMilliseconds(10);
            });
            services.Configure<Images.ImageOptions>(options =>
            {
                options.Root = Path.Combine(Root, "images");
                options.WaitInterval = TimeSpan.FromMilliseconds(10);
            });
            services.Configure<SpeechOptions>(options =>
            {
                options.Root = Path.Combine(Root, "speech");
                options.WaitInterval = TimeSpan.FromMilliseconds(10);
            });

            services.Configure<Api.Machines.MachineOptions>(options =>
            {
                options.KeyFile = Path.Combine(Root, "helper.key");
                options.PollInterval = TimeSpan.FromMilliseconds(20);
                Machines?.Invoke(options);
            });
            if (HelperHandler is { } helperHandler)
            {
                services.AddHttpClient(Api.Machines.HelperClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(helperHandler);
            }

            services.Configure<Queue.QueueOptions>(options => options.BundleWindow = BundleWindow);
            services.Configure<PushOptions>(options => options.DataPath = Path.Combine(Root, "push.json"));
            services.AddSingleton<IPushSender>(Push);
            services.AddSingleton<IAudioEncoder>(Encoder);
            services.AddSingleton<IAudioTagger>(Tagger);
            services.Configure<DataOptions>(options => options.Path = Path.Combine(Root, "yueui.db"));
            services.Configure<Logs.LogOptions>(options =>
            {
                options.Directory = Path.Combine(Root, "logs");
                options.UpdateLog = Path.Combine(Root, "tonwerk-update.log");
            });
            services.Configure<BackupOptions>(options =>
            {
                options.WebDavUrl = NextcloudUrl;
                options.Username = "marcel";
                options.AppPassword = "app-password";
                options.EnvFile = BackupEnvFile;
                options.At = BackupAt;
                // The test's files are seconds old.
                options.Settle = TimeSpan.Zero;
                options.TransientRetryDelay = TimeSpan.Zero;
            });
            services.AddHttpClient(WebDavClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new FakeNextcloud.Handler(Nextcloud));
            services.Configure<Monitoring.MonitoringOptions>(options =>
            {
                options.Loki.Url = LokiUrl;
                options.Loki.Host = "mini";
                options.Loki.BatchDelay = TimeSpan.FromMilliseconds(10);
            });
            services.AddHttpClient(Monitoring.LokiShipper.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new FakeLoki.Handler(Loki));
            if (_fakeWorker)
            {
                services.AddSingleton<IWorkerLauncher>(Launcher);
            }
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        // WebApplicationFactory gets here from Dispose and from DisposeAsync.
        if (disposing && Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    /// <summary>Some effects follow the state that announces them, e.g. deleting an upload; wait for them.</summary>
    public static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition never held.");
            }
            await Task.Delay(20);
        }
    }

    /// <summary>The worker runs on its own thread; wait until the state shows what a test expects.</summary>
    public async Task<StatusSnapshot> WaitForStatus(HttpClient client, Func<StatusSnapshot, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            var status = (await client.GetFromJsonAsync<StatusSnapshot>("/api/status", Json))!;
            if (condition(status))
            {
                return status;
            }
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Status never matched: {JsonSerializer.Serialize(status, Json)}");
            }
            await Task.Delay(20);
        }
    }

    /// <summary>The state as the browsers get it, without a round trip (for checks right after a request).</summary>
    public StatusSnapshot Snapshot() => Services.GetRequiredService<YueUI.Api.Status.StatusHub>().Snapshot();

    public string AudioPath(string run, string song) => Path.Combine(OutputDir, run, song, "audio.flac");
}

public sealed class FakeLauncher : IWorkerLauncher
{
    public int Launches { get; private set; }

    public FakeWorker? Current { get; private set; }

    /// <summary>As when YuE Studio is not installed.</summary>
    public bool Fail { get; set; }

    public IWorkerConnection Launch()
    {
        if (Fail)
        {
            throw new WorkerUnavailableException("YuE Studio is not installed.");
        }
        Launches++;
        return Current = new FakeWorker();
    }
}

public sealed class FakeWorker : IWorkerConnection
{
    private readonly Channel<WorkerLine> _output = Channel.CreateUnbounded<WorkerLine>();
    private readonly Channel<JsonObject> _commands = Channel.CreateUnbounded<JsonObject>();

    public ChannelReader<WorkerLine> Output => _output.Reader;

    public bool Disposed { get; private set; }

    public Task SendAsync(string line, CancellationToken cancellationToken)
    {
        _commands.Writer.TryWrite(JsonNode.Parse(line)!.AsObject());
        return Task.CompletedTask;
    }

    public async Task<JsonObject> NextCommand()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await _commands.Reader.ReadAsync(timeout.Token);
    }

    public void Emit(object message) => _output.Writer.TryWrite(new WorkerLine(JsonSerializer.Serialize(message), false));

    /// <summary>A line the worker writes to stderr, such as a Python traceback.</summary>
    public void Stderr(string text) => _output.Writer.TryWrite(new WorkerLine(text, true));

    public void Exit() => _output.Writer.TryComplete();

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        Exit();
        return ValueTask.CompletedTask;
    }
}

public sealed class FakeStudio : IStudioDetector
{
    public bool Running { get; set; }

    public bool IsRunning() => Running;
}

/// <summary>LM Studio's server as the lyrics writer sees it: models, chat completions and unload, all recorded.</summary>
public sealed class FakeLmStudio : ILmStudioStarter
{
    public bool Running { get; set; } = true;

    /// <summary>Whether <c>lms</c> is installed; starting it makes the server answer.</summary>
    public bool CanStart { get; set; } = true;

    public int Starts { get; private set; }

    public string Answer { get; set; } = "[Verse]\nLine one\n\n[Chorus]\nLine two";

    /// <summary>The whole answer instead of one built from <see cref="Answer"/>, e.g. with only reasoning.</summary>
    public object? RawAnswer { get; set; }

    /// <summary>An instance the user loaded in LM Studio themselves.</summary>
    public string? LoadedInstance { get; set; }

    /// <summary>Another model the user left loaded in LM Studio; unloading it clears this.</summary>
    public string? OtherLoadedInstance { get; set; }

    /// <summary>LM Studio's guardrails refusing the load, with this message.</summary>
    public string? LoadRefusal { get; set; }

    /// <summary>The context LM Studio's load answer claims, instead of the one asked for.</summary>
    public int? EchoedContext { get; set; }

    /// <summary>Limits <see cref="LoadRefusal"/> to loads it holds for, given the context asked for.</summary>
    public Func<int, bool>? RefusesContext { get; set; }

    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    /// <summary>The native chat API refusing a <c>reasoning</c> the model does not support, as LM Studio does.</summary>
    public bool RefusesReasoning { get; set; }

    /// <summary>Holds the completion until a test lets it go, to look at the server meanwhile.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public List<(string Path, JsonObject? Body)> Requests { get; } = [];

    public Task<bool> StartAsync(CancellationToken cancellationToken)
    {
        Starts++;
        Running |= CanStart;
        return Task.FromResult(CanStart);
    }

    public sealed class Handler(FakeLmStudio lm) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!lm.Running)
            {
                throw new HttpRequestException("Connection refused");
            }
            var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken)) as JsonObject;
            var path = request.RequestUri!.AbsolutePath;
            lock (lm.Requests)
            {
                lm.Requests.Add((path, body));
            }
            switch (path)
            {
                case "/v1/models":
                    return Json(HttpStatusCode.OK, new { data = new[] { new { id = "google/gemma-4-e4b" } } });
                case "/api/v1/models" when request.Method == HttpMethod.Get:
                    return Json(HttpStatusCode.OK, new
                    {
                        models = new object[]
                        {
                            new
                            {
                                type = "llm",
                                key = "google/gemma-4-e4b",
                                display_name = "Gemma 4 E4B",
                                size_bytes = 5_000_000_000L,
                                loaded_instances = lm.LoadedInstance is { } id ? new object[] { new { id } } : [],
                                capabilities = new { vision = true, trained_for_tool_use = true },
                            },
                            new
                            {
                                type = "llm",
                                key = "qwen/qwen3-8b",
                                display_name = "Qwen3 8B",
                                size_bytes = 5_500_000_000L,
                                loaded_instances = lm.OtherLoadedInstance is { } other ? new object[] { new { id = other } } : [],
                                capabilities = new { vision = false, trained_for_tool_use = true },
                            },
                            new { type = "embedding", key = "text-embedding-nomic", display_name = "Nomic Embed", size_bytes = 80_000_000L, loaded_instances = Array.Empty<object>() },
                        },
                    });
                case "/api/v1/models/load":
                    return lm.LoadRefusal is { } refusal && lm.RefusesContext?.Invoke((int)body!["context_length"]!) != false
                        ? Json(HttpStatusCode.InternalServerError, new { error = new { type = "model_load_failed", message = refusal } })
                        : Json(HttpStatusCode.OK, new
                        {
                            type = "llm",
                            instance_id = $"{body?["model"]}:1",
                            status = "loaded",
                            load_time_seconds = 2.5,
                            load_config = new { context_length = lm.EchoedContext ?? (int?)body?["context_length"] },
                        });
                case "/v1/chat/completions":
                    if (lm.Gate is { } gate)
                    {
                        await gate.Task.WaitAsync(cancellationToken);
                    }
                    return lm.Status == HttpStatusCode.OK
                        ? Json(HttpStatusCode.OK, lm.RawAnswer ?? new { choices = new[] { new { message = new { role = "assistant", content = lm.Answer } } } })
                        : Json(lm.Status, new { error = new { message = "Model not found" } });
                case "/api/v1/chat":
                    return lm.RefusesReasoning
                        ? Json(HttpStatusCode.BadRequest, new { error = new { message = "This model does not support the reasoning setting" } })
                        : Json(HttpStatusCode.OK, new
                        {
                            model_instance_id = (string?)body?["model"],
                            output = new object[] { new { type = "reasoning", content = "Counting syllables" }, new { type = "message", content = lm.Answer } },
                            stats = new { input_tokens = 900, total_output_tokens = 300, reasoning_output_tokens = 120 },
                        });
                case "/api/v1/models/unload":
                    if ((string?)body?["instance_id"] == lm.OtherLoadedInstance)
                    {
                        lm.OtherLoadedInstance = null;
                    }
                    return Json(HttpStatusCode.OK, new { instance_id = (string?)body?["instance_id"] });
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        private static HttpResponseMessage Json(HttpStatusCode status, object value) =>
            new(status) { Content = JsonContent.Create(value) };
    }
}

/// <summary>Records what would have gone to the push services; <see cref="Result"/> decides their answer.</summary>
public sealed class FakePushSender : IPushSender
{
    private readonly Channel<(PushSubscriptionEntry Subscription, JsonObject Payload)> _sent = Channel.CreateUnbounded<(PushSubscriptionEntry, JsonObject)>();

    public PushResult Result { get; set; } = PushResult.Delivered;

    public Task<PushResult> SendAsync(PushSubscriptionEntry subscription, string payload, CancellationToken cancellationToken)
    {
        _sent.Writer.TryWrite((subscription, JsonNode.Parse(payload)!.AsObject()));
        return Task.FromResult(Result);
    }

    public async Task<(PushSubscriptionEntry Subscription, JsonObject Payload)> Next()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await _sent.Reader.ReadAsync(timeout.Token);
    }

    public bool TryNext(out (PushSubscriptionEntry Subscription, JsonObject Payload) sent) => _sent.Reader.TryRead(out sent);
}

/// <summary>Remembers the tags instead of writing them, since the fake audio files are no real MP3s.</summary>
public sealed class FakeTagger : IAudioTagger
{
    public SongTags? Tags { get; private set; }

    public string? Path { get; private set; }

    /// <summary>Set to fail like TagLib on a broken file.</summary>
    public string? Failure { get; set; }

    /// <summary>How often it tagged, to see a changed title tag a copy again.</summary>
    public int Calls { get; private set; }

    public void Write(string path, SongTags tags)
    {
        Calls++;
        Path = path;
        Tags = tags;
        if (Failure is not null)
        {
            throw new InvalidOperationException(Failure);
        }
    }
}

/// <summary>Writes a few bytes instead of running afconvert; remembers what it was asked to encode.</summary>
public sealed class FakeEncoder : IAudioEncoder
{
    public static readonly byte[] M4a = [.. "ftypM4A "u8, 1, 2, 3];

    /// <summary>False stands for a machine with neither afconvert nor ffmpeg.</summary>
    public bool Available { get; set; } = true;

    /// <summary>Set to make the encoder fail like afconvert with a non-zero exit code.</summary>
    public string? Failure { get; set; }

    public string? Source { get; private set; }

    public string? Target { get; private set; }

    public AudioFormat? Format { get; private set; }

    public int? BitRate { get; private set; }

    /// <summary>How often it encoded, to see a streaming copy made once.</summary>
    public int Calls => _calls;

    private int _calls;

    public Task<bool> EncodeAsync(string flac, string m4a, AudioFormat format, int bitRate, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        Source = flac;
        Target = m4a;
        Format = format;
        BitRate = bitRate;
        // A FLAC is only encoded to change its loudness, which the tests leave to LoudnessTests: the original goes.
        if (!Available || format == AudioFormat.Flac)
        {
            return Task.FromResult(false);
        }
        if (Failure is not null)
        {
            File.WriteAllBytes(m4a, [1]);
            throw new InvalidOperationException(Failure);
        }
        File.WriteAllBytes(m4a, M4a);
        return Task.FromResult(true);
    }
}

/// <summary>ChangeMyVoice's Mac API: reference voices and jobs, with the requests it got.</summary>
public sealed class FakeVoiceService
{
    public List<JsonObject> Voices { get; } =
    [
        new() { ["id"] = "v1", ["label"] = "Eurobecca", ["createdAtUtc"] = "2026-09-26T10:00:00+00:00", ["stored"] = new JsonObject { ["durationSeconds"] = 24.5 } },
    ];

    /// <summary>The states GET /jobs/{id} answers with in turn; the last one stays.</summary>
    public Queue<string> JobStatuses { get; } = new(["RUNNING", "COMPLETED"]);

    public string? JobError { get; set; }

    public byte[] Result { get; set; } = [.. "RIFF"u8, 1, 2, 3, 4];

    public List<(HttpMethod Method, string Path, string? Key)> Requests { get; } = [];

    /// <summary>The form of the last POST, fields as text and files as their size.</summary>
    public Dictionary<string, string> Form { get; } = [];

    public int DeletedJobs { get; private set; }

    public sealed class Handler(FakeVoiceService voice) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (voice.Requests)
            {
                voice.Requests.Add((request.Method, path, request.Headers.TryGetValues("X-Api-Key", out var keys) ? keys.Single() : null));
            }
            if (request.Content is MultipartFormDataContent form)
            {
                voice.Form.Clear();
                foreach (var part in form)
                {
                    var name = part.Headers.ContentDisposition!.Name!.Trim('"');
                    voice.Form[name] = part.Headers.ContentDisposition.FileName is null
                        ? await part.ReadAsStringAsync(cancellationToken)
                        : $"{(await part.ReadAsByteArrayAsync(cancellationToken)).Length} bytes";
                }
            }
            switch (request.Method.Method, path)
            {
                case ("GET", "/api/v1/voices"):
                    return Json(HttpStatusCode.OK, new JsonArray([.. voice.Voices.Select(v => v.DeepClone())]));
                case ("POST", "/api/v1/voices"):
                    var added = new JsonObject { ["id"] = "v2", ["label"] = voice.Form["label"], ["stored"] = new JsonObject { ["durationSeconds"] = 12.0 } };
                    voice.Voices.Add(added);
                    return Json(HttpStatusCode.Created, added.DeepClone());
                case ("GET", "/api/v1/voices/v1/audio"):
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([.. "RIFF"u8, 9]) };
                case ("DELETE", "/api/v1/voices/v1"):
                    return Json(HttpStatusCode.Conflict, new JsonObject { ["title"] = "Referenzstimme in Verwendung" });
                case ("DELETE", _) when path.StartsWith("/api/v1/voices/", StringComparison.Ordinal):
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                case ("POST", "/api/v1/jobs"):
                    return Json(HttpStatusCode.Accepted, Job("QUEUED"));
                case ("GET", "/api/v1/jobs/j1"):
                    string status;
                    lock (voice.JobStatuses)
                    {
                        status = voice.JobStatuses.Count > 1 ? voice.JobStatuses.Dequeue() : voice.JobStatuses.Peek();
                    }
                    return Json(HttpStatusCode.OK, Job(status));
                case ("GET", "/api/v1/jobs/j1/result"):
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(voice.Result) };
                case ("DELETE", "/api/v1/jobs/j1"):
                    voice.DeletedJobs++;
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            JsonObject Job(string status) => new()
            {
                ["jobId"] = "j1",
                ["status"] = status,
                ["startedAtUtc"] = status == "QUEUED" ? null : "2026-09-26T10:00:00+00:00",
                ["estimatedDurationSeconds"] = 600,
                ["error"] = status == "FAILED" ? new JsonObject { ["code"] = "OUT_OF_MEMORY", ["message"] = voice.JobError } : null,
            };
        }

        private static HttpResponseMessage Json(HttpStatusCode status, JsonNode value) =>
            new(status) { Content = new StringContent(value.ToJsonString(), System.Text.Encoding.UTF8, "application/json") };
    }
}

/// <summary>
/// StemMyWav's Mac API: answers POST /api/separate with a ZIP of stems, one second of 16-bit stereo each whose level
/// rises with the length of its name, and GET /api/models with <see cref="Models"/> (404 while null, as the Mac API may).
/// </summary>
public sealed class FakeStems
{
    public Uri? RequestUri { get; private set; }

    public string? Key { get; private set; }

    public int Calls { get; private set; }

    /// <summary>Holds the separation until a test lets it go, to look at the server meanwhile.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    public string[] Files { get; set; } = ["vocals_dry.wav", "vocals_reverb.wav", "instrumental.wav"];

    public JsonArray? Models { get; set; }

    /// <summary>A WAV of one second at 8 kHz whose samples are all <paramref name="level"/> (0–1).</summary>
    public static byte[] Wav(double level, int rate = 8000)
    {
        var data = new byte[rate * 4];
        var sample = (short)(level * short.MaxValue);
        for (var index = 0; index < data.Length; index += 2)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(index), sample);
        }
        var wav = new MemoryStream();
        using (var writer = new BinaryWriter(wav, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("RIFF"u8);
            writer.Write(36 + data.Length);
            writer.Write("WAVEfmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)2);
            writer.Write(rate);
            writer.Write(rate * 4);
            writer.Write((short)4);
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(data.Length);
            writer.Write(data);
        }
        return wav.ToArray();
    }

    public sealed class Handler(FakeStems stems) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/models")
            {
                return stems.Models is { } models
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(models.ToJsonString(), System.Text.Encoding.UTF8, "application/json") }
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            stems.Calls++;
            stems.RequestUri = request.RequestUri;
            stems.Key = request.Headers.GetValues("X-Api-Key").Single();
            if (stems.Gate is { } gate)
            {
                await gate.Task.WaitAsync(cancellationToken);
            }
            if (stems.Status != HttpStatusCode.OK)
            {
                return new HttpResponseMessage(stems.Status)
                {
                    Content = new StringContent("""{"title":"Unbekanntes Modell"}""", System.Text.Encoding.UTF8, "application/problem+json"),
                };
            }
            var zip = new MemoryStream();
            using (var archive = new System.IO.Compression.ZipArchive(zip, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var name in stems.Files)
                {
                    using var entry = archive.CreateEntry(name).Open();
                    entry.Write(Wav(Math.Min(1, name.Length / 20.0)));
                }
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip.ToArray()) };
        }
    }
}

/// <summary>Writes a small FLAC instead of running ffmpeg, and remembers what it was given.</summary>
public sealed class FakeMixer : IAudioMixer
{
    public static readonly byte[] Flac = [.. "fLaC"u8, 7, 7, 7];

    public MixInput? Input { get; private set; }

    /// <summary>What <see cref="EncodeFlacAsync"/> was given, by file name; it fails for names in <see cref="Unencodable"/>.</summary>
    public List<string> Encoded { get; } = [];

    public HashSet<string> Unencodable { get; } = [];

    /// <summary>What <see cref="DecodeAsync"/> wrote, by file name; it copies instead of decoding.</summary>
    public List<string> Decoded { get; } = [];

    public Task DecodeAsync(string input, string output, CancellationToken cancellationToken)
    {
        Decoded.Add(Path.GetFileName(output));
        File.Copy(input, output, overwrite: true);
        return Task.CompletedTask;
    }

    /// <summary>What <see cref="DecodeWaveAsync"/> was given, by file name; it writes a second of 48 kHz WAV.</summary>
    public List<string> Waves { get; } = [];

    public Task DecodeWaveAsync(string input, string output, CancellationToken cancellationToken)
    {
        Waves.Add(Path.GetFileName(input));
        File.WriteAllBytes(output, FakeStems.Wav(0.5, 48000));
        return Task.CompletedTask;
    }

    public Task EncodeFlacAsync(string input, string output, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(input);
        if (Unencodable.Contains(name))
        {
            throw new InvalidOperationException("ffmpeg exited with 1");
        }
        Encoded.Add(name);
        File.WriteAllBytes(output, Flac);
        return Task.CompletedTask;
    }

    public Task MixAsync(MixInput input, string output, CancellationToken cancellationToken)
    {
        Input = input with
        {
            Instrumental = Path.GetFileName(input.Instrumental),
            Vocals = Path.GetFileName(input.Vocals),
            OriginalVocals = Path.GetFileName(input.OriginalVocals),
            Reverb = input.Reverb is null ? null : Path.GetFileName(input.Reverb),
        };
        File.WriteAllBytes(output, Flac);
        return Task.CompletedTask;
    }
}

/// <summary>mlx-audio and ffmpeg: records what it was asked to speak and writes a WAV of the length a test sets.</summary>
public sealed class FakeSpeech : ISpeechEngine
{
    public bool Installed { get; set; } = true;

    public bool CanPrepareVoices { get; set; } = true;

    /// <summary>How long a prepared recording is, after the silence is cut.</summary>
    public double VoiceSeconds { get; set; } = 8;

    /// <summary>Held open to keep a take speaking; completed by default.</summary>
    public TaskCompletionSource Gate { get; set; } = CompletedGate();

    /// <summary>Set to make the next takes fail with it.</summary>
    public string? Failure { get; set; }

    public List<SpeechJob> Jobs { get; } = [];

    public List<CleanJob> Cleanups { get; } = [];

    /// <summary>How long a prepared recording to clean is.</summary>
    public double RecordingSeconds { get; set; } = 90;

    /// <summary>The first four bytes of each recording posted, in hex.</summary>
    public List<string> Recordings { get; } = [];

    public bool Downloaded(SpeechModel model) => model.Id == "chatterbox";

    public async Task PrepareVoiceAsync(string input, string output, CancellationToken cancellationToken)
    {
        lock (Recordings)
        {
            Recordings.Add(Convert.ToHexString(File.ReadAllBytes(input), 0, 4));
        }
        await File.WriteAllBytesAsync(output, Wav(VoiceSeconds), cancellationToken);
    }

    public async Task PrepareRecordingAsync(string input, string output, CancellationToken cancellationToken)
    {
        lock (Recordings)
        {
            Recordings.Add(Convert.ToHexString(File.ReadAllBytes(input), 0, 4));
        }
        await File.WriteAllBytesAsync(output, Wav(RecordingSeconds), cancellationToken);
    }

    public async Task<SpeechResult> CleanAsync(CleanJob job, Action<string> stage, Action<double> progress, CancellationToken cancellationToken)
    {
        lock (Cleanups)
        {
            Cleanups.Add(job);
        }
        stage("loading");
        stage("cleaning");
        progress(0.5);
        await Gate.Task.WaitAsync(cancellationToken);
        if (Failure is { } failure)
        {
            throw new SpeechException(failure);
        }
        await File.WriteAllBytesAsync(job.Output, Wav(RecordingSeconds), cancellationToken);
        return new SpeechResult(1.5, 9.75, 1.2);
    }

    public async Task<SpeechResult> SpeakAsync(SpeechJob job, Action<string> stage, CancellationToken cancellationToken)
    {
        lock (Jobs)
        {
            Jobs.Add(job);
        }
        stage("loading");
        stage("speaking");
        await Gate.Task.WaitAsync(cancellationToken);
        if (Failure is { } failure)
        {
            throw new SpeechException(failure);
        }
        await File.WriteAllBytesAsync(job.Output, Wav(2.5), cancellationToken);
        return new SpeechResult(12.5, 3.25, 4.8);
    }

    /// <summary>16-bit mono PCM at 24 kHz, as ffmpeg writes a recording; silent.</summary>
    public static byte[] Wav(double seconds)
    {
        const int rate = 24000;
        var data = (int)(seconds * rate) * 2;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + data);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(rate);
        writer.Write(rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(data);
        writer.Write(new byte[data]);
        return stream.ToArray();
    }

    private static TaskCompletionSource CompletedGate()
    {
        var gate = new TaskCompletionSource();
        gate.SetResult();
        return gate;
    }
}

/// <summary>A Nextcloud's WebDAV: keeps uploaded files by path, answers MKCOL with 201 once and 405 after.</summary>
public sealed class FakeNextcloud
{
    private readonly Lock _gate = new();

    public Dictionary<string, byte[]> Files { get; } = [];

    public HashSet<string> Folders { get; } = [];

    public List<string> Puts { get; } = [];

    public string? Authorization { get; private set; }

    /// <summary>Answers every request with this instead, e.g. 401 for a wrong password.</summary>
    public HttpStatusCode? Status { get; set; }

    /// <summary>Paths answered with 423, as Nextcloud does while it still holds a lock on the file.</summary>
    public HashSet<string> Locked { get; } = [];

    /// <summary>Holds every request until a test lets it go, to look at the server meanwhile.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public byte[] File(string path)
    {
        lock (_gate)
        {
            return Files[path];
        }
    }

    public string[] PutPaths()
    {
        lock (_gate)
        {
            return [.. Puts];
        }
    }

    public sealed class Handler(FakeNextcloud cloud) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (cloud.Gate is { } gate)
            {
                await gate.Task.WaitAsync(cancellationToken);
            }
            var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath).TrimEnd('/');
            lock (cloud._gate)
            {
                cloud.Authorization = request.Headers.Authorization?.ToString();
                if (cloud.Status is { } status)
                {
                    return new HttpResponseMessage(status);
                }
                if (request.Method.Method == "MKCOL")
                {
                    return new HttpResponseMessage(cloud.Folders.Add(path) ? HttpStatusCode.Created : HttpStatusCode.MethodNotAllowed);
                }
                if (request.Method == HttpMethod.Put)
                {
                    if (cloud.Locked.Contains(path))
                    {
                        return new HttpResponseMessage(HttpStatusCode.Locked);
                    }
                    var folder = path[..path.LastIndexOf('/')];
                    if (!cloud.Folders.Contains(folder))
                    {
                        return new HttpResponseMessage(HttpStatusCode.Conflict);
                    }
                    cloud.Files[path] = body!;
                    cloud.Puts.Add(path);
                    return new HttpResponseMessage(HttpStatusCode.Created);
                }
                return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
            }
        }
    }
}

/// <summary>Writes a few bytes instead of running ffmpeg; can be held back to see a video in the works.</summary>
public sealed class FakeVideoRenderer : Video.IVideoRenderer
{
    public static readonly byte[] Mp4 = [.. "ftypisom"u8, 1, 2, 3];

    /// <summary>False stands for a machine without ffmpeg.</summary>
    public bool Available { get; set; } = true;

    /// <summary>Set to make the render fail like ffmpeg would.</summary>
    public string? Failure { get; set; }

    /// <summary>Set to hold every render until it is released or cancelled.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public Video.VideoLayers? Layers { get; private set; }

    public double? Seconds { get; private set; }

    public async Task RenderAsync(
        Video.VideoState video,
        string flac,
        double? seconds,
        Video.VideoLayers layers,
        string output,
        Action<double> progress,
        CancellationToken cancellationToken)
    {
        Layers = layers;
        Seconds = seconds;
        progress(0.5);
        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }
        if (Failure is { } failure)
        {
            throw new Video.VideoException(failure);
        }
        await File.WriteAllBytesAsync(output, Mp4, cancellationToken);
    }

    public static readonly byte[] SmallMp4 = [.. "ftypisom"u8, 9];

    /// <summary>How often a small copy was made.</summary>
    public int Shrunk { get; private set; }

    public async Task ShrinkAsync(string source, string format, string output, CancellationToken cancellationToken)
    {
        Shrunk++;
        if (Failure is { } failure)
        {
            throw new Video.VideoException(failure);
        }
        await File.WriteAllBytesAsync(output, SmallMp4, cancellationToken);
    }
}

/// <summary>Writes a few bytes as the montage instead of planning and cutting; records what it was asked for.</summary>
public sealed class FakeMontage : Video.IMontageBuilder
{
    public bool Installed { get; set; } = true;

    /// <summary>Set to make the montage fail like the planner would.</summary>
    public string? Failure { get; set; }

    public (string Folder, string? Motif, string Work)? Built { get; private set; }

    public async Task BuildAsync(
        Video.VideoState video,
        string flac,
        string folder,
        string work,
        string output,
        Action<double> progress,
        CancellationToken cancellationToken)
    {
        Built = (folder, video.Motif, work);
        Directory.CreateDirectory(work);
        await File.WriteAllTextAsync(Path.Combine(work, "still-0001.jpg"), "still", cancellationToken);
        progress(0.5);
        if (Failure is { } failure)
        {
            throw new Video.VideoException(failure);
        }
        await File.WriteAllBytesAsync(output, FakeVideoRenderer.Mp4, cancellationToken);
    }
}

/// <summary>mflux: records what it was asked to paint and writes a few JPEG bytes; can be held back to see one in the works.</summary>
public sealed class FakeImages : Images.IImageEngine
{
    public static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 0xFF, 0xD9];

    public bool Installed { get; set; } = true;

    public bool TokenFound { get; set; }

    /// <summary>Held open to keep a picture painting; completed by default.</summary>
    public TaskCompletionSource Gate { get; set; } = CompletedGate();

    /// <summary>Set to make the next pictures fail with it.</summary>
    public string? Failure { get; set; }

    public List<Images.ImageJob> Jobs { get; } = [];

    public bool Downloaded(Images.ImageModel model) => model.Id == "klein-4b";

    public async Task<Images.ImageResult> PaintAsync(Images.ImageJob job, Action<string, double> progress, CancellationToken cancellationToken)
    {
        lock (Jobs)
        {
            Jobs.Add(job);
        }
        progress("loading", 0);
        progress("painting", 0.5);
        await Gate.Task.WaitAsync(cancellationToken);
        if (Failure is { } failure)
        {
            throw new Images.ImageException(failure);
        }
        await File.WriteAllBytesAsync(job.Output, Jpeg, cancellationToken);
        return new Images.ImageResult(20.5, 31.25, 8.9);
    }

    private static TaskCompletionSource CompletedGate()
    {
        var gate = new TaskCompletionSource();
        gate.SetResult();
        return gate;
    }
}

/// <summary>Claude translating a picture's description: off (no key) unless a test switches it on.</summary>
public sealed class FakeTranslator : Images.IPromptTranslator
{
    public bool Configured { get; set; }

    /// <summary>The answer; null stands for English input, a failure or no key alike.</summary>
    public string? Answer { get; set; }

    public List<string> Calls { get; } = [];

    public Task<string?> TranslateAsync(string prompt, CancellationToken cancellationToken)
    {
        lock (Calls)
        {
            Calls.Add(prompt);
        }
        return Task.FromResult(Answer);
    }
}

/// <summary>Claude writing lyrics: off (no key) unless a test switches it on; can be held back or made to fail.</summary>
public sealed class FakeCloudLyrics : ICloudLyricsModel
{
    public bool Configured { get; set; }

    public string Answer { get; set; } = "**Verse:**\nCloud line\n\n[Chorus]\nSky line";

    public TaskCompletionSource Gate { get; set; } = CompletedGate();

    public string? Failure { get; set; }

    public List<(string Model, string System, string Prompt, string? Image)> Calls { get; } = [];

    public async Task<string> WriteAsync(string model, string system, string prompt, string? image, CancellationToken cancellationToken)
    {
        lock (Calls)
        {
            Calls.Add((model, system, prompt, image));
        }
        await Gate.Task.WaitAsync(cancellationToken);
        return Failure is { } failure ? throw new LyricsUnavailableException(failure) : Answer;
    }

    private static TaskCompletionSource CompletedGate()
    {
        var gate = new TaskCompletionSource();
        gate.SetResult();
        return gate;
    }
}

/// <summary>Claude: answers every sketch with <see cref="Plan"/>; can be held back or made to fail.</summary>
public sealed class FakeSketchModel : Sketches.ISketchModel
{
    public Sketches.SketchPlan Plan { get; set; } = SketchSamples.Plan();

    public TaskCompletionSource Gate { get; set; } = CompletedGate();

    public string? Failure { get; set; }

    public List<(string Sketch, string? Instruction)> Calls { get; } = [];

    public List<string> Models { get; } = [];

    public async Task<Sketches.SketchAnswer> PlanAsync(string sketch, string? instruction, string model, CancellationToken cancellationToken)
    {
        lock (Calls)
        {
            Calls.Add((sketch, instruction));
            Models.Add(model);
        }
        await Gate.Task.WaitAsync(cancellationToken);
        if (Failure is { } failure)
        {
            throw new Sketches.SketchModelException(failure);
        }
        return new Sketches.SketchAnswer(Plan, "claude-test", 1200, 9000);
    }

    private static TaskCompletionSource CompletedGate()
    {
        var gate = new TaskCompletionSource();
        gate.SetResult();
        return gate;
    }
}

/// <summary>
/// acestep.cpp: records what it was asked for and writes a WAV per song, with the request as ace-lm would fill it in
/// (lyrics, tempo, key) when the LM runs; can be held back to see a job in the works.
/// </summary>
public sealed class FakeAce : Ace.IAceEngine
{
    public bool Installed { get; set; } = true;

    public Ace.AceModelFiles Models { get; set; } = new(
        ["acestep-5Hz-lm-4B-Q8_0.gguf", "acestep-5Hz-lm-1.7B-Q8_0.gguf"],
        ["acestep-v15-turbo-Q8_0.gguf", "acestep-v15-sft-Q8_0.gguf"],
        true,
        true,
        ["singer.safetensors"]);

    /// <summary>Held open to keep a job running; completed by default.</summary>
    public TaskCompletionSource Gate { get; set; } = CompletedGate();

    /// <summary>Set to make the next jobs fail with it.</summary>
    public string? Failure { get; set; }

    public List<Ace.AceRun> Runs { get; } = [];

    /// <summary>What the source WAV held when the job ran, for covers.</summary>
    public byte[]? Source { get; private set; }

    public Ace.AceModelFiles Files() => Models;

    public async Task<IReadOnlyList<Ace.AceOutput>> GenerateAsync(Ace.AceRun run, Action<string, double> progress, CancellationToken cancellationToken)
    {
        lock (Runs)
        {
            Runs.Add(run);
        }
        Source = run.SourceAudio is { } source ? await File.ReadAllBytesAsync(source, cancellationToken) : null;
        progress(run.UsesLm ? "composing" : "synthesizing", 0.4);
        await Gate.Task.WaitAsync(cancellationToken);
        if (Failure is { } failure)
        {
            throw new Ace.AceException(failure);
        }
        var batch = (int?)run.Request["lm_batch_size"] ?? (int?)run.Request["synth_batch_size"] ?? 1;
        List<Ace.AceOutput> outputs = [];
        for (var i = 0; i < batch; i++)
        {
            var wave = Path.Combine(run.WorkDirectory, $"request{i}0.wav");
            await File.WriteAllBytesAsync(wave, FakeSpeech.Wav(2.5), cancellationToken);
            var request = run.Request.DeepClone().AsObject();
            request["seed"] = (long)run.Request["seed"]! + i;
            if (run.UsesLm)
            {
                request["lyrics"] ??= $"[Verse]\nWritten by the LM {i}";
                request["bpm"] ??= 120;
                request["keyscale"] ??= "A minor";
                request["audio_codes"] = "1,2,3";
            }
            outputs.Add(new Ace.AceOutput(wave, request));
        }
        return outputs;
    }

    private static TaskCompletionSource CompletedGate()
    {
        var gate = new TaskCompletionSource();
        gate.SetResult();
        return gate;
    }
}

/// <summary>Basic Pitch: records what it was asked to convert and writes a few MIDI bytes, or fails as told.</summary>
public sealed class FakeAudioMidi : AudioMidi.IAudioMidiEngine
{
    public static readonly byte[] Midi = [.. "MThd"u8, 0, 0, 0, 6, 0, 1, 0, 1, 1, 0xE0];

    public bool Installed { get; set; } = true;

    /// <summary>How many notes it reports; 0 stands for a track without any.</summary>
    public int Notes { get; set; } = 42;

    /// <summary>Set to make the next conversions fail with it.</summary>
    public string? Failure { get; set; }

    public List<AudioMidi.AudioMidiJob> Jobs { get; } = [];

    /// <summary>The file a job writes, for tests that read it back; <see cref="Midi"/> (a bare header) without one.</summary>
    public Func<AudioMidi.AudioMidiJob, byte[]>? Output { get; set; }

    /// <summary>The audio each job got, read while it still existed (an upload's folder goes with the request).</summary>
    public List<byte[]> Audio { get; } = [];

    public async Task<AudioMidi.AudioMidiResult> ConvertAsync(AudioMidi.AudioMidiJob job, CancellationToken cancellationToken)
    {
        lock (Jobs)
        {
            Jobs.Add(job);
            Audio.Add(File.ReadAllBytes(job.Audio));
        }
        if (Failure is { } failure)
        {
            throw new AudioMidi.AudioMidiException(failure);
        }
        await File.WriteAllBytesAsync(job.Output, Output?.Invoke(job) ?? Midi, cancellationToken);
        return new AudioMidi.AudioMidiResult(Notes, 1.5);
    }
}

/// <summary>Loki's push endpoint: keeps every body it is sent.</summary>
public sealed class FakeLoki
{
    private readonly Lock _gate = new();
    private readonly List<(string Path, JsonObject Body)> _pushes = [];

    /// <summary>Answers the next pushes with this status before taking them, e.g. 503 while Loki starts.</summary>
    public Queue<HttpStatusCode> Refusals { get; } = new();

    public (string Path, JsonObject Body)[] Pushes()
    {
        lock (_gate)
        {
            return [.. _pushes];
        }
    }

    /// <summary>Every line pushed so far with its labels.</summary>
    public (JsonObject Labels, string Line)[] Lines() =>
        [.. Pushes().SelectMany(push => push.Body["streams"]!.AsArray()
            .SelectMany(stream => stream!["values"]!.AsArray()
                .Select(value => (stream["stream"]!.AsObject(), value![1]!.GetValue<string>()))))];

    public sealed class Handler(FakeLoki loki) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            lock (loki._gate)
            {
                if (loki.Refusals.TryDequeue(out var refusal))
                {
                    return new HttpResponseMessage(refusal);
                }
                loki._pushes.Add((request.RequestUri!.AbsolutePath, body));
            }
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
}

/// <summary>librosa: hears what it is given per song id; off by default.</summary>
public sealed class FakeSongListener : Analysis.ISongListener
{
    public bool Installed { get; set; }

    /// <summary>Tempo and key by song id; a song missing here fails.</summary>
    public Dictionary<string, (double Tempo, string Key)> Songs { get; } = [];

    public List<IReadOnlyList<Analysis.SongToHear>> Batches { get; } = [];

    public Task<IReadOnlyList<Analysis.HeardSong>> HearAsync(IReadOnlyList<Analysis.SongToHear> songs, CancellationToken cancellationToken)
    {
        lock (Batches)
        {
            Batches.Add(songs);
        }
        return Task.FromResult<IReadOnlyList<Analysis.HeardSong>>(
        [
            .. songs.Select(s => Songs.TryGetValue(s.Id, out var heard)
                ? new Analysis.HeardSong(s.Id, heard.Tempo, heard.Key, null)
                : new Analysis.HeardSong(s.Id, null, null, "unreadable")),
        ]);
    }
}

/// <summary>Whisper: hears the words it is given; off by default, so the other tests' songs are not timed.</summary>
public sealed class FakeListener : Timing.IWordListener
{
    public bool Installed { get; set; }

    public List<Timing.HeardWord> Words { get; set; } = [];

    /// <summary>Set to make the next songs fail with it.</summary>
    public string? Failure { get; set; }

    public List<(string Audio, string? Language, string Prompt)> Calls { get; } = [];

    public Task<IReadOnlyList<Timing.HeardWord>> ListenAsync(string audio, string? language, string prompt, CancellationToken cancellationToken)
    {
        lock (Calls)
        {
            Calls.Add((audio, language, prompt));
        }
        return Failure is not null
            ? Task.FromException<IReadOnlyList<Timing.HeardWord>>(new Timing.TimingException(Failure))
            : Task.FromResult<IReadOnlyList<Timing.HeardWord>>(Words);
    }
}

public sealed class FakeSinging : Singing.ISingingEngine
{
    public bool Installed { get; set; } = true;

    public bool ModelDownloaded { get; set; } = true;

    /// <summary>Held open to keep a take singing; completed by default.</summary>
    public TaskCompletionSource Gate { get; set; } = CompletedGate();

    /// <summary>Set to make the next takes fail with it.</summary>
    public string? Failure { get; set; }

    public List<Singing.SingingJob> Jobs { get; } = [];

    public async Task<Singing.SingingResult> SingAsync(
        Singing.SingingJob job,
        Action<string> stage,
        Action<double> progress,
        Action<Singing.SingingAlignment> aligned,
        CancellationToken cancellationToken)
    {
        lock (Jobs)
        {
            Jobs.Add(job);
        }
        aligned(new Singing.SingingAlignment(job.Notes.Count, 0, 0));
        stage("loading");
        stage("singing");
        progress(0.5);
        await Gate.Task.WaitAsync(cancellationToken);
        if (Failure is { } failure)
        {
            throw new Singing.SingingException(failure);
        }
        await File.WriteAllBytesAsync(job.Output, FakeSpeech.Wav(3), cancellationToken);
        return new Singing.SingingResult(20.5, 6.25, 5.1);
    }

    public List<(string Recording, string Lyrics)> Voices { get; } = [];

    /// <summary>Set to make the next voice fail with it, as the script does when it hears no singing.</summary>
    public string? VoiceFailure { get; set; }

    /// <summary>Writes a second of prompt and one segment of metadata: the first two words over two notes.</summary>
    public async Task<Singing.SingingVoiceMade> MakeVoiceAsync(string recording, string lyrics, string audio, string metadata, CancellationToken cancellationToken)
    {
        if (!Installed)
        {
            throw new Singing.SingingException("SoulX-Singer is not installed; run deploy/install-singing.sh.", Microsoft.AspNetCore.Http.StatusCodes.Status501NotImplemented);
        }
        lock (Voices)
        {
            Voices.Add((recording, lyrics));
        }
        if (VoiceFailure is { } failure)
        {
            throw new Singing.SingingException(failure, Microsoft.AspNetCore.Http.StatusCodes.Status422UnprocessableEntity);
        }
        var words = lyrics.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(2).ToArray();
        await File.WriteAllBytesAsync(audio, FakeSpeech.Wav(1), cancellationToken);
        await File.WriteAllTextAsync(metadata, $$"""[{"text": "{{string.Join(' ', words)}}", "note_pitch": "60 62"}]""", cancellationToken);
        return new Singing.SingingVoiceMade(1, 6.5, 2, 0, 0, Math.Max(0, lyrics.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length - 2), string.Join(' ', words), 0.5, 60, 62);
    }

    /// <summary>The script's rules in small: a note's own word, "-" holding the one before, else one word of the lyrics per note.</summary>
    public Task<Singing.SingingAligned> AlignAsync(IReadOnlyList<Singing.SungNote> notes, string? lyrics, CancellationToken cancellationToken)
    {
        if (!Installed)
        {
            throw new Singing.SingingException("SoulX-Singer is not installed; run deploy/install-singing.sh.", Microsoft.AspNetCore.Http.StatusCodes.Status501NotImplemented);
        }
        var order = notes.Select((note, index) => (note, index)).OrderBy(n => n.note.Start).ToList();
        var result = new Singing.AlignedNote[notes.Count];
        var own = notes.Any(n => !string.IsNullOrWhiteSpace(n.Lyric));
        var words = (lyrics ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        string? last = null;
        var next = 0;
        foreach (var (note, index) in order)
        {
            var word = own ? note.Lyric?.Trim() : next < words.Length ? words[next++] : null;
            result[index] = word switch
            {
                "-" when last is not null => new(last, 3, last.ToUpperInvariant()),
                null or "" or "-" => new(null, 1, ""),
                _ => new(last = word, 2, word.ToUpperInvariant()),
            };
        }
        return Task.FromResult(new Singing.SingingAligned(result, Math.Max(0, words.Length - next)));
    }

    private static TaskCompletionSource CompletedGate()
    {
        var gate = new TaskCompletionSource();
        gate.SetResult();
        return gate;
    }
}
