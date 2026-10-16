namespace YueUI.Api.Video;

/// <summary>
/// A song as a video to upload: its cover (or none) over a blurred copy of itself, optionally the title, an analyzer
/// or a waveform, and drifting particles or a plasma ball moving to the music. The browser draws the still layers (it already draws covers),
/// ffmpeg animates and encodes them (<see cref="FfmpegVideoRenderer"/>). The files are this app's own, in
/// <c>videos/&lt;id&gt;/</c> next to its database. <see cref="Stage"/>: queued, rendering, done, failed or cancelled.
/// </summary>
public sealed record VideoState
{
    public required string Id { get; init; }

    /// <summary><c>run/songN</c>.</summary>
    public required string SongId { get; init; }

    /// <summary>The run's title when it was asked for, for the queue, the notification and the file name.</summary>
    public required string Title { get; init; }

    /// <inheritdoc cref="VideoFormats"/>
    public required string Format { get; init; }

    /// <inheritdoc cref="VideoEffects"/>
    public required string Effect { get; init; }

    /// <inheritdoc cref="VideoMotions"/>
    public string Motion { get; init; } = VideoMotions.None;

    /// <summary>The cover sits in the middle; without it only its blurred copy in the background is left.</summary>
    public bool ShowCover { get; init; } = true;

    /// <summary>
    /// The analyzer's or waveform's colour as <c>#rrggbb</c>, picked by the browser from the cover so it matches;
    /// null for Tonwerk's own.
    /// </summary>
    public string? Color { get; init; }

    /// <summary>The title fades in under the cover.</summary>
    public bool ShowTitle { get; init; }

    /// <summary>
    /// A photo video: the subfolder of <see cref="MontageOptions.MediaRoot"/> whose photos and clips are cut to the
    /// song's beats (<see cref="MontageBuilder"/>) and take the blurred cover's place. Null for a cover video.
    /// </summary>
    public string? Media { get; init; }

    /// <summary>What the pictures should show ("Hund am Strand"); with it CLIP picks the ones that fit best.</summary>
    public string? Motif { get; init; }

    public string Stage { get; init; } = "queued";

    /// <summary>How much of the song is rendered, 0 to 1.</summary>
    public double Fraction { get; init; }

    /// <summary>Why it failed.</summary>
    public string? Message { get; init; }

    /// <summary>The finished file's size.</summary>
    public long? Bytes { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public bool Finished => Stage is "done" or "failed" or "cancelled";
}

/// <summary>
/// <c>landscape</c>: 1920×1080, what YouTube shows in its player. <c>portrait</c>: 1080×1920, for Shorts, Reels and
/// TikTok, which crop anything else.
/// </summary>
public static class VideoFormats
{
    public const string Landscape = "landscape";
    public const string Portrait = "portrait";

    public static readonly IReadOnlyList<string> All = [Landscape, Portrait];
}

/// <summary><c>bars</c>: a spectrum analyzer; <c>wave</c>: the waveform; <c>none</c>: nothing in the band.</summary>
public static class VideoEffects
{
    public const string Bars = "bars";
    public const string Wave = "wave";
    public const string None = "none";

    public static readonly IReadOnlyList<string> All = [Bars, Wave, None];
}

/// <summary>
/// What moves behind the cover. <c>particles</c>: specks drifting up that light up with the bass; <c>plasma</c>: a
/// plasma ball, lightning from the middle (behind the cover, if there is one) that flickers and flares with the bass;
/// <c>none</c>: nothing.
/// </summary>
public static class VideoMotions
{
    public const string None = "none";
    public const string Particles = "particles";
    public const string Plasma = "plasma";

    public static readonly IReadOnlyList<string> All = [None, Particles, Plasma];
}

/// <summary>
/// Where things go in a frame. The browser draws cover and title around the band (<c>video.ts</c> holds the same
/// numbers), the analyzer or waveform fills the band; particles and background take the whole frame.
/// </summary>
public sealed record VideoLayout(int Width, int Height, int BandY, int BandHeight)
{
    /// <summary>YouTube and the phones' apps play 30 frames a second; more only makes the file bigger.</summary>
    public const int FrameRate = 30;

    /// <summary>
    /// One analyzer bar and the gap after it, in pixels: 48 bars across a landscape frame, 27 in portrait, about as
    /// many as the app's full-screen player shows (40), and the gap a fifth as there.
    /// </summary>
    public const int BarPitch = 40;

    public const int BarWidth = 32;

    /// <summary>How many analyzer bars fit across the frame.</summary>
    public int Bars => Width / BarPitch;

