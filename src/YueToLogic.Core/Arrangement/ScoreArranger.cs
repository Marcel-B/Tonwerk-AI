using System.Globalization;
using YueToLogic.Core.Conversion;
using YueToLogic.Core.Diagnostics;
using YueToLogic.Core.Model;

namespace YueToLogic.Core.Arrangement;

public interface IScoreArranger
{
    /// <param name="recorded">Bass and drums heard in the recording, which take the place of the generated ones.</param>
    ArrangementResult Arrange(ScoreDocument score, ArrangementOptions? options = null, RecordedParts? recorded = null);
}

public sealed record ArrangementResult(ScoreDocument Score, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// Transposes score voices by octaves, adds backing vocals below the melodies, appends the generated chord, bass, drum and guide-tone tracks, and finally
/// gives everything its groove and, where asked for, a monophonic shape. The result is a new
/// <see cref="ScoreDocument"/>, so JSON output, MIDI file and Logic project always show the same arrangement.
/// Stateless and thread-safe.
/// </summary>
public sealed class ScoreArranger : IScoreArranger
{
    public ArrangementResult Arrange(ScoreDocument score, ArrangementOptions? options = null, RecordedParts? recorded = null)
    {
        ArgumentNullException.ThrowIfNull(score);
        options ??= new ArrangementOptions();
        var diagnostics = new DiagnosticBag();

        var tracks = ShiftOctaves(score.Voices, options, diagnostics);
        if (options.Doubling is { } doubling)
        {
            AddDoubling(tracks, doubling, diagnostics);
        }

        // Derived from the melody as it sounds, so after the octave shifts; placed after the melodies they follow.
        if (options.Harmony is { } harmony)
        {
            var melodies = tracks.FindLastIndex(t => t.Kind is TrackKind.Melody or TrackKind.Doubling) + 1;
            tracks.InsertRange(melodies, HarmonyGenerator.Generate(score, tracks, harmony, diagnostics));
        }

        if (options.Chords is { } chords && score.Chords.Count > 0)
        {
            tracks.Add(ChordTrackGenerator.Generate(score, chords));
        }

        // A part heard in the recording wins over the generated one; one that cannot be read leaves the generated
        // one in place (or none, if none was asked for), with a warning.
        if ((recorded?.Bass is { } recordedBass ? RecordedPartReader.Bass(score, recordedBass, diagnostics) : null) is { } heardBass)
        {
            tracks.Add(heardBass);
        }
        else if (options.Bass is { } bass)
        {
            tracks.Add(BassLineGenerator.Generate(score, bass));
        }

        if ((recorded?.Drums is { } recordedDrums ? RecordedPartReader.Drums(score, recordedDrums, diagnostics) : null) is { } heardDrums)
        {
            tracks.AddRange(DrumPatternGenerator.Tracks(heardDrums, options.Drums ?? new DrumOptions()));
        }
        else if (options.Drums is { } drums)
        {
            tracks.AddRange(DrumPatternGenerator.GenerateTracks(score, drums));
        }

        if (options.GuideTones is { } guideTones)
        {
            if (score.Chords.Count == 0)
            {
                diagnostics.Warning(
                    DiagnosticCodes.NoChords,
                    "Guide tones were asked for, but the score has no chord symbols to take them from.");
            }
            else
            {
                tracks.Add(GuideToneGenerator.Generate(score, guideTones));
            }
        }

        // The groove moves notes, so the monophonic clean-up runs after it: its guarantees have to hold
        // for what is finally written, not for an intermediate state.
        IReadOnlyList<VoiceTrack> result = tracks;
        if (options.Groove is { } groove)
        {
            result = GrooveProcessor.Apply(score, result, groove);
        }

        if (options.Mono is { } mono)
        {
            result = [.. result.Select(track => IsMonophonic(track.Kind, mono) ? MonoProcessor.Apply(score, track, mono) : track)];
        }

        var arranged = score with { Voices = result };

        // Last of all: the count-in moves the finished arrangement back, including the generated tracks.
        if (options.CountIn is { } countIn)
        {
            arranged = CountInBuilder.Apply(arranged, countIn, options.Drums?.Notes);
        }

        return new ArrangementResult(arranged, diagnostics.ToList());
    }

    /// <summary>Chords and drums are polyphonic by nature; the single-line tracks are the ones a mono synth plays.</summary>
    private static bool IsMonophonic(TrackKind kind, MonoOptions options) => kind switch
    {
        TrackKind.Melody or TrackKind.Doubling or TrackKind.Harmony => true,
        TrackKind.Bass => options.IncludeBass,
        _ => false,
    };

    private static void AddDoubling(List<VoiceTrack> tracks, DoublingOptions options, DiagnosticBag diagnostics)
    {
        var source = tracks.Find(track =>
            track.Kind == TrackKind.Melody && track.Id.Equals(options.VoiceId, StringComparison.OrdinalIgnoreCase));
        if (source is null)
        {
            diagnostics.Warning(
                DiagnosticCodes.UnknownVoice,
                $"Cannot double voice '{options.VoiceId}': the score has no such voice (available: {string.Join(", ", tracks.Where(t => t.Kind == TrackKind.Melody).Select(t => t.Id))}).");
            return;
        }

        var notes = source.Notes
            .Where(note => note.NoteNumber + options.Semitones is >= 0 and <= 127)
            .Select(note => note with
            {
                NoteNumber = note.NoteNumber + options.Semitones,
                Velocity = options.Velocity ?? note.Velocity,
            })
            .ToArray();

        var dropped = source.Notes.Count - notes.Length;
        if (dropped > 0)
        {
            diagnostics.Warning(
                DiagnosticCodes.PitchOutOfRange,
                string.Create(CultureInfo.InvariantCulture, $"Doubling voice '{source.Id}' by {options.Semitones} semitone(s) moves {dropped} note(s) outside the MIDI range; they are left out."));
        }

        var id = DoublingId(source.Id, options.Semitones);
        tracks.Add(new VoiceTrack(id, id, notes, TrackKind.Doubling));
    }

    /// <summary>Names the copy the way a score would: <c>8vb</c> an octave below, <c>8va</c> an octave above.</summary>
    private static string DoublingId(string voiceId, int semitones) => semitones switch
    {
        -12 => $"{voiceId} 8vb",
        12 => $"{voiceId} 8va",
        _ => string.Create(CultureInfo.InvariantCulture, $"{voiceId} {semitones:+0;-0}"),
    };

    private static List<VoiceTrack> ShiftOctaves(
        IReadOnlyList<VoiceTrack> voices,
        ArrangementOptions options,
        DiagnosticBag diagnostics)
    {
        var melodyIds = voices.Where(v => v.Kind == TrackKind.Melody).Select(v => v.Id).ToList();
        foreach (var voiceId in options.OctaveShifts.Keys)
        {
            if (!melodyIds.Exists(id => id.Equals(voiceId, StringComparison.OrdinalIgnoreCase)))
            {
                diagnostics.Warning(
                    DiagnosticCodes.UnknownVoice,
                    $"Cannot transpose voice '{voiceId}': the score has no such voice (available: {string.Join(", ", melodyIds)}).");
            }
        }

        return voices.Select(voice => voice.Kind == TrackKind.Melody
                ? Shift(voice, OctavesFor(voice.Id, options), diagnostics)
                : voice)
            .ToList();
    }

    private static int OctavesFor(string voiceId, ArrangementOptions options)
    {
        foreach (var (id, octaves) in options.OctaveShifts)
        {
            if (id.Equals(voiceId, StringComparison.OrdinalIgnoreCase))
            {
                return octaves;
            }
        }

        return options.DefaultOctaveShift;
    }

    private static VoiceTrack Shift(VoiceTrack voice, int octaves, DiagnosticBag diagnostics)
    {
        if (octaves == 0)
        {
            return voice;
        }

        var semitones = octaves * 12;
        var shifted = voice.Notes
            .Where(n => n.NoteNumber + semitones is >= 0 and <= 127)
            .Select(n => n with { NoteNumber = n.NoteNumber + semitones })
            .ToArray();

        var dropped = voice.Notes.Count - shifted.Length;
        if (dropped > 0)
        {
            diagnostics.Warning(
                DiagnosticCodes.PitchOutOfRange,
                string.Create(CultureInfo.InvariantCulture, $"Transposing voice '{voice.Id}' by {octaves} octave(s) moves {dropped} note(s) outside the MIDI range; they are left out."));
        }

        return voice with { Notes = shifted };
    }
}
