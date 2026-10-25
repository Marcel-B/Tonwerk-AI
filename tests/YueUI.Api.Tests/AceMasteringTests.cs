using YueUI.Api.Ace;

namespace YueUI.Api.Tests;

public sealed class AceMasteringTests : IDisposable
{
    private const int Rate = 48000;

    private readonly string _root = Directory.CreateTempSubdirectory("yueui-mastering-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void A_float_wave_over_full_scale_is_brought_down_whole_instead_of_clipped()
    {
        // A sine at 1.6, as ace-synth's float output may be: one gain for the song, so its shape survives.
        var input = FloatWave("loud.wav", i => 1.6 * Math.Sin(2 * Math.PI * 440 * i / Rate));
        var output = Path.Combine(_root, "out.wav");

        AceMastering.Process(input, output, brighten: false);

        var (rate, channels, samples) = ReadPcm24(output);
        Assert.Equal((Rate, 2), (rate, channels));
        Assert.Equal(AceMastering.Peak, samples.Max(Math.Abs), 3);
        // Still a sine: a clipped one would hold its peak for many samples.
        Assert.True(samples.Count(s => Math.Abs(s) > AceMastering.Peak - 0.0001) < samples.Length / 50);
        Assert.Equal(1.0, AceComposer.WaveSeconds(output));
    }

    [Fact]
    public void Brightening_lifts_the_highs_the_vae_loses_and_leaves_the_middle()
    {
        var input = FloatWave("mix.wav", i => 0.3 * Math.Sin(2 * Math.PI * 1000 * i / Rate) + 0.3 * Math.Sin(2 * Math.PI * 16000 * i / Rate));
        var plain = Path.Combine(_root, "plain.wav");
        var bright = Path.Combine(_root, "bright.wav");

        AceMastering.Process(input, plain, brighten: false);
        AceMastering.Process(input, bright, brighten: true);

        double Difference(string file)
        {
            var samples = ReadPcm24(file).Samples.Where((_, i) => i % 2 == 0).Skip(4800).ToArray();
            return 20 * Math.Log10(Level(samples, 16000) / Level(samples, 1000));
        }
        Assert.Equal(0, Difference(plain), 1);
        // The shelf gives about 5.5 dB at 16 kHz and nothing at 1 kHz.
        Assert.InRange(Difference(bright), 4.5, 6.5);
        Assert.Equal(AceMastering.Peak, ReadPcm24(bright).Samples.Max(Math.Abs), 3);
    }

    [Fact]
    public void Silence_and_sixteen_bit_waves_pass()
    {
        var silent = Path.Combine(_root, "silent.wav");
        File.WriteAllBytes(silent, FakeSpeech.Wav(1));
        var output = Path.Combine(_root, "out.wav");

        AceMastering.Process(silent, output, brighten: true);

        var (rate, channels, samples) = ReadPcm24(output);
        Assert.Equal((24000, 1, 24000), (rate, channels, samples.Length));
        Assert.All(samples, s => Assert.Equal(0, s));
    }

    /// <summary>The amplitude of one frequency, by correlating with its sine and cosine.</summary>
    private static double Level(double[] samples, double frequency)
    {
        double re = 0, im = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            var phase = 2 * Math.PI * frequency * i / Rate;
            re += samples[i] * Math.Cos(phase);
            im += samples[i] * Math.Sin(phase);
        }
        return 2 * Math.Sqrt(re * re + im * im) / samples.Length;
    }

    private string FloatWave(string name, Func<int, double> sample)
    {
        var path = Path.Combine(_root, name);
        using var writer = new BinaryWriter(File.Create(path));
        var frames = Rate;
        writer.Write("RIFF"u8);
        writer.Write(36 + frames * 8);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)3);
        writer.Write((short)2);
        writer.Write(Rate);
        writer.Write(Rate * 8);
        writer.Write((short)8);
        writer.Write((short)32);
        writer.Write("data"u8);
        writer.Write(frames * 8);
        for (var i = 0; i < frames; i++)
        {
            writer.Write((float)sample(i));
            writer.Write((float)sample(i));
        }
        return path;
    }

    private static (int Rate, int Channels, double[] Samples) ReadPcm24(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal(1, BitConverter.ToInt16(bytes, 20));
        Assert.Equal(24, BitConverter.ToInt16(bytes, 34));
        var channels = BitConverter.ToInt16(bytes, 22);
        var rate = BitConverter.ToInt32(bytes, 24);
        var size = BitConverter.ToInt32(bytes, 40);
        var samples = new double[size / 3];
        for (var i = 0; i < samples.Length; i++)
        {
            var o = 44 + i * 3;
            samples[i] = (bytes[o] | bytes[o + 1] << 8 | (sbyte)bytes[o + 2] << 16) / 8388607.0;
        }
        return (rate, channels, samples);
    }
}
