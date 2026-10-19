using System.Text;
using System.Text.RegularExpressions;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using YueToLogic.Core.Abc;
using YueToLogic.Core.Model;

namespace YueUI.Api.Singing;

/// <summary>The melody of a file, ready to be sung, or why there is none.</summary>
/// <param name="Part">The track or voice it was taken from.</param>
/// <param name="TempoBpm">The first tempo of the file, for the note editor's grid.</param>
public sealed record SingingScore(IReadOnlyList<SungNote> Notes, string? Part, string? Problem, double TempoBpm = 120);

/// <summary>
/// Reads the melody to sing from a MIDI file or a YuE2 score (<c>score.abc</c>, as the library's songs, the sketch page
/// and the Logic page write it). Only one part is sung: the one named like a vocal, else the one with lyrics, else the
/// fullest that is not drums.
/// </summary>
public static partial class SingingScores
{
    /// <summary>A MIDI file starts with this; anything else is read as ABC.</summary>
    public static bool IsMidi(ReadOnlySpan<byte> file) => file.Length >= 4 && file[..4].SequenceEqual("MThd"u8);

    public static SingingScore Read(byte[] file, IAbcScoreParser parser) =>
        IsMidi(file) ? FromMidi(file) : FromAbc(Encoding.UTF8.GetString(file), parser);

    public static SingingScore FromAbc(string abc, IAbcScoreParser parser)
    {
        var parsed = parser.Parse(abc);
        if (parsed.Score is not { } score)
        {
            return new([], null, parsed.Diagnostics.FirstOrDefault()?.Message ?? "The score could not be read.");
        }
        var voices = score.Voices.Where(v => v.Kind == TrackKind.Melody && v.Notes.Count > 0).ToList();
        var voice = voices.FirstOrDefault(v => Sung(v.Id) || Sung(v.DisplayName)) ?? voices.FirstOrDefault();
        if (voice is null)
        {
            return new([], null, "The score has no notes.");
        }
        var secondsPerTick = 60.0 / score.TempoBpm / score.TicksPerQuarterNote;
        var notes = voice.Notes
            .Select(n => new SungNote(
                Round(n.StartTicks * secondsPerTick),
                Round((n.StartTicks + n.DurationTicks) * secondsPerTick),
                n.NoteNumber))
            .ToList();
        return new(notes, voice.Id, null, score.TempoBpm);
    }

