using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using YueUI.Api.Share;
using YueUI.Api.Voices;

namespace YueUI.Api.Video;

/// <summary>A subfolder of <see cref="MontageOptions.MediaRoot"/> the dialog offers, with what the planner would read in it.</summary>
public sealed record MediaFolder(string Name, int Photos, int Videos);

/// <summary>What the dialog needs to offer photo videos.</summary>
/// <param name="Installed">The planner's Python environment is there (<c>deploy/install-montage.sh</c>).</param>
/// <param name="Root">Where the folders are looked for, so the dialog can say where to put pictures.</param>
public sealed record MediaInfo(bool Installed, string Root, IReadOnlyList<MediaFolder> Folders);

/// <summary>One shot of the plan (<c>Video/yueui_montage.py</c> writes them): a photo's still to zoom into, or a stretch of a clip.</summary>
public sealed record MontageShot
{
    public double Start { get; init; }

    public double End { get; init; }

    /// <summary><c>photo</c> or <c>video</c>.</summary>
    public string Kind { get; init; } = "photo";

    /// <summary>A photo's still in the work folder, twice the frame's size and already cut to its shape.</summary>
    public string? Still { get; init; }

    /// <summary>A photo's zoom at the shot's start and end.</summary>
    public double[]? Zoom { get; init; }

    /// <summary>Where a photo's zoom is centred at start and end, as shares of the free room: x, y, x, y.</summary>
    public double[]? Focus { get; init; }

    /// <summary>A clip's path.</summary>
    public string? File { get; init; }

    /// <summary>Where in the clip the shot starts, in seconds.</summary>
    public double From { get; init; }

    /// <summary><c>cover</c>: cut to fill the frame; <c>blur</c>: whole, over a blurred copy of itself.</summary>
    public string? Fit { get; init; }

    /// <summary>HLG or PQ, as iPhones film: tone mapped to SDR, or it looks washed out beside the photos.</summary>
    public bool Hdr { get; init; }
}

public sealed record MontagePlan(double Seconds, double Tempo, IReadOnlyList<MontageShot> Shots);

/// <summary>Cuts a folder of photos and clips to a song. Tests replace it.</summary>
public interface IMontageBuilder
{
    /// <summary>The planner's Python environment is there.</summary>
    bool Installed { get; }

    /// <summary>
    /// Writes the pictures, cut on the song's beats, as a silent video of the frame's size and the song's length
    /// (<paramref name="output"/>), which <see cref="FfmpegVideoRenderer"/> then takes as the background.
    /// </summary>
    /// <param name="work">A folder of its own for the plan, the stills and the shots; the caller deletes it.</param>
    /// <exception cref="VideoException">The planner or ffmpeg failed; the message says why.</exception>
    Task BuildAsync(VideoState video, string flac, string folder, string work, string output, Action<double> progress, CancellationToken cancellationToken);
}

/// <summary>
/// Runs <c>Video/yueui_montage.py</c> with its own Python (beats with librosa, the pictures judged by sharpness,
/// exposure and, with a motif, by CLIP; see the script), then renders each planned shot with ffmpeg and joins them.
/// A shot of its own per ffmpeg run keeps every command short and lets a cut sit on the exact frame its beat falls on;
/// all shots are encoded alike, so they are joined without encoding again. CLIP is small (about 1.5 GB) and loaded
/// only while the pictures are ranked, so a photo video waits for none of the big models, like the cover videos.
/// </summary>
public sealed class MontageBuilder(IOptions<MontageOptions> options, ILogger<MontageBuilder> logger) : IMontageBuilder
{
    private const string EventPrefix = "YUEUI ";

    /// <summary>How much of the whole the planner's stages are; the shots take the rest.</summary>
    private static readonly Dictionary<string, (double From, double Share)> Stages = new()
    {
        ["music"] = (0, 0.04),
        ["media"] = (0.04, 0.2),
        ["motif"] = (0.24, 0.12),
        ["plan"] = (0.36, 0.04),
    };

    private const double PlanShare = 0.4;

