using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using YueUI.Api.Data;
using YueUI.Api.Share;
using YueUI.Api.Video;

namespace YueUI.Api.Tests;

/// <summary>The measurement against the real ffmpeg, where there is one; parsing and the filter everywhere.</summary>
public sealed class LoudnessTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("yueui-loud-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private Loudness Make(LoudnessOptions? options = null) => new(
        new SqliteDatabase(Options.Create(new DataOptions { Path = Path.Combine(_directory, "yueui.db") })),
        Options.Create(options ?? new LoudnessOptions()),
        NullLogger<Loudness>.Instance);

    [Fact]
    public void The_first_pass_is_read_from_among_ffmpegs_output()
    {
        const string output = """
            [out#0/null @ 0x5] video:0kB audio:1875kB
            [Parsed_loudnorm_0 @ 0x5]
            {
            	"input_i" : "-8.75",
            	"input_tp" : "0.31",
            	"input_lra" : "4.20",
            	"input_thresh" : "-18.80",
            	"output_i" : "-13.95",
            	"output_tp" : "-1.00",
            	"output_lra" : "3.00",
            	"output_thresh" : "-23.95",
            	"normalization_type" : "dynamic",
            	"target_offset" : "-0.05"
            }
            """;

        var measured = Loudness.Parse(output);

        Assert.Equal(new Measurement(-8.75, 0.31, 4.2, -18.8, -0.05), measured);
        Assert.Equal(
            "loudnorm=I=-14:TP=-1:LRA=50:measured_I=-8.75:measured_TP=0.31:measured_LRA=4.2:measured_thresh=-18.8:offset=-0.05:linear=true",
            Loudness.Filter(new LoudnessOptions(), measured!));
    }

    [Fact]
    public void Silence_and_garbage_are_not_measurements()
    {
        Assert.Null(Loudness.Parse("""{ "input_i" : "-inf", "input_tp" : "-inf", "input_lra" : "0.00", "input_thresh" : "-70.00", "target_offset" : "inf" }"""));
        Assert.Null(Loudness.Parse("no such file"));
    }

    [Fact]
    public async Task Switched_off_it_leaves_the_audio_alone()
    {
        var loudness = Make(new LoudnessOptions { Enabled = false });

        Assert.Equal("", loudness.Key);
        Assert.Null(await loudness.FilterAsync(Path.Combine(_directory, "any.flac"), CancellationToken.None));
    }

    [Fact]
    public void A_videos_soundtrack_is_levelled_before_it_is_split_for_the_effects()
    {
        var video = new VideoState { Id = "v1", SongId = "run/song1", Title = "", Format = VideoFormats.Landscape, Effect = VideoEffects.Wave };

        var graph = FfmpegVideoRenderer.Graph(video, 3, false, false, 0, level: "loudnorm=I=-14");

        Assert.Contains("[1:a]loudnorm=I=-14,aresample=48000,asplit=2[a][ae]", graph);
        Assert.Contains("[1:a]asplit=2[a][ae]", FfmpegVideoRenderer.Graph(video, 3, false, false, 0));
    }

    [Fact]
    public async Task A_loud_song_comes_out_at_the_target_and_its_measurement_is_kept()
    {
        if (AacEncoder.FindFfmpeg() is not { } ffmpeg)
        {
            return; // Nothing to run against on a machine without ffmpeg; the Mac and CI's runner have it.
        }
        var flac = Path.Combine(_directory, "audio.flac");
        // Pink noise near full scale, as loud as YuE2's masters or louder.
        await Run(ffmpeg, "-nostdin", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "anoisesrc=color=pink:amplitude=0.8:duration=8:sample_rate=44100", "-c:a", "flac", flac);
        var loudness = Make();

        var filter = await loudness.FilterAsync(flac, CancellationToken.None);
        Assert.NotNull(filter);
        Assert.Contains("linear=true", filter);

        foreach (var (format, name) in new[] { (AudioFormat.M4a, "out.m4a"), (AudioFormat.Mp3, "out.mp3"), (AudioFormat.Flac, "out.flac") })
        {
            var output = Path.Combine(_directory, name);
            Assert.True(await new AacEncoder(loudness, NullLogger<AacEncoder>.Instance).EncodeAsync(flac, output, format, 192_000, CancellationToken.None));
            var measured = Loudness.Parse(await Run(ffmpeg, "-nostdin", "-hide_banner", "-nostats", "-i", output, "-af", "loudnorm=print_format=json", "-f", "null", "-"));
            Assert.NotNull(measured);
            Assert.InRange(measured!.Integrated, -15, -13);
            Assert.True(measured.TruePeak <= -0.5, $"{name} peaks at {measured.TruePeak} dBTP");
        }

        // The second time comes from the database, even without ffmpeg running: same filter.
        Assert.Equal(filter, await Make().FilterAsync(flac, CancellationToken.None));
    }

    /// <summary>Standard error too, since that is where ffmpeg prints the measurement.</summary>
    private static async Task<string> Run(string tool, params string[] arguments)
    {
        var start = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
        return await output + error;
    }
}