    public static SingingScore FromMidi(byte[] file)
    {
        MidiFile midi;
        try
        {
            midi = MidiFile.Read(new MemoryStream(file), new ReadingSettings
            {
                InvalidChannelEventParameterValuePolicy = InvalidChannelEventParameterValuePolicy.SnapToLimits,
                InvalidMetaEventParameterValuePolicy = InvalidMetaEventParameterValuePolicy.SnapToLimits,
                InvalidChunkSizePolicy = InvalidChunkSizePolicy.Ignore,
                MissedEndOfTrackPolicy = MissedEndOfTrackPolicy.Ignore,
                UnexpectedTrackChunksCountPolicy = UnexpectedTrackChunksCountPolicy.Ignore,
                UnknownChunkIdPolicy = UnknownChunkIdPolicy.ReadAsUnknownChunk,
                // Logic writes lyrics in the system's encoding; most tools in Latin-1 or UTF-8.
                TextEncoding = Encoding.UTF8,
            });
        }
        catch (Exception exception) when (exception is MidiException or IOException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return new([], null, $"The file is not a readable MIDI file: {exception.Message}");
        }
        if (midi.TimeDivision is not TicksPerQuarterNoteTimeDivision)
        {
            return new([], null, "The file counts time in SMPTE frames, which is not supported.");
        }

        var tempo = midi.GetTempoMap();
        var chunks = midi.GetTrackChunks()
            .Select((chunk, index) => (
                Name: chunk.Events.OfType<SequenceTrackNameEvent>().FirstOrDefault()?.Text?.Trim() ?? $"Track {index + 1}",
                Named: !string.IsNullOrWhiteSpace(chunk.Events.OfType<SequenceTrackNameEvent>().FirstOrDefault()?.Text),
                Notes: chunk.GetNotes().Where(n => n.Channel != 9).ToList(),
                Lyrics: chunk.GetTimedEvents().Where(e => e.Event is LyricEvent).ToList()))
            .ToList();
        var tracks = chunks.Where(t => t.Notes.Count > 0).ToList();
        if (tracks.Count == 0)
        {
            return new([], null, "The MIDI file has no notes outside the drums.");
        }
        // Tonwerk's own backing vocals are named "Harmony …"; the melody is what they were made from.
        var melodic = tracks.Where(t => !t.Name.StartsWith("Harmony", StringComparison.OrdinalIgnoreCase)).ToList();
        if (melodic.Count == 0)
        {
            melodic = tracks;
        }
        // Value tuples: a part not found comes back as the default, with its notes null.
        var track = melodic.FirstOrDefault(t => Sung(t.Name));
        if (track.Notes is null)
        {
            track = melodic.Where(t => t.Lyrics.Count > 0).OrderByDescending(t => t.Notes.Count).FirstOrDefault();
        }
        if (track.Notes is null)
        {
            track = melodic.OrderByDescending(t => t.Notes.Count).First();
        }

        // A format 0 file split into tracks (as DryWetMidi and some converters do) keeps its lyrics in the first, note-less one.
        if (tracks.Count == 1 && chunks.FirstOrDefault(c => c.Notes.Count == 0 && (c.Lyrics.Count > 0 || c.Named)) is { Notes: not null } loose)
        {
            if (track.Lyrics.Count == 0)
            {
                track.Lyrics = loose.Lyrics;
            }
            if (!track.Named && loose.Named)
            {
                track.Name = loose.Name;
            }
        }

        double Seconds(long ticks) => TimeConverter.ConvertTo<MetricTimeSpan>(ticks, tempo).TotalMicroseconds / 1_000_000.0;
        var lyrics = track.Lyrics
            .Select(e => (e.Time, Text: ((LyricEvent)e.Event).Text?.Trim() ?? ""))
            .Where(l => l.Text.Length > 0)
            .ToList();
        var notes = new List<SungNote>(track.Notes.Count);
        var next = 0;
        // A lyric belongs to the note it starts with, give or take a hundredth of a beat (as SoulX-Singer's editor reads it).
        var tolerance = Math.Max(1, ((TicksPerQuarterNoteTimeDivision)midi.TimeDivision).TicksPerQuarterNote / 100);
        foreach (var note in track.Notes.OrderBy(n => n.Time).ThenByDescending(n => n.NoteNumber))
        {
            while (next < lyrics.Count && lyrics[next].Time < note.Time - tolerance)
            {
                next++;
            }
            string? lyric = null;
            if (next < lyrics.Count && Math.Abs(lyrics[next].Time - note.Time) <= tolerance)
            {
                lyric = lyrics[next].Text;
                next++;
            }
            notes.Add(new SungNote(Round(Seconds(note.Time)), Round(Seconds(note.EndTime)), note.NoteNumber, lyric));
        }
        var first = tempo.GetTempoAtTime(new MidiTimeSpan(0)).BeatsPerMinute;
        return new(notes, track.Name, null, Math.Round(first, 3));
    }

    /// <summary>Whether a part's name says it is sung: <c>Vocal</c>, <c>Lead Vocals</c>, <c>Melody</c>, <c>Gesang</c>, ….</summary>
    public static bool Sung(string? name) => name is not null && SungName().IsMatch(name);

    private static double Round(double seconds) => Math.Round(seconds, 4);

    [GeneratedRegex(@"vocal|voice|melod|lead|sing|gesang|stimme|lyric", RegexOptions.IgnoreCase)]
    private static partial Regex SungName();
}
