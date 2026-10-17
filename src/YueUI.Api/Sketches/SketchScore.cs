using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using YueToLogic.Core.Abc;
using YueToLogic.Core.Harmony;
using YueToLogic.Core.Model;
using YueUI.Api.Ace;
using YueUI.Api.Timing;

namespace YueUI.Api.Sketches;

/// <summary>Something the code repaired in a plan or doubts about it; the page words it from <see cref="Code"/>.</summary>
/// <param name="Code">barLength, note, chord, chordSimplified, syllables, key, tooLong</param>
/// <param name="Bar">The bar, counted from 1 over the whole song.</param>
/// <param name="Detail">The symbol, the counts or the length in question.</param>
public sealed record SketchWarning(string Code, int? Bar = null, string? Detail = null);

/// <summary>A plan laid out on the grid: the score's document and what the MIDI draft and the form need besides.</summary>
/// <param name="Chords">The chords as planned, for the MIDI draft's voicings (add9 and the like survive there).</param>
public sealed record LaidOutSketch(
    ScoreDocument Score,
    IReadOnlyList<PlannedChord> Chords,
    IReadOnlyList<SketchWarning> Warnings,
    int BarUnits);

/// <param name="Start">In sixteenths from the song's start.</param>
/// <param name="Symbol">The core YuE2 reads, null when not even a root could be read.</param>
/// <param name="AddNinth">The planned symbol asked for a ninth (add9, 9, maj9, m9…), which the core drops.</param>
public sealed record PlannedChord(long Start, long End, string Text, ChordSymbol? Symbol, bool AddNinth, int Bar);

/// <summary>
/// Turns a <see cref="SketchPlan"/> into what YuE2 reads: a score in its ABC dialect (written by YueToLogic.Core's
/// <see cref="AbcScoreWriter"/>, which lays it out as YuE2 does), lyrics with section tags, and the form's request.
/// Deterministic, so the model's answer is checked and repaired here rather than trusted: bars that do not add up are
/// filled or cut, chords outside YuE2's vocabulary keep their core, and lines whose syllables do not match their notes
/// are reported.
/// </summary>
public static partial class SketchScore
{
    public const int TicksPerQuarter = 480;
    private const int TicksPerUnit = TicksPerQuarter / 4;
    private const int TokensPerSecond = 25;

    /// <summary>The worker's own limit; above it only with YueUI's extension, see <see cref="GenerateRequest.MaxTokensLimit"/>.</summary>
    private const int TokenMargin = 250;