    /// <summary>One shot is a few seconds of video; anything beyond this is hanging.</summary>
    private static readonly TimeSpan ShotTimeout = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Script => Path.Combine(AppContext.BaseDirectory, "Video", "yueui_montage.py");

    public bool Installed => File.Exists(options.Value.ResolvedPython);

    public async Task BuildAsync(VideoState video, string flac, string folder, string work, string output, Action<double> progress, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var ffmpeg = AacEncoder.FindFfmpeg() ?? throw new VideoException("ffmpeg is not installed.");
        if (!Installed)
        {
            throw new VideoException($"Photo videos are not installed ({settings.ResolvedPython}); run deploy/install-montage.sh.");
        }
        Directory.CreateDirectory(work);
        var layout = VideoLayout.For(video.Format);
        var request = new JsonObject
        {
            ["audio"] = flac,
            ["folder"] = folder,
            ["work"] = work,
            ["width"] = layout.Width,
            ["height"] = layout.Height,
            ["motif"] = video.Motif,
            ["clipModel"] = settings.ClipModel,
            ["clipPretrained"] = settings.ClipPretrained,
            // The same song and folder give the same zooms, so a video made again only changes where asked.
            ["seed"] = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(video.SongId))) & 0x7fffffff,
            ["ffmpeg"] = ffmpeg,
            ["ffprobe"] = Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe"),
        };
        ToolResult run;
        JsonNode? done = null;
        try
        {
            run = await ToolProcess.RunAsync(
                settings.ResolvedPython,
                ["-u", Script, request.ToJsonString()],
                settings.Timeout,
                cancellationToken,
                new Dictionary<string, string>
                {
                    ["PYTHONUNBUFFERED"] = "1",
                    ["PYTHONWARNINGS"] = "ignore",
                    ["HF_HOME"] = settings.ResolvedModelCache,
                },
                line: line =>
                {
                    if (!line.StartsWith(EventPrefix, StringComparison.Ordinal) || JsonNode.Parse(line[EventPrefix.Length..]) is not { } data)
                    {
                        return;
                    }
                    switch ((string?)data["event"])
                    {
                        case "stage" when Stages.TryGetValue((string?)data["stage"] ?? "", out var stage):
                            progress(stage.From + stage.Share * ((double?)data["fraction"] ?? 0));
                            break;
                        case "note":
                            logger.LogInformation("Photo video {Id}: {Note}", video.Id, (string?)data["message"]);
                            break;
                        case "done":
                            done = data;
                            break;
                    }
                });
        }
        catch (VoiceServiceException exception)
        {
            throw new VideoException(exception.Message);
        }
        var planPath = Path.Combine(work, "plan.json");
        if (run.ExitCode != 0 || done is null || !File.Exists(planPath))
        {
            logger.LogWarning("Planning photo video {Id} failed:\n{Output}", video.Id, string.Join('\n', run.Tail));
            throw new VideoException(ToolProcess.Reason(run, "The photo video could not be planned."));
        }
        logger.LogInformation(
            "Photo video {Id}: {Shots} shots of {Used} pictures ({Photos} photos, {Videos} clips, motif {Motif}) at {Tempo} BPM, planned in {Seconds} s",
            video.Id, (int?)done["shots"], (int?)done["used"], (int?)done["photos"], (int?)done["videos"], (bool?)done["motif"], (double?)done["tempo"], (double?)done["seconds"]);

        MontagePlan plan;
        try
        {
            plan = JsonSerializer.Deserialize<MontagePlan>(await File.ReadAllTextAsync(planPath, cancellationToken), Json)
                ?? throw new VideoException("The plan is empty.");
        }
        catch (JsonException exception)
        {
            throw new VideoException($"The plan cannot be read: {exception.Message}");
        }
        await RenderShotsAsync(ffmpeg, plan, layout, work, output, fraction => progress(PlanShare + (1 - PlanShare) * fraction), cancellationToken);
    }

    /// <summary>Every shot into its own file of exactly its frames, then all of them joined into <paramref name="output"/>.</summary>
    public static async Task RenderShotsAsync(string ffmpeg, MontagePlan plan, VideoLayout layout, string work, string output, Action<double> progress, CancellationToken cancellationToken)
    {
        var tonemap = await CanTonemapAsync(ffmpeg, cancellationToken);
        var list = new List<string>();
        for (var index = 0; index < plan.Shots.Count; index++)
        {
            var shot = plan.Shots[index];
            var frames = Frames(shot);
            if (frames < 1)
            {
                continue;
            }
            var file = Path.Combine(work, $"shot-{index + 1:0000}.mp4");
            await RunAsync(ffmpeg, ShotArguments(shot, frames, work, layout, file, tonemap), ShotTimeout, cancellationToken);
            list.Add($"file '{file.Replace("'", @"'\''")}'");
            progress(0.95 * (index + 1) / plan.Shots.Count);
        }
        if (list.Count == 0)
        {
            throw new VideoException("The plan has no shots.");
        }
        var listPath = Path.Combine(work, "shots.txt");
        await File.WriteAllLinesAsync(listPath, list, cancellationToken);
        await RunAsync(
            ffmpeg,
            ["-nostdin", "-loglevel", "error", "-y", "-f", "concat", "-safe", "0", "-i", listPath, "-c", "copy", "-an", "-movflags", "+faststart", output],
            ShotTimeout,
            cancellationToken);
        progress(1);
    }

    /// <summary>
    /// The shot's frames: from the frame its start falls on to the one its end does, counted from the song's start,
    /// so rounding never adds up and every cut stays on its beat.
    /// </summary>
    public static int Frames(MontageShot shot) =>
        (int)Math.Round(shot.End * VideoLayout.FrameRate) - (int)Math.Round(shot.Start * VideoLayout.FrameRate);

    /// <summary>
    /// The ffmpeg command for one shot. A photo: zoompan over its still (twice the frame's size, so the slow zoom
    /// moves in half pixels instead of jumping whole ones). A clip: from its start, turned upright by ffmpeg itself,
    /// tone mapped if it is HDR, cut to the frame or laid whole over its blurred self, its last frame held when it
    /// ends before the shot. Every shot is encoded alike (and nearly losslessly, since it is encoded once more).
    /// </summary>
    public static IReadOnlyList<string> ShotArguments(MontageShot shot, int frames, string work, VideoLayout layout, string output, bool tonemap = true)
    {
        var (w, h) = (layout.Width, layout.Height);
        List<string> arguments = ["-nostdin", "-loglevel", "error", "-y"];
        string graph;
        if (shot.Kind == "photo")
        {
            var still = Path.Combine(work, Path.GetFileName(shot.Still ?? throw new VideoException("A photo shot without its still.")));
            var zoom = shot.Zoom is [var z0, var z1] ? (z0, z1) : (1.0, 1.0);
            var focus = shot.Focus is [var x0, var y0, var x1, var y1] ? (x0, y0, x1, y1) : (0.5, 0.5, 0.5, 0.5);
            // on counts the shot's frames from 0; p runs from 0 to 1 across them.
            var p = frames > 1 ? $"on/{frames - 1}" : "0";
            arguments.AddRange(["-i", still]);
            graph =
                $"[0:v]zoompan=z='{Number(zoom.Item1)}+({Number(zoom.Item2 - zoom.Item1)})*{p}'"
                + $":x='(iw-iw/zoom)*({Number(focus.Item1)}+({Number(focus.Item3 - focus.Item1)})*{p})'"
                + $":y='(ih-ih/zoom)*({Number(focus.Item2)}+({Number(focus.Item4 - focus.Item2)})*{p})'"
                + $":d={frames}:s={w}x{h}:fps={VideoLayout.FrameRate},setsar=1,format=yuv420p[v]";
        }
        else
        {
            var file = shot.File ?? throw new VideoException("A video shot without its clip.");
            arguments.AddRange(["-ss", Number(Math.Max(0, shot.From)), "-i", file]);
            var source = $"[0:v]fps={VideoLayout.FrameRate},"
                + (shot.Hdr && tonemap
                    ? "zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p,"
                    : "");
            graph = shot.Fit == "blur"
                ? $"{source}split[a][b];"
                    + $"[a]scale={w / 8}:{h / 8}:force_original_aspect_ratio=increase,crop={w / 8}:{h / 8},boxblur=4,scale={w}:{h},eq=brightness=-0.12[bg];"
                    + $"[b]scale={w * 47 / 50}:{h * 47 / 50}:force_original_aspect_ratio=decrease:force_divisible_by=2[fg];"
                    + "[bg][fg]overlay=(W-w)/2:(H-h)/2,setsar=1,format=yuv420p,tpad=stop_mode=clone:stop=-1[v]"
                : $"{source}scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h},setsar=1,format=yuv420p,tpad=stop_mode=clone:stop=-1[v]";
        }
        arguments.AddRange(["-filter_complex", graph, "-map", "[v]", "-an", "-sn", "-dn", "-frames:v", frames.ToString(CultureInfo.InvariantCulture)]);
        arguments.AddRange([
            "-r", VideoLayout.FrameRate.ToString(CultureInfo.InvariantCulture),
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "14", "-pix_fmt", "yuv420p",
            // The same time base in every shot, or joining them without encoding again shifts the cuts.
            "-video_track_timescale", "15360",
            output,
        ]);
        return arguments;
    }

    private static readonly Dictionary<string, bool> Tonemapping = [];

    /// <summary>Tone mapping needs zscale (zimg), which not every ffmpeg has; without it HDR clips stay as they are.</summary>
    private static async Task<bool> CanTonemapAsync(string ffmpeg, CancellationToken cancellationToken)
    {
        lock (Tonemapping)
        {
            if (Tonemapping.TryGetValue(ffmpeg, out var known))
            {
                return known;
            }
        }
        var result = await ToolProcess.RunAsync(ffmpeg, ["-hide_banner", "-filters"], TimeSpan.FromMinutes(1), cancellationToken);
        var can = result.Output.Contains(" zscale ", StringComparison.Ordinal) && result.Output.Contains(" tonemap ", StringComparison.Ordinal);
        lock (Tonemapping)
        {
            Tonemapping[ffmpeg] = can;
        }
        return can;
    }

    private static async Task RunAsync(string ffmpeg, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ToolResult result;
        try
        {
            result = await ToolProcess.RunAsync(ffmpeg, arguments, timeout, cancellationToken);
        }
        catch (VoiceServiceException exception)
        {
            throw new VideoException(exception.Message);
        }
        if (result.ExitCode != 0)
        {
            throw new VideoException(ToolProcess.Reason(result, $"ffmpeg exited with {result.ExitCode}."));
        }
    }

    private static string Number(double value) => value.ToString("0.#####", CultureInfo.InvariantCulture);
}

