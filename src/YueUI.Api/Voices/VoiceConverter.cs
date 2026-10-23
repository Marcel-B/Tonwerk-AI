using System.Threading.Channels;
using Microsoft.Extensions.Options;
using YueUI.Api.Data;
using YueUI.Api.Library;
using YueUI.Api.Lyrics;
using YueUI.Api.Machines;
using YueUI.Api.Worker;

namespace YueUI.Api.Voices;

/// <summary>
/// Sings songs with another voice, one at a time: StemMyWav separates the vocals, ChangeMyVoice (Seed-VC) converts
/// them to a reference voice, ffmpeg mixes them back under the instrumental. The result is a version of the song
/// (<see cref="SqliteVersionStore"/>); the song itself is not touched. It also splits songs into their stems for the
/// voices page (<see cref="SqliteStemStore"/>) and sings uploaded recordings with a voice (<see cref="SqliteSwapStore"/>),
/// in the same queue, since that needs the same memory.
/// </summary>
/// <remarks>
/// YuE2 does not know voices: no reference audio, no speaker embedding, and the same seed does not give the same
/// singer in another song. Converting afterwards is what makes a voice recognizable across songs.
/// The Mac has 24 GB, and YuE2, a separation model and Seed-VC do not fit beside each other, so this works like the
/// lyrics writer: a version waits while songs are generated or lyrics written, an idle YuE worker is shut down
/// first, and while <see cref="IsConverting"/> no song or draft starts. A job may run on another Mac instead
/// (<see cref="Machines.Machines"/>): it is separated and converted there, under that Mac's memory, and mixed here. A take of the speech lab is waited for the same way. Queued versions survive a restart; one that
/// was halfway is marked failed, since its intermediate files are gone.
/// </remarks>
public sealed class VoiceConverter(
    SqliteVersionStore store,
    SqliteStemStore stemStore,
    SqliteSwapStore swapStore,
    SongLibrary library,
    Status.StatusHub hub,
    Memory.ModelMemory memory,
    Machines.Machines machines,
    Machines.RemoteEngines remote,
    VoiceEngine voices,
    StemSeparator stems,
    IAudioMixer mixer,
    IOptions<VoiceOptions> options,
    TimeProvider time,
    Export.TaggedFiles tagged,
    ILogger<VoiceConverter> logger) : BackgroundService
{
    /// <summary>Stem sets share the queue with versions; their entries carry this before the id.</summary>
    private const string StemsKey = "stems:";

    /// <summary>Swaps of uploaded recordings share it too, with this before the id.</summary>
    private const string SwapKey = "swap:";

    /// <summary>Slices of a stem's waveform: a few per pixel of a phone, a bar each on a wide screen.</summary>
    private const int PeakCount = 400;

    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Lock _gate = new();
    private string? _running;
    private CancellationTokenSource? _cancel;

    /// <summary>A separation or conversion holds the memory; YuE2 and the lyrics model have to wait.</summary>
    public bool IsConverting => memory.Holds(Memory.LargeModel.Voices);

    /// <summary>
    /// When the version next in line was asked for, while it waits for the memory; null when none waits. The queue
    /// stops sending songs ahead of it once it has waited too long (<see cref="Queue.QueueOptions.BundleWindow"/>).
    /// </summary>
    public DateTimeOffset? WaitingSince => Interlocked.Read(ref _waitingTicks) is > 0 and var ticks ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;

    /// <summary>UTC ticks behind <see cref="WaitingSince"/>, 0 for none; read by requests while the queue loop writes.</summary>
    private long _waitingTicks;

    /// <summary>Whether a version or stems of a song of the run (or of that song) are queued or in the works.</summary>
    public bool IsWorkingOn(string run, string? song = null) =>
        store.Unfinished().Any(v => song is null ? v.Run == run : v.SongId == $"{run}/{song}")
        || stemStore.Unfinished().Any(s => song is null ? s.Run == run : s.SongId == $"{run}/{song}");

    public StemSetState EnqueueStems(string songId, string title, string model, bool dereverb, string? machine = null)
    {
        var now = time.GetUtcNow();
        return EnqueueStems(new StemSetState
        {
            Id = Guid.NewGuid().ToString("N"),
            SongId = songId,
            Title = title,
            Model = model,
            Dereverb = dereverb,
            Machine = machine,
            CreatedAt = now,
            UpdatedAt = now,
        });
    }

    /// <summary>
    /// Queues a set as it is; an upload's file must already lie in its folder (<see cref="SqliteStemStore.SourcePath"/>).
    /// </summary>
    public StemSetState EnqueueStems(StemSetState set)
    {
        stemStore.Add(set);
        hub.UpdateStems(set);
        _queue.Writer.TryWrite(StemsKey + set.Id);
        return set;
    }

    /// <summary>Stops the separation if it runs, and removes the set with its files.</summary>
    public void DeleteStems(StemSetState set)
    {
        lock (_gate)
        {
            if (_running == StemsKey + set.Id)
            {
                _cancel?.Cancel();
            }
        }
        stemStore.Remove(set.Id);
        hub.UpdateStems(set with { Stage = "cancelled", UpdatedAt = time.GetUtcNow() });
    }

    /// <summary>
    /// Queues a swap whose upload already lies in its folder (<see cref="SqliteSwapStore.Folder"/>, as <c>source.*</c>).
    /// </summary>
    public SwapState EnqueueSwap(SwapState swap)
    {
        swapStore.Add(swap);
        hub.UpdateSwap(swap);
        _queue.Writer.TryWrite(SwapKey + swap.Id);
        return swap;
    }

    /// <summary>Stops the swap if it runs, and removes it with its files.</summary>
    public void DeleteSwap(SwapState swap)
    {
        lock (_gate)
        {
            if (_running == SwapKey + swap.Id)
            {
                _cancel?.Cancel();
            }
        }
        swapStore.Remove(swap.Id);
        hub.UpdateSwap(swap with { Stage = "cancelled", UpdatedAt = time.GetUtcNow() });
    }

    /// <param name="machine">Where to make it, as <see cref="Machines.Machines.Normalize"/> stores it.</param>
    public VersionState Enqueue(string songId, string title, ReferenceVoice voice, VersionRequest request, string? machine = null)
    {
        var now = time.GetUtcNow();
        var version = new VersionState
        {
            Id = Guid.NewGuid().ToString("N"),
            SongId = songId,
            Title = title,
            VoiceId = voice.Id,
            VoiceLabel = voice.Label,
            SemiToneShift = request.SemiToneShift,
            Strength = request.Strength,
            DiffusionSteps = request.DiffusionSteps,
            KeepReverb = request.KeepReverb,
            StemModel = options.Value.StemModel,
            Machine = machine,
            CreatedAt = now,
            UpdatedAt = now,
        };
        store.Add(version);
        hub.UpdateVersion(version);
        // The library lists the version from now on, in every open browser.
        hub.LibraryChanged();
        _queue.Writer.TryWrite(version.Id);
        return version;
    }

    /// <summary>Stops the version if it runs, and removes it with its audio.</summary>
    public void Delete(VersionState version)
    {
        lock (_gate)
        {
            if (_running == version.Id)
            {
                _cancel?.Cancel();
            }
        }
        store.Remove(version.Id);
        hub.UpdateVersion(version with { Stage = "cancelled", UpdatedAt = time.GetUtcNow() });
        hub.LibraryChanged();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            var versions = store.Unfinished().Select(v => (v.CreatedAt, Key: v.Id, Resume: v.Stage == "queued", Fail: (Action)(() =>
                Update(v, u => u with { Stage = "failed", Message = "The server restarted while the version was being made." }))));
            var sets = stemStore.Unfinished().Select(s => (s.CreatedAt, Key: StemsKey + s.Id, Resume: s.Stage == "queued", Fail: (Action)(() =>
                UpdateStems(s, u => u with { Stage = "failed", Message = "The server restarted while the stems were being separated." }))));
            var swaps = swapStore.Unfinished().Select(s => (s.CreatedAt, Key: SwapKey + s.Id, Resume: s.Stage == "queued", Fail: (Action)(() =>
                UpdateSwap(s, u => u with { Stage = "failed", Message = "The server restarted while the voice was being swapped." }))));
            foreach (var (_, key, resume, fail) in versions.Concat(sets).Concat(swaps).OrderBy(e => e.CreatedAt))
            {
                if (resume)
                {
                    _queue.Writer.TryWrite(key);
                }
                else
                {
                    fail();
                }
            }
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not read the queued versions");
        }

        try
        {
            await foreach (var key in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                if (key.StartsWith(StemsKey, StringComparison.Ordinal))
                {
                    await SeparateAsync(key[StemsKey.Length..], stoppingToken);
                }
                else if (key.StartsWith(SwapKey, StringComparison.Ordinal))
                {
                    await SwapAsync(key[SwapKey.Length..], stoppingToken);
                }
                else
                {
                    await ProcessAsync(key, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task ProcessAsync(string id, CancellationToken stoppingToken)
    {
        if (store.Get(id) is not { Finished: false } version)
        {
            return;
        }
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        lock (_gate)
        {
            _running = id;
            _cancel = cancel;
        }
        var work = Path.Combine(Path.GetTempPath(), $"yueui-voice-{id}");
        try
        {
            Interlocked.Exchange(ref _waitingTicks, version.CreatedAt.UtcTicks);
            Placement placement;
            try
            {
                placement = await PlaceAsync(Machines.Engines.Voices, version.Machine, options.Value.ConversionConfigured, cancel.Token);
            }
            finally
            {
                Interlocked.Exchange(ref _waitingTicks, 0);
            }
            await using var placed = placement;
            if (library.SongDirectory(version.Run, version.SongId[(version.Run.Length + 1)..]) is not { } directory
                || !File.Exists(Path.Combine(directory, "audio.flac")))
            {
                throw new VoiceServiceException("The song is gone.");
            }

            version = Update(version, v => v with { Stage = "separating", RanOn = placement.Name, StartedAt = time.GetUtcNow() });
            var separated = await stems.SeparateAsync(Path.Combine(directory, "audio.flac"), version.StemModel, work, cancel.Token, Separator(placement));

            version = Update(version, v => v with { Stage = "converting", Fraction = 0 });
            var converted = Path.Combine(work, "converted.wav");
            // Reaching 1 means the estimate ran out; the browser says so instead of a percentage stuck at the end.
            await ConvertAsync(placement, version.Conversion, separated.Vocals, converted, (fraction, estimated) =>
                version = Update(version, v => v with { Fraction = Math.Round(Math.Clamp(fraction, 0, 1), 3), EstimatedSeconds = estimated }), cancel.Token);
            // Mixing needs no model; whatever waits for the memory may go on.
            placement.Release();

            version = Update(version, v => v with { Stage = "mixing", Fraction = 0 });
            var mix = Path.Combine(work, "mix.flac");
            await mixer.MixAsync(new MixInput(separated.Instrumental, converted, separated.Vocals, version.KeepReverb ? separated.Reverb : null), mix, cancel.Token);
            var target = store.FilePath(id);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(mix, target, overwrite: true);

            if (store.Get(id) is null)
            {
                // Deleted while it was mixed.
                File.Delete(target);
            }
            else
            {
                Update(version, v => v with { Stage = "done", Fraction = 1 });
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            if (!stoppingToken.IsCancellationRequested)
            {
                Update(version, v => v with { Stage = "cancelled" });
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "The version {Id} of {Song} failed", id, version.SongId);
            Update(version, v => v with { Stage = "failed", Message = exception.Message });
        }
        finally
        {
            lock (_gate)
            {
                _running = null;
                _cancel = null;
            }
            try
            {
                if (Directory.Exists(work))
                {
                    Directory.Delete(work, recursive: true);
                }
            }
            catch (IOException exception)
            {
                logger.LogWarning(exception, "Could not delete {Directory}", work);
            }
        }
    }

    /// <summary>
    /// Splits a song into its stems for the voices page: StemMyWav separates, the WAVs are measured for the page's
    /// waveforms and stored as FLAC (the WAV where ffmpeg cannot).
    /// </summary>
    private async Task SeparateAsync(string id, CancellationToken stoppingToken)
    {
        if (stemStore.Get(id) is not { Finished: false } set)
        {
            return;
        }
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        lock (_gate)
        {
            _running = StemsKey + id;
            _cancel = cancel;
        }
        var work = Path.Combine(Path.GetTempPath(), $"yueui-stems-{id}");
        // Apart from work, where the separator's WAVs land and are taken as stems.
        var decoded = Path.Combine(Path.GetTempPath(), $"yueui-stems-{id}-source");
        var target = stemStore.Folder(id);
        string? source = null;
        try
        {
            Interlocked.Exchange(ref _waitingTicks, set.CreatedAt.UtcTicks);
            Placement placement;
            try
            {
                placement = await PlaceAsync(Machines.Engines.Stems, set.Machine, options.Value.StemsConfigured, cancel.Token);
            }
            finally
            {
                Interlocked.Exchange(ref _waitingTicks, 0);
            }
            await using var placed = placement;
            string input;
            if (set.Upload)
            {
                source = stemStore.SourcePath(id) ?? throw new VoiceServiceException("The uploaded file is gone.");
                // FLAC, since the stem service takes it as such; the local separator reads anything.
                Directory.CreateDirectory(decoded);
                input = Path.Combine(decoded, "song.flac");
                await mixer.DecodeAsync(source, input, cancel.Token);
            }
            else if (library.SongDirectory(set.Run, set.Song) is { } directory && File.Exists(Path.Combine(directory, "audio.flac")))
            {
                input = Path.Combine(directory, "audio.flac");
            }
            else
            {
                throw new VoiceServiceException("The song is gone.");
            }
            set = UpdateStems(set, s => s with { Stage = "separating", RanOn = placement.Name, StartedAt = time.GetUtcNow() });
            var files = await stems.ExtractAsync(input, set.Model, set.Dereverb, work, cancel.Token, Separator(placement));
            if (files.Count == 0)
            {
                throw new VoiceServiceException($"The separation model {set.Model} gave no stems.");
            }
            // The model has left the memory; measuring and encoding need none to speak of.
            placement.Release();

            var measured = files.Select(file => (File: file, Read: WavPeaks.Read(file, PeakCount))).ToList();
            // One scale for the whole set, so a quiet stem (the reverb) also looks quiet beside the others.
            var loudest = measured.Max(m => m.Read is { Peaks.Length: > 0 } read ? read.Peaks.Max() : 0);
            Directory.CreateDirectory(target);
            List<StemFile> result = [];
            foreach (var (file, read) in measured)
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var stored = $"{name}.flac";
                try
                {
                    await mixer.EncodeFlacAsync(file, Path.Combine(target, stored), cancel.Token);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning(exception, "Could not encode the stem {Name} of {Song} as FLAC; keeping the WAV", name, set.SongId);
                    stored = Path.GetFileName(file);
                    File.Move(file, Path.Combine(target, stored), overwrite: true);
                }
                // Stems of a song are parts of an AI-made song; those of an upload are the user's own recording, split.
                if (!string.IsNullOrEmpty(set.SongId))
                {
                    tagged.TryMark(Path.Combine(target, stored), "einem Songmodell");
                }
                var peaks = read?.Peaks.Select(p => Math.Round(loudest > 0 ? p / loudest : p, 3)).ToList();
                result.Add(new StemFile(name, stored, Math.Round(read?.Seconds ?? 0, 2), peaks));
            }

            if (stemStore.Get(id) is null)
            {
                // Deleted while it was encoded.
                DeleteFolder(target);
            }
            else
            {
                UpdateStems(set, s => s with { Stage = "done", Stems = result });
                // The stems are what was asked for; the upload would only double the space they take.
                if (source is not null)
                {
                    TryDeleteFile(source);
                }
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // A server stop leaves the folder: a set still waiting resumes after the restart and needs its upload.
            if (!stoppingToken.IsCancellationRequested)
            {
                DeleteFolder(target);
                UpdateStems(set, s => s with { Stage = "cancelled" });
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "The stems {Id} of {Song} failed", id, set.SongId);
            DeleteFolder(target);
            UpdateStems(set, s => s with { Stage = "failed", Message = exception.Message });
        }
        finally
        {
            lock (_gate)
            {
                _running = null;
                _cancel = null;
            }
            DeleteFolder(work);
            DeleteFolder(decoded);
        }
    }

    /// <summary>
    /// Sings an uploaded recording with a voice: a bare vocal track straight through Seed-VC, a whole song separated
    /// first and mixed back as a version is.
    /// </summary>
    private async Task SwapAsync(string id, CancellationToken stoppingToken)
    {
        if (swapStore.Get(id) is not { Finished: false } swap)
        {
            return;
        }
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        lock (_gate)
        {
            _running = SwapKey + id;
            _cancel = cancel;
        }
        var work = Path.Combine(Path.GetTempPath(), $"yueui-swap-{id}");
        try
        {
            Interlocked.Exchange(ref _waitingTicks, swap.CreatedAt.UtcTicks);
            Placement placement;
            try
            {
                placement = await PlaceAsync(
                    Machines.Engines.Voices, swap.Machine, swap.Separate ? options.Value.ConversionConfigured : options.Value.VoicesConfigured, cancel.Token);
            }
            finally
            {
                Interlocked.Exchange(ref _waitingTicks, 0);
            }
            await using var placed = placement;
            if (swapStore.SourcePath(id) is not { } source)
            {
                throw new VoiceServiceException("The uploaded file is gone.");
            }
            swap = UpdateSwap(swap, s => s with { RanOn = placement.Name, StartedAt = time.GetUtcNow() });
            Directory.CreateDirectory(work);
            var converted = Path.Combine(work, "converted.wav");
            void Progress(double fraction, double estimated) =>
                swap = UpdateSwap(swap, s => s with { Fraction = Math.Round(Math.Clamp(fraction, 0, 1), 3), EstimatedSeconds = estimated });
            var result = Path.Combine(work, SqliteSwapStore.ResultFile);

            if (swap.Separate)
            {
                // FLAC, since the stem service takes it as such; the local separator reads anything.
                var song = Path.Combine(work, "song.flac");
                await mixer.DecodeAsync(source, song, cancel.Token);
                swap = UpdateSwap(swap, s => s with { Stage = "separating" });
                var separated = await stems.SeparateAsync(song, swap.StemModel, Path.Combine(work, "stems"), cancel.Token, Separator(placement));

                swap = UpdateSwap(swap, s => s with { Stage = "converting", Fraction = 0 });
                await ConvertAsync(placement, swap.Conversion, separated.Vocals, converted, Progress, cancel.Token);
                placement.Release();

                swap = UpdateSwap(swap, s => s with { Stage = "mixing", Fraction = 0 });
                await mixer.MixAsync(new MixInput(separated.Instrumental, converted, separated.Vocals, swap.KeepReverb ? separated.Reverb : null), result, cancel.Token);
            }
            else
            {
                var vocals = Path.Combine(work, "vocals.wav");
                await mixer.DecodeAsync(source, vocals, cancel.Token);
                swap = UpdateSwap(swap, s => s with { Stage = "converting", Fraction = 0 });
                await ConvertAsync(placement, swap.Conversion, vocals, converted, Progress, cancel.Token);
                placement.Release();
                await mixer.EncodeFlacAsync(converted, result, cancel.Token);
            }

            if (swapStore.Get(id) is null)
            {
                // Deleted meanwhile, with its folder.
                return;
            }
            File.Move(result, swapStore.ResultPath(id), overwrite: true);
            tagged.TryMark(swapStore.ResultPath(id), "Seed-VC");
            UpdateSwap(swap, s => s with { Stage = "done", Fraction = 1 });
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            if (!stoppingToken.IsCancellationRequested)
            {
                UpdateSwap(swap, s => s with { Stage = "cancelled" });
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "The voice swap {Id} of {File} failed", id, swap.FileName);
            UpdateSwap(swap, s => s with { Stage = "failed", Message = exception.Message });
        }
        finally
        {
            lock (_gate)
            {
                _running = null;
                _cancel = null;
            }
            DeleteFolder(work);
        }
    }

    /// <summary>Waits for the memory, here or on another Mac (<see cref="Machines.Machines.PlaceAsync"/>).</summary>
    /// <param name="localAvailable">Whether this Mac can do it (its own tools or the old services).</param>
    private Task<Placement> PlaceAsync(string engine, string? machine, bool localAvailable, CancellationToken cancellationToken) =>
        machines.PlaceAsync(Memory.LargeModel.Voices, engine, machine, options.Value.WaitInterval, cancellationToken, localAvailable);

    /// <summary>The separator of the Mac the job was placed on; null for this one's.</summary>
    private IStemBackend? Separator(Placement placement) => placement.Helper is { } helper ? remote.Stems(helper) : null;

    /// <summary>Seed-VC of the Mac the job was placed on; another Mac gets the reference voice sent along.</summary>
    private async Task ConvertAsync(
        Placement placement, VoiceConversion conversion, string vocals, string output, VoiceProgress progress, CancellationToken cancellationToken)
    {
        if (placement.Helper is not { } helper)
        {
            await voices.ConvertAsync(conversion, vocals, output, progress, cancellationToken);
            return;
        }
        var reference = await voices.VoiceAudioAsync(conversion.VoiceId, cancellationToken);
        await remote.ConvertAsync(helper, conversion, reference.Audio, vocals, output, progress, cancellationToken);
    }

    /// <summary>Stores the change (a swap deleted meanwhile stays deleted) and tells the browsers.</summary>
    private SwapState UpdateSwap(SwapState swap, Func<SwapState, SwapState> change)
    {
        var updated = change(swap) with { UpdatedAt = time.GetUtcNow() };
        try
        {
            if (!swapStore.Update(updated))
            {
                return updated;
            }
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            logger.LogWarning(exception, "Could not store the voice swap {Id}", swap.Id);
        }
        hub.UpdateSwap(updated);
        return updated;
    }

    private void TryDeleteFile(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not delete {File}", file);
        }
    }

    private void DeleteFolder(string folder)
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
            logger.LogWarning(exception, "Could not delete {Directory}", folder);
        }
    }

    /// <summary>Stores the change (a set deleted meanwhile stays deleted) and tells the browsers.</summary>
    private StemSetState UpdateStems(StemSetState set, Func<StemSetState, StemSetState> change)
    {
        var updated = change(set) with { UpdatedAt = time.GetUtcNow() };
        try
        {
            if (!stemStore.Update(updated))
            {
                return updated;
            }
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            logger.LogWarning(exception, "Could not store the stems {Id}", set.Id);
        }
        hub.UpdateStems(updated);
        return updated;
    }

    /// <summary>Stores the change (a version deleted meanwhile stays deleted) and tells the browsers.</summary>
    private VersionState Update(VersionState version, Func<VersionState, VersionState> change)
    {
        var updated = change(version) with { UpdatedAt = time.GetUtcNow() };
        try
        {
            if (!store.Update(updated))
            {
                return updated;
            }
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            logger.LogWarning(exception, "Could not store the version {Id}", version.Id);
        }
        hub.UpdateVersion(updated);
        if (updated.Finished)
        {
            hub.LibraryChanged();
        }
        return updated;
    }
}