    private static readonly Dictionary<string, string> TagTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["intro"] = "Intro",
        ["verse"] = "Verse",
        ["pre-chorus"] = "Pre-Chorus",
        ["chorus"] = "Chorus",
        ["bridge"] = "Bridge",
        ["interlude"] = "Interlude",
        ["outro"] = "Outro",
    };

    public static double Seconds(SketchPlan plan) =>
        plan.Sections.Sum(s => s.Bars.Count) * Math.Clamp(plan.BeatsPerBar, 1, 12) * 60.0 / Tempo(plan);

    public static SketchResult Result(SketchJob job)
    {
        var plan = job.Plan ?? throw new InvalidOperationException("The sketch has no plan yet.");
        var laid = LayOut(plan);
        var warnings = laid.Warnings.ToList();
        var abc = new AbcScoreWriter().Write(laid.Score);
        var lyrics = Lyrics(plan);
        var seconds = Seconds(plan);
        var tokens = (int)Math.Ceiling(seconds * TokensPerSecond) + TokenMargin;
        if (tokens > GenerateRequest.MaxTokensLimit)
        {
            warnings.Add(new SketchWarning("tooLong", null, ((int)seconds).ToString(CultureInfo.InvariantCulture)));
        }
        var request = new Library.SongRequest(
            Title: plan.Title.Trim(),
            Style: plan.Style.Trim(),
            Lyrics: lyrics,
            Instrumental: false,
            Quality: null,
            // The plan's chords are the point: "full" keeps them, "melody" would plan new ones.
            Cot: "full",
            Seed: null,
            Engines: null,
            DraftSteps: null,
            MaxTokens: Math.Clamp(tokens, GenerateRequest.MinTokens, GenerateRequest.MaxTokensLimit),
            Abc: abc,
            FullSteps: null,
            AbcSampling: null,
            SemanticSampling: null);
        return new SketchResult(
            job, abc, lyrics, request, AceRequest(plan, lyrics, seconds), warnings, plan.Sections.Sum(s => s.Bars.Count),
            seconds);
    }

    /// <summary>
    /// What of the plan ACE-Step can take: it reads no score, so melody and chords stay behind, but the lyrics with
    /// their section tags, the style as caption, tempo, key, metre and length are what its LM otherwise picks itself.
    /// Everything else is left to the page's defaults.
    /// </summary>
    public static AceSongRequest AceRequest(SketchPlan plan, string lyrics, double seconds)
    {
        var (key, _, minor) = Key(plan.Key, []);
        var beats = Math.Clamp(plan.BeatsPerBar, 1, 12);
        return new AceSongRequest(
            Title: plan.Title.Trim(),
            Caption: plan.Style.Trim(),
            Lyrics: lyrics,
            Instrumental: false,
            Language: LyricsTimer.Language(plan.Style, lyrics),
            Duration: Math.Clamp(Math.Ceiling(seconds), 10, 600),
            Bpm: (int)Tempo(plan),
            Key: AceKey(key, minor),
            TimeSignature: AceSongRequest.TimeSignatures.Contains(beats) ? beats : null,
            Task: "text2music");
    }

    /// <summary>"F#m" → "F# minor", spelled with the roots ACE-Step's key list has (no Cb, D#, G#, A#).</summary>
    private static string AceKey(string key, bool minor)
    {
        var root = minor ? key[..^1] : key;
        root = root switch
        {
            "Cb" => "B",
            "D#" => "Eb",
            "G#" => "Ab",
            "A#" => "Bb",
            _ => root,
        };
        return root + (minor ? " minor" : " major");
    }

    /// <summary>
    /// The lyrics as YuE2 takes them: each section under its tag, its lines as the sketch wrote them. Sections without
    /// words keep their tag, so the model hears the form (an instrumental's structure is written the same way).
    /// </summary>
    public static string Lyrics(SketchPlan plan)
    {
        var text = new StringBuilder();
        foreach (var section in plan.Sections)
        {
            if (text.Length > 0)
            {
                text.Append('\n');
            }
            text.Append('[').Append(TagTitles.GetValueOrDefault(section.Tag.Trim(), "Verse")).Append("]\n");
            foreach (var line in SungLines(section))
            {
                text.Append(line).Append('\n');
            }
        }
        return text.ToString().TrimEnd() + "\n";
    }

    /// <summary>The section's lines as the sketch wrote them, else its bars' words, a line per bar.</summary>
    private static IEnumerable<string> SungLines(SketchSection section)
    {
        var lines = (section.Lyrics ?? []).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        return lines.Count > 0 ? lines : section.Bars.Select(b => b.Lyric?.Trim() ?? "").Where(l => l.Length > 0);
    }

    public static LaidOutSketch LayOut(SketchPlan plan)
    {
        var warnings = new List<SketchWarning>();
        var beats = Math.Clamp(plan.BeatsPerBar, 1, 12);
        var barUnits = beats * 4;
        var (keyName, sharps, minor) = Key(plan.Key, warnings);

        var vocal = new NoteCollector();
        var ins = new NoteCollector();
        var chords = new List<PlannedChord>();
        var sections = new List<SectionMarker>();
        var simplified = new SortedSet<string>(StringComparer.Ordinal);
        var bar = 0;
        foreach (var section in plan.Sections)
        {
            sections.Add(new SectionMarker((long)bar * barUnits * TicksPerUnit, Tag(section.Tag)));
            foreach (var planned in section.Bars)
            {
                var start = (long)bar * barUnits;
                var number = bar + 1;
                var onsets = vocal.Add(planned.Vocal, start, barUnits, number, warnings);
                ins.Add(planned.Ins, start, barUnits, number, warnings);
                AddChords(planned.Chords, start, barUnits, number, chords, warnings, simplified);
                if (planned.Lyric is { } lyric && Syllables(lyric) is var syllables and > 0
                    && Math.Abs(syllables - onsets) > Math.Max(1, syllables / 6))
                {
                    warnings.Add(new SketchWarning("syllables", number, $"{onsets}/{syllables}"));
                }
                bar++;
            }
        }
        if (simplified.Count > 0)
        {
            warnings.Add(new SketchWarning("chordSimplified", null, string.Join(", ", simplified)));
        }

        var length = (long)bar * barUnits;
        // A chord lasts until the next one; the last until the song ends.
        for (var i = 0; i < chords.Count; i++)
        {
            chords[i] = chords[i] with { End = i + 1 < chords.Count ? chords[i + 1].Start : length };
        }
        var events = chords
            .Where(c => c.Symbol is not null && c.End > c.Start)
            .Select(c => new ChordEvent(c.Start * TicksPerUnit, (c.End - c.Start) * TicksPerUnit, c.Text, c.Symbol))
            .ToList();

        var score = new ScoreDocument(
            TicksPerQuarter,
            plan.Title,
            Tempo(plan),
            [new TimeSignatureChange(0, beats, 4)],
            [new KeySignatureChange(0, keyName, sharps, minor)],
            sections,
            [
                new VoiceTrack(AbcScoreWriter.VocalVoiceId, "Vocal", vocal.Notes),
                new VoiceTrack(AbcScoreWriter.InsVoiceId, "Ins", ins.Notes),
            ],
            events,
            Math.Max(length, 1) * TicksPerUnit);
        return new LaidOutSketch(score, chords, warnings, barUnits);
    }

    public static double Tempo(SketchPlan plan) => double.IsFinite(plan.Tempo) ? Math.Clamp(Math.Round(plan.Tempo), 30, 300) : 120;

    private static string Tag(string tag) => TagTitles.ContainsKey(tag.Trim()) ? tag.Trim().ToLowerInvariant() : "verse";

    /// <summary>Collects one voice's notes across bars, joining tied notes.</summary>
    private sealed class NoteCollector
    {
        private readonly List<NoteEvent> _notes = [];
        private bool _tieOpen;

        public IReadOnlyList<NoteEvent> Notes => _notes;

        /// <returns>New note onsets in the bar (a tied continuation is none).</returns>
        public int Add(string? line, long barStart, int barUnits, int bar, List<SketchWarning> warnings)
        {
            var position = 0;
            var onsets = 0;
            var tokens = (line ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var cut = false;
            foreach (var token in tokens)
            {
                if (!TryParseNote(token, out var pitch, out var units, out var tie))
                {
                    warnings.Add(new SketchWarning("note", bar, token));
                    continue;
                }
                if (position >= barUnits)
                {
                    cut = true;
                    break;
                }
                if (position + units > barUnits)
                {
                    units = barUnits - position;
                    cut = true;
                }
                var start = (barStart + position) * TicksPerUnit;
                if (pitch is { } note)
                {
                    if (_tieOpen && _notes.Count > 0 && _notes[^1].NoteNumber == note && _notes[^1].StartTicks + _notes[^1].DurationTicks == start)
                    {
                        var last = _notes[^1];
                        _notes[^1] = last with { DurationTicks = last.DurationTicks + units * TicksPerUnit };
                    }
                    else
                    {
                        _notes.Add(new NoteEvent(start, units * TicksPerUnit, note));
                        onsets++;
                    }
                }
                _tieOpen = tie && pitch is not null;
                position += units;
            }
            if (tokens.Length > 0 && (cut || position < barUnits))
            {
                warnings.Add(new SketchWarning("barLength", bar, $"{position}/{barUnits}"));
            }
            if (position < barUnits)
            {
                // The rest of a short bar is silence; a tie cannot reach across it.
                _tieOpen = _tieOpen && position == barUnits;
            }
            return onsets;
        }
    }

    /// <summary><c>A4:2</c>, <c>Bb3:4~</c>, <c>F#5:8</c>, <c>r:16</c>.</summary>
    public static bool TryParseNote(string token, out int? pitch, out int units, out bool tie)
    {
        pitch = null;
        units = 0;
        tie = false;
        var match = NotePattern().Match(token);
        if (!match.Success || !int.TryParse(match.Groups["length"].Value, CultureInfo.InvariantCulture, out units) || units <= 0)
        {
            return false;
        }
        tie = match.Groups["tie"].Success;
        if (match.Groups["rest"].Success)
        {
            return true;
        }
        var letter = char.ToUpperInvariant(match.Groups["letter"].Value[0]);
        var semitone = "C D EF G A B".IndexOf(letter, StringComparison.Ordinal);
        foreach (var accidental in match.Groups["accidental"].Value)
        {
            semitone += accidental == '#' ? 1 : -1;
        }
        var octave = int.Parse(match.Groups["octave"].Value, CultureInfo.InvariantCulture);
        var number = (octave + 1) * 12 + semitone;
        if (number is < 0 or > 127)
        {
            return false;
        }
        pitch = number;
        return true;
    }

    [GeneratedRegex(@"^(?:(?<rest>[rRzZ])|(?<letter>[A-Ga-g])(?<accidental>##|bb|#|b)?(?<octave>-?\d))[:/](?<length>\d{1,3})(?<tie>~)?$")]
    private static partial Regex NotePattern();

    private static void AddChords(
        string text,
        long barStart,
        int barUnits,
        int bar,
        List<PlannedChord> chords,
        List<SketchWarning> warnings,
        SortedSet<string> simplified)
    {
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t is not ("|" or "-" or "%"))
            .ToList();
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var offset = barUnits * i / tokens.Count;
            var at = token.LastIndexOf('@');
            if (at > 0 && int.TryParse(token[(at + 1)..], CultureInfo.InvariantCulture, out var given))
            {
                offset = Math.Clamp(given, 0, barUnits - 1);
                token = token[..at];
            }
            var (core, addNinth) = Normalize(token);
            ChordSymbol? symbol = null;
            if (core is not null && ChordSymbolParser.TryParse(core, out var parsed))
            {
                symbol = parsed;
            }
            else if (ChordSymbolParser.ParseRootOnly(token) is { } root)
            {
                symbol = root;
                core = ChordSymbolParser.Format(root);
                warnings.Add(new SketchWarning("chord", bar, token));
            }
            else
            {
                warnings.Add(new SketchWarning("chord", bar, token));
                continue;
            }
            if (!string.Equals(core, token, StringComparison.Ordinal))
            {
                simplified.Add($"{token} → {core}");
            }
            var start = barStart + offset;
            if (chords.Count > 0 && chords[^1].Start >= start)
            {
                // Two chords on one spot: the later one wins.
                chords.RemoveAt(chords.Count - 1);
            }
            if (chords.Count > 0 && chords[^1].Text == core && chords[^1].AddNinth == addNinth)
            {
                continue;
            }
            chords.Add(new PlannedChord(start, start, core!, symbol, addNinth, bar));
        }
    }

    /// <summary>
    /// A chord symbol reduced to YuE2's vocabulary (major, m, dim, aug, 7, maj7, m7, dim7, m7b5, sus4, sus2, 6, m6,
    /// 7sus4, m(maj7), with a slash bass), and whether it asked for a ninth, which only the MIDI draft can play.
    /// </summary>
    public static (string? Core, bool AddNinth) Normalize(string symbol)
    {
        var text = symbol.Trim().Replace('♭', 'b').Replace('♯', '#').Replace("Δ", "maj7", StringComparison.Ordinal);
        var slash = text.IndexOf('/');
        string? bass = null;
        if (slash > 0 && slash < text.Length - 1 && text[(slash + 1)..] is var after && BassPattern().IsMatch(after))
        {
            bass = char.ToUpperInvariant(after[0]) + after[1..];
            text = text[..slash];
        }
        var match = RootPattern().Match(text);
        if (!match.Success)
        {
            return (null, false);
        }
        var root = char.ToUpperInvariant(match.Groups["root"].Value[0]) + match.Groups["root"].Value[1..];
        var suffix = match.Groups["suffix"].Value;
        var addNinth = suffix.Contains('9', StringComparison.Ordinal) || suffix.Contains("add2", StringComparison.OrdinalIgnoreCase);
        var plain = suffix.Replace("(", "", StringComparison.Ordinal).Replace(")", "", StringComparison.Ordinal);
        string quality;
        if (plain == "m(maj7)" || plain is "mmaj7" or "mM7" or "m/maj7" or "minmaj7" || suffix == "m(maj7)")
        {
            quality = "m(maj7)";
        }
        else
        {
            var lower = plain.Replace("add9", "", StringComparison.OrdinalIgnoreCase)
                .Replace("add2", "", StringComparison.OrdinalIgnoreCase)
                .Replace("add11", "", StringComparison.OrdinalIgnoreCase)
                .Replace("add4", "", StringComparison.OrdinalIgnoreCase);
            quality = lower switch
            {
                "" or "maj" or "M" or "5" => "",
                "m" or "min" or "-" => "m",
                "dim" or "°" or "o" => "dim",
                "aug" or "+" => "aug",
                "7" or "9" or "11" or "13" or "7b9" or "7#9" or "7b13" or "7#11" or "7alt" or "13b9" => "7",
                "maj7" or "M7" or "maj9" or "M9" or "maj13" or "maj7#11" or "ma7" or "j7" => "maj7",
                "m7" or "min7" or "-7" or "m9" or "m11" or "min9" or "-9" or "m13" => "m7",
                "dim7" or "°7" or "o7" => "dim7",
                "m7b5" or "ø" or "ø7" or "-7b5" or "min7b5" => "m7b5",
                "sus4" or "sus" => "sus4",
                "sus2" => "sus2",
                "6" or "69" or "6/9" => "6",
                "m6" or "min6" or "m69" => "m6",
                "7sus4" or "7sus" or "9sus4" or "9sus" or "11sus" => "7sus4",
                _ => "?",
            };
            if (quality == "?")
            {
                return (null, addNinth);
            }
        }
        return (root + quality + (bass is null ? "" : "/" + bass), addNinth);
    }

    [GeneratedRegex(@"^(?<root>[A-Ga-g](?:#|b)?)(?<suffix>.*)$")]
    private static partial Regex RootPattern();

    [GeneratedRegex(@"^[A-Ga-g](?:#|b)?$")]
    private static partial Regex BassPattern();

    /// <summary>Sharps (positive) or flats of every key the key picker offers, with its mode.</summary>
    private static readonly Dictionary<string, (int Sharps, bool Minor)> Keys = BuildKeys();

    private static Dictionary<string, (int, bool)> BuildKeys()
    {
        string[] majors = ["Cb", "Gb", "Db", "Ab", "Eb", "Bb", "F", "C", "G", "D", "A", "E", "B", "F#", "C#"];
        string[] minors = ["Abm", "Ebm", "Bbm", "Fm", "Cm", "Gm", "Dm", "Am", "Em", "Bm", "F#m", "C#m", "G#m", "D#m", "A#m"];
        var keys = new Dictionary<string, (int, bool)>(StringComparer.Ordinal);
        for (var i = 0; i < majors.Length; i++)
        {
            keys[majors[i]] = (i - 7, false);
            keys[minors[i]] = (i - 7, true);
        }
        return keys;
    }

    private static (string Name, int Sharps, bool Minor) Key(string text, List<SketchWarning> warnings)
    {
        var compact = text.Trim().Replace(" ", "", StringComparison.Ordinal).Replace("♭", "b", StringComparison.Ordinal)
            .Replace("♯", "#", StringComparison.Ordinal);
        var minor = compact.EndsWith("minor", StringComparison.OrdinalIgnoreCase) || compact.EndsWith("min", StringComparison.OrdinalIgnoreCase)
            || compact.EndsWith("moll", StringComparison.OrdinalIgnoreCase) || (compact.Length >= 2 && compact.EndsWith('m'));
        var rootMatch = RootPattern().Match(compact);
        if (rootMatch.Success)
        {
            var root = char.ToUpperInvariant(rootMatch.Groups["root"].Value[0]) + rootMatch.Groups["root"].Value[1..];
            var name = root + (minor ? "m" : "");
            if (Keys.TryGetValue(name, out var key))
            {
                return (name, key.Sharps, key.Minor);
            }
        }
        warnings.Add(new SketchWarning("key", null, text));
        return ("C", 0, false);
    }

    /// <summary>
    /// Syllables of a line by its vowel groups: a guess (English silent e and diphthongs, German umlauts), close enough
    /// to tell a bar with far too many or too few notes.
    /// </summary>
    public static int Syllables(string line)
    {
        var count = 0;
        foreach (Match word in WordPattern().Matches(line.ToLowerInvariant()))
        {
            var text = word.Value;
            var groups = VowelGroups().Matches(text).Count;
            if (groups > 1 && text.EndsWith('e') && !text.EndsWith("le", StringComparison.Ordinal)
                && !text.EndsWith("ee", StringComparison.Ordinal) && text.Length > 3 && !IsGerman(line))
            {
                groups--;
            }
            if (groups > 1 && (text.EndsWith("ed", StringComparison.Ordinal) && !text.EndsWith("ted", StringComparison.Ordinal)
                && !text.EndsWith("ded", StringComparison.Ordinal)) && !IsGerman(line))
            {
                groups--;
            }
            count += Math.Max(1, groups);
        }
        return count;
    }

    private static bool IsGerman(string line) => line.IndexOfAny(['ä', 'ö', 'ü', 'ß', 'Ä', 'Ö', 'Ü']) >= 0;

    [GeneratedRegex(@"[\p{L}']+")]
    private static partial Regex WordPattern();

    [GeneratedRegex(@"(?:ie|ei|eu|äu|au|ai|ee|oo|ou|oa|ea|[aeiouyäöü])+")]
    private static partial Regex VowelGroups();
}