/// <summary>The folders under <see cref="MontageOptions.MediaRoot"/>, each one video's pictures.</summary>
public static class MediaFolders
{
    /// <summary>What <c>yueui_montage.py</c> reads; anything else in a folder is left alone.</summary>
    public static readonly IReadOnlySet<string> Photos = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".heic", ".heif", ".webp" };

    public static readonly IReadOnlySet<string> Videos = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov", ".m4v" };

    public static IReadOnlyList<MediaFolder> List(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }
        return [.. new DirectoryInfo(root).EnumerateDirectories()
            .Where(d => !d.Name.StartsWith('.'))
            .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(d =>
            {
                var files = Files(d.FullName).ToList();
                return new MediaFolder(d.Name, files.Count(f => Photos.Contains(Path.GetExtension(f))), files.Count(f => Videos.Contains(Path.GetExtension(f))));
            })];
    }

    /// <summary>The folder's full path when <paramref name="name"/> is one of the root's subfolders; null for anything else (a path, "..").</summary>
    public static string? Resolve(string root, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith('.') || name != Path.GetFileName(name) || name.Contains('/') || name.Contains('\\'))
        {
            return null;
        }
        var path = Path.Combine(root, name);
        return Directory.Exists(path) ? path : null;
    }

    private static IEnumerable<string> Files(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System })
                .Where(f => !Path.GetFileName(f).StartsWith('.'));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
