using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using YueUI.Api.Data;
using YueUI.Api.Library;
using YueUI.Api.Push;
using YueUI.Api.Worker;

namespace YueUI.Api.Backup;

/// <summary>How the last backup went.</summary>
/// <param name="Archive">The ZIP with the database, named by weekday (<c>tonwerk-mon.zip</c>).</param>
/// <param name="Files">Files uploaded besides the ZIP: new or changed songs, versions, stems, …</param>
/// <param name="Error">Why it failed; files uploaded before that stay uploaded and are not sent again.</param>
public sealed record BackupRun(
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    bool Success,
    string? Archive,
    int Files,
    long Bytes,
    string? Error,
    bool Scheduled);

/// <param name="Configured">A Nextcloud address, user and password are set.</param>
/// <param name="Done">While running: files handled of <paramref name="Total"/>.</param>
/// <param name="NextRun">The next nightly backup, null when only manual backups are configured.</param>
public sealed record BackupStatus(
    bool Configured,
    bool Running,
    int Done,
    int Total,
    BackupRun? Last,
    DateTimeOffset? NextRun,
    bool Files);

/// <summary>
/// Backs Tonwerk's data up into a Nextcloud over WebDAV, every night and on request, after the model of module-o-mat's
/// <c>RemoteBackupRunner</c>: a ZIP named by weekday, so the Nextcloud keeps the last seven, holds a consistent copy
/// of <c>yueui.db</c> (SQLite's online backup, since the app may be writing) and <c>push.json</c>.
/// </summary>
/// <remarks>
/// The songs are too large to zip every night over a home upload, so they go as files into folders of their own
/// (<c>songs/&lt;run&gt;/songN/…</c>, <c>versions/</c>, <c>stems/</c>, <c>covers/</c>, <c>speech/voices/</c>,
/// <c>speech/takes/</c>, <c>singing/voices/</c>, <c>transcriptions/</c>, <c>loras/</c>) and only when new or changed since the last upload, by size and
/// modification time remembered in <c>backup.json</c> next to the database. Nothing is deleted in the Nextcloud: a
/// song deleted by mistake stays there. Left out on purpose: the streaming copies (made again from the FLAC), the
/// speech lab's Python environment and models (downloaded again), and songs the worker is still on, or files written
/// in the last two minutes (<see cref="BackupOptions.Settle"/>), which the next backup takes.
/// </remarks>
public sealed class CloudBackup(
    IOptions<BackupOptions> options,
    WebDavClient webDav,
    SqliteDatabase database,
    SqliteCoverStore covers,
    IOptions<PushOptions> pushOptions,
    YuePaths paths,
    SongLibrary library,
    WorkerHost host,
    PushStore pushStore,
    IPushSender pushSender,
    TimeProvider time,
    ILogger<CloudBackup> logger) : BackgroundService
{
    /// <summary>How often the scheduler looks at the clock.</summary>
    public static TimeSpan CheckInterval { get; set; } = TimeSpan.FromMinutes(1);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly Lock _gate = new();
    private State? _state;
    private Task? _running;
    private int _done;
    private int _total;
    private DateTimeOffset? _failedAt;

    private string StatePath => Path.Combine(database.Directory, "backup.json");

    public BackupStatus Status
    {
        get
        {
            var settings = options.Value;
            lock (_gate)
            {
                var state = LoadState();
                return new BackupStatus(
                    settings.Configured,
                    _running is { IsCompleted: false },
                    _done,
                    _total,
                    state.Last,
                    settings.Configured ? NextRun(state) : null,
                    settings.Files);
            }
        }
    }

    /// <summary>Starts a backup now; false while one runs.</summary>
    public bool Start() => StartRun(scheduled: false) is not null;

    /// <summary>The running backup, for tests to wait on.</summary>
    public Task Running
    {
        get
        {
            lock (_gate)
            {
                return _running ?? Task.CompletedTask;
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Configured)
        {
            logger.LogInformation("Nextcloud backup is off: no NEXTCLOUD_WEBDAV_URL, NEXTCLOUD_USERNAME and NEXTCLOUD_APP_PASSWORD in {EnvFile} or the environment", options.Value.EnvFile);
        }
        using var timer = new PeriodicTimer(CheckInterval, time);
        try
        {
            do
            {
                if (Due())
                {
                    _ = StartRun(scheduled: true);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // A backup cut off by the shutdown is remembered up to its last batch of files and goes on next time.
        }
    }

    private bool Due()
    {
        var settings = options.Value;
        if (!settings.Configured || settings.DailyAt is null)
        {
            return false;
        }
        lock (_gate)
        {
            if (_running is { IsCompleted: false })
            {
                return false;
            }
            if (_failedAt is { } failed && time.GetUtcNow() - failed < settings.RetryAfter)
            {
                return false;
            }
            return NextRun(LoadState()) is { } next && next <= time.GetUtcNow();
        }
    }

    /// <summary>Today's start if today's nightly backup has not succeeded yet, otherwise tomorrow's.</summary>
    private DateTimeOffset? NextRun(State state)
    {
        var settings = options.Value;
        if (settings.DailyAt is not { } at)
        {
            return null;
        }
        var zone = settings.Zone;
        var now = TimeZoneInfo.ConvertTime(time.GetUtcNow(), zone);
        var today = DateOnly.FromDateTime(now.DateTime);
        var day = state.LastDaily is { } last && last >= today ? today.AddDays(1) : today;
        var local = day.ToDateTime(at);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    private Task? StartRun(bool scheduled)
    {
        lock (_gate)
        {
            if (!options.Value.Configured || _running is { IsCompleted: false })
            {
                return null;
            }
            _done = 0;
            _total = 0;
            _running = Task.Run(() => RunAsync(scheduled));
            return _running;
        }
    }

    private async Task RunAsync(bool scheduled)
    {
        var started = time.GetUtcNow();
        var settings = options.Value;
        var zone = settings.Zone;
        string? archive = null;
        var files = 0;
        long bytes = 0;
        string? error = null;
        var scratch = Directory.CreateTempSubdirectory("tonwerk-backup-").FullName;
        logger.LogInformation("Nextcloud backup started ({Kind})", scheduled ? "nightly" : "manual");
        try
        {
            webDav.Forget();
            await webDav.EnsureFolderAsync("", CancellationToken.None);

            archive = $"tonwerk-{TimeZoneInfo.ConvertTime(started, zone).DayOfWeek.ToString()[..3].ToLowerInvariant()}.zip";
            var zip = Path.Combine(scratch, archive);
            WriteArchive(zip, scratch);
            await webDav.PutAsync(archive, zip, CancellationToken.None);
            bytes += new FileInfo(zip).Length;

            if (settings.Files)
            {
                (files, var sent, var skipped) = await UploadFilesAsync();
                bytes += sent;
                // Failed, so that a nightly backup tries again after RetryAfter instead of tomorrow.
                if (skipped.Count > 0)
                {
                    error = skipped.Count == 1
                        ? $"{skipped[0]}; the next backup tries it again"
                        : $"{skipped.Count} files could not be uploaded yet, e.g. {skipped[0]}; the next backup tries them again";
                }
            }
        }
        catch (Exception exception) when (exception is WebDavException or HttpRequestException or IOException or UnauthorizedAccessException or SqliteException)
        {
            error = exception.Message;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Nextcloud backup failed unexpectedly");
            error = exception.Message;
        }
        finally
        {
            try
            {
                Directory.Delete(scratch, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        var run = new BackupRun(started, time.GetUtcNow(), error is null, archive, files, bytes, error, scheduled);
        lock (_gate)
        {
            var state = LoadState();
            state.Last = run;
            if (scheduled && error is null)
            {
                state.LastDaily = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(started, zone).DateTime);
            }
            _failedAt = error is null ? null : time.GetUtcNow();
            SaveState(state);
        }
        if (error is null)
        {
            logger.LogInformation("Nextcloud backup done: {Archive} and {Files} files, {Bytes} bytes", archive, files, bytes);
        }
        else
        {
            logger.LogWarning("Nextcloud backup failed: {Error}", error);
            await NotifyAsync(error);
        }
    }

    /// <summary>The database through SQLite's online backup, which copies a consistent state while others write.</summary>
    private void WriteArchive(string zip, string scratch)
    {
        var copy = Path.Combine(scratch, "yueui.db");
        using (var source = database.Open())
        using (var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copy, Pooling = false }.ToString()))
        {
            target.Open();
            source.BackupDatabase(target);
        }
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        archive.CreateEntryFromFile(copy, "yueui.db", CompressionLevel.Optimal);
        var push = pushOptions.Value.ResolvedDataPath;
        if (File.Exists(push))
        {
            archive.CreateEntryFromFile(push, "push.json", CompressionLevel.Optimal);
        }
    }

    /// <summary>
    /// Uploads what is new or changed. A file the Nextcloud refuses for the moment (<see cref="WebDavException.Transient"/>)
    /// is tried again after <see cref="BackupOptions.TransientRetryDelay"/>, then left for the next backup while the
    /// others go on; any other refusal (password, full disk) ends the backup, since every file would meet it.
    /// </summary>
    private async Task<(int Files, long Bytes, List<string> Skipped)> UploadFilesAsync()
    {
        var candidates = Candidates().ToList();
        Dictionary<string, string> uploaded;
        lock (_gate)
        {
            uploaded = new Dictionary<string, string>(LoadState().Files, StringComparer.Ordinal);
            _total = candidates.Count;
        }
        var settled = time.GetUtcNow() - options.Value.Settle;
        var count = 0;
        long bytes = 0;
        var skipped = new List<string>();
        try
        {
            foreach (var (remote, file) in candidates)
            {
                Interlocked.Increment(ref _done);
                var info = new FileInfo(file);
                if (!info.Exists || info.LastWriteTimeUtc > settled.UtcDateTime)
                {
                    continue;
                }
                var stamp = $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
                if (uploaded.GetValueOrDefault(remote) == stamp)
                {
                    continue;
                }
                if (remote.LastIndexOf('/') is var slash and > 0)
                {
                    await webDav.EnsureFoldersAsync(remote[..slash], CancellationToken.None);
                }
                if (!await PutPatientlyAsync(remote, file, skipped))
                {
                    continue;
                }
                uploaded[remote] = stamp;
                count++;
                bytes += info.Length;
                // Remembered as it goes, so that a backup cut short by a restart does not send it all again.
                if (count % 20 == 0)
                {
                    Remember(uploaded);
                }
            }
        }
        finally
        {
            Remember(uploaded);
        }
        return (count, bytes, skipped);
    }

    private async Task<bool> PutPatientlyAsync(string remote, string file, List<string> skipped)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await webDav.PutAsync(remote, file, CancellationToken.None);
                return true;
            }
            catch (WebDavException exception) when (exception.Transient)
            {
                if (attempt >= 3)
                {
                    logger.LogWarning("Leaving {Path} for the next backup: {Error}", remote, exception.Message);
                    skipped.Add(exception.Message);
                    return false;
                }
                await Task.Delay(options.Value.TransientRetryDelay * attempt, time);
            }
        }
    }

    private void Remember(Dictionary<string, string> uploaded)
    {
        lock (_gate)
        {
            var state = LoadState();
            state.Files = new Dictionary<string, string>(uploaded, StringComparer.Ordinal);
            SaveState(state);
        }
    }

    /// <summary>Remote path and local file of everything that goes along besides the ZIP.</summary>
    private IEnumerable<(string Remote, string File)> Candidates()
    {
        // YuE's runs and ACE-Step's alike; RunNames only takes the worker's run names, which leaves transcriptions and
        // strays out. Both go under songs/, as the names do not repeat.
        foreach (var run in library.RunNames().Distinct().Order(StringComparer.Ordinal))
        {
            foreach (var song in library.SongDirectories(run) ?? [])
            {
                var name = Path.GetFileName(song);
                if (host.IsWorkingOn(run, name))
                {
                    continue;
                }
                foreach (var file in Below(song, $"songs/{run}/{name}"))
                {
                    yield return file;
                }
            }
        }
        foreach (var file in Below(paths.TranscriptionsDir, "transcriptions"))
        {
            yield return file;
        }
        // The versions folder also holds their streaming copies (.m4a), which are made again from the FLAC.
        foreach (var file in Below(Path.Combine(database.Directory, "versions"), "versions").Where(f => f.File.EndsWith(".flac", StringComparison.OrdinalIgnoreCase)))
        {
            yield return file;
        }
        foreach (var file in Below(Path.Combine(database.Directory, "stems"), "stems"))
        {
            yield return file;
        }
        foreach (var file in Below(Path.Combine(database.Directory, "swaps"), "swaps"))
        {
            yield return file;
        }
        foreach (var file in Below(covers.Folder, "covers"))
        {
            yield return file;
        }
        // Not the whole speech folder: it may also hold the lab's Python environment and models, gigabytes.
        foreach (var file in Below(Path.Combine(database.Directory, "speech", "voices"), "speech/voices"))
        {
            yield return file;
        }
        foreach (var file in Below(Path.Combine(database.Directory, "speech", "takes"), "speech/takes"))
        {
            yield return file;
        }
        // Recordings of one's own singing; the takes are sung again from their notes.
        foreach (var file in Below(Path.Combine(database.Directory, "singing", "voices"), "singing/voices"))
        {
            yield return file;
        }
        // Hours of training each; the trainer's latent cache (.lora-cache) and logs are left out.
        var loras = Path.Combine(database.Directory, "loras");
        foreach (var file in Below(loras, "loras").Where(f =>
            f.File.EndsWith(".safetensors", StringComparison.Ordinal) && Path.GetDirectoryName(f.File) == loras))
        {
            yield return file;
        }
    }

    private static IEnumerable<(string Remote, string File)> Below(string directory, string remote)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }
        return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(file => !Path.GetFileName(file).StartsWith('.'))
            .Order(StringComparer.Ordinal)
            .Select(file => ($"{remote}/{Path.GetRelativePath(directory, file).Replace(Path.DirectorySeparatorChar, '/')}", file));
    }

    private async Task NotifyAsync(string error)
    {
        foreach (var subscription in pushStore.Subscriptions)
        {
            try
            {
                var payload = PushNotifier.Payload(PushTexts.BackupFailed(error, subscription.Language), "backup");
                if (await pushSender.SendAsync(subscription, payload, CancellationToken.None) == PushResult.Gone)
                {
                    pushStore.Remove(subscription.Endpoint);
                }
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not announce the failed backup");
            }
        }
    }

    private State LoadState()
    {
        if (_state is not null)
        {
            return _state;
        }
        try
        {
            _state = File.Exists(StatePath) ? JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath), Json) : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            // Without it every file goes up again once, which is slow but harmless.
            logger.LogWarning(exception, "Could not read {Path}", StatePath);
        }
        return _state ??= new State();
    }

    private void SaveState(State state)
    {
        _state = state;
        try
        {
            Directory.CreateDirectory(database.Directory);
            var temp = StatePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state, Json));
            File.Move(temp, StatePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not write {Path}", StatePath);
        }
    }

    /// <summary>What <c>backup.json</c> keeps.</summary>
    private sealed class State
    {
        public BackupRun? Last { get; set; }

        /// <summary>The local day of the last nightly backup that succeeded.</summary>
        public DateOnly? LastDaily { get; set; }

        /// <summary>Remote path → "size:modified ticks" of what was uploaded.</summary>
        public Dictionary<string, string> Files { get; set; } = new(StringComparer.Ordinal);
    }
}
