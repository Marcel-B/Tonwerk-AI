using System.Text.Json.Serialization;
using YueUI.Api;
using YueUI.Api.AudioMidi;
using YueUI.Api.Backup;
using YueUI.Api.Data;
using YueUI.Api.Export;
using YueUI.Api.Library;
using YueUI.Api.Logs;
using YueUI.Api.Loras;
using YueUI.Api.Logic;
using YueUI.Api.Monitoring;
using YueUI.Api.Lyrics;
using YueUI.Api.Timing;
using YueUI.Api.Push;
using YueUI.Api.Queue;
using YueUI.Api.Share;
using YueUI.Api.Images;
using YueUI.Api.Ace;
using YueUI.Api.Speech;
using YueUI.Api.Video;
using YueUI.Api.Voices;
using YueUI.Api.Worker;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = PublishedContentRoot(),
});

// YuE Studio's installation and song folder; see YuePaths for the defaults.
builder.Services.AddSingleton(YuePaths.FromConfiguration(builder.Configuration));
builder.Services.AddSingleton<SongLibrary>();
builder.Services.AddSingleton<LoraLibrary>();
builder.Services.AddSingleton<TranscriptionLibrary>();
builder.Services.AddSingleton<IWorkerLauncher, PythonWorkerLauncher>();
builder.Services.AddSingleton<IStudioDetector, StudioDetector>();
builder.Services.AddSingleton(TimeProvider.System);
// One worker for the whole server; as a hosted service it is asked to quit (and free its memory) on shutdown.
// What every browser sees and hears about; the worker hands its part over itself (Status/StatusHub.cs). Built from a
// factory since the worker publishes through the hub: the hub only asks for the worker once someone subscribes.
builder.Services.AddSingleton(services => new YueUI.Api.Status.StatusHub(
    () => services.GetRequiredService<WorkerHost>(),
    services.GetRequiredService<Microsoft.Extensions.Options.IOptions<QueueOptions>>()));
builder.Services.AddSingleton<WorkerHost>();
builder.Services.AddHostedService(services => services.GetRequiredService<WorkerHost>());
// Which large model holds the memory; on 24 GB they take turns (Memory/ModelMemory.cs).
builder.Services.AddSingleton<YueUI.Api.Memory.ModelMemory>();
// Lyrics drafts from LM Studio; loading a 15 GB model and writing take far longer than HttpClient's 100 s default allows for.
builder.Services.Configure<LyricsOptions>(builder.Configuration.GetSection(LyricsOptions.Section));
builder.Services.AddSingleton<ILmStudioStarter, LmsCli>();
builder.Services.AddSingleton<LyricsWriter>();
builder.Services.AddHttpClient(LyricsWriter.HttpClientName, client => client.Timeout = TimeSpan.FromMinutes(10));
// Logic Pro projects and MIDI read back into scores, by YueToLogic.Core in this process.
builder.Services.Configure<LogicOptions>(builder.Configuration.GetSection(LogicOptions.Section));
builder.Services.AddYueToLogic();

// Sharing a song from the phone as a small AAC instead of the FLAC.
builder.Services.Configure<LoudnessOptions>(builder.Configuration.GetSection(LoudnessOptions.Section));
builder.Services.AddSingleton<ILoudness, Loudness>();
builder.Services.AddSingleton<IAudioEncoder, AacEncoder>();
builder.Services.AddSingleton<StreamCopies>();
builder.Services.AddHostedService<StreamCopyMaker>();
// Exporting it as MP3, M4A or FLAC with title, lyrics and cover.
builder.Services.AddSingleton<IAudioTagger, TagLibTagger>();
builder.Services.AddSingleton<TaggedFiles>();

