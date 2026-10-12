using YueToLogic.Core.Model;

namespace YueToLogic.Core.Arrangement;

/// <summary>
/// Drum grooves on the score's meter, all of them with a hi-hat on every half beat: <see cref="DrumPattern.FourOnTheFloor"/>
/// puts the kick on every beat, <see cref="DrumPattern.Backbeat"/> on every other beat plus an open hi-hat on the last
/// half beat of the bar, <see cref="DrumPattern.HalfTime"/> plays kick and snare once per bar, and
/// <see cref="DrumPattern.Disco"/> combines the kick on every beat with an open hi-hat on every off-beat. The snare
/// falls on every second beat, in half time only in the middle of the bar. Optionally a crash cymbal replaces the
/// hi-hat where a section starts. Compound meters such as 6/8 count dotted quarters as beats and play the hi-hat on
/// every eighth. The patterns are written in drums, not in notes; which note a drum is played on comes from
/// <see cref="DrumOptions.Notes"/>, General MIDI unless a drum machine says otherwise.
/// </summary>
internal static class DrumPatternGenerator
{
    public const string TrackId = "Drums";

    /// <summary>The tracks a split drum kit uses, and which of its drums belong on each.</summary>
    public static readonly (string TrackId, Drum[] Drums)[] SeparateTracks =
    [
        ("Kick", [Drum.Kick]),
        ("Snare", [Drum.Snare]),
        ("HiHat", [Drum.ClosedHiHat, Drum.OpenHiHat]),
        ("Crash", [Drum.Crash]),
    ];

    private const int KickVelocity = 110;
    private const int SnareVelocity = 100;
    private const int CrashVelocity = 100;
    private const int HiHatOnBeatVelocity = 80;
    private const int HiHatOffBeatVelocity = 60;
    private const int OpenHiHatVelocity = 75;

    /// <summary>A hit of the pattern, still named by its drum so a split kit sorts by drum and not by note number.</summary>
    internal readonly record struct Hit(Drum Drum, long StartTicks, long DurationTicks, int Velocity);

    /// <summary>Every hi-hat position of the segment: each beat opens one, the offsets fill it.</summary>
    private static IEnumerable<long> HiHatTicks(long start, long end, long beatTicks, int beatsPerBar, long[] offsets)
    {
        for (var bar = start; bar < end; bar += beatTicks * beatsPerBar)
        {
            for (var beat = 0; beat < beatsPerBar; beat++)
            {
                foreach (var offset in offsets)
                {
                    var tick = bar + (beat * beatTicks) + offset;
                    if (tick < end)
                    {
                        yield return tick;
                    }
                }
            }
        }
    }

    /// <summary>
    /// The drum track, or one track per drum when the options ask for it. Splitting only sorts the same hits
    /// into several tracks, so both ways play exactly the same thing. The sorting goes by drum, not by note:
    /// a map that gives two drums one note still keeps every hit on one track.
    /// </summary>
    public static IEnumerable<VoiceTrack> GenerateTracks(ScoreDocument score, DrumOptions options)
    {
        return Tracks(Hits(score, options), options);
    }

    /// <summary>
    /// Hits as tracks, one or one per drum, on the notes the options name: the pattern's, or those heard in a drum stem
    /// (<see cref="RecordedPartReader"/>), so a recorded groove reaches a drum machine and a split kit the same way.
    /// </summary>
    public static IEnumerable<VoiceTrack> Tracks(IReadOnlyList<Hit> hits, DrumOptions options)
    {
        var notes = options.Notes ?? new DrumNotes();
        if (!options.SeparateTracks)
        {
            return [new VoiceTrack(TrackId, TrackId, [.. hits.Select(hit => ToNote(hit, notes))], TrackKind.Drums)];
        }

        return SeparateTracks
            .Select(track => new VoiceTrack(
                track.TrackId,
                track.TrackId,
                [.. hits.Where(hit => track.Drums.Contains(hit.Drum)).Select(hit => ToNote(hit, notes))],
                TrackKind.Drums))
            .Where(track => track.Notes.Count > 0);
    }

    public static VoiceTrack Generate(ScoreDocument score, DrumOptions options) =>
        GenerateTracks(score, options with { SeparateTracks = false }).Single();

