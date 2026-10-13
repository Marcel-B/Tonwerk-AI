using System.Globalization;
using YueUI.Api.Share;
using YueUI.Api.Voices;

namespace YueUI.Api.Video;

/// <summary>
/// Animates the browser's still layers with ffmpeg's own audio visualizations and encodes what YouTube, Shorts, Reels
/// and TikTok recommend: H.264 High at 30 frames a second in yuv420p, AAC at 320 kbit/s and 48 kHz, the index at the
/// front (<c>faststart</c>). Only ffmpeg's built-in filters are used, so Homebrew's ffmpeg needs nothing extra (no
/// fonts: the title is a picture). One frame of a three-minute song is a still most of the way, so CRF 18 keeps it
/// sharp at a few tens of megabytes.
/// </summary>
/// <param name="loudness">Brings the soundtrack to the exports' loudness; none leaves it as it is.</param>
public sealed class FfmpegVideoRenderer(ILogger<FfmpegVideoRenderer> logger, ILoudness? loudness = null) : IVideoRenderer
{
    /// <summary>A long song with particles takes a few minutes on the Mac mini; anything beyond this is hanging.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromHours(2);

    /// <summary>
    /// The analyzer's colour when the browser picked none from the cover: Tonwerk's emerald a shade lighter, so it
    /// stands out on a dark background.
    /// </summary>
    public const string Accent = "0x34d399";

    /// <summary>How fast the particles drift up, in pixels a second.</summary>
    public const int ParticleSpeed = 40;

    /// <summary>How often a second the plasma ball's lightning changes its shape.</summary>
    public const int PlasmaFlicker = 12;

    public bool Available => AacEncoder.FindFfmpeg() is not null;

