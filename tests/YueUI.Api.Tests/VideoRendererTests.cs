using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using YueUI.Api.Share;
using YueUI.Api.Video;

namespace YueUI.Api.Tests;

/// <summary>Against the real ffmpeg, where there is one: the filter graph is what could break.</summary>
public sealed class VideoRendererTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("yueui-video-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    public static TheoryData<string, string, bool, string, bool> Settings => new()
    {
        { VideoFormats.Landscape, VideoEffects.Bars, true, VideoMotions.Particles, true },
        { VideoFormats.Portrait, VideoEffects.Wave, false, VideoMotions.Particles, true },
        { VideoFormats.Landscape, VideoEffects.None, true, VideoMotions.None, true },
        { VideoFormats.Portrait, VideoEffects.Bars, false, VideoMotions.None, true },
        { VideoFormats.Landscape, VideoEffects.Bars, true, VideoMotions.Plasma, false },
        { VideoFormats.Portrait, VideoEffects.None, false, VideoMotions.Plasma, true },
    };

    [Theory]
    [MemberData(nameof(Settings))]
    public async Task A_song_becomes_an_H264_video_of_the_frame_size_and_the_song_length(string format, string effect, bool title, string motion, bool cover)
    {
        if (AacEncoder.FindFfmpeg() is not { } ffmpeg)
        {
            return; // Nothing to run against on a machine without ffmpeg; the Mac and CI's runner have it.
        }
        var layout = VideoLayout.For(format);
        var size = $"{layout.Width}x{layout.Height}";
        var flac = Path.Combine(_directory, "audio.flac");
        await Run(ffmpeg, "-nostdin", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=110:sample_rate=48000:duration=3", "-ac", "2", flac);
        var layers = new VideoLayers(
            await Picture(ffmpeg, "background.png", $"color=0x1e1b4b:s={size}"),
            cover ? await Picture(ffmpeg, "cover.png", $"color=black@0:s={size},format=rgba,drawbox=x=100:y=100:w=400:h=400:color=0x10b981@1:t=fill") : null,
            title ? await Picture(ffmpeg, "title.png", $"color=black@0:s={size},format=rgba,drawbox=x=100:y=600:w=800:h=60:color=white@1:t=fill") : null,
            motion switch
            {
                VideoMotions.Particles => [await Picture(ffmpeg, "particles.png", $"color=black@0:s={size},format=rgba,drawbox=x=300:y=50:w=6:h=6:color=white@0.8:t=fill")],
                VideoMotions.Plasma => await PlasmaFrames(ffmpeg, layout.PlasmaArea(cover)),
                _ => [],
            });
        var video = new VideoState { Id = "v1", SongId = "run/song1", Title = "Neon Night", Format = format, Effect = effect, Color = cover ? null : "#a78bfa", Motion = motion, ShowCover = cover, ShowTitle = title };
        var output = Path.Combine(_directory, "video.mp4");
        var progress = new List<double>();

        await new FfmpegVideoRenderer(NullLogger<FfmpegVideoRenderer>.Instance)
            .RenderAsync(video, flac, seconds: null, layers, output, progress.Add, CancellationToken.None);

        var probe = await Run(
            ffmpeg.Replace("ffmpeg", "ffprobe"),
            "-v", "error", "-show_entries", "stream=codec_name,width,height,pix_fmt,sample_rate:format=duration", "-of", "default=nw=1", output);
        Assert.Contains("codec_name=h264", probe);
        Assert.Contains($"width={layout.Width}", probe);
        Assert.Contains($"height={layout.Height}", probe);
        Assert.Contains("pix_fmt=yuv420p", probe);
        Assert.Contains("codec_name=aac", probe);
        Assert.Contains("sample_rate=48000", probe);
        Assert.Matches(@"duration=(2\.9|3\.0)", probe);
        Assert.False(File.Exists(output + ".part.mp4"));
        Assert.NotEmpty(progress);
        Assert.True(progress[^1] > 0.9, $"Last progress {progress[^1]}");
    }

    [Fact]
    public async Task A_photo_videos_montage_moves_under_the_band_and_the_title()
    {
        if (AacEncoder.FindFfmpeg() is not { } ffmpeg)
        {
            return;
        }
        var layout = VideoLayout.For(VideoFormats.Landscape);
        var flac = Path.Combine(_directory, "audio.flac");
        await Run(ffmpeg, "-nostdin", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=110:sample_rate=48000:duration=3", "-ac", "2", flac);
        var montage = Path.Combine(_directory, "montage.mp4");
        // A frame short of the song: the last one is held.
        await Run(ffmpeg, "-nostdin", "-loglevel", "error", "-y", "-f", "lavfi", "-i", $"testsrc2=s={layout.Width}x{layout.Height}:r=30", "-frames:v", "89",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", montage);
        var title = await Picture(ffmpeg, "title.png", $"color=black@0:s={layout.Width}x{layout.Height},format=rgba,drawbox=x=100:y=600:w=800:h=60:color=white@1:t=fill");
        var shade = await Picture(ffmpeg, "shade.png", $"color=black@0.4:s={layout.Width}x{layout.Height},format=rgba");
        var video = new VideoState { Id = "v1", SongId = "run/song1", Title = "Hund", Format = VideoFormats.Landscape, Effect = VideoEffects.Bars, ShowCover = false, ShowTitle = true, Media = "Hund" };
        var output = Path.Combine(_directory, "video.mp4");

        await new FfmpegVideoRenderer(NullLogger<FfmpegVideoRenderer>.Instance)
            .RenderAsync(video, flac, seconds: null, new VideoLayers(montage, null, title, [], shade), output, _ => { }, CancellationToken.None);

        var probe = await Run(ffmpeg.Replace("ffmpeg", "ffprobe"), "-v", "error", "-count_frames", "-select_streams", "v", "-show_entries", "stream=width,nb_read_frames", "-of", "default=nw=1", output);
        Assert.Contains($"width={layout.Width}", probe);
        Assert.Matches("nb_read_frames=(89|90)", probe);
    }

    [Fact]
    public async Task The_small_copy_is_720p_with_the_same_length_and_far_fewer_bytes()
    {
        if (AacEncoder.FindFfmpeg() is not { } ffmpeg)
        {
            return;
        }
        // Noise is the worst case for an encoder, like a photo zooming on every beat.
        var source = Path.Combine(_directory, "video.mp4");
        await Run(ffmpeg, "-nostdin", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "nullsrc=s=1080x1920:r=30:d=4,geq=random(1)*255:128:128",
            "-f", "lavfi", "-i", "sine=frequency=220:sample_rate=48000:duration=4",
            "-c:v", "libx264", "-crf", "18", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "320k", "-shortest", source);
        var output = Path.Combine(_directory, "small.mp4");

        await new FfmpegVideoRenderer(NullLogger<FfmpegVideoRenderer>.Instance).ShrinkAsync(source, VideoFormats.Portrait, output, CancellationToken.None);

        var probe = await Run(ffmpeg.Replace("ffmpeg", "ffprobe"), "-v", "error", "-count_frames", "-show_entries", "stream=codec_type,width,height,nb_read_frames", "-of", "default=nw=1", output);
        Assert.Contains("width=720", probe);
        Assert.Contains("height=1280", probe);
        Assert.Contains("nb_read_frames=120", probe);
        Assert.Contains("codec_type=audio", probe);
        // 2.5 Mbit/s and 128 kbit/s for four seconds, with room for the rate control's buffer.
        Assert.InRange(new FileInfo(output).Length, 1, 2_000_000);
        Assert.True(new FileInfo(output).Length < new FileInfo(source).Length / 3);
    }

    [Fact]
    public async Task Lyrics_lines_show_in_the_band_while_they_are_sung()
    {
        if (AacEncoder.FindFfmpeg() is not { } ffmpeg)
        {
            return;
        }
        var layout = VideoLayout.For(VideoFormats.Landscape);
        var (w, h) = (layout.Width, layout.BandHeight);
        var flac = Path.Combine(_directory, "audio.flac");
        await Run(ffmpeg, "-nostdin", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=110:sample_rate=48000:duration=3", "-ac", "2", flac);
        var background = await Picture(ffmpeg, "background.png", $"color=black:s={w}x{layout.Height}");
        // Strip 0: an empty slot and 24 lines, the first red, the rest only later than the song; strip 1: one blue line.
        var first = await Picture(ffmpeg, "lyrics0.png", $"color=black@0:s={w}x{h * 25},format=rgba,drawbox=x=0:y={h}:w={w}:h={h}:color=red@1:t=fill:replace=1");
        var second = await Picture(ffmpeg, "lyrics1.png", $"color=black@0:s={w}x{h * 2},format=rgba,drawbox=x=0:y={h}:w={w}:h={h}:color=blue@1:t=fill:replace=1");
        List<LyricsCue> cues = [new(0.9, 1.5), .. Enumerable.Range(0, 23).Select(i => new LyricsCue(10 + i, 11 + i))];
        var strips = new List<LyricsStrip> { new(first, cues), new(second, [new LyricsCue(2.0, 2.8)]) };
        var video = new VideoState { Id = "v1", SongId = "run/song1", Title = "", Format = VideoFormats.Landscape, Effect = VideoEffects.None, ShowCover = false };
        var output = Path.Combine(_directory, "video.mp4");

        await new FfmpegVideoRenderer(NullLogger<FfmpegVideoRenderer>.Instance)
            .RenderAsync(video, flac, seconds: null, new VideoLayers(background, null, null, [], Lyrics: strips), output, _ => { }, CancellationToken.None);

        async Task<byte[]> PixelAt(double seconds)
        {
            var pixel = Path.Combine(_directory, "pixel.rgb");
            await Run(ffmpeg, "-nostdin", "-loglevel", "error", "-y", "-ss", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "-i", output,
                "-frames:v", "1", "-vf", $"format=rgb24,crop=2:2:{w / 2}:{layout.BandY + h / 2}", "-f", "rawvideo", "-pix_fmt", "rgb24", pixel);
            return await File.ReadAllBytesAsync(pixel);
        }
        var red = await PixelAt(1.2);
        Assert.True(red[0] > 150 && red[2] < 80, $"red at 1.2 s: {string.Join(",", red)}");
        var none = await PixelAt(1.75);
        Assert.True(none.All(c => c < 60), $"nothing at 1.75 s: {string.Join(",", none)}");
        var blue = await PixelAt(2.4);
        Assert.True(blue[2] > 150 && blue[0] < 80, $"blue at 2.4 s: {string.Join(",", blue)}");
    }

    [Fact]
    public void The_band_takes_the_videos_colour_or_Tonwerks()
    {
        var video = new VideoState { Id = "v1", SongId = "run/song1", Title = "", Format = VideoFormats.Landscape, Effect = VideoEffects.Bars };

        Assert.Contains("color=0xa78bfa:", FfmpegVideoRenderer.Graph(video with { Color = "#a78bfa" }, 3, true, false, 0));
        Assert.Contains($"color={FfmpegVideoRenderer.Accent}:", FfmpegVideoRenderer.Graph(video, 3, true, false, 0));
    }

    [Fact]
    public async Task A_failing_ffmpeg_says_why()
    {
        if (AacEncoder.FindFfmpeg() is null)
        {
            return;
        }
        var flac = Path.Combine(_directory, "audio.flac");
        await File.WriteAllTextAsync(flac, "not audio");
        var missing = Path.Combine(_directory, "missing.png");
        var video = new VideoState { Id = "v1", SongId = "run/song1", Title = "", Format = VideoFormats.Landscape, Effect = VideoEffects.Bars };

        var failure = await Assert.ThrowsAsync<VideoException>(() => new FfmpegVideoRenderer(NullLogger<FfmpegVideoRenderer>.Instance)
            .RenderAsync(video, flac, 3, new VideoLayers(missing, missing, null, []), Path.Combine(_directory, "video.mp4"), _ => { }, CancellationToken.None));

        Assert.NotEmpty(failure.Message);
        Assert.False(File.Exists(Path.Combine(_directory, "video.mp4")));
    }

    private async Task<string[]> PlasmaFrames(string ffmpeg, (int X, int Y, int Width, int Height) area)
    {
        var frames = new string[VideoLayout.PlasmaFrames];
        for (var i = 0; i < frames.Length; i++)
        {
            frames[i] = await Picture(
                ffmpeg,
                $"plasma{i}.png",
                $"color=black@0:s={area.Width}x{area.Height},format=rgba,drawbox=x={100 + i * 10}:y=100:w=6:h=300:color=0xe879f9@1:t=fill");
        }
        return frames;
    }

    private async Task<string> Picture(string ffmpeg, string name, string source)
    {
        var path = Path.Combine(_directory, name);
        await Run(ffmpeg, "-nostdin", "-loglevel", "error", "-y", "-f", "lavfi", "-i", source, "-frames:v", "1", path);
        return path;
    }

    private static async Task<string> Run(string tool, params string[] arguments)
    {
        var start = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
        return output;
    }
}
