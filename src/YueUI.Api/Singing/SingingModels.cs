using System.Text.Json.Serialization;

namespace YueUI.Api.Singing;

/// <summary>A note to sing, in seconds from the start, with the word written on it in the MIDI file where there was one.</summary>
/// <param name="Lyric">The MIDI file's lyric event at the note's start; null where the words come from the lyrics text.</param>
public sealed record SungNote(double Start, double End, int Pitch, string? Lyric = null);

/// <summary>
/// A reference voice SoulX-Singer clones: a few seconds of singing with its notes and words in the model's metadata
/// format, which says what is sung when. A class with setters, since the list can be replaced in the configuration
/// (<see cref="SingingOptions.Voices"/>).
/// </summary>
public sealed class SingingVoice
{
    /// <summary>Stable key, stored with each take.</summary>
    public string Id { get; set; } = "";

    public string Label { get; set; } = "";

    /// <summary>The recording; relative paths are read from SoulX-Singer's checkout (<see cref="SingingOptions.ResolvedSource"/>).</summary>
    public string Audio { get; set; } = "";

    /// <summary>Its notes, words and timing as SoulX-Singer's preprocessing writes them (a JSON list, the first segment is used).</summary>
    public string Metadata { get; set; } = "";

    /// <summary>What the voice sings in; the model sings English words with every voice.</summary>
    public string Language { get; set; } = "";

    /// <summary>
    /// The two singers SoulX-Singer's checkout brings as examples (<c>example/audio</c>, Apache 2.0 with the code): one
    /// English, one Mandarin. Voices of one's own need their notes and words written out first, which is a later step.
    /// </summary>
    public static IReadOnlyList<SingingVoice> Defaults { get; } =
    [
        new() { Id = "en", Label = "Beispielstimme Englisch", Audio = "example/audio/en_prompt.mp3", Metadata = "example/audio/en_prompt.json", Language = "English" },
        new() { Id = "zh", Label = "Beispielstimme Mandarin", Audio = "example/audio/zh_prompt.mp3", Metadata = "example/audio/zh_prompt.json", Language = "Mandarin" },
    ];
}

public sealed record SingingVoiceInfo(string Id, string Label, string Language);

/// <param name="Installed">The environment with SoulX-Singer is there.</param>
/// <param name="ModelDownloaded">Its weights are there; deploy/install-singing.sh downloads them.</param>
/// <param name="Python">Where the server looks for the environment, which the install script asks for.</param>
/// <param name="Source">Where it looks for SoulX-Singer's checkout.</param>
/// <param name="Model">Where it looks for the weights.</param>
/// <param name="NltkData">Where it looks for the pronunciation dictionary.</param>
public sealed record SingingInfo(
    bool Installed,
    bool ModelDownloaded,
    string Python,
    string Source,
    string Model,
    string NltkData,
    IReadOnlyList<SingingVoiceInfo> Voices,
    IReadOnlyList<SingTake> Takes);

/// <summary>A melody with words sung by SoulX-Singer (<see cref="SingingStudio"/>).</summary>
public sealed record SingTake
{
    public required string Id { get; init; }

    /// <summary>The file's name without extension, or the library song's title.</summary>
    public string Title { get; init; } = "";

    /// <summary>queued, loading, singing, done, failed or cancelled.</summary>
    public string Stage { get; init; } = "queued";

    /// <summary>Why it failed.</summary>
    public string? Message { get; init; }

    /// <summary>How much of the melody is sung, 0 to 1, by segments.</summary>
    public double? Progress { get; init; }

    public string VoiceId { get; init; } = "";

    public string VoiceLabel { get; init; } = "";

    /// <summary>The library song (<c>run/songN</c>) whose score was sung, or null for an uploaded file.</summary>
    public string? SongId { get; init; }

    /// <summary>The uploaded file's name, or null for a library song.</summary>
    public string? FileName { get; init; }

    /// <summary>The words as given; empty when the MIDI file carried them.</summary>
    public string Lyrics { get; init; } = "";

    /// <summary>Semitones the melody is moved by before it is sung.</summary>
    public int PitchShift { get; init; }

    /// <summary>Moves the melody into the voice's range (the median of its notes onto the voice's) instead.</summary>
    public bool AutoShift { get; init; }

    /// <summary>How many notes the melody has.</summary>
    public int NoteCount { get; init; }

    /// <summary>Notes that got a word; the rest became rests.</summary>
    public int? Sung { get; init; }

    /// <summary>Notes left without a word, because the lyrics ran out.</summary>
    public int? Unsung { get; init; }

    /// <summary>Syllables of the lyrics left over after the last note.</summary>
    public int? Leftover { get; init; }

    public double? Seconds { get; init; }

    public double? LoadSeconds { get; init; }

    public double? SingSeconds { get; init; }

    public double? PeakMemoryGb { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    [JsonIgnore]
    public bool Finished => Stage is "done" or "failed" or "cancelled";
}

/// <summary>What the script made of the words: how many notes got one.</summary>
public sealed record SingingAlignment(int Sung, int Unsung, int Leftover);

/// <summary>The script's measurements; null where it could not tell.</summary>
public sealed record SingingResult(double? LoadSeconds, double? SingSeconds, double? PeakMemoryGb);

/// <param name="Output">Where the singing is written, as 24 kHz mono WAV.</param>
public sealed record SingingJob(
    IReadOnlyList<SungNote> Notes,
    string Lyrics,
    SingingVoice Voice,
    int PitchShift,
    bool AutoShift,
    string Output);

public sealed class SingingException(string message, int status = StatusCodes.Status500InternalServerError) : Exception(message)
{
    public int Status { get; } = status;
}
