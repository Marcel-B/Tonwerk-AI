namespace YueToLogic.Core.Diagnostics;

/// <summary>Stable identifiers for <see cref="Diagnostic.Code"/>, usable as localization keys by frontends.</summary>
public static class DiagnosticCodes
{
    // Header and structure
    public const string MissingHeaderField = "YTL001";
    public const string InvalidFieldValue = "YTL002";
    public const string UnsupportedKey = "YTL003";
    public const string IgnoredField = "YTL004";
    public const string UndeclaredVoice = "YTL005";
    public const string TempoChangeIgnored = "YTL006";

    // Music lines
    public const string UnsupportedNotation = "YTL010";
    public const string NonNativeDuration = "YTL011";
    public const string BarLengthMismatch = "YTL012";
    public const string VoicesOutOfSync = "YTL013";
    public const string TieMismatch = "YTL014";
    public const string PitchOutOfRange = "YTL015";
    public const string MissingBarLine = "YTL016";
    public const string ConflictingChange = "YTL017";
    public const string ScoreTruncated = "YTL018";

    // Harmony
    public const string UnknownChord = "YTL020";
    public const string ChordOutsideVocal = "YTL021";
    public const string NoChords = "YTL022";

    // Arrangement
    public const string UnknownVoice = "YTL030";

    /// <summary>Which key the backing vocals were derived in, and where it came from.</summary>
    public const string HarmonyKey = "YTL031";

    /// <summary>The score had no chord symbols, so the backing vocals follow chords guessed from the melody.</summary>
    public const string HarmonyChords = "YTL032";

    /// <summary>No note lies in the sections the backing vocals were asked for.</summary>
    public const string HarmonySections = "YTL033";

    /// <summary>A part heard in the recording (bass or drums from a stem) replaced the generated one, or could not be read.</summary>
    public const string RecordedPart = "YTL034";

    // YTL060 and YTL061 belonged to the tempo fit, which was removed because it rarely matched the recording;
    // they stay unused so that old output is not misread.

    // Logic Pro project
    public const string InvalidAudio = "YTL050";
    public const string UnsupportedSampleRate = "YTL051";
    public const string AudioLengthMismatch = "YTL052";
    public const string LogicTemplateLimitation = "YTL053";

    // YTL054 and YTL056 were the stem and voice imports of yue-to-logic-pro's own API; not to be reused.

    /// <summary>An instrument names a MIDI output the Logic template does not know, so its track is not routed.</summary>
    public const string MidiPortUnknown = "YTL055";

    // MIDI file read back into a score (MIDI → ABC)

    /// <summary>The file is not a Standard MIDI File, or one this reader cannot place on a beat grid.</summary>
    public const string MidiUnreadable = "YTL070";

    /// <summary>None of the tracks read as Vocal, Ins or Chords has a note.</summary>
    public const string MidiNoNotes = "YTL071";

    /// <summary>Notes off the sixteenth grid were moved onto it.</summary>
    public const string MidiQuantized = "YTL072";

    /// <summary>A voice had overlapping notes; YuE2's voices play one note at a time.</summary>
    public const string MidiPolyphony = "YTL073";

    /// <summary>Chords could not be read: no chord track, or notes that form no chord of the vocabulary.</summary>
    public const string MidiChords = "YTL074";

    /// <summary>Silent bars at the start, such as a count-in, were left out.</summary>
    public const string MidiBarsSkipped = "YTL075";

    /// <summary>Which track of the file became which voice.</summary>
    public const string MidiTrackRoles = "YTL076";

    /// <summary>The tempo was rounded, or tempo changes were dropped.</summary>
    public const string MidiTempo = "YTL077";

    /// <summary>A meter or key change had to be moved to a bar line, or a meter cannot be written.</summary>
    public const string MidiSignature = "YTL078";

    // Fatal
    public const string NoMusic = "YTL040";
    public const string TickResolution = "YTL041";
    public const string InvalidOption = "YTL042";
}
