using System.Globalization;
using YueToLogic.Core.Conversion;
using YueToLogic.Core.Diagnostics;
using YueToLogic.Core.Midi;
using YueToLogic.Core.Model;

namespace YueToLogic.Core.Arrangement;

/// <summary>
/// Places the notes of a <see cref="RecordedParts"/> MIDI file on the score's ticks. The file and the score each count
/// in their own beats, so a note goes through seconds: the file's tempo map out, the score's tempo in. Time zero of the
/// file is the recording's start, which is the score's start too (YuE2 sings the score from its first bar).
/// </summary>
internal static class RecordedPartReader
{
    /// <summary>The drums a recorded hit can be, by General MIDI note.</summary>
    private static readonly Dictionary<int, Drum> DrumsByNote = new()
    {
        [GeneralMidiDrums.Kick] = Drum.Kick,
        [35] = Drum.Kick,
        [GeneralMidiDrums.Snare] = Drum.Snare,
        [40] = Drum.Snare,
        [GeneralMidiDrums.ClosedHiHat] = Drum.ClosedHiHat,
        [44] = Drum.ClosedHiHat,
        [GeneralMidiDrums.OpenHiHat] = Drum.OpenHiHat,
        [GeneralMidiDrums.Crash] = Drum.Crash,
    };

    public static VoiceTrack? Bass(ScoreDocument score, byte[] midi, DiagnosticBag diagnostics)
    {
        var notes = Read(score, midi, "bass", diagnostics);
        if (notes is null)
        {
            return null;
        }

        var track = new VoiceTrack(
            BassLineGenerator.TrackId,
            BassLineGenerator.TrackId,
            [.. notes.Select(n => new NoteEvent(n.Start, n.End - n.Start, n.Pitch, n.Velocity))],
            TrackKind.Bass);
        diagnostics.Info(
            DiagnosticCodes.RecordedPart,
            string.Create(CultureInfo.InvariantCulture, $"The bass comes from the recording: {track.Notes.Count} note(s) heard in its stem."));
        return track;
    }

    public static IReadOnlyList<DrumPatternGenerator.Hit>? Drums(ScoreDocument score, byte[] midi, DiagnosticBag diagnostics)
    {
        var notes = Read(score, midi, "drums", diagnostics);
        if (notes is null)
        {
            return null;
        }

        var hits = notes
            .Where(n => DrumsByNote.ContainsKey(n.Pitch))
            .Select(n => new DrumPatternGenerator.Hit(DrumsByNote[n.Pitch], n.Start, n.End - n.Start, n.Velocity))
            .ToList();
        diagnostics.Info(
            DiagnosticCodes.RecordedPart,
            string.Create(CultureInfo.InvariantCulture, $"The drums come from the recording: {hits.Count} hit(s) heard in its stem ({Count(hits, Drum.Kick)} kick, {Count(hits, Drum.Snare)} snare, {Count(hits, Drum.ClosedHiHat) + Count(hits, Drum.OpenHiHat)} hi-hat)."));
        return hits;
    }

    private static int Count(List<DrumPatternGenerator.Hit> hits, Drum drum) => hits.Count(hit => hit.Drum == drum);

    /// <summary>Every note of the file in score ticks, cut to the score's length; null with a warning when there is none.</summary>
    private static List<MidiNote>? Read(ScoreDocument score, byte[] midi, string part, DiagnosticBag diagnostics)
    {
        var read = new DiagnosticBag();
        using var stream = new MemoryStream(midi, writable: false);
        var content = MidiFileReader.Read(stream, read);
        if (content is null)
        {
            diagnostics.Warning(DiagnosticCodes.RecordedPart, $"The recorded {part} could not be read, so it is left out: {string.Join(" ", read.ToList().Select(d => d.Message))}");
            return null;
        }

        var tempos = content.Tempos.Count > 0 && content.Tempos[0].Tick == 0
            ? content.Tempos
            : [new MidiTempo(0, 120), .. content.Tempos];
        var ticksPerSecond = score.TempoBpm / 60.0 * score.TicksPerQuarterNote;
        long ToScore(long tick) => (long)Math.Round(Seconds(tick, tempos, content.TicksPerQuarterNote) * ticksPerSecond);

        var notes = new List<MidiNote>();
        foreach (var note in content.Sources.SelectMany(s => s.Notes))
        {
            var start = ToScore(note.Start);
            if (start >= score.LengthTicks)
            {
                continue;
            }

            var end = Math.Min(score.LengthTicks, Math.Max(start + 1, ToScore(note.End)));
            notes.Add(new MidiNote(start, end, note.Pitch, Math.Clamp(note.Velocity, 1, 127)));
        }

        if (notes.Count == 0)
        {
            diagnostics.Warning(DiagnosticCodes.RecordedPart, $"No note was heard in the recorded {part} within the score, so it is left out.");
            return null;
        }

        notes.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Pitch.CompareTo(b.Pitch));
        return notes;
    }

    private static double Seconds(long tick, IReadOnlyList<MidiTempo> tempos, int ticksPerQuarterNote)
    {
        var seconds = 0.0;
        for (var i = 0; i < tempos.Count; i++)
        {
            var from = tempos[i].Tick;
            if (tick <= from && i > 0)
            {
                break;
            }

            var to = i + 1 < tempos.Count ? Math.Min(tick, tempos[i + 1].Tick) : tick;
            seconds += (to - from) / (double)ticksPerQuarterNote * 60.0 / tempos[i].Bpm;
            if (to == tick)
            {
                break;
            }
        }

        return seconds;
    }
}
