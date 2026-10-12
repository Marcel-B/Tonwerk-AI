using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using YueUI.Api.Share;
using YueUI.Api.Video;

namespace YueUI.Api.Tests;

/// <summary>
/// Against the real ffmpeg, where there is one, and the real planner where its environment is: the default location or
/// <c>YUEUI_MONTAGE_PYTHON</c> (a Python that <c>deploy/install-montage.sh</c> set up, or any with librosa, Pillow
/// and pillow-heif; CLIP is only needed with a motif, which these tests leave out since its weights are a download).
/// </summary>
public sealed class MontageBuilderTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("yueui-montage-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Each_shot_ends_on_the_frame_its_cut_falls_on()
    {
        // 0.52 s at 30 fps is frame 15.6; the next shot starts there too, so nothing is lost or doubled.
        var shots = new[] { new MontageShot { Start = 0, End = 0.52 }, new MontageShot { Start = 0.52, End = 1.04 } };

        Assert.Equal([16, 15], shots.Select(MontageBuilder.Frames));
    }

    [Fact]
    public void A_photo_zooms_from_its_first_to_its_last_frame_around_its_focus()
    {
        var shot = new MontageShot { Kind = "photo", Still = "still-0001.jpg", Zoom = [1, 1.1], Focus = [0.4, 0.5, 0.6, 0.5], Start = 0, End = 2 };

        var arguments = MontageBuilder.ShotArguments(shot, 60, "/work", VideoLayout.For(VideoFormats.Landscape), "/work/shot.mp4");

        Assert.Contains("/work/still-0001.jpg", arguments);
        var graph = arguments[arguments.ToList().IndexOf("-filter_complex") + 1];
        Assert.Contains("z='1+(0.1)*on/59'", graph);
        Assert.Contains("x='(iw-iw/zoom)*(0.4+(0.2)*on/59)'", graph);
        Assert.Contains(":d=60:s=1920x1080:fps=30", graph);
    }

    [Fact]
    public async Task Shots_of_photos_and_clips_become_one_silent_video_of_the_frame_size_and_the_plans_length()
    {
        if (AacEncoder.FindFfmpeg() is not { } ffmpeg)
        {
            return; // The Mac and CI's runner have ffmpeg.
        }
        var layout = VideoLayout.For(VideoFormats.Portrait);
        var still = Path.Combine(_directory, "still-0001.jpg");
        await Run(ffmpeg, "-nostdin", "-loglevel", "error", "-y", "-f", "lavfi", "-i", $"testsrc2=s={layout.Width * 2}x{layout.Height * 2}", "-frames:v", "1", still);
        var wide = Path.Combine(_directory, "wide.mp4");
        await Run(ffmpeg, "-nostdin", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc=s=640x360:r=25:d=1", "-c:v", "libx264", "-pix_fmt", "yuv420p", wide);
        // Tagged as an iPhone's HLG clip, so the tone mapping runs where ffmpeg has zscale.
        var hdr = Path.Combine(_directory, "hdr.mov");
        await Run(ffmpeg, "-nostdin", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=360x640:r=30:d=2", "-c:v", "libx264", "-pix_fmt", "yuv420p",
            "-color_trc", "arib-std-b67", "-color_primaries", "bt2020", "-colorspace", "bt2020nc", hdr);
        var plan = new MontagePlan(3.1, 120,
        [
            new MontageShot { Start = 0, End = 1.02, Kind = "photo", Still = "still-0001.jpg", Zoom = [1, 1.08], Focus = [0.5, 0.5, 0.6, 0.4] },
            // Longer than its clip: the last frame is held.
            new MontageShot { Start = 1.02, End = 2.04, Kind = "video", File = wide, From = 0.5, Fit = "blur" },
            new MontageShot { Start = 2.04, End = 3.1, Kind = "video", File = hdr, From = 0.2, Fit = "cover", Hdr = true },
        ]);
        var output = Path.Combine(_directory, "montage.mp4");
        var progress = new List<double>();

        await MontageBuilder.RenderShotsAsync(ffmpeg, plan, layout, _directory, output, progress.Add, CancellationToken.None);

        var probe = await Run(ffmpeg.Replace("ffmpeg", "ffprobe"), "-v", "error", "-count_frames", "-show_entries",
            "stream=codec_type,width,height,nb_read_frames", "-of", "default=nw=1", output);
        Assert.Contains("codec_type=video", probe);
        Assert.DoesNotContain("codec_type=audio", probe);
        Assert.Contains($"width={layout.Width}", probe);
        Assert.Contains($"height={layout.Height}", probe);
        Assert.Contains("nb_read_frames=93", probe);
        Assert.Equal(1, progress[^1]);
    }

    [Fact]
    public async Task A_song_and_a_folder_become_a_montage_cut_on_the_beat()
    {
        if (AacEncoder.FindFfmpeg() is not { } ffmpeg || Builder() is not { Installed: true } builder)
        {
            return;
        }
        var flac = Path.Combine(_directory, "audio.flac");
        // Twelve seconds of a kick at 120 BPM.
        await Run(ffmpeg, "-nostdin", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
            "aevalsrc='0.8*sin(2*PI*60*t)*exp(-30*mod(t,0.5))+0.1*sin(2*PI*220*t)':s=44100:d=12", "-ac", "2", flac);
        var folder = Directory.CreateDirectory(Path.Combine(_directory, "Hund")).FullName;
        string[] sources = ["mandelbrot=s=1200x900", "cellauto=s=1200x900:rule=110", "life=s=1200x900:seed=3:mold=10", "smptehdbars=s=1200x900"];
        for (var i = 0; i < sources.Length; i++)
        {
            await Run(ffmpeg, "-nostdin", "-loglevel", "error", "-y", "-f", "lavfi", "-i", sources[i],
                "-frames:v", "1", Path.Combine(folder, $"photo{i}.jpg"));
        }
        await Run(ffmpeg, "-nostdin", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc=s=640x360:r=30:d=6", "-c:v", "libx264", "-pix_fmt", "yuv420p",
            Path.Combine(folder, "walk.mp4"));
        var video = new VideoState { Id = "v1", SongId = "run/song1", Title = "Hund", Format = VideoFormats.Landscape, Effect = VideoEffects.None, Media = "Hund" };
        var work = Path.Combine(_directory, "work");
        var output = Path.Combine(_directory, "montage.mp4");
        var progress = new List<double>();

        await builder.BuildAsync(video, flac, folder, work, output, progress.Add, CancellationToken.None);

        var probe = await Run(ffmpeg.Replace("ffmpeg", "ffprobe"), "-v", "error", "-count_frames", "-show_entries",
            "stream=width,height,nb_read_frames", "-of", "default=nw=1", output);
        Assert.Contains("width=1920", probe);
        Assert.Contains("nb_read_frames=360", probe);
        var plan = System.Text.Json.JsonSerializer.Deserialize<MontagePlan>(
            File.ReadAllText(Path.Combine(work, "plan.json")), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.InRange(plan.Tempo, 115, 125);
        Assert.Contains(plan.Shots, s => s.Kind == "video");
        Assert.Contains(plan.Shots, s => s.Kind == "photo");
        // Every cut but the last lies on the beat grid (half a second, give or take a frame of the beat tracker).
        Assert.All(plan.Shots.Skip(1), shot => Assert.InRange(Math.Abs(shot.Start - Math.Round(shot.Start * 2) / 2), 0, 0.06));
        Assert.True(progress.Zip(progress.Skip(1)).All(p => p.Second >= p.First), "progress only grows");
    }

    private static MontageBuilder? Builder()
    {
        var python = Environment.GetEnvironmentVariable("YUEUI_MONTAGE_PYTHON");
        var options = new MontageOptions { Python = string.IsNullOrEmpty(python) ? null : python };
        return File.Exists(options.ResolvedPython) ? new MontageBuilder(Options.Create(options), NullLogger<MontageBuilder>.Instance) : null;
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