    public async Task RenderAsync(
        VideoState video,
        string flac,
        double? seconds,
        VideoLayers layers,
        string output,
        Action<double> progress,
        CancellationToken cancellationToken)
    {
        var ffmpeg = AacEncoder.FindFfmpeg() ?? throw new VideoException("ffmpeg is not installed.");
        var duration = await ProbeAsync(ffmpeg, flac, cancellationToken) ?? seconds;
        var temp = output + ".part.mp4";
        var spectrum = video.Effect == VideoEffects.Bars ? output + ".bars" : null;
        try
        {
            if (spectrum is not null)
            {
                await SpectrumBands.WriteAsync(ffmpeg, flac, VideoLayout.For(video.Format).Bars, spectrum, cancellationToken);
            }
            var level = loudness is null ? null : await loudness.FilterAsync(flac, cancellationToken);
            var result = await ToolProcess.RunAsync(
                ffmpeg,
                Arguments(video, flac, duration, layers, temp, spectrum, level),
                Timeout,
                cancellationToken,
                line: line =>
                {
                    // -progress writes key=value lines; out_time_us is how far the output is (out_time_ms is too, despite its name).
                    if (duration is > 0 && line.StartsWith("out_time_us=", StringComparison.Ordinal)
                        && long.TryParse(line.AsSpan("out_time_us=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var micros))
                    {
                        progress(Math.Clamp(micros / 1e6 / duration.Value, 0, 1));
                    }
                });
            if (result.ExitCode != 0 || !File.Exists(temp))
            {
                var reason = ToolProcess.Reason(result, $"ffmpeg exited with {result.ExitCode}.");
                logger.LogWarning("Video {Id} failed: {Reason}", video.Id, string.Join(" | ", result.Tail));
                throw new VideoException(reason);
            }
            File.Move(temp, output, overwrite: true);
        }
        catch (VoiceServiceException exception)
        {
            throw new VideoException(exception.Message);
        }
        finally
        {
            File.Delete(temp);
            if (spectrum is not null)
            {
                File.Delete(spectrum);
            }
        }
    }

    /// <summary>The whole command line; the graph is in <see cref="Graph"/>.</summary>
    /// <param name="spectrum">The bars' heights from <see cref="SpectrumBands"/>, when the video has bars.</param>
    /// <param name="level">The loudness filter for the soundtrack (<see cref="ILoudness"/>), if any.</param>
    public static IReadOnlyList<string> Arguments(
        VideoState video, string flac, double? seconds, VideoLayers layers, string output, string? spectrum = null, string? level = null)
    {
        var rate = VideoLayout.FrameRate.ToString(CultureInfo.InvariantCulture);
        List<string> arguments = ["-nostdin", "-loglevel", "error", "-nostats", "-progress", "pipe:1", "-y"];
        // Each picture is read once; the graph repeats its frame (loop), so a PNG is not decoded thirty times a second.
        // A photo video's background is the montage, a video of its own.
        arguments.AddRange(video.Media is null ? ["-framerate", rate, "-i", layers.Background] : ["-i", layers.Background]);
        arguments.AddRange(["-i", flac]);
        foreach (var layer in new[] { layers.Cover, layers.Title }.OfType<string>().Concat(layers.Motion))
        {
            arguments.AddRange(["-framerate", rate, "-i", layer]);
        }
        if (spectrum is not null)
        {
            var bars = VideoLayout.For(video.Format).Bars;
            arguments.AddRange(["-f", "rawvideo", "-pix_fmt", "gray", "-video_size", $"{bars}x1", "-framerate", rate, "-i", spectrum]);
        }
        if (layers.Shade is not null)
        {
            arguments.AddRange(["-framerate", rate, "-i", layers.Shade]);
        }
        arguments.AddRange(["-filter_complex", Graph(video, seconds, layers.Cover is not null, layers.Title is not null, layers.Motion.Count, layers.Shade is not null, level)]);
        arguments.AddRange(["-map", "[v]", "-map", "[a]"]);
        if (seconds is > 0)
        {
            arguments.AddRange(["-t", Number(seconds.Value)]);
        }
        // The pictures repeat forever; without a length the audio's end has to end the video.
        arguments.Add("-shortest");
        arguments.AddRange([
            "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-profile:v", "high", "-pix_fmt", "yuv420p",
            "-r", rate, "-g", (VideoLayout.FrameRate * 2).ToString(CultureInfo.InvariantCulture),
            "-c:a", "aac", "-b:a", "320k", "-ar", "48000",
            "-movflags", "+faststart", "-f", "mp4", output,
        ]);
        return arguments;
    }

    /// <summary>
    /// Background, then particles or the plasma ball, then the analyzer or waveform in its band, then the cover, then
    /// the title fading in; the whole fades in from and out to black. Inputs: 0 background, 1 audio, then cover and
    /// title when there are, then the moving layer's pictures, the bars' heights and a photo video's shade.
    /// </summary>
    public static string Graph(VideoState video, double? seconds, bool cover, bool title, int motion, bool shade = false, string? level = null)
    {
        var layout = VideoLayout.For(video.Format);
        var (w, h, bandY, bandH) = (layout.Width, layout.Height, layout.BandY, layout.BandHeight);
        var rate = VideoLayout.FrameRate;
        var coverInput = 2;
        var titleInput = cover ? 3 : 2;
        var motionInput = titleInput + (title ? 1 : 0);
        var particles = motion > 0 && video.Motion == VideoMotions.Particles;
        var plasma = motion > 0 && video.Motion == VideoMotions.Plasma;
        var effect = video.Effect is VideoEffects.Bars or VideoEffects.Wave;

        List<string> chains = [];
        List<string> audio = ["a"];
        if (video.Effect == VideoEffects.Wave)
        {
            audio.Add("ae");
        }
        if (particles || plasma)
        {
            audio.Add("ap");
        }
        // The loudness filter comes first, so the waveform and the bass pulse move with what is heard.
        var source = level is null ? "[1:a]" : $"[1:a]{level},aresample=48000,";
        chains.Add(audio.Count == 1 ? $"{source}anull[a]" : $"{source}asplit={audio.Count}{string.Concat(audio.Select(a => $"[{a}]"))}");
        // The montage ends with the song; tpad holds its last frame should it be a frame short.
        chains.Add(video.Media is null ? "[0:v]format=rgba,loop=loop=-1:size=1[bg]" : "[0:v]format=rgba,tpad=stop_mode=clone:stop=-1[bg]");
        var current = "bg";
        if (shade)
        {
            var shadeInput = motionInput + motion + (video.Effect == VideoEffects.Bars ? 1 : 0);
            chains.Add($"[{shadeInput}:v]format=rgba,loop=loop=-1:size=1[shade]");
            chains.Add($"[{current}][shade]overlay=0:0:format=auto[shaded]");
            current = "shaded";
        }

        if (particles)
        {
            // Two copies stacked and a window moving up through them: the browser drew the picture so that its top
            // and bottom edges meet.
            chains.Add($"[{motionInput}:v]format=rgba,loop=loop=-1:size=1,split[p1][p2]");
            chains.Add($"[p1][p2]vstack,crop={w}:{h}:0:'{h}-mod(t*{ParticleSpeed},{h})',split[pc][pt]");
            chains.Add("[pt]alphaextract[pa]");
            // The bass (30 to 250 Hz) as one brightness a frame: the share of the constant-Q bars that light up,
            // between 35 and 100 per cent, so the particles never vanish.
            chains.Add(
                $"[ap]showcqt=s=192x32:fps={rate}:sono_h=0:bar_h=32:axis=0:basefreq=30:endfreq=250,format=gray,"
                + "lut=y='if(gt(val,6),255,0)',scale=1:1:flags=area,lut=y='90+val*0.65',"
                + $"scale={w}:{h}:flags=neighbor[pulse]");
            chains.Add("[pa][pulse]blend=all_mode=multiply[pm]");
            chains.Add("[pc][pm]alphamerge[particles]");
            chains.Add($"[{current}][particles]overlay=0:0:format=auto[withparticles]");
            current = "withparticles";
        }

        if (plasma)
        {
            // The browser drew the ball's lightning PlasmaFrames times. Stacked once and then repeated (stacking every
            // frame would copy them all thirty times a second), a window jumps between them twelve times a second in
            // an order that only repeats after PlasmaFrames² steps, so the eye sees no cycle.
            var (x, y, width, height) = layout.PlasmaArea(cover);
            var frames = motion;
            var inputs = string.Concat(Enumerable.Range(motionInput, frames).Select(i => $"[{i}:v]"));
            chains.Add(
                $"{inputs}vstack=inputs={frames},format=rgba,loop=loop=-1:size=1,"
                + $"crop={width}:{height}:0:'{height}*mod(floor(t*{PlasmaFlicker})*3+floor(t*{PlasmaFlicker}/{frames})*5,{frames})',split[lc][lt]");
            chains.Add("[lt]alphaextract[la]");
            // Like the particles' pulse, but steeper: the lightning glows at 40 per cent without bass and flares to
            // full strength once a third of the bass band sounds, as on a kick.
            chains.Add(
                $"[ap]showcqt=s=192x32:fps={rate}:sono_h=0:bar_h=32:axis=0:basefreq=30:endfreq=250,format=gray,"
                + "lut=y='if(gt(val,6),255,0)',scale=1:1:flags=area,lut=y='100+val*2',"
                + $"scale={width}:{height}:flags=neighbor[pulse]");
            chains.Add("[la][pulse]blend=all_mode=multiply[lm]");
            chains.Add("[lc][lm]alphamerge[plasma]");
            chains.Add($"[{current}][plasma]overlay={x}:{y}:format=auto[withplasma]");
            current = "withplasma";
        }

        if (effect)
        {
            if (video.Effect == VideoEffects.Bars)
            {
                // The bars' heights come from SpectrumBands, computed as the app's analyzer computes them, as a
                // raw grey video one pixel high: one pixel per bar, drawn here as a column with a soft top edge and
                // cut apart by the gap mask.
                var bars = layout.Bars;
                chains.Add(
                    $"[{motionInput + motion}:v]format=gray,"
                    + $"scale={bars}:{bandH}:flags=neighbor,geq=lum='clip((p(X,Y)*{bandH}/255-({bandH}-1-Y))*255,0,255)',"
                    + $"scale={w}:{bandH}:flags=neighbor[bh]");
                chains.Add(
                    $"color=black:s={w}x{bandH}:r={rate}:d=1,format=gray,"
                    + $"geq=lum='if(lt(mod(X,{VideoLayout.BarPitch}),{VideoLayout.BarWidth}),255,0)',loop=loop=-1:size=1[bm]");
                chains.Add("[bh][bm]blend=all_mode=multiply[ba]");
            }
            else
            {
                chains.Add(
                    $"[ae]showwaves=s={w}x{bandH}:mode=cline:rate={rate}:scale=sqrt:draw=full:colors=white,format=gray,"
                    + "lut=y='if(gt(val,40),255,0)'[ba]");
            }
            chains.Add($"color={Colour(video)}:s={w}x{bandH}:r={rate},format=rgba[bc]");
            chains.Add("[bc][ba]alphamerge[band]");
            chains.Add($"[{current}][band]overlay=0:{bandY}:format=auto[withband]");
            current = "withband";
        }

        if (cover)
        {
            chains.Add($"[{coverInput}:v]format=rgba,loop=loop=-1:size=1[cover]");
            chains.Add($"[{current}][cover]overlay=0:0:format=auto[withcover]");
            current = "withcover";
        }

        if (title)
        {
            chains.Add($"[{titleInput}:v]format=rgba,loop=loop=-1:size=1,fade=t=in:st=0.8:d=1.2:alpha=1[title]");
            chains.Add($"[{current}][title]overlay=0:0:format=auto[withtitle]");
            current = "withtitle";
        }

        var fades = "fade=t=in:st=0:d=0.8";
        if (seconds is > 3)
        {
            fades += $",fade=t=out:st={Number(seconds.Value - 1.5)}:d=1.5";
        }
        chains.Add($"[{current}]{fades},format=yuv420p[v]");
        return string.Join(";", chains);
    }

    /// <summary>The FLAC's length from ffprobe, which lies next to ffmpeg; null when it cannot tell.</summary>
    private async Task<double?> ProbeAsync(string ffmpeg, string flac, CancellationToken cancellationToken)
    {
        var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe");
        if (!File.Exists(ffprobe))
        {
            return null;
        }
        try
        {
            var result = await ToolProcess.RunAsync(
                ffprobe,
                ["-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", flac],
                TimeSpan.FromMinutes(1),
                cancellationToken);
            return result.ExitCode == 0
                && double.TryParse(result.Output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                && seconds > 0
                    ? seconds
                    : null;
        }
        catch (VoiceServiceException exception)
        {
            logger.LogWarning("ffprobe failed on {Flac}: {Message}", flac, exception.Message);
            return null;
        }
    }

    /// <summary>The video's colour as ffmpeg writes it; the endpoint only accepts <c>#rrggbb</c>.</summary>
    private static string Colour(VideoState video) =>
        video.Color is { Length: 7 } colour && colour[0] == '#' ? $"0x{colour[1..]}" : Accent;

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