    private static NoteEvent ToNote(Hit hit, DrumNotes notes) =>
        new(hit.StartTicks, hit.DurationTicks, Math.Clamp(notes.Of(hit.Drum), 0, 127), hit.Velocity);

    /// <summary>The pattern over the whole score, ordered by position, in the order the drums were added at the same tick.</summary>
    private static List<Hit> Hits(ScoreDocument score, DrumOptions options)
    {
        var ppq = score.TicksPerQuarterNote;
        var hitTicks = Math.Max(1, ppq / 4);
        var pattern = options.Pattern;
        var crashTicks = options.CrashOnSections
            ? score.Sections.Select(s => s.StartTicks).Where(t => t < score.LengthTicks).ToHashSet()
            : [];
        var hits = new List<Hit>();

        for (var i = 0; i < score.TimeSignatures.Count; i++)
        {
            var signature = score.TimeSignatures[i];
            var segmentEnd = i + 1 < score.TimeSignatures.Count ? score.TimeSignatures[i + 1].StartTicks : score.LengthTicks;
            var compound = signature.Denominator == 8 && signature.Numerator > 3 && signature.Numerator % 3 == 0;
            var noteTicks = 4L * ppq / signature.Denominator;
            var beatTicks = compound ? 3 * noteTicks : noteTicks;
            var beatsPerBar = compound ? signature.Numerator / 3 : signature.Numerator;
            // Where the hi-hat falls inside a beat: halves, quarters, or the first and third triplet.
            long[] divisions = compound
                ? [noteTicks, 2 * noteTicks]
                : pattern switch
                {
                    DrumPattern.SixteenthHats => [beatTicks / 4, beatTicks / 2, 3 * beatTicks / 4],
                    DrumPattern.Shuffle => [2 * beatTicks / 3],
                    _ => [beatTicks / 2],
                };
            long[] offsets = [0, .. divisions.Where(d => d > 0)];
            var hiHatTicks = Math.Max(1, offsets.Length > 1 ? offsets[1] : beatTicks);

            for (var bar = signature.StartTicks; bar < segmentEnd; bar += beatTicks * beatsPerBar)
            {
                for (var beat = 0; beat < beatsPerBar && bar + beat * beatTicks < segmentEnd; beat++)
                {
                    var tick = bar + beat * beatTicks;
                    var kick = pattern switch
                    {
                        DrumPattern.Backbeat or DrumPattern.SixteenthHats or DrumPattern.Shuffle => beat % 2 == 0,
                        DrumPattern.HalfTime => beat == 0,
                        _ => true,
                    };
                    if (kick)
                    {
                        hits.Add(new Hit(Drum.Kick, tick, hitTicks, KickVelocity));
                    }

                    var snare = pattern == DrumPattern.HalfTime ? beat == beatsPerBar / 2 : beat % 2 == 1;
                    if (snare && beat > 0)
                    {
                        hits.Add(new Hit(Drum.Snare, tick, hitTicks, SnareVelocity));
                    }
                }
            }

            var measureTicks = beatTicks * beatsPerBar;
            foreach (var tick in HiHatTicks(signature.StartTicks, segmentEnd, beatTicks, beatsPerBar, offsets))
            {
                if (crashTicks.Contains(tick))
                {
                    continue;
                }

                var positionInBar = (tick - signature.StartTicks) % measureTicks;
                var onBeat = positionInBar % beatTicks == 0;
                var open = pattern switch
                {
                    // Open hi-hat on the last half beat ("4+"), ringing into the next downbeat.
                    DrumPattern.Backbeat => positionInBar + hiHatTicks >= measureTicks,
                    DrumPattern.Disco => !onBeat,
                    _ => false,
                };
                if (open)
                {
                    hits.Add(new Hit(Drum.OpenHiHat, tick, hiHatTicks, OpenHiHatVelocity));
                    continue;
                }

                hits.Add(new Hit(Drum.ClosedHiHat, tick, hitTicks, onBeat ? HiHatOnBeatVelocity : HiHatOffBeatVelocity));
            }
        }

        hits.AddRange(crashTicks.Select(tick => new Hit(Drum.Crash, tick, ppq, CrashVelocity)));
        // Ordered by position, keeping the order the drums were added in at the same tick.
        return [.. hits.OrderBy(hit => hit.StartTicks)];
    }
}
