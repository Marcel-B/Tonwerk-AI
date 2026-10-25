using System.Buffers.Binary;

namespace YueUI.Api.Ace;

/// <summary>
/// What becomes of <c>ace-synth</c>'s WAV before it is stored: brought to a peak of <see cref="Peak"/> by one gain for the
/// whole song, and with <see cref="AceSongRequest.Brighten"/> given its lost highs back first.
/// </summary>
/// <remarks>
/// <para>
/// acestep.cpp's own normalisation (<c>audio_normalize</c>) scales the 99.999th percentile of the samples to full scale
/// and clips what lies above, which can crackle in loud passages; its <c>peak_clip</c> would turn that off, but
/// <c>ace-synth</c> never hands it to the writer. So the request asks for 32-bit float (<c>wav32</c>), which it writes
/// untouched, and the song is normalised here without clipping.
/// </para>
/// <para>
/// ACE-Step 1.5's VAE loses about 8 dB above 16 kHz and almost 3 dB between 8 and 16 kHz (measured by the music-generator
/// project, MIT, by passing finished music through the VAE alone), so its songs sound dull. The cure is theirs: a gentle
/// high shelf of +6 dB from 12 kHz (the Audio EQ Cookbook's, slope 0.7, as ffmpeg's <c>treble=g=6:f=12000:t=s:w=0.7</c>).
/// </para>
/// Two passes over the file, the filter run twice, so a ten-minute song needs no more memory than a block.
/// </remarks>
public static class AceMastering
{
    public const double ShelfGainDb = 6;

    public const double ShelfFrequency = 12000;

    public const double ShelfSlope = 0.7;

    /// <summary>Just under full scale, so a lossy export's own overshoots have a little room.</summary>
    public const double Peak = 0.95;

    private const int BlockFrames = 1 << 14;