// Web Push: notifies subscribed browsers (the app on a phone's home screen) when something finishes.
builder.Services.Configure<PushOptions>(builder.Configuration.GetSection(PushOptions.Section));
builder.Services.AddSingleton<PushStore>();
builder.Services.AddSingleton<IPushSender, WebPushSender>();
builder.Services.AddHttpClient(WebPushSender.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHostedService<PushNotifier>();

// The app's own state (playlists, titles, ratings, …) in one SQLite file, shared by every browser.
builder.Services.Configure<DataOptions>(builder.Configuration.GetSection(DataOptions.Section));
builder.Services.AddSingleton<SqliteDatabase>();
builder.Services.AddSingleton<SqlitePlaylistStore>();
builder.Services.AddSingleton<SqliteRunTitleStore>();
builder.Services.AddSingleton<SqliteSongRatingStore>();
builder.Services.AddSingleton<SqliteSongNoteStore>();
builder.Services.AddSingleton<SqlitePromptTemplateStore>();
builder.Services.AddSingleton<SqliteVersionStore>();
builder.Services.AddSingleton<SqliteStemStore>();
builder.Services.AddSingleton<SqliteSwapStore>();
builder.Services.AddSingleton<SqliteVideoStore>();
builder.Services.AddSingleton<SqliteCoverStore>();
builder.Services.AddSingleton<SqliteInstrumentStore>();
builder.Services.AddSingleton<SqliteLogicPresetStore>();
builder.Services.AddSingleton<SqliteSynthStore>();

// Songs sung with a reference voice: mlx-audio-separator separates, Seed-VC converts (both run here when installed,
// else StemMyWav's and ChangeMyVoice's APIs), ffmpeg mixes. A slow separation model takes three times as long as the song.
builder.Services.Configure<VoiceOptions>(builder.Configuration.GetSection(VoiceOptions.Section));
builder.Services.AddSingleton<SqliteReferenceVoiceStore>();
builder.Services.AddSingleton<VoiceClient>();
builder.Services.AddSingleton<SeedVcVoices>();
builder.Services.AddSingleton<VoiceEngine>();
builder.Services.AddSingleton<StemClient>();
builder.Services.AddSingleton<MlxStemSeparator>();
builder.Services.AddSingleton<StemSeparator>();
builder.Services.AddSingleton<IAudioMixer, FfmpegMixer>();
builder.Services.AddHttpClient(VoiceClient.HttpClientName, client => client.Timeout = TimeSpan.FromMinutes(10));
builder.Services.AddHttpClient(StemClient.HttpClientName, client => client.Timeout = TimeSpan.FromHours(2));
builder.Services.AddSingleton<VoiceConverter>();
builder.Services.AddHostedService(services => services.GetRequiredService<VoiceConverter>());
builder.Services.AddHostedService<AutoVersions>();
builder.Services.AddSingleton<QuickSongs>();
builder.Services.Configure<TimingOptions>(builder.Configuration.GetSection(TimingOptions.Section));
builder.Services.AddSingleton<TimingFiles>();
builder.Services.AddSingleton<IWordListener, WhisperListener>();
builder.Services.AddSingleton<LyricsTimer>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<LyricsTimer>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<QuickSongs>());

// Music videos: the browser draws the still layers, ffmpeg animates and encodes them, one at a time.
builder.Services.AddSingleton<IVideoRenderer, FfmpegVideoRenderer>();
builder.Services.Configure<MontageOptions>(builder.Configuration.GetSection(MontageOptions.Section));
builder.Services.AddSingleton<IMontageBuilder, MontageBuilder>();
builder.Services.AddSingleton<VideoMaker>();
builder.Services.AddHostedService(services => services.GetRequiredService<VideoMaker>());

// The speech lab: text spoken by local models (mlx-audio in its own Python environment), one take at a time.
builder.Services.Configure<SpeechOptions>(builder.Configuration.GetSection(SpeechOptions.Section));
builder.Services.AddSingleton<SqliteSpeechStore>();
builder.Services.AddSingleton<ISpeechEngine, MlxAudioEngine>();
builder.Services.AddSingleton<SpeechLab>();
builder.Services.AddHostedService(services => services.GetRequiredService<SpeechLab>());
builder.Services.Configure<ImageOptions>(builder.Configuration.GetSection(ImageOptions.Section));
builder.Services.AddSingleton<IImageEngine, MfluxEngine>();
builder.Services.AddSingleton<ImageMaker>();
builder.Services.AddHostedService(services => services.GetRequiredService<ImageMaker>());
// ACE-Step 1.5 through acestep.cpp: songs of a second engine, one job at a time, into a library folder of their own.
builder.Services.Configure<AceOptions>(builder.Configuration.GetSection(AceOptions.Section));
builder.Services.AddSingleton<SqliteAceStore>();
builder.Services.AddSingleton<IAceEngine, AceCppEngine>();
builder.Services.AddSingleton<AceComposer>();
builder.Services.AddHostedService(services => services.GetRequiredService<AceComposer>());
builder.Services.Configure<AudioMidiOptions>(builder.Configuration.GetSection(AudioMidiOptions.Section));
builder.Services.AddSingleton<IAudioMidiEngine, BasicPitchEngine>();
builder.Services.AddSingleton<StemParts>();

