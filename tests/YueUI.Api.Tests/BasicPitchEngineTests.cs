using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using YueUI.Api.AudioMidi;

namespace YueUI.Api.Tests;

/// <summary>
/// Against the real Basic Pitch, where its environment is: the default location, or <c>YUEUI_BASICPITCH_PYTHON</c>
/// (a Python that <c>deploy/install-midi.sh</c> set up). Without one there is nothing to run against.
/// </summary>
public sealed class BasicPitchEngineTests : IDisposable
{
    /// <summary>C D E F G F E D C, half a second each: a scale a voice could sing.</summary>
    private static readonly int[] Melody = [60, 62, 64, 65, 67, 65, 64, 62, 60];

    private readonly string _directory = Directory.CreateTempSubdirectory("yueui-basicpitch-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task A_sung_scale_comes_back_note_for_note_on_the_grid_without_its_overtones()
    {
        if (Engine() is not { Installed: true } engine)
        {
            return;
        }
        var audio = Path.Combine(_directory, "vocals.wav");
        File.WriteAllBytes(audio, Voice(Melody, 0.5));
        var output = Path.Combine(_directory, "vocals.mid");

        var result = await engine.ConvertAsync(
            new AudioMidiJob(audio, output, "Vocal", new AudioMidiSettings(Mono: true, Quantize: true), 120, AudioMidiKind.Voice), CancellationToken.None);

        var midi = MidiFile.Read(output);
        var map = midi.GetTempoMap();
        var notes = midi.GetNotes().ToList();
        Assert.Equal(Melody.Length, result.Notes);
        Assert.Equal(Melody, notes.Select(n => (int)n.NoteNumber));
        // At 120 bpm a half second is a quarter: every note starts on a beat.
        Assert.Equal(Enumerable.Range(0, Melody.Length).Select(i => i * 480L), notes.Select(n => n.Time * 480 / ((TicksPerQuarterNoteTimeDivision)map.TimeDivision).TicksPerQuarterNote));
        Assert.Equal(120, map.GetTempoAtTime(new MidiTimeSpan(0)).BeatsPerMinute, 1);
        Assert.Equal("Vocal", midi.GetTrackChunks().SelectMany(c => c.Events).OfType<SequenceTrackNameEvent>().Single(e => e.Text.Length > 0).Text);
    }

    [Fact]
    public async Task Without_one_note_at_a_time_the_overtones_stay()
    {
        if (Engine() is not { Installed: true } engine)
        {
            return;
        }
        var audio = Path.Combine(_directory, "vocals.wav");
        File.WriteAllBytes(audio, Voice(Melody, 0.5));

        var result = await engine.ConvertAsync(
            new AudioMidiJob(audio, Path.Combine(_directory, "poly.mid"), "Vocal", new AudioMidiSettings(Mono: false), 120, AudioMidiKind.Voice), CancellationToken.None);

        Assert.True(result.Notes >= Melody.Length);
    }

    [Fact]
    public async Task A_bass_line_comes_back_as_one_line_without_its_overtones()
    {
        if (Engine() is not { Installed: true } engine)
        {
            return;
        }
        int[] line = [33, 36, 38, 40, 43, 40, 38, 36];
        var audio = Path.Combine(_directory, "bass.wav");
        File.WriteAllBytes(audio, Voice(line, 0.5));
        var output = Path.Combine(_directory, "bass.mid");

        // Even asked for several notes at once: a bass plays one.
        await engine.ConvertAsync(
            new AudioMidiJob(audio, output, "Bass", new AudioMidiSettings(Mono: false, Quantize: true), 120, AudioMidiKind.Bass), CancellationToken.None);

        Assert.Equal(line, MidiFile.Read(output).GetNotes().Select(n => (int)n.NoteNumber));
    }

    [Fact]
    public async Task A_drum_groove_comes_back_as_kick_snare_and_hi_hat_on_the_grid()
    {
        if (Engine() is not { Installed: true } engine)
        {
            return;
        }
        var audio = Path.Combine(_directory, "drums.wav");
        File.WriteAllBytes(audio, Drums(bars: 4));
        var output = Path.Combine(_directory, "drums.mid");

        var result = await engine.ConvertAsync(
            new AudioMidiJob(audio, output, "Drums", new AudioMidiSettings(Quantize: true), 120, AudioMidiKind.Drums), CancellationToken.None);

        var midi = MidiFile.Read(output);
        var sixteenth = ((TicksPerQuarterNoteTimeDivision)midi.TimeDivision).TicksPerQuarterNote / 4;
        var notes = midi.GetNotes().ToList();
        Assert.Equal(notes.Count, result.Notes);
        long[] Sixteenths(int note) => [.. notes.Where(n => n.NoteNumber == note).Select(n => n.Time / sixteenth).Order()];
        // Kick on one and three, snare on two and four, the hi-hat on every eighth between them.
        Assert.Equal(Enumerable.Range(0, 8).Select(i => i * 8L), Sixteenths(36));
        Assert.Equal(Enumerable.Range(0, 8).Select(i => i * 8L + 4), Sixteenths(38));
        Assert.Equal(Enumerable.Range(0, 32).Where(i => i % 2 == 1).Select(i => i * 2L), Sixteenths(42));
        Assert.All(notes, n => Assert.Equal(0, n.Time % sixteenth));
    }

    [Fact]
    public async Task A_crash_after_the_file_is_written_still_delivers_it()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var output = Path.Combine(_directory, "crash.mid");
        // As on the Mac: the file and the done event are out, then the process aborts on its way out.
        var python = Path.Combine(_directory, "python");
        File.WriteAllText(python, $$"""
            #!/bin/sh
            printf 'MThd' > '{{output}}'
            echo 'YUEUI {"event": "done", "notes": 488, "seconds": 9.86}'
            echo 'libc++abi: terminating due to uncaught exception of type std::__1::system_error: recursive_mutex lock failed' >&2
            exit 134
            """);
        File.SetUnixFileMode(python, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var engine = new BasicPitchEngine(Options.Create(new AudioMidiOptions { Python = python }), NullLogger<BasicPitchEngine>.Instance);

        var result = await engine.ConvertAsync(
            new AudioMidiJob("vocals.wav", output, "Vocal", new AudioMidiSettings(), 120, AudioMidiKind.Voice), CancellationToken.None);

        Assert.Equal(488, result.Notes);
    }

    [Fact]
    public async Task A_crash_before_the_file_is_written_is_a_failure()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var python = Path.Combine(_directory, "python");
        File.WriteAllText(python, """
            #!/bin/sh
            echo 'Error: librosa could not read the file.'
            exit 1
            """);
        File.SetUnixFileMode(python, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var engine = new BasicPitchEngine(Options.Create(new AudioMidiOptions { Python = python }), NullLogger<BasicPitchEngine>.Instance);

        var failure = await Assert.ThrowsAsync<AudioMidiException>(() => engine.ConvertAsync(
            new AudioMidiJob("vocals.wav", Path.Combine(_directory, "none.mid"), "Vocal", new AudioMidiSettings(), 120, AudioMidiKind.Voice), CancellationToken.None));

        Assert.Contains("librosa could not read", failure.Message);
    }

    private static BasicPitchEngine? Engine()
    {
        var options = new AudioMidiOptions { Python = Environment.GetEnvironmentVariable("YUEUI_BASICPITCH_PYTHON") };
        return File.Exists(options.ResolvedPython)
            ? new BasicPitchEngine(Options.Create(options), NullLogger<BasicPitchEngine>.Instance)
            : null;
    }

    /// <summary>A voice-like tone per note: five harmonics, a little vibrato, soft edges, 44.1 kHz mono 16 bit.</summary>
    private static byte[] Voice(int[] notes, double seconds)
    {
        const int rate = 44100;
        var length = (int)(rate * seconds);
        var samples = new short[notes.Length * length];
        var phase = 0.0;
        for (var n = 0; n < notes.Length; n++)
        {
            var frequency = 440 * Math.Pow(2, (notes[n] - 69) / 12.0);
            for (var i = 0; i < length; i++)
            {
                var t = i / (double)rate;
                phase += 2 * Math.PI * frequency * (1 + 0.006 * Math.Sin(2 * Math.PI * 5.5 * t)) / rate;
                var tone = 0.0;
                for (var k = 1; k <= 5; k++)
                {
                    tone += Math.Sin(k * phase) / k;
                }
                var envelope = Math.Min(1, Math.Min(t / 0.03, (seconds - t) / 0.05));
                samples[n * length + i] = (short)(tone * 0.2 * envelope * short.MaxValue);
            }
        }
        return Wav(samples, rate);
    }

    /// <summary>
    /// A drum machine at 120 bpm: a kick (a sine falling from 120 to 50 Hz) on one and three, a snare (band-limited
    /// noise and a 190 Hz body) on two and four, a closed hi-hat (high noise) on the off-beat eighths.
    /// </summary>
    private static byte[] Drums(int bars)
    {
        const int rate = 44100;
        var eighth = rate / 4;
        var samples = new double[(bars * 8 + 2) * eighth];
        var random = new Random(1);
        void Add(int at, int length, Func<double, double> sound)
        {
            for (var i = 0; i < length && at + i < samples.Length; i++)
            {
                samples[at + i] += sound(i / (double)rate);
            }
        }
        // Noise through a one-pole high-pass: the hi-hat keeps what is above some kHz, the snare a broader band.
        double[] Noise(int length, double keep)
        {
            var noise = new double[length];
            double last = 0, output = 0;
            for (var i = 0; i < length; i++)
            {
                var input = random.NextDouble() * 2 - 1;
                output = keep * (output + input - last);
                last = input;
                noise[i] = output;
            }
            return noise;
        }
        for (var e = 0; e < bars * 8; e++)
        {
            var at = e * eighth;
            if (e % 4 == 0)
            {
                Add(at, rate / 3, t => 0.8 * Math.Sin(2 * Math.PI * (50 * t + 70 * (1 - Math.Exp(-t * 30)) / 30)) * Math.Exp(-t * 8));
            }
            else if (e % 4 == 2)
            {
                var noise = Noise(rate / 4, 0.7);
                Add(at, noise.Length, t => (0.5 * noise[(int)(t * rate)] + 0.3 * Math.Sin(2 * Math.PI * 190 * t)) * Math.Exp(-t * 18));
            }
            else
            {
                var noise = Noise(rate / 12, 0.25);
                Add(at, noise.Length, t => 0.6 * noise[(int)(t * rate)] * Math.Exp(-t * 60));
            }
        }
        var peak = samples.Max(Math.Abs);
        return Wav([.. samples.Select(x => (short)(x / peak * 0.8 * short.MaxValue))], rate);
    }

    private static byte[] Wav(short[] samples, int rate)
    {
        var wav = new MemoryStream();
        using (var writer = new BinaryWriter(wav))
        {
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
                writer.Write(sample);
            }
        }
        return wav.ToArray();
    }
}
