namespace YueToLogic.Core.Conversion;

/// <summary>
/// Parts heard in the song's recording rather than generated from its chords: Standard MIDI Files whose time zero is
/// the start of the recording, as an audio-to-MIDI tool writes them from a separated stem. They take the place of the
/// generated bass and drums, and go through the rest of the arrangement (groove, mono, count-in) like those would.
/// </summary>
/// <param name="Bass">One line of notes, which becomes the <c>Bass</c> track.</param>
/// <param name="Drums">
/// Hits on General MIDI's drum notes (kick 36, snare 38, hi-hats 42/46, crash 49), laid out like the drum pattern: on
/// one <c>Drums</c> track or one per drum, and on a drum machine's notes where <see cref="Arrangement.DrumOptions.Notes"/>
/// names them. Other notes are left out.
/// </param>
public sealed record RecordedParts(byte[]? Bass = null, byte[]? Drums = null);
