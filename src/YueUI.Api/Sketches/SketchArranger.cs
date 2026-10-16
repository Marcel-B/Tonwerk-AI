using YueToLogic.Core.Arrangement;
using YueToLogic.Core.Harmony;
using YueToLogic.Core.Midi;
using YueToLogic.Core.Model;

namespace YueUI.Api.Sketches;

/// <summary>
/// The plan as a MIDI draft to download: the sung melody, the instrumental line, and piano, pad, bass and drums played
/// per section as the plan's <c>parts</c> and <c>energy</c> say. YueToLogic.Core's arranger generates the same parts
/// for a whole score at once; a draft needs them to come and go with the sections (piano alone in the intro, bass
/// from the second half of the verse, a groove only from the chorus), so they are played here and rendered by its
/// <see cref="MidiRenderer"/>. Deterministic: the same plan gives the same file.
/// </summary>
public static class SketchArranger
{
    private const int Unit = SketchScore.TicksPerQuarter / 4;
    private const int Eighth = 2 * Unit;
    private const int Quarter = 4 * Unit;

    // General MIDI programs, 1-based as MidiRenderOptions takes them.
    private static readonly Dictionary<string, int> Programs = new()
    {
        ["Vocal"] = 54, // Voice Oohs
        ["Ins"] = 1,
        ["Piano"] = 1,
        ["Pad"] = 90, // Warm Pad
        ["Bass"] = 39, // Synth Bass 1
    };

    public static byte[] Midi(SketchPlan plan)
    {
        var laid = SketchScore.LayOut(plan);
        var score = laid.Score;
        var barUnits = laid.BarUnits;
        var barTicks = (long)barUnits * Unit;
        var piano = new List<NoteEvent>();
        var pad = new List<NoteEvent>();
        var bass = new List<NoteEvent>();
        var drums = new List<NoteEvent>();
        var random = new Random(7);
        int Human(int velocity, int spread = 5) => Math.Clamp(velocity + random.Next(-spread, spread + 1), 1, 127);
        int Jitter() => random.Next(-6, 7);

        var markers = new List<SectionMarker>();
        var bar = 0;
        foreach (var section in plan.Sections)
        {
            markers.Add(new SectionMarker(bar * barTicks, string.IsNullOrWhiteSpace(section.Name) ? section.Tag : section.Name.Trim()));
            var energy = Math.Clamp(section.Energy, 1, 3);
            for (var i = 0; i < section.Bars.Count; i++, bar++)
            {
                var planned = section.Bars[i];
                var parts = new HashSet<string>(planned.Parts ?? section.Parts, StringComparer.OrdinalIgnoreCase);
                var start = bar * barTicks;
                var lastOfSection = i == section.Bars.Count - 1;
                foreach (var chord in ChordsIn(laid.Chords, bar, barUnits))
                {
                    var from = Math.Max(chord.Start * Unit, start);
                    var to = Math.Min(chord.End * Unit, start + barTicks);
                    if (to <= from || chord.Symbol is not { } symbol)
                    {
                        continue;
                    }
                    var (left, right) = Voicing(symbol, chord.AddNinth);
                    if (parts.Contains("piano"))
                    {
                        Piano(piano, from, to, left, right, energy, parts.Contains("bass"), Human);
                    }
                    if (parts.Contains("pad"))
                    {
                        var notes = energy == 3 ? [.. right, right[^1] + 12] : right;
                        foreach (var note in notes)
                        {
                            pad.Add(new NoteEvent(from, to - from, note, energy switch { 1 => 34, 2 => 42, _ => 50 }));
                        }
                    }
                    if (parts.Contains("bass"))
                    {
                        Bass(bass, from, to, BassNote(symbol), energy, bar, Human);
                    }
                }
                if (parts.Contains("drums"))
                {
                    Drums(drums, start, barTicks, energy, full: true, bar, lastOfSection && NextIsChorus(plan, section), Human, Jitter);
                }
                else if (parts.Contains("pulse"))
                {
                    Drums(drums, start, barTicks, energy, full: false, bar, false, Human, Jitter);
                }
                else if (parts.Contains("sparse-drums"))
                {
                    drums.Add(new NoteEvent(start, Unit, GeneralMidiDrums.Kick, Human(60)));
                    drums.Add(new NoteEvent(start + barTicks - Quarter + Jitter(), Unit, GeneralMidiDrums.Snare, Human(46)));
                    for (var beat = 0L; beat < barTicks; beat += Quarter)
                    {
                        drums.Add(new NoteEvent(start + beat + Jitter(), Unit / 2, GeneralMidiDrums.ClosedHiHat, Human(42)));
                    }
                }
            }
        }

        var voices = new List<VoiceTrack>(score.Voices.Where(v => v.Notes.Count > 0));
        AddTrack(voices, "Piano", piano, TrackKind.Chords);
        AddTrack(voices, "Pad", pad, TrackKind.GuideTones);
        AddTrack(voices, "Bass", bass, TrackKind.Bass);
        AddTrack(voices, "Drums", drums, TrackKind.Drums);
        var draft = score with { Voices = voices, Sections = markers };
        return new MidiRenderer().Render(draft, new MidiRenderOptions
        {
            // Piano plays the chords; block chords on top of it would double them.
            IncludeChordTrack = true,
            MelodyVelocity = 88,
            Programs = Programs,
        });
    }