    /// <summary>
    /// The plasma ball comes as this many pictures of its area (<see cref="PlasmaArea"/>), which the renderer stacks
    /// and flickers between.
    /// </summary>
    public const int PlasmaFrames = 8;

    /// <summary>
    /// Where the plasma ball's pictures go. Behind a cover: a square centred on the cover's middle (<c>video.ts</c>
    /// draws the cover there), large enough for the lightning to reach well beyond it. Without a cover the lightning
    /// runs from the same middle to the frame's edges, so its pictures are the whole frame.
    /// </summary>
    /// <remarks>The landscape square reaches above the frame; overlay cuts it off.</remarks>
    public (int X, int Y, int Width, int Height) PlasmaArea(bool behindCover) => !behindCover
        ? (0, 0, Width, Height)
        : Width == 1080
            ? (0, 70, 1080, 1080)
            : (460, -130, 1000, 1000);

    /// <summary>
    /// Lyrics come as strips of this many lines, each a slot of the band's size under an empty one (the browser draws
    /// them, <c>video.ts</c> <c>drawLyricsStrips</c>): a whole song in one picture would be taller than iOS lets a
    /// canvas be.
    /// </summary>
    public const int LyricsPerStrip = 24;

    /// <summary>How long a line stays after it was sung, unless the next one starts sooner.</summary>
    public const double LyricsLinger = 1.5;

    /// <summary>When each timed line is on screen: from its start until the next starts, at most a moment past its end.</summary>
    public static IReadOnlyList<LyricsCue> Cues(Timing.LyricsTiming timing) =>
    [
        .. timing.Lines.Select((line, i) => new LyricsCue(
            line.Start,
            Math.Max(line.Start, Math.Min(line.End + LyricsLinger, i + 1 < timing.Lines.Count ? timing.Lines[i + 1].Start : double.MaxValue)))),
    ];

    /// <remarks>
    /// The vertical band sits above the bottom fifth, which Shorts, Reels and TikTok cover with caption and buttons.
    /// </remarks>
    public static VideoLayout For(string format) => format == VideoFormats.Portrait
        ? new VideoLayout(1080, 1920, 1270, 220)
        : new VideoLayout(1920, 1080, 850, 180);
}

/// <summary>
/// The still layers the browser drew, as PNGs of the frame's size (a photo video's <see cref="Background"/> is the
/// montage instead, a silent MP4 of the frame's size and the song's length) (the plasma ball's of its area, see
/// <see cref="VideoLayout.PlasmaArea"/>); all but the background only when asked for. <see cref="Motion"/>: one
/// picture of particles, <see cref="VideoLayout.PlasmaFrames"/> of the plasma ball, none without motion.
/// </summary>
/// <param name="Shade">
/// A photo video's darkening over its pictures, towards the bottom and the edges as the cover video's background has it,
/// so the band and the title stay readable on a bright photo.
/// </param>
/// <param name="Lyrics">
/// The timed lyrics' strips (<see cref="VideoLayout.LyricsPerStrip"/>), each with the times of its lines; none without.
/// </param>
public sealed record VideoLayers(
    string Background,
    string? Cover,
    string? Title,
    IReadOnlyList<string> Motion,
    string? Shade = null,
    IReadOnlyList<LyricsStrip>? Lyrics = null);

/// <summary>When a lyrics line is on screen, in seconds of the song.</summary>
public sealed record LyricsCue(double Start, double End);

/// <summary>A picture of lyrics lines, an empty slot first, then one slot per line, with when each is shown.</summary>
public sealed record LyricsStrip(string Path, IReadOnlyList<LyricsCue> Cues);

/// <summary>Makes the video from a song's FLAC and the layers. Tests replace it.</summary>
public interface IVideoRenderer
{
    /// <summary>False when this machine has no ffmpeg.</summary>
    bool Available { get; }

    /// <param name="seconds">The song's length from its <c>result.json</c>, for when ffprobe cannot tell.</param>
    /// <param name="progress">How much is rendered, 0 to 1.</param>
    /// <exception cref="VideoException">ffmpeg failed; the message says why.</exception>
    Task RenderAsync(
        VideoState video,
        string flac,
        double? seconds,
        VideoLayers layers,
        string output,
        Action<double> progress,
        CancellationToken cancellationToken);

    /// <summary>
    /// A small copy of a finished video to send through a messenger (720p, a few Mbit/s): the video itself stays at
    /// the quality YouTube gets, which encodes everything again anyway, but is hundreds of megabytes for a photo video.
    /// </summary>
    /// <exception cref="VideoException">ffmpeg failed; the message says why.</exception>
    Task ShrinkAsync(string source, string format, string output, CancellationToken cancellationToken);
}

public sealed class VideoException(string message) : Exception(message);
