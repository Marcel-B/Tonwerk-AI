using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using YueUI.Api.Analysis;

namespace YueUI.Api.Tests;

/// <summary>
/// Against the real librosa, where a Python with it is: the photo videos' or audio to MIDI's environment at its default
/// place, or <c>YUEUI_ANALYSIS_PYTHON</c>. Without one there is nothing to run against.
/// </summary>
public sealed class LibrosaListenerTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("yueui-analysis-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task A_song_in_a_minor_at_100_bpm_is_heard_as_such_and_a_broken_one_fails_alone()
    {
        var listener = new LibrosaListener(
            Options.Create(new AnalysisOptions { Python = Environment.GetEnvironmentVariable("YUEUI_ANALYSIS_PYTHON") }),
            Options.Create(new Video.MontageOptions()),
            Options.Create(new AudioMidi.AudioMidiOptions()),
            NullLogger<LibrosaListener>.Instance);
        if (!listener.Installed)
        {
            return;
        }
        var song = Path.Combine(_directory, "song.wav");
        File.WriteAllBytes(song, Song(100, [[57, 60, 64], [53, 57, 60], [48, 52, 55], [52, 56, 59]], 30));
        var broken = Path.Combine(_directory, "broken.flac");
        File.WriteAllBytes(broken, [.. "fLaC"u8, .. new byte[96]]);

        var heard = await listener.HearAsync([new SongToHear("a/song1", song, null), new SongToHear("a/song2", broken, null)], CancellationToken.None);

        Assert.Equal(2, heard.Count);
        Assert.Equal("Am", heard[0].Key);
        Assert.InRange(heard[0].Tempo!.Value, 98, 102);
        Assert.NotNull(heard[1].Error);
    }

    /// <summary>A chord per bar (sine pairs), a kick on every beat and a hi-hat between, 44.1 kHz mono 16 bit.</summary>
    private static byte[] Song(double bpm, int[][] chords, int seconds)
    {
        const int rate = 44100;
        var samples = new double[rate * seconds];
        var beat = 60 / bpm;
        var random = new Random(1);
        for (var i = 0; i < samples.Length; i++)
        {
            var t = i / (double)rate;
            foreach (var note in chords[(int)(t / (beat * 4)) % chords.Length])
            {
                var frequency = 440 * Math.Pow(2, (note - 69) / 12.0);
                samples[i] += 0.08 * Math.Sin(2 * Math.PI * frequency * t) + 0.03 * Math.Sin(4 * Math.PI * frequency * t);
            }
            var sinceBeat = t % beat;
            samples[i] += 0.6 * Math.Exp(-sinceBeat * 30) * Math.Sin(2 * Math.PI * 55 * sinceBeat);
            var sinceOff = (t + beat / 2) % beat;
            if (sinceOff < 0.03)
            {
                samples[i] += 0.1 * (random.NextDouble() * 2 - 1) * Math.Exp(-sinceOff * 100);
            }
        }
        var peak = samples.Max(Math.Abs);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + samples.Length * 2);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(rate);
        writer.Write(rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(samples.Length * 2);
        foreach (var sample in samples)
        {
            writer.Write((short)(sample / peak * 0.8 * short.MaxValue));
        }
        writer.Flush();
        return stream.ToArray();
    }
}
