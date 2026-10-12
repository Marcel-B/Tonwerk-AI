using System.Globalization;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using YueToLogic.Core.Diagnostics;

namespace YueToLogic.Core.Midi;

/// <summary>What a Standard MIDI File holds that a score can be made from, in the file's own ticks.</summary>
internal sealed record MidiContent(
    int TicksPerQuarterNote,
    IReadOnlyList<MidiSource> Sources,
    IReadOnlyList<MidiTempo> Tempos,
    IReadOnlyList<MidiMeter> Meters,
    IReadOnlyList<MidiKey> Keys,
    IReadOnlyList<MidiText> Markers);

/// <summary>
/// The notes of one track on one channel. A track that plays on several channels (every track of a type 0 file
/// does) becomes one source per channel, since those are different parts.
/// </summary>
/// <param name="Texts">Text events of the track, which is where <see cref="MidiRenderer"/> writes the chord names.</param>
internal sealed record MidiSource(string Name, int Channel, IReadOnlyList<MidiNote> Notes, IReadOnlyList<MidiText> Texts);

internal readonly record struct MidiNote(long Start, long End, int Pitch, int Velocity = 100);

internal readonly record struct MidiTempo(long Tick, double Bpm);

internal readonly record struct MidiMeter(long Tick, int Numerator, int Denominator);

internal readonly record struct MidiKey(long Tick, int Sharps, bool IsMinor);

internal readonly record struct MidiText(long Tick, string Text);