    private static void AddTrack(List<VoiceTrack> voices, string name, List<NoteEvent> notes, TrackKind kind)
    {
        if (notes.Count > 0)
        {
            // Played timing may pull the very first hit before the song's start.
            var placed = notes.Select(n => n.StartTicks < 0 ? n with { StartTicks = 0 } : n);
            voices.Add(new VoiceTrack(name, name, [.. placed.OrderBy(n => n.StartTicks).ThenBy(n => n.NoteNumber)], kind));
        }
    }

    private static IEnumerable<PlannedChord> ChordsIn(IReadOnlyList<PlannedChord> chords, int bar, int barUnits)
    {
        var start = (long)bar * barUnits;
        var end = start + barUnits;
        return chords.Where(c => c.Start < end && c.End > start);
    }

    private static bool NextIsChorus(SketchPlan plan, SketchSection section)
    {
        var index = plan.Sections.ToList().IndexOf(section);
        return index >= 0 && index + 1 < plan.Sections.Count
            && string.Equals(plan.Sections[index + 1].Tag, "chorus", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(section.Tag, "chorus", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The left hand on the root (or the slash bass) between E2 and D#3, the right hand's chord tones between F3 and
    /// E4, as a pianist would place them in the middle register: the root left out of the right hand when there
    /// are enough other tones, a ninth added where the plan asked for one.
    /// </summary>
    public static (int Left, int[] Right) Voicing(ChordSymbol chord, bool addNinth)
    {
        var intervals = ChordVoicing.GetIntervals(chord.Quality).ToList();
        if (addNinth)
        {
            intervals.Add(14);
        }
        var tones = intervals.Select(i => (chord.RootPitchClass + i) % 12).Distinct().ToList();
        if (tones.Count > 3)
        {
            tones.Remove(chord.RootPitchClass);
        }
        if (tones.Count > 3)
        {
            // Keep the colour (third, seventh, ninth) over the fifth.
            tones.Remove((chord.RootPitchClass + 7) % 12);
        }
        var right = tones.Select(pc => InRange(pc, 53)).OrderBy(n => n).ToArray();
        var left = InRange(chord.BassPitchClass ?? chord.RootPitchClass, 40);
        return (left, right);
    }

    private static int BassNote(ChordSymbol chord) => InRange(chord.BassPitchClass ?? chord.RootPitchClass, 33);

    /// <summary>The pitch class's note in the octave from <paramref name="low"/> up.</summary>
    private static int InRange(int pitchClass, int low) => low + ((pitchClass - low) % 12 + 12) % 12;

    private static void Piano(List<NoteEvent> piano, long from, long to, int left, int[] right, int energy, bool withBass, Func<int, int, int> human)
    {
        var length = to - from;
        // With the bass in, the left hand only marks the chord.
        piano.Add(new NoteEvent(from, withBass ? Math.Min(length, 2 * Quarter) : length, left, human(withBass ? 40 : 50, 5)));
        switch (energy)
        {
            case 1:
                for (var i = 0; i < right.Length; i++)
                {
                    piano.Add(new NoteEvent(from + 10 * i, length - 10 * i, right[i], human(44, 4)));
                }
                break;
            case 2:
                // A short broken figure rather than running eighths.
                var figure = new[] { right[0], right[Math.Min(1, right.Length - 1)], right[^1] };
                for (long at = 0, step = 0; at < length; at += Eighth, step++)
                {
                    if (step % 4 == 0)
                    {
                        continue;
                    }
                    var note = figure[(int)(step % 4) - 1];
                    piano.Add(new NoteEvent(from + at, Math.Min(Eighth * (4 - step % 4), length - at), note, human(step % 4 == 1 ? 58 : 50, 5)));
                }
                break;
            default:
                // On the beat and pushed onto 2-and, held.
                var push = Math.Min(3 * Eighth, length);
                foreach (var note in right)
                {
                    piano.Add(new NoteEvent(from, push, note, human(62, 5)));
                    if (length > push)
                    {
                        piano.Add(new NoteEvent(from + push, length - push, note, human(54, 5)));
                    }
                }
                break;
        }
    }

    private static void Bass(List<NoteEvent> bass, long from, long to, int note, int energy, int bar, Func<int, int, int> human)
    {
        var velocity = energy == 3 ? 84 : 74;
        for (var at = from; at < to; at += 2 * Quarter)
        {
            var length = Math.Min(2 * Quarter, to - at);
            var withFifth = energy >= 2 && bar % 4 == 1 && length == 2 * Quarter && at != from;
            if (withFifth)
            {
                // A short fifth on 3-and now and then.
                bass.Add(new NoteEvent(at, Eighth - 20, note, human(velocity - 4, 4)));
                bass.Add(new NoteEvent(at + Eighth, Eighth - 20, note + 7, human(velocity - 10, 4)));
            }
            else
            {
                bass.Add(new NoteEvent(at, length - 30, note, human(velocity, 4)));
            }
            if (energy == 3 && bar % 4 == 3 && at + 2 * Quarter >= to && length == 2 * Quarter)
            {
                // An octave answer at the end of a phrase.
                bass.Add(new NoteEvent(at + 3 * Eighth, Eighth - 20, note + 12, human(velocity - 12, 4)));
            }
        }
    }

    private static void Drums(
        List<NoteEvent> drums,
        long start,
        long barTicks,
        int energy,
        bool full,
        int bar,
        bool breathBeforeChorus,
        Func<int, int, int> human,
        Func<int> jitter)
    {
        var kick = full ? (energy == 3 ? 84 : 76) : 58;
        for (var beat = 0L; beat < barTicks; beat += 2 * Quarter)
        {
            drums.Add(new NoteEvent(start + beat, Unit, GeneralMidiDrums.Kick, human(kick, 4)));
        }
        if (full && energy >= 2 && bar % 4 == 2 && barTicks >= 4 * Quarter)
        {
            drums.Add(new NoteEvent(start + 5 * Eighth + jitter(), Unit, GeneralMidiDrums.Kick, human(48, 4)));
        }
        // The last beat before a chorus stays free.
        var end = breathBeforeChorus ? barTicks - Quarter : barTicks;
        if (full)
        {
            for (var beat = Quarter; beat < end; beat += 2 * Quarter)
            {
                drums.Add(new NoteEvent(start + beat + jitter(), Unit, GeneralMidiDrums.Snare, human(66, 4)));
            }
        }
        for (long at = 0, step = 0; at < end; at += Eighth, step++)
        {
            var accent = step % 2 == 0;
            drums.Add(new NoteEvent(start + at + jitter(), Unit / 2, GeneralMidiDrums.ClosedHiHat,
                human(full ? (accent ? 56 : 42) : (accent ? 46 : 34), 4)));
        }
    }
}
