using System.Text.Json.Nodes;
using YueUI.Api.Lyrics;
using YueUI.Api.Singing;
using YueUI.Api.Voices;

namespace YueUI.Api.Machines;

// What a job carries to a helper. Models and voices go by id, the helper resolves them in its own settings; files go
// as the run's multipart parts under the names given in each record's remarks.

/// <summary>A take of the speech lab. Files: <c>voice</c> (the recording to clone) or <c>source</c> (the recording to clean). Out: <c>take.wav</c>.</summary>
public sealed record SpeechHelperJob(string Model, bool Clean, string? Text = null, string? RefText = null, double Seconds = 0);

/// <summary>A painted picture. Out: <c>image.jpg</c>.</summary>
public sealed record ImageHelperJob(string Model, string Prompt, long Seed, int Width, int Height);

/// <summary>A sung melody. Out: <c>take.wav</c>; an <c>aligned</c> event before the model loads.</summary>
public sealed record SingingHelperJob(IReadOnlyList<SungNote> Notes, string Lyrics, string Voice, int PitchShift, bool AutoShift);

/// <summary>An ACE-Step job, acestep.cpp's request already built. Files: <c>source</c> for a cover or repaint. Out: <c>song0.wav</c>, …; the result's <c>requests</c> in the same order.</summary>
public sealed record AceHelperJob(JsonObject Request, bool UsesLm);

/// <summary>A separation. Files: <c>audio</c>. Out: the stems' WAVs.</summary>
public sealed record StemsHelperJob(string Model, bool Dereverb);

/// <summary>A conversion with Seed-VC. Files: <c>vocals</c>, <c>reference</c>. Out: <c>converted.wav</c>; progress with <c>data</c> the estimate in seconds.</summary>
public sealed record VoiceHelperJob(VoiceConversion Conversion);

/// <summary>A lyrics draft with the helper's LM Studio. Result: <c>lyrics</c>.</summary>
public sealed record LyricsHelperJob(
    string? Model,
    string Keywords,
    string? Style,
    LyricsLanguage Language,
    string? Image = null,
    LyricsRevision? Revision = null);
