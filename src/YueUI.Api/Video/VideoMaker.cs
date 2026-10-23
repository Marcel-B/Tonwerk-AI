using System.Threading.Channels;
using Microsoft.Extensions.Options;
using YueUI.Api.Data;
using YueUI.Api.Library;
using YueUI.Api.Worker;

namespace YueUI.Api.Video;

/// <summary>
/// Renders music videos one at a time, in the order they were asked for. ffmpeg needs the CPU and a few hundred
/// megabytes, not the memory the models fight over, so a video does not wait for YuE2, a draft or a voice; it only
/// slows them down a little. Progress goes out through <see cref="Status.StatusHub.UpdateVideo"/>. After a restart the
/// unfinished ones start over, since everything they need is on disk. A photo video first has its pictures cut to the
/// song (<see cref="IMontageBuilder"/>), which takes the first part of its progress.
/// </summary>
public sealed class VideoMaker(
    SqliteVideoStore store,
    IVideoRenderer renderer,
    IMontageBuilder montage,
    IOptions<MontageOptions> montageOptions,
    SongLibrary library,
    Status.StatusHub hub,
    TimeProvider time,
    ILogger<VideoMaker> logger) : BackgroundService
{
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();
    private readonly Lock _gate = new();
    private string? _running;
    private CancellationTokenSource? _cancel;
    private readonly SemaphoreSlim _shrinking = new(1, 1);

    /// <summary>A video is being rendered; a restart would start it over.</summary>
    public bool IsRendering
    {
        get
        {
            lock (_gate)
            {
                return _running is not null;
            }
        }
    }

    public bool Available => renderer.Available;

    /// <summary>Keeps the video (its layers already lie in its folder) and queues it.</summary>
    public VideoState Enqueue(VideoState video)
    {
        store.Add(video);
        hub.UpdateVideo(video);
        _queue.Writer.TryWrite(video.Id);
        return video;
    }

    /// <summary>Stops a video in the works, or removes a finished one, with its layers.</summary>
    public void Delete(VideoState video)
    {
        lock (_gate)
        {
            if (_running == video.Id)
            {
                _cancel?.Cancel();
            }
        }
        store.Remove(video.Id);
        hub.LibraryChanged();
        // Finished ones too: an open dialog lays the stream's states over its list and would bring a done one back.
        hub.UpdateVideo(video with { Stage = "cancelled", UpdatedAt = time.GetUtcNow() });
    }

    /// <summary>
    /// The small copy of a finished video (<see cref="IVideoRenderer.ShrinkAsync"/>), made on its first request, since
    /// only the videos that are sent need one, and kept beside it. One at a time: a second request for the same video
    /// waits and then finds it made.
    /// </summary>
    public async Task<string> SmallAsync(VideoState video, CancellationToken cancellationToken)
    {
        var source = store.VideoPath(video.Id);
        var small = store.SmallPath(video.Id);
        await _shrinking.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(small) || File.GetLastWriteTimeUtc(small) < File.GetLastWriteTimeUtc(source))
            {
                await renderer.ShrinkAsync(source, video.Format, small, cancellationToken);
            }
            return small;
        }
        finally
        {
            _shrinking.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        foreach (var video in store.Unfinished())
        {
            var queued = video with { Stage = "queued", Fraction = 0, UpdatedAt = time.GetUtcNow() };
            store.Update(queued);
            hub.UpdateVideo(queued);
            _queue.Writer.TryWrite(video.Id);
        }
        try
        {
            await foreach (var id in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                if (store.Get(id) is { Stage: "queued" } video)
                {
                    await RenderAsync(video, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RenderAsync(VideoState video, CancellationToken stoppingToken)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        lock (_gate)
        {
            _running = video.Id;
            _cancel = cancel;
        }
        var current = Save(video with { Stage = "rendering", Fraction = 0, StartedAt = time.GetUtcNow() });
        try
        {
            if (video.SongId.Split('/') is not [var run, var song]
                || library.SongDirectory(run, song) is not { } directory
                || !File.Exists(Path.Combine(directory, "audio.flac")))
            {
                throw new VideoException("The song is gone.");
            }
            var seconds = SongLibrary.SecondsOf(directory);
            var shown = 0.0;
            void Report(double fraction)
            {
                // A step per per cent is enough for the ring and spares every browser thirty events a second.
                if (fraction - shown >= 0.01)
                {
                    shown = fraction;
                    current = Save(current with { Fraction = fraction }, persist: false);
                }
            }
            // The montage is the photo video's slower half: planning, then a few seconds of ffmpeg per shot.
            var rendered = 0.0;
            if (video.Media is not null)
            {
                if (MediaFolders.Resolve(montageOptions.Value.ResolvedMediaRoot, video.Media) is not { } folder)
                {
                    throw new VideoException($"The folder {video.Media} is gone.");
                }
                rendered = 0.6;
                await montage.BuildAsync(
                    video,
                    Path.Combine(directory, "audio.flac"),
                    folder,
                    store.WorkFolder(video.Id),
                    store.MontagePath(video.Id),
                    fraction => Report(rendered * fraction),
                    cancel.Token);
            }
            await renderer.RenderAsync(
                video,
                Path.Combine(directory, "audio.flac"),
                seconds,
                store.Layers(video),
                store.VideoPath(video.Id),
                fraction => Report(rendered + (1 - rendered) * fraction),
                cancel.Token);
            Save(current with { Stage = "done", Fraction = 1, Bytes = new FileInfo(store.VideoPath(video.Id)).Length });
            ReplaceOlder(video);
            hub.LibraryChanged();
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            // Deleted while rendering: Delete has said so and removed the folder; ffmpeg may have written into it since.
            store.Remove(video.Id);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down: it stays "rendering" in the database and starts over after the restart.
            throw;
        }
        catch (Exception exception) when (exception is VideoException or IOException or InvalidOperationException)
        {
            logger.LogWarning("Video {Id} of {Song} failed: {Message}", video.Id, video.SongId, exception.Message);
            Save(current with { Stage = "failed", Message = exception.Message });
        }
        finally
        {
            lock (_gate)
            {
                _running = null;
                _cancel = null;
            }
            // Stills, shots and the montage are hundreds of megabytes and made again on a retry.
            DeleteMontage(video.Id);
        }
    }

    private void DeleteMontage(string id)
    {
        try
        {
            if (Directory.Exists(store.WorkFolder(id)))
            {
                Directory.Delete(store.WorkFolder(id), recursive: true);
            }
            File.Delete(store.MontagePath(id));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not clean up photo video {Id}: {Message}", id, exception.Message);
        }
    }

    /// <summary>
    /// A video is reproducible from its song and settings, so a new one replaces the song's older ones in the same
    /// format, done or failed, once it is finished itself; until then the old one stays to be watched. The other
    /// format is kept, since YouTube and Shorts want both, and so are videos of the other kind: a photo video does not
    /// replace the cover video, nor one of another folder. Ones still waiting or rendering are left alone.
    /// </summary>
    private void ReplaceOlder(VideoState video)
    {
        foreach (var older in store.ForSong(video.SongId))
        {
            if (older.Id != video.Id && older.Format == video.Format && older.Media == video.Media && older.CreatedAt <= video.CreatedAt
                && older.Stage is "done" or "failed")
            {
                store.Remove(older.Id);
                hub.UpdateVideo(older with { Stage = "cancelled", UpdatedAt = time.GetUtcNow() });
            }
        }
    }

    /// <summary>Progress is only sent, not written: it would be stale after a restart anyway.</summary>
    private VideoState Save(VideoState video, bool persist = true)
    {
        var updated = video with { UpdatedAt = time.GetUtcNow() };
        // Gone from the database: deleted itself or with its song; the queue must not keep showing it.
        var kept = persist ? store.Update(updated) : store.Get(updated.Id) is not null;
        hub.UpdateVideo(kept ? updated : updated with { Stage = "cancelled" });
        return updated;
    }
}