    /// <summary>Reads a PCM (16, 24, 32 bit) or float (32 bit) WAV and writes it as 24-bit PCM at the same rate.</summary>
    public static void Process(string input, string output, bool brighten)
    {
        var format = WaveFormat.Read(input);
        var peak = 0.0;
        Run(input, format, brighten, block =>
        {
            foreach (var sample in block)
            {
                peak = Math.Max(peak, Math.Abs(sample));
            }
        });
        // A silent song stays silent rather than having its noise floor raised to full scale.
        var gain = peak > 1e-6 ? Peak / peak : 1;

        using var stream = File.Create(output);
        using var writer = new BinaryWriter(stream);
        var frames = format.DataBytes / format.BlockAlign;
        var dataBytes = frames * format.Channels * 3;
        writer.Write("RIFF"u8);
        writer.Write((uint)(36 + dataBytes));
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16u);
        writer.Write((ushort)1);
        writer.Write((ushort)format.Channels);
        writer.Write((uint)format.SampleRate);
        writer.Write((uint)(format.SampleRate * format.Channels * 3));
        writer.Write((ushort)(format.Channels * 3));
        writer.Write((ushort)24);
        writer.Write("data"u8);
        writer.Write((uint)dataBytes);
        var bytes = new byte[BlockFrames * format.Channels * 3];
        Run(input, format, brighten, block =>
        {
            for (var i = 0; i < block.Length; i++)
            {
                var value = (int)Math.Round(Math.Clamp(block[i] * gain, -1, 1) * 8388607);
                bytes[i * 3] = (byte)value;
                bytes[i * 3 + 1] = (byte)(value >> 8);
                bytes[i * 3 + 2] = (byte)(value >> 16);
            }
            writer.Write(bytes, 0, block.Length * 3);
        });
    }

    /// <summary>The shelf's coefficients (b0, b1, b2, a1, a2), normalised by a0.</summary>
    public static (double B0, double B1, double B2, double A1, double A2) Shelf(int sampleRate)
    {
        var a = Math.Pow(10, ShelfGainDb / 40);
        var w0 = 2 * Math.PI * Math.Min(ShelfFrequency, sampleRate * 0.45) / sampleRate;
        var (cos, sin) = (Math.Cos(w0), Math.Sin(w0));
        var alpha = sin / 2 * Math.Sqrt((a + 1 / a) * (1 / ShelfSlope - 1) + 2);
        var sa = 2 * Math.Sqrt(a) * alpha;
        var a0 = (a + 1) - (a - 1) * cos + sa;
        return (
            a * ((a + 1) + (a - 1) * cos + sa) / a0,
            -2 * a * ((a - 1) + (a + 1) * cos) / a0,
            a * ((a + 1) + (a - 1) * cos - sa) / a0,
            2 * ((a - 1) - (a + 1) * cos) / a0,
            ((a + 1) - (a - 1) * cos - sa) / a0);
    }

    /// <summary>Reads the samples block by block, interleaved, through the shelf when asked.</summary>
    private static void Run(string input, WaveFormat format, bool brighten, Action<double[]> block)
    {
        var (b0, b1, b2, a1, a2) = Shelf(format.SampleRate);
        var state = new double[format.Channels * 4];
        using var stream = File.OpenRead(input);
        stream.Seek(format.DataOffset, SeekOrigin.Begin);
        var raw = new byte[BlockFrames * format.BlockAlign];
        var samples = new double[BlockFrames * format.Channels];
        var left = format.DataBytes - format.DataBytes % format.BlockAlign;
        while (left > 0)
        {
            var want = (int)Math.Min(raw.Length, left);
            stream.ReadExactly(raw, 0, want);
            left -= want;
            var count = want / format.BytesPerSample;
            for (var i = 0; i < count; i++)
            {
                var x = format.Read(raw, i * format.BytesPerSample);
                if (brighten)
                {
                    var s = i % format.Channels * 4;
                    var y = b0 * x + b1 * state[s] + b2 * state[s + 1] - a1 * state[s + 2] - a2 * state[s + 3];
                    state[s + 1] = state[s];
                    state[s] = x;
                    state[s + 3] = state[s + 2];
                    state[s + 2] = y;
                    x = y;
                }
                samples[i] = x;
            }
            block(count == samples.Length ? samples : samples[..count]);
        }
    }

    private sealed record WaveFormat(int Channels, int SampleRate, int Bits, bool Float, long DataOffset, long DataBytes)
    {
        public int BytesPerSample => Bits / 8;

        public int BlockAlign => Channels * BytesPerSample;

        public double Read(byte[] raw, int offset) => (Bits, Float) switch
        {
            (32, true) => BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan(offset)),
            (32, false) => BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(offset)) / 2147483648.0,
            (24, _) => ((raw[offset] | raw[offset + 1] << 8 | (sbyte)raw[offset + 2] << 16)) / 8388608.0,
            _ => BinaryPrimitives.ReadInt16LittleEndian(raw.AsSpan(offset)) / 32768.0,
        };

        public static WaveFormat Read(string path)
        {
            using var reader = new BinaryReader(File.OpenRead(path));
            if (new string(reader.ReadChars(4)) != "RIFF")
            {
                throw new AceException($"{Path.GetFileName(path)} is no WAV.");
            }
            reader.ReadUInt32();
            if (new string(reader.ReadChars(4)) != "WAVE")
            {
                throw new AceException($"{Path.GetFileName(path)} is no WAV.");
            }
            (int Channels, int Rate, int Bits, bool Float)? fmt = null;
            while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
            {
                var id = new string(reader.ReadChars(4));
                var size = reader.ReadUInt32();
                var next = reader.BaseStream.Position + size + (size & 1);
                if (id == "fmt ")
                {
                    var tag = reader.ReadUInt16();
                    var channels = reader.ReadUInt16();
                    var rate = (int)reader.ReadUInt32();
                    reader.ReadUInt32();
                    reader.ReadUInt16();
                    var bits = reader.ReadUInt16();
                    if (tag == 0xFFFE && size >= 40)
                    {
                        // WAVE_FORMAT_EXTENSIBLE: the real tag is the sub-format GUID's first two bytes.
                        reader.ReadUInt16();
                        reader.ReadUInt16();
                        reader.ReadUInt32();
                        tag = reader.ReadUInt16();
                    }
                    if (tag is not (1 or 3) || bits is not (16 or 24 or 32) || tag == 3 && bits != 32 || channels == 0)
                    {
                        throw new AceException($"{Path.GetFileName(path)} has a WAV format this cannot read ({tag}, {bits} bit).");
                    }
                    fmt = (channels, rate, bits, tag == 3);
                }
                else if (id == "data" && fmt is { } f)
                {
                    // A writer that streamed may leave the size open; the file's end tells then.
                    var bytes = Math.Min(size, reader.BaseStream.Length - reader.BaseStream.Position);
                    return new WaveFormat(f.Channels, f.Rate, f.Bits, f.Float, reader.BaseStream.Position, bytes);
                }
                reader.BaseStream.Seek(next, SeekOrigin.Begin);
            }
            throw new AceException($"{Path.GetFileName(path)} has no audio.");
        }
    }
}
