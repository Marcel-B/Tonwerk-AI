namespace YueUI.Api.Sketches;

/// <summary>
/// A whole song as Claude plans it from a written sketch: the song's frame, then every bar with its chords, the sung
/// melody, the words sung in it and an instrumental line. Everything else (the score YuE2 reads, its lyrics, the MIDI
/// draft) is derived from this by code (<see cref="SketchScore"/>, <see cref="SketchArranger"/>), so the model only
/// writes music and never a file format.
/// </summary>
/// <param name="Style">YuE2's style prompt: English, comma separated, genre, instruments, voice, mood and tempo.</param>
/// <param name="Tempo">Quarter notes per minute.</param>
/// <param name="BeatsPerBar">Quarter notes per bar: 4 for 4/4, 3 for 3/4.</param>
/// <param name="Key">The key as the score names it: <c>C</c>, <c>F#m</c>, <c>Bb</c>, <c>Dm</c>.</param>
/// <param name="Notes">What the model decided or assumed beyond the sketch, for the person to check.</param>
public sealed record SketchPlan(
    string Title,
    string Style,
    double Tempo,
    int BeatsPerBar,
    string Key,
    IReadOnlyList<SketchSection> Sections,
    IReadOnlyList<string> Notes);

/// <param name="Name">The sketch's name for the section, e.g. "Chorus 2".</param>
/// <param name="Tag">intro, verse, pre-chorus, chorus, bridge, interlude or outro: the names YuE2's scores use.</param>
/// <param name="Parts">Which accompaniment plays in the MIDI draft: piano, pad, bass, drums, pulse, sparse-drums.</param>
/// <param name="Energy">1 quiet (held chords), 2 moving (broken chords), 3 full (rhythmic chords, the widest pad).</param>
/// <param name="Lyrics">The section's lines word for word as the sketch writes them; YuE2's lyrics are made of these.</param>
public sealed record SketchSection(
    string Name,
    string Tag,
    IReadOnlyList<string> Parts,
    int Energy,
    IReadOnlyList<string> Lyrics,
    IReadOnlyList<SketchBar> Bars);

/// <param name="Chords">The bar's chords, spread evenly over it (<c>F C/E</c> is half a bar each), or placed with
/// <c>@</c> and a sixteenth (<c>Gm7 C@12</c>).</param>
/// <param name="Vocal">The sung melody in sixteenths: <c>A4:2 C5:6 r:8</c>; <c>~</c> after a length ties it to the
/// next note of the same pitch. Null for a bar without singing.</param>
/// <param name="Lyric">The words whose syllables the melody of this bar starts, one note per syllable.</param>
/// <param name="Ins">An instrumental line (a motif, a riff) in the same notation, or null.</param>
/// <param name="Parts">This bar's accompaniment where it differs from the section's.</param>
public sealed record SketchBar(
    string Chords,
    string? Vocal,
    string? Lyric,
    string? Ins,
    IReadOnlyList<string>? Parts);

/// <summary>A sketch on its way through Claude (<see cref="SketchMaker"/>); kept in <c>song_sketches</c>.</summary>
public sealed record SketchJob
{
    public required string Id { get; init; }

    /// <summary>The sketch's first heading until the plan names the song.</summary>
    public string Title { get; init; } = "";

    /// <summary>queued, writing, done or failed.</summary>
    public string Stage { get; init; } = "queued";

    /// <summary>Why it failed.</summary>
    public string? Message { get; init; }

    /// <summary>The written sketch as it was sent.</summary>
    public string Source { get; init; } = "";

    /// <summary>What the person added for this attempt ("more of a ballad", "keep the chorus melody").</summary>
    public string? Instruction { get; init; }

    public string? Model { get; init; }

    public SketchPlan? Plan { get; init; }

    public int? InputTokens { get; init; }

    public int? OutputTokens { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public bool Finished => Stage is "done" or "failed";
}

/// <summary>A list entry: the job without its sketch and plan.</summary>
public sealed record SketchSummary(
    string Id,
    string Title,
    string Stage,
    string? Message,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int? Bars,
    double? Seconds)
{
    public static SketchSummary Of(SketchJob job) => new(
        job.Id,
        job.Title,
        job.Stage,
        job.Message,
        job.CreatedAt,
        job.UpdatedAt,
        job.Plan?.Sections.Sum(s => s.Bars.Count),
        job.Plan is { } plan ? SketchScore.Seconds(plan) : null);
}

/// <summary>A finished sketch with everything made from its plan.</summary>
/// <param name="Request">The song as the create form takes it, score and planning included.</param>
/// <param name="Warnings">What the code had to repair or doubts (a bar too long, a line with more syllables than notes).</param>
public sealed record SketchResult(
    SketchJob Job,
    string Abc,
    string Lyrics,
    Library.SongRequest Request,
    IReadOnlyList<SketchWarning> Warnings,
    int Bars,
    double Seconds);

/// <param name="Instruction">Optional wishes for this attempt.</param>
public sealed record SketchRequest(string Text, string? Instruction = null);

/// <summary>Whether Claude can be asked (a key is set), and with which model.</summary>
public sealed record SketchInfo(bool Configured, string Model);
