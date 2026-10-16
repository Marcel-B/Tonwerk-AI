using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using YueUI.Api.Data;
using YueUI.Api.Library;
using YueUI.Api.Video;

namespace YueUI.Api;

/// <summary>
/// Music videos of a song (<see cref="VideoMaker"/>): the browser sends the still layers it drew as PNGs of the
/// frame's size, the server animates and encodes them in the background and keeps the MP4 to download or share. A
/// photo video takes a folder of the Mac's pictures instead of the blurred cover (<see cref="MontageBuilder"/>).
/// </summary>
public static class VideoEndpoints
{
    /// <summary>A full-frame PNG of a photo is a few megabytes; this only guards against anything else.</summary>
    public const long MaxLayerBytes = 20 * 1024 * 1024;

    /// <summary>A motif is a few words for CLIP, whose text half reads 77 tokens at most.</summary>
    public const int MaxMotifLength = 200;

    public static RouteGroupBuilder MapVideoEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/songs/{run}/{song}/videos", (string run, string song, SongLibrary library, SqliteVideoStore store) =>
            library.SongDirectory(run, song) is null ? Results.NotFound() : Results.Ok(store.ForSong($"{run}/{song}")))
            .WithName("ListSongVideos")
            .Produces<IReadOnlyList<VideoState>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);
        // The folders a photo video can take its pictures from, and whether the planner is installed at all.
        api.MapGet("/videos/media", (IMontageBuilder montage, IOptions<MontageOptions> options) =>
            new MediaInfo(montage.Installed, options.Value.ResolvedMediaRoot, MediaFolders.List(options.Value.ResolvedMediaRoot)))
            .WithName("GetVideoMedia")
            .Produces<MediaInfo>(StatusCodes.Status200OK);
        api.MapPost("/songs/{run}/{song}/videos", AddAsync)
            // Posted from a plain form; there is no login and no cookie that a forged request could use.
            .DisableAntiforgery()
            .WithFormOptions(multipartBodyLengthLimit: 4 * MaxLayerBytes)
            .WithMetadata(new RequestSizeLimitAttribute(4 * MaxLayerBytes + 64 * 1024))
            .WithName("CreateVideo")
            .Produces<VideoState>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status501NotImplemented);
        // Range requests: Safari plays a video only from a source that answers them.
        // With small, the copy for a messenger, made on the first request (see VideoMaker.SmallAsync).
        api.MapGet("/videos/{id}/file", async (
            string id,
            bool? download,
            bool? small,
            SqliteVideoStore store,
            VideoMaker maker,
            IHostApplicationLifetime lifetime,
            ILogger<VideoMaker> logger,
            CancellationToken cancellationToken) =>
        {
            if (store.Get(id) is not { Stage: "done" } video || !File.Exists(store.VideoPath(id)))
            {
                return Results.NotFound();
            }
            var path = store.VideoPath(id);
            if (small == true)
            {
                try
                {
                    // Made to the end even when the phone gives up waiting, so asking again finds it ready.
                    path = await maker.SmallAsync(video, lifetime.ApplicationStopping).WaitAsync(cancellationToken);
                }
                catch (VideoException exception)
                {
                    logger.LogWarning("Small copy of video {Id} failed: {Message}", id, exception.Message);
                    return Results.Problem(exception.Message, statusCode: StatusCodes.Status500InternalServerError);
                }
                catch (IOException) when (store.Get(id) is null)
                {
                    return Results.NotFound(); // deleted meanwhile
                }
            }
            return Results.File(path, "video/mp4", download == true ? FileName(video, small == true) : null, enableRangeProcessing: true);
        })
            .WithName("GetVideoFile")
            .ProducesFile("video/mp4")
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError);
        api.MapDelete("/videos/{id}", (string id, SqliteVideoStore store, VideoMaker maker) =>
        {
            if (store.Get(id) is not { } video)
            {
                return Results.NotFound();
            }
            maker.Delete(video);
            return Results.NoContent();
        })
            .WithName("DeleteVideo")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);
        return api;
    }

    /// <summary>"Neon Night-song2-youtube.mp4": the title, the song and where it is meant to go; "-small" for a messenger.</summary>
    public static string FileName(VideoState video, bool small = false) =>
        $"{LibraryEndpoints.FileName(video.Title, video.SongId.Split('/')[0])}-{video.SongId.Split('/')[^1]}-{(video.Format == VideoFormats.Portrait ? "vertical" : "youtube")}{(small ? "-small" : "")}.mp4";

    private static async Task<IResult> AddAsync(
        string run,
        string song,
        [FromForm] string? format,
        [FromForm] string? effect,
        [FromForm] string? motion,
        [FromForm] string? color,
        [FromForm] bool? showCover,
        [FromForm] bool? showTitle,
        [FromForm] string? media,
        [FromForm] string? motif,
        [FromForm] bool? showLyrics,
        IFormFileCollection files,
        SongLibrary library,
        SqliteVideoStore store,
        VideoMaker maker,
        IMontageBuilder montage,
        IOptions<MontageOptions> montageOptions,
        Timing.TimingFiles timings,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (library.SongDirectory(run, song) is not { } directory || !File.Exists(Path.Combine(directory, "audio.flac")))
        {
            return Results.NotFound();
        }
        if (!maker.Available)
        {
            return Results.Problem(title: "Videos need ffmpeg, which is not installed.", statusCode: StatusCodes.Status501NotImplemented);
        }
        var errors = new Dictionary<string, string[]>();
        if (!VideoFormats.All.Contains(format ?? ""))
        {
            errors["format"] = [$"One of {string.Join(", ", VideoFormats.All)}."];
        }
        if (!VideoEffects.All.Contains(effect ?? ""))
        {
            errors["effect"] = [$"One of {string.Join(", ", VideoEffects.All)}."];
        }
        // Goes into the filter graph, so nothing but #rrggbb.
        if (!string.IsNullOrEmpty(color) && !System.Text.RegularExpressions.Regex.IsMatch(color, @"^#[0-9a-fA-F]{6}\z"))
        {
            errors["color"] = ["A colour as #rrggbb."];
        }
        motion ??= VideoMotions.None;
        if (!VideoMotions.All.Contains(motion))
        {
            errors["motion"] = [$"One of {string.Join(", ", VideoMotions.All)}."];
        }
        media = string.IsNullOrWhiteSpace(media) ? null : media.Trim();
        motif = string.IsNullOrWhiteSpace(motif) ? null : motif.Trim();
        if (media is not null)
        {
            if (!montage.Installed)
            {
                return Results.Problem(title: "Photo videos are not installed; run deploy/install-montage.sh.", statusCode: StatusCodes.Status501NotImplemented);
            }
            if (MediaFolders.Resolve(montageOptions.Value.ResolvedMediaRoot, media) is null)
            {
                errors["media"] = ["One of the folders GET /api/videos/media lists."];
            }
            // The photos move themselves; particles or lightning over them would only clutter the picture.
            if (motion != VideoMotions.None)
            {
                errors["motion"] = ["A photo video has no motion layer."];
            }
            if (motif?.Length > MaxMotifLength)
            {
                errors["motif"] = [$"At most {MaxMotifLength} characters."];
            }
        }
        else
        {
            motif = null;
        }
        // Older pages sent no showCover; they always drew the cover.
        var withCover = showCover != false;
        var layout = VideoLayout.For(format ?? "");
        // The layers by their form name; each must be a PNG of exactly the size the renderer lays it out at, or
        // nothing lines up. A photo video's background is its pictures.
        var wanted = new List<(string Name, int Width, int Height)>();
        if (media is null)
        {
            wanted.Add(("background", layout.Width, layout.Height));
        }
        else if (files.GetFile("shade") is not null)
        {
            wanted.Add(("shade", layout.Width, layout.Height));
        }
        if (withCover)
        {
            wanted.Add(("cover", layout.Width, layout.Height));
        }
        if (showTitle == true)
        {
            wanted.Add(("title", layout.Width, layout.Height));
        }
        if (motion == VideoMotions.Particles)
        {
            wanted.Add((VideoMotions.Particles, layout.Width, layout.Height));
        }
        else if (motion == VideoMotions.Plasma)
        {
            var area = layout.PlasmaArea(withCover);
            for (var i = 0; i < VideoLayout.PlasmaFrames; i++)
            {
                wanted.Add(($"plasma{i}", area.Width, area.Height));
            }
        }
        // Lyrics come as strips of lines, an empty slot above them, each slot the band's size; the times are the
        // server's own, so the strips need only the right number of lines.
        IReadOnlyList<LyricsCue>? cues = null;
        if (showLyrics == true)
        {
            if (timings.Get(run, song) is not { Lines.Count: > 0 } timing)
            {
                errors["showLyrics"] = ["The song's lyrics have no times yet."];
            }
            else
            {
                cues = VideoLayout.Cues(timing);
                var strip = 0;
                foreach (var chunk in cues.Chunk(VideoLayout.LyricsPerStrip))
                {
                    wanted.Add(($"lyrics{strip++}", layout.Width, layout.BandHeight * (chunk.Length + 1)));
                }
            }
        }
        var layers = new Dictionary<string, byte[]>();
        foreach (var (name, wantedWidth, wantedHeight) in wanted)
        {
            var file = files.GetFile(name);
            var data = file is { Length: > 0 and <= MaxLayerBytes } ? await ReadAsync(file, cancellationToken) : null;
            if (data is null || PngSize(data) is not var (width, height) || width != wantedWidth || height != wantedHeight)
            {
                errors[name] = [$"A PNG of {wantedWidth}×{wantedHeight}."];
                continue;
            }
            layers[name] = data;
        }
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var now = time.GetUtcNow();
        var video = new VideoState
        {
            Id = Guid.NewGuid().ToString("N"),
            SongId = $"{run}/{song}",
            Title = library.TitleOf(run, directory),
            Format = format!,
            Effect = effect!,
            Color = string.IsNullOrEmpty(color) ? null : color.ToLowerInvariant(),
            Motion = motion,
            ShowCover = withCover,
            ShowTitle = showTitle == true,
            Media = media,
            Motif = motif,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Directory.CreateDirectory(store.Folder(video.Id));
        foreach (var (name, data) in layers)
        {
            await File.WriteAllBytesAsync(Path.Combine(store.Folder(video.Id), $"{name}.png"), data, cancellationToken);
        }
        if (cues is not null)
        {
            await File.WriteAllTextAsync(store.LyricsCuesPath(video.Id), System.Text.Json.JsonSerializer.Serialize(cues, SqliteVideoStore.Json), cancellationToken);
        }
        return Results.Accepted($"/api/videos/{video.Id}/file", maker.Enqueue(video));
    }

    private static async Task<byte[]> ReadAsync(IFormFile file, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream((int)file.Length);
        await file.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    /// <summary>Width and height from the PNG's header chunk, which always comes first; null for anything else.</summary>
    public static (int Width, int Height)? PngSize(byte[] data) =>
        data is [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, _, _, _, _, (byte)'I', (byte)'H', (byte)'D', (byte)'R', ..] && data.Length >= 24
            ? (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16)), System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(20)))
            : null;
}