// Songs, renders and lyrics drafts wait here for the memory instead of being refused; kept in the database.
builder.Services.Configure<QueueOptions>(builder.Configuration.GetSection(QueueOptions.Section));
builder.Services.AddSingleton<SqliteJobStore>();
builder.Services.AddSingleton<JobQueue>();
builder.Services.AddHostedService(services => services.GetRequiredService<JobQueue>());

// Nightly backup of the database and the songs into a Nextcloud over WebDAV; off until Backup is configured.
builder.Services.Configure<BackupOptions>(builder.Configuration.GetSection(BackupOptions.Section));
builder.Services.AddSingleton<WebDavClient>();
builder.Services.AddHttpClient(WebDavClient.HttpClientName);
builder.Services.AddSingleton<CloudBackup>();
builder.Services.AddHostedService(services => services.GetRequiredService<CloudBackup>());

// Every log message, the worker's output and the updater's log in daily files, read on the log page.
builder.Services.Configure<LogOptions>(builder.Configuration.GetSection(LogOptions.Section));
builder.Services.AddSingleton<LogFeed>();
builder.Services.AddSingleton<LogStore>();
builder.Services.AddSingleton<ILoggerProvider, LogStoreProvider>();
builder.Services.AddSingleton<LogReport>();

builder.Services.AddSingleton<YueUI.Api.Storage.StorageScanner>();

// Metrics for Prometheus (GET /metrics) and the log for Loki, collected by a monitoring stack on another machine
// (deploy/monitoring/); shipping to Loki is off until Monitoring:Loki:Url is set.
builder.Services.Configure<MonitoringOptions>(builder.Configuration.GetSection(MonitoringOptions.Section));
builder.Services.AddSingleton<JobMetrics>();
builder.Services.AddHostedService(services => services.GetRequiredService<JobMetrics>());
builder.Services.AddHostedService<LokiShipper>();
// Without its loggers: each push would log four lines of its own, which the next push would send, forever.
builder.Services.AddHttpClient(LokiShipper.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30)).RemoveAllLoggers();

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase)));
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler();
}

app.UseClientAppAssets();

app.MapOpenApi("/api/openapi");

var api = app.MapGroup("/api");
// HEAD as well, since uptime monitors often probe with it.
api.MapMethods("/health", ClientAppEndpoints.GetAndHead, () => Results.Text("ok"));
api.MapWorkerEndpoints();
api.MapLoraEndpoints();
api.MapLibraryEndpoints();
api.MapTimingEndpoints();
api.MapExportEndpoints();
api.MapCoverEndpoints();
api.MapTranscriptionEndpoints();
api.MapLyricsEndpoints();
api.MapLogicEndpoints();
api.MapInstrumentEndpoints();
api.MapSynthEndpoints();
api.MapPushEndpoints();
api.MapPlaylistEndpoints();
api.MapTemplateEndpoints();
api.MapVoiceEndpoints();
api.MapStemEndpoints();
api.MapSwapEndpoints();
api.MapVideoEndpoints();
api.MapQueueEndpoints();
api.MapSpeechEndpoints();
api.MapImageEndpoints();
api.MapAceEndpoints();
api.MapAudioMidiEndpoints();
api.MapBackupEndpoints();
api.MapLogEndpoints();
api.MapStorageEndpoints();

app.MapMetrics();
app.MapClientApp();

app.Run();

// ASP.NET Core looks for appsettings.json and wwwroot in the working directory. A published app started from
// elsewhere (e.g. by launchd) would then miss its web frontend, so use the app's own directory instead.
static string? PublishedContentRoot()
{
    const string settings = "appsettings.json";
    var appDirectory = AppContext.BaseDirectory;
    return !File.Exists(Path.Combine(Directory.GetCurrentDirectory(), settings)) && File.Exists(Path.Combine(appDirectory, settings))
        ? appDirectory
        : null;
}

/// <summary>Entry point; public so that integration tests can start the app with WebApplicationFactory.</summary>
public partial class Program;
