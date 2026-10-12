using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using YueToLogic.Core.Arrangement;
using YueToLogic.Core.Conversion;
using YueToLogic.Core.Diagnostics;
using YueToLogic.Core.Model;
using ScoreNote = YueToLogic.Core.Model.NoteEvent;
using static YueToLogic.Core.Tests.TestScores;

namespace YueToLogic.Core.Tests;

/// <summary>Bass and drums heard in a stem, laid into the arrangement in place of the generated ones.</summary>
public class RecordedPartsTests
{
    /// <summary>The sample's Q:1/4=88.</summary>
    private const double SampleBpm = 88;

    [Fact]
    public void A_recorded_bass_replaces_the_generated_one_on_the_scores_own_beats()
    {
        // At 120 bpm in the file, half a second apart: at 88 in the score that is 0.733 of a quarter.
        var options = new ConversionOptions
        {
            Arrangement = new ArrangementOptions { Bass = new BassOptions { Pattern = BassPattern.Walking } },
            Recorded = new RecordedParts(Bass: Midi(120, (0, 240, 40, 90), (480, 240, 43, 70))),
        };

        var result = new ScoreConverter().Convert(File.ReadAllText(SamplePath), options);

        var bass = result.Score!.Voice("Bass");
        Assert.Equal(TrackKind.Bass, bass.Kind);
        long Ticks(double seconds) => (long)Math.Round(seconds * SampleBpm / 60 * Ppq);
        Assert.Equal(
            [new ScoreNote(0, Ticks(0.25), 40, 90), new ScoreNote(Ticks(0.5), Ticks(0.75) - Ticks(0.5), 43, 70)],
            bass.Notes);
        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.RecordedPart && d.Severity == DiagnosticSeverity.Info);
    }

    [Fact]
    public void Recorded_drums_go_onto_the_drum_machines_notes_and_tracks_and_keep_only_the_kit()
    {
        var options = new ConversionOptions
        {
            Arrangement = new ArrangementOptions
            {
                Drums = new DrumOptions { SeparateTracks = true, Notes = new DrumNotes { Kick = 60, Snare = 62, ClosedHiHat = 64 } },
            },
            // A tom (45) is no drum of the pattern's kit.
            Recorded = new RecordedParts(Drums: Midi(SampleBpm, (0, 60, 36, 100), (480, 60, 38, 100), (480, 60, 42, 60), (960, 60, 45, 100))),
        };

        var result = new ScoreConverter().Convert(File.ReadAllText(SamplePath), options);

        var score = result.Score!;
        Assert.Equal([60], score.Voice("Kick").Notes.Select(n => n.NoteNumber));
        Assert.Equal([Ppq], score.Voice("Snare").Notes.Select(n => n.StartTicks));
        Assert.Equal([(64, 60)], score.Voice("HiHat").Notes.Select(n => (n.NoteNumber, n.Velocity ?? 0)));
        Assert.DoesNotContain(score.Voices, v => v.Id == "Crash");
    }

    [Fact]
    public void Recorded_parts_are_moved_with_the_count_in()
    {
        var options = new ConversionOptions
        {
            Arrangement = new ArrangementOptions { CountIn = new CountInOptions { Bars = 1 } },
            Recorded = new RecordedParts(Bass: Midi(SampleBpm, (0, 480, 40, 100))),
        };

        var score = new ScoreConverter().Convert(File.ReadAllText(SamplePath), options).Score!;

        Assert.Equal(score.CountInTicks, score.Voice("Bass").Notes.Single().StartTicks);
    }

    [Fact]
    public void An_unreadable_recording_leaves_the_generated_bass_with_a_warning()
    {
        var options = new ConversionOptions
        {
            Arrangement = new ArrangementOptions { Bass = new BassOptions() },
            Recorded = new RecordedParts(Bass: "not midi"u8.ToArray()),
        };

        var result = new ScoreConverter().Convert(File.ReadAllText(SamplePath), options);

        Assert.True(result.Success);
        Assert.NotEmpty(result.Score!.Voice("Bass").Notes);
        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.RecordedPart && d.Severity == DiagnosticSeverity.Warning);
    }

    /// <summary>One track at 480 ticks per quarter.</summary>
    private static byte[] Midi(double bpm, params (long Tick, long Length, int Note, int Velocity)[] notes)
    {
        var timed = notes
            .SelectMany(n => new (long Tick, MidiEvent Event)[]
            {
                (n.Tick, new Melanchall.DryWetMidi.Core.NoteOnEvent((SevenBitNumber)n.Note, (SevenBitNumber)n.Velocity)),
                (n.Tick + n.Length, new NoteOffEvent((SevenBitNumber)n.Note, (SevenBitNumber)0)),
            })
            .OrderBy(e => e.Tick)
            .ThenBy(e => e.Event is Melanchall.DryWetMidi.Core.NoteOnEvent ? 1 : 0)
            .ToList();
        var events = new List<MidiEvent> { new SetTempoEvent((long)Math.Round(60_000_000 / bpm)) };
        var last = 0L;
        foreach (var (tick, midiEvent) in timed)
        {
            midiEvent.DeltaTime = tick - last;
            last = tick;
            events.Add(midiEvent);
        }
        using var stream = new MemoryStream();
        new MidiFile(new TrackChunk(events)) { TimeDivision = new TicksPerQuarterNoteTimeDivision(480) }.Write(stream);
        return stream.ToArray();
    }
}