/// <summary>
/// Reads a Standard MIDI File as a DAW writes it. The notes are paired here rather than by DryWetMidi's note
/// detection, so that the rules are visible: a note-off (or a note-on at velocity 0) ends the oldest open note
/// of its pitch and channel, and a note left open ends with its track.
/// </summary>
internal static class MidiFileReader
{
    /// <returns>The content, or <c>null</c> with an error in <paramref name="diagnostics"/>.</returns>
    public static MidiContent? Read(Stream stream, DiagnosticBag diagnostics)
    {
        MidiFile file;
        try
        {
            file = MidiFile.Read(stream, new ReadingSettings
            {
                // Tolerate what DAWs and hand-made files get slightly wrong; none of it touches the notes.
                InvalidChannelEventParameterValuePolicy = InvalidChannelEventParameterValuePolicy.SnapToLimits,
                InvalidMetaEventParameterValuePolicy = InvalidMetaEventParameterValuePolicy.SnapToLimits,
                InvalidChunkSizePolicy = InvalidChunkSizePolicy.Ignore,
                MissedEndOfTrackPolicy = MissedEndOfTrackPolicy.Ignore,
                UnexpectedTrackChunksCountPolicy = UnexpectedTrackChunksCountPolicy.Ignore,
                UnknownChunkIdPolicy = UnknownChunkIdPolicy.ReadAsUnknownChunk,
            });
        }
        catch (Exception ex) when (ex is MidiException or IOException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            diagnostics.Error(DiagnosticCodes.MidiUnreadable, $"The file is not a readable Standard MIDI File: {ex.Message}");
            return null;
        }

        if (file.TimeDivision is not TicksPerQuarterNoteTimeDivision { TicksPerQuarterNote: > 0 } division)
        {
            diagnostics.Error(DiagnosticCodes.MidiUnreadable, "The file counts time in SMPTE frames rather than beats, so its notes cannot be placed in bars.");
            return null;
        }

        var sources = new List<MidiSource>();
        var tempos = new List<MidiTempo>();
        var meters = new List<MidiMeter>();
        var keys = new List<MidiKey>();
        var markers = new List<MidiText>();
        var trackNumber = 0;
        foreach (var chunk in file.Chunks.OfType<TrackChunk>())
        {
            trackNumber++;
            string? name = null;
            var texts = new List<MidiText>();
            var open = new Dictionary<(int Channel, int Pitch), Queue<(long Start, int Velocity)>>();
            var notes = new Dictionary<int, List<MidiNote>>();
            var time = 0L;
            foreach (var midiEvent in chunk.Events)
            {
                time += midiEvent.DeltaTime;
                switch (midiEvent)
                {
                    case SequenceTrackNameEvent trackName when name is null && !string.IsNullOrWhiteSpace(trackName.Text):
                        name = Clean(trackName.Text);
                        break;
                    case InstrumentNameEvent instrument when name is null && !string.IsNullOrWhiteSpace(instrument.Text):
                        name = Clean(instrument.Text);
                        break;
                    case SetTempoEvent tempo when tempo.MicrosecondsPerQuarterNote > 0:
                        tempos.Add(new MidiTempo(time, 60_000_000.0 / tempo.MicrosecondsPerQuarterNote));
                        break;
                    case TimeSignatureEvent meter:
                        meters.Add(new MidiMeter(time, meter.Numerator, meter.Denominator));
                        break;
                    case KeySignatureEvent key:
                        keys.Add(new MidiKey(time, key.Key, key.Scale == 1));
                        break;
                    case MarkerEvent marker when !string.IsNullOrWhiteSpace(marker.Text):
                        markers.Add(new MidiText(time, Clean(marker.Text)));
                        break;
                    case TextEvent text when !string.IsNullOrWhiteSpace(text.Text):
                        texts.Add(new MidiText(time, Clean(text.Text)));
                        break;
                    case NoteOnEvent on when on.Velocity > 0:
                        var slot = ((int)on.Channel, (int)on.NoteNumber);
                        if (!open.TryGetValue(slot, out var starts))
                        {
                            open[slot] = starts = new Queue<(long Start, int Velocity)>();
                        }

                        starts.Enqueue((time, on.Velocity));
                        break;
                    case NoteOnEvent or NoteOffEvent:
                        var note = (NoteEvent)midiEvent;
                        if (open.TryGetValue(((int)note.Channel, (int)note.NoteNumber), out var pending) && pending.Count > 0)
                        {
                            var (start, velocity) = pending.Dequeue();
                            Add(notes, note.Channel, new MidiNote(start, time, note.NoteNumber, velocity));
                        }

                        break;
                }
            }

            foreach (var ((channel, pitch), pending) in open)
            {
                while (pending.Count > 0)
                {
                    var (start, velocity) = pending.Dequeue();
                    Add(notes, (FourBitNumber)(byte)channel, new MidiNote(start, Math.Max(time, start + 1), pitch, velocity));
                }
            }

            var label = name ?? string.Create(CultureInfo.InvariantCulture, $"Track {trackNumber}");
            foreach (var (channel, channelNotes) in notes.OrderBy(n => n.Key))
            {
                var sourceName = notes.Count == 1 ? label : string.Create(CultureInfo.InvariantCulture, $"{label} (channel {channel + 1})");
                sources.Add(new MidiSource(sourceName, channel + 1, [.. channelNotes.OrderBy(n => n.Start).ThenBy(n => n.Pitch)], texts));
            }
        }

        return new MidiContent(
            division.TicksPerQuarterNote,
            sources,
            [.. tempos.OrderBy(t => t.Tick)],
            [.. meters.OrderBy(m => m.Tick)],
            [.. keys.OrderBy(k => k.Tick)],
            [.. markers.OrderBy(m => m.Tick)]);
    }

    private static void Add(Dictionary<int, List<MidiNote>> notes, FourBitNumber channel, MidiNote note)
    {
        if (note.End <= note.Start)
        {
            note = note with { End = note.Start + 1 };
        }

        if (!notes.TryGetValue(channel, out var list))
        {
            notes[channel] = list = [];
        }

        list.Add(note);
    }

    /// <summary>Text as a DAW stores it may end in NUL bytes or carry line breaks; neither belongs in a name.</summary>
    private static string Clean(string text) =>
        string.Join(' ', text.Replace("\0", string.Empty, StringComparison.Ordinal).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
}
