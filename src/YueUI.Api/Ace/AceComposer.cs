using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using YueUI.Api.Data;
using YueUI.Api.Library;
using YueUI.Api.Memory;
using YueUI.Api.Voices;
using YueUI.Api.Worker;

namespace YueUI.Api.Ace;

/// <summary>
/// Makes songs with ACE-Step 1.5 (<see cref="IAceEngine"/>), one job at a time, into run folders the library lists
/// beside YuE2's: <c>audio.flac</c>, <c>request.json</c> (style, lyrics, seed), <c>result.json</c> (title, length),
/// <c>ace.json</c> (what acestep.cpp synthesized, with the LM's lyrics and metadata) and <c>ace-request.json</c> (what
/// the page asked for), so the player, ratings, covers, exports, videos and stems work on them unchanged.
/// </summary>
/// <remarks>
/// The 4B LM and the DiT hold several GB in turn, so a job follows the memory rule of the speech lab: it waits while
/// songs are generated, lyrics written or a voice sung, shuts an idle YuE worker down first, and while it holds the
/// memory (<see cref="ModelMemory"/>) the others wait. A run folder is put together in <c>.work/</c> and moved into
/// place when complete, so the library never lists half a run. Jobs are kept in the database; a restart queues the
/// waiting ones again and marks a half-made one failed.
/// </remarks>
public sealed class AceComposer(
    SqliteAceStore store,
    IAceEngine engine,
    IAudioMixer mixer,
    SongLibrary library,
    Machines.Machines machines,
    Machines.RemoteEngines remote,
    Status.StatusHub hub,
    IOptions<AceOptions> options,
    TimeProvider time,
    ILogger<AceComposer> logger) : BackgroundService
{
    public const string RequestFile = "ace-request.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Lock _gate = new();
    private readonly Dictionary<string, AceJob> _jobs = [];
    private string? _running;
    private CancellationTokenSource? _cancel;

    private string WorkRoot => Path.Combine(library.AceDirectory, ".work");

    /// <summary>Every job, newest first.</summary>
    public IReadOnlyList<AceJob> Jobs()
    {
        lock (_gate)
        {
            return [.. _jobs.Values.OrderByDescending(j => j.CreatedAt)];
        }
    }

    public AceJob? Get(string id)
    {
        lock (_gate)
        {
            return _jobs.GetValueOrDefault(id);
        }
    }

    public AceJob Enqueue(AceSongRequest request, long seed)
    {
        var now = time.GetUtcNow();
        var job = new AceJob
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = TitleFor(request),
            Request = request,
            Seed = seed,
            Machine = request.Machine,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Save(job);
        _queue.Writer.TryWrite(job.Id);
        return job;
    }

    /// <summary>Stops the job if it runs and forgets it; the songs of a finished one stay in the library.</summary>
    public void Delete(AceJob job)
    {
        lock (_gate)
        {
            if (_running == job.Id)
            {
                _cancel?.Cancel();
            }
            _jobs.Remove(job.Id);
        }
        try
        {
            store.Remove(job.Id);
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            logger.LogWarning(exception, "Could not remove the ACE-Step job {Id}", job.Id);
        }
        if (!job.Finished)
        {
            // The queue page drops it with this; a finished one leaves the page that deleted it on its own.
            hub.UpdateAce(job with { Stage = "cancelled", UpdatedAt = time.GetUtcNow() });
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        Load();
        try
        {
            await foreach (var id in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                await MakeAsync(id, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private void Load()
    {
        IReadOnlyList<AceJob> stored;
        try
        {
            stored = store.All();
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not read ACE-Step's jobs");
            return;
        }
        foreach (var job in stored)
        {
            if (job.Stage == "queued")
            {
                Save(job);
                _queue.Writer.TryWrite(job.Id);
            }
            else if (!job.Finished)
            {
                Save(job with { Stage = "failed", Message = "The server restarted while the song was made.", UpdatedAt = time.GetUtcNow() });
            }
            else
            {
                lock (_gate)
                {
                    _jobs[job.Id] = job;
                }
            }
        }
    }

    private async Task MakeAsync(string id, CancellationToken stoppingToken)
    {
        if (Get(id) is not { Finished: false } job)
        {
            return;
        }
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        lock (_gate)
        {
            _running = id;
            _cancel = cancel;
        }
        var work = Path.Combine(WorkRoot, id);
        try
        {
            await using var placement = await machines.PlaceAsync(
                LargeModel.Ace, Machines.Engines.Ace, job.Machine, options.Value.WaitInterval, cancel.Token,
                engine.Installed && engine.Files().Complete);
            var composer = placement.Helper is { } helper ? remote.Ace(helper) : engine;
            // The models of the Mac that makes the song: a helper's as it last said.
            var files = composer.Files();
            if (!files.Complete)
            {
                throw new AceException($"The ACE-Step models on {placement.Name} are incomplete; run deploy/install-ace.sh there.");
            }
            var lm = job.Request.LmModel ?? files.Lm.FirstOrDefault();
            var dit = job.Request.DitModel ?? files.Dit[0];
            if (job.Request.UsesLm && (lm is null || !files.Lm.Contains(lm)))
            {
                throw new AceException($"The LM {lm} is not on {placement.Name}.");
            }
            if (!files.Dit.Contains(dit))
            {
                throw new AceException($"The DiT {dit} is not on {placement.Name}.");
            }

            var started = time.GetTimestamp();
            job = Update(job, j => j with { Stage = "loading", Fraction = 0, RanOn = placement.Name, StartedAt = time.GetUtcNow() });

            if (Directory.Exists(work))
            {
                Directory.Delete(work, recursive: true);
            }
            Directory.CreateDirectory(work);
            string? source = null;
            if (!job.Request.UsesLm)
            {
                source = await SourceAsync(job.Request.Source, work, cancel.Token);
            }

            var request = AceRequestJson.Build(job.Request, job.Seed, lm, dit);
            var shown = (Stage: job.Stage, Fraction: 0.0);
            var outputs = await composer.GenerateAsync(
                new AceRun(work, request, job.Request.UsesLm, source),
                (stage, fraction) =>
                {
                    if (stage != shown.Stage || fraction - shown.Fraction >= 0.02)
                    {
                        shown = (stage, fraction);
                        job = Update(job, j => j with { Stage = stage, Fraction = fraction }, persist: false);
                    }
                },
                cancel.Token);

            job = Update(job, j => j with { Stage = "saving", Fraction = 1 }, persist: false);
            var run = await StoreAsync(job, outputs, work, cancel.Token);
            // Gone before the job says done, so nothing of it is left over when the page hears that.
            DeleteWork(work);
            if (Get(id) is null)
            {
                // Deleted while it was made: the songs went into the library all the same, as a cancel came too late.
                hub.LibraryChanged();
                return;
            }
            Update(job, j => j with { Stage = "done", Run = run, Seconds = time.GetElapsedTime(started).TotalSeconds });
            hub.LibraryChanged();
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // Deleted (Delete has said so) or shutting down, in which case it stays unfinished and fails after the restart.
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "The ACE-Step song {Id} failed", id);
            Update(job, j => j with { Stage = "failed", Message = exception.Message });
        }
        finally
        {
            lock (_gate)
            {
                _running = null;
                _cancel = null;
            }
            DeleteWork(work);
        }
    }

    private void DeleteWork(string work)
    {
        try
        {
            if (Directory.Exists(work))
            {
                Directory.Delete(work, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not delete {Folder}", work);
        }
    }

    /// <summary>acestep.cpp reads WAV or MP3; the library's songs are FLAC.</summary>
    private async Task<string> SourceAsync(string? songId, string work, CancellationToken cancellationToken)
    {
        if (songId?.Split('/') is not [var run, var song]
            || library.SongDirectory(run, song) is not { } directory
            || !File.Exists(Path.Combine(directory, "audio.flac")))
        {
            throw new AceException($"The song {songId} is no longer in the library.");
        }
        var wave = Path.Combine(work, "source.wav");
        await mixer.DecodeAsync(Path.Combine(directory, "audio.flac"), wave, cancellationToken);
        return wave;
    }

    /// <summary>Writes the run in the work folder and moves it into the library in one step.</summary>
    private async Task<string> StoreAsync(AceJob job, IReadOnlyList<AceOutput> outputs, string work, CancellationToken cancellationToken)
    {
        var name = RunNameFor(job.Title);
        var staged = Path.Combine(work, "run", name);
        for (var index = 0; index < outputs.Count; index++)
        {
            var output = outputs[index];
            // Counted from 1, as YuE Studio names its songs.
            var song = Path.Combine(staged, $"song{index + 1}");
            Directory.CreateDirectory(song);
            await mixer.EncodeFlacAsync(output.Wave, Path.Combine(song, "audio.flac"), cancellationToken);

            var written = output.Request.DeepClone().AsObject();
            // The LM's 5 Hz codes are of no use once the song is there, and take kilobytes.
            written.Remove("audio_codes");
            var lyrics = (string?)written["lyrics"] ?? "";
            var instrumental = lyrics.Trim() == AceRequestJson.Instrumental;
            var seed = (long?)written["seed"] ?? job.Seed + index;
            await Write(song, "request.json", new JsonObject
            {
                // The caption as typed, like YuE's style; the LM's fuller one is in ace.json.
                ["style"] = job.Request.Caption ?? (string?)written["caption"] ?? "",
                ["lyrics"] = instrumental ? "" : lyrics,
                ["instrumental"] = instrumental,
                ["seed"] = seed,
            });
            await Write(song, "result.json", new JsonObject
            {
                ["title"] = job.Title,
                ["audio_seconds"] = WaveSeconds(output.Wave),
                ["engine"] = "ace-step",
            });
            await Write(song, SongLibrary.AceFile, written);
            // The page's request for this very song: its own seed, and the lyrics the LM wrote when none were given,
            // so taking it over makes the same song again.
            var own = job.Request with
            {
                Seed = seed,
                Batch = null,
                // Where it ran is no part of the song; making it again goes wherever is free.
                Machine = null,
                Lyrics = instrumental || !string.IsNullOrWhiteSpace(job.Request.Lyrics) ? job.Request.Lyrics : lyrics,
            };
            await File.WriteAllTextAsync(Path.Combine(song, RequestFile), JsonSerializer.Serialize(own, Json), cancellationToken);
        }

        Directory.CreateDirectory(library.AceDirectory);
        Directory.Move(staged, Path.Combine(library.AceDirectory, name));
        return name;

        Task Write(string folder, string file, JsonObject content) =>
            File.WriteAllTextAsync(Path.Combine(folder, file), content.ToJsonString(), cancellationToken);
    }

    /// <summary>Like the worker's: local time, the title's slug, and "b"s against a name already taken in either library.</summary>
    private string RunNameFor(string title)
    {
        var slug = Slug(title);
        var name = time.GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + (slug.Length > 0 ? $"-{slug}" : "");
        while (library.RunDirectory(name) is not null)
        {
            name += "b";
        }
        return name;
    }

    /// <summary>"Wüste Nacht!" → "Wueste-Nacht"; the run name's pattern takes ASCII letters, digits and hyphens.</summary>
    public static string Slug(string title)
    {
        var builder = new StringBuilder();
        foreach (var c in title.Trim())
        {
            var replaced = c switch
            {
                'ä' => "ae", 'ö' => "oe", 'ü' => "ue", 'Ä' => "Ae", 'Ö' => "Oe", 'Ü' => "Ue", 'ß' => "ss",
                _ when char.IsAsciiLetterOrDigit(c) => c.ToString(),
                _ => "-",
            };
            builder.Append(replaced);
        }
        var slug = string.Join('-', builder.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length > 40 ? slug[..40].TrimEnd('-') : slug;
    }

    /// <summary>The title given, else the caption's first words, which is what the library would show anyway.</summary>
    public static string TitleFor(AceSongRequest request)
    {
        if (request.Title?.Trim() is { Length: > 0 } title)
        {
            return title;
        }
        var words = (request.Caption ?? "").Split([' ', ',', '\n'], StringSplitOptions.RemoveEmptyEntries).Take(5);
        return string.Join(' ', words) is { Length: > 0 } caption ? caption : "ACE-Step";
    }

    /// <summary>The length from the WAV's header: the data chunk's bytes over the bytes per second.</summary>
    public static double? WaveSeconds(string path)
    {
        try
        {
            using var reader = new BinaryReader(File.OpenRead(path));
            if (new string(reader.ReadChars(4)) != "RIFF")
            {
                return null;
            }
            reader.ReadUInt32();
            if (new string(reader.ReadChars(4)) != "WAVE")
            {
                return null;
            }
            uint byteRate = 0;
            while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
            {
                var id = new string(reader.ReadChars(4));
                var size = reader.ReadUInt32();
                if (id == "fmt ")
                {
                    reader.ReadUInt16();
                    reader.ReadUInt16();
                    reader.ReadUInt32();
                    byteRate = reader.ReadUInt32();
                    reader.BaseStream.Seek(size - 12 + (size & 1), SeekOrigin.Current);
                }
                else if (id == "data")
                {
                    return byteRate > 0 ? Math.Round(size / (double)byteRate, 2) : null;
                }
                else
                {
                    reader.BaseStream.Seek(size + (size & 1), SeekOrigin.Current);
                }
            }
            return null;
        }
        catch (Exception exception) when (exception is IOException or EndOfStreamException)
        {
            return null;
        }
    }

    /// <summary>Keeps the change unless the job was deleted meanwhile, and tells the browsers.</summary>
    private AceJob Update(AceJob job, Func<AceJob, AceJob> change, bool persist = true)
    {
        var updated = change(job) with { UpdatedAt = time.GetUtcNow() };
        lock (_gate)
        {
            if (!_jobs.ContainsKey(job.Id))
            {
                return updated;
            }
        }
        if (persist)
        {
            Save(updated);
        }
        else
        {
            lock (_gate)
            {
                _jobs[updated.Id] = updated;
            }
            hub.UpdateAce(updated);
        }
        return updated;
    }

    private void Save(AceJob job)
    {
        lock (_gate)
        {
            _jobs[job.Id] = job;
        }
        try
        {
            store.Save(job);
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not store the ACE-Step job {Id}", job.Id);
        }
        hub.UpdateAce(job);
    }
}
