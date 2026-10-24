using System.Globalization;
using System.Text;

namespace YueUI.Api.Timing;

/// <summary>A word Whisper heard, with its time in the song in seconds.</summary>
public sealed record HeardWord(string Word, double Start, double End);

/// <param name="Text">The line as the lyrics have it.</param>
/// <param name="Section">The section tag it is under (<c>Verse</c>, <c>Chorus</c>, …), if any.</param>
/// <param name="Start">When it starts to be sung, in seconds.</param>
/// <param name="End">When it ends.</param>
/// <param name="Heard">
/// How many of its words Whisper heard; 0 for a line whose time is only guessed between its neighbours.
/// </param>
public sealed record TimedLine(string Text, string? Section, double Start, double End, int Heard);

/// <param name="Lines">The lyrics' sung lines in order, section tags left out.</param>
/// <param name="Source"><c>vocals</c> when a vocal stem was heard, else <c>mix</c>.</param>
/// <param name="Coverage">The share of the lyrics' words Whisper heard, 0 to 1: how far to trust the times.</param>
public sealed record LyricsTiming(IReadOnlyList<TimedLine> Lines, string Source, double Coverage, DateTimeOffset CreatedAt);

/// <summary>
/// Lays the lyrics' lines onto the words Whisper heard. The lyrics are what was sung, Whisper only says when: it
/// mishears words in a mix and skips or invents some, so the two word sequences are aligned (longest common
/// subsequence, with words counted alike when they are nearly the same) and each line takes the time of its first and
/// last heard word. A line with no heard word gets its time from its neighbours, split evenly.
/// </summary>
/// <remarks>
/// Repeated choruses are why this is a sequence alignment rather than a search per line: the order keeps the second
/// chorus from taking the first one's words.
/// </remarks>
public static partial class LyricsAligner
{
    /// <summary>Lines shorter than this are stretched to it; a single word still gets its moment.</summary>
    private const double MinLineSeconds = 0.6;

    /// <summary>
    /// The lyrics' sung lines with the section they are under, cut as the full-screen player shows them (the web page's
    /// <c>timing.ts</c> <c>shownLines</c>, the app's <c>ShownLine</c>; keep the three alike): <c>[Tags]</c> start a
    /// section wherever they stand, directions in round brackets are left out (they are not sung), and a paragraph
    /// whose line breaks were lost is broken where a line plausibly ended, so Whisper times each of its lines.
    /// </summary>
    public static IReadOnlyList<(string Text, string? Section)> SungLines(string lyrics)
    {
        List<(string, string?)> lines = [];
        string? section = null;
        var english = !LooksGerman(lyrics);
        foreach (var raw in lyrics.Split('\n'))
        {
            foreach (var (kind, text) in Pieces(raw.Trim()))
            {
                if (kind == '[')
                {
                    section = text[1..^1].Trim();
                }
                else if (kind == ' ')
                {
                    lines.AddRange(Broken(text, english).Where(piece => Words(piece).Count > 0).Select(piece => (piece, section)));
                }
            }
        }
        return lines;
    }

    /// <summary>Longer than this, a line is a paragraph whose line breaks were lost (as a chatbot's lyrics often come).</summary>
    private const int Paragraph = 60;

    /// <summary>A line cut into <c>[tags]</c> ('['), <c>(directions)</c> ('(') and the text between (' ').</summary>
    private static List<(char Kind, string Text)> Pieces(string line)
    {
        List<(char, string)> found = [];
        var text = new StringBuilder();
        for (var i = 0; i < line.Length;)
        {
            var open = line[i];
            var end = open switch
            {
                '[' => line.IndexOf(']', i + 1),
                '(' => line.IndexOf(')', i + 1),
                _ => -1,
            };
            if (end > i + 1)
            {
                found.Add((' ', text.ToString()));
                found.Add((open, line[i..(end + 1)]));
                text.Clear();
                i = end + 1;
            }
            else
            {
                text.Append(open);
                i++;
            }
        }
        found.Add((' ', text.ToString()));
        return [.. found.Select(p => (p.Item1, Whitespace().Replace(p.Item2, " ").Trim())).Where(p => p.Item2.Length > 0)];
    }

    /// <summary>
    /// A paragraph broken after a sentence's end, and in English also before a capital that follows a lower-case word
    /// ("carbon glass The ghosts"), never before "I". German capitalises every noun, so there only sentence ends count.
    /// </summary>
    private static IEnumerable<string> Broken(string text, bool english)
    {
        if (text.Length <= Paragraph)
        {
            return [text];
        }
        List<List<string>> lines = [];
        foreach (var word in text.Split(' '))
        {
            var previous = lines.Count > 0 ? lines[^1][^1] : null;
            var ends = previous is not null
                && (SentenceEnd().IsMatch(previous)
                    || (english && char.IsLower(previous[^1]) && char.IsUpper(word[0]) && !LoneI().IsMatch(word)));
            if (previous is null || ends)
            {
                lines.Add([word]);
            }
            else
            {
                lines[^1].Add(word);
            }
        }
        return lines.Select(words => string.Join(' ', words));
    }

    private static bool LooksGerman(string lyrics) => Umlaut().IsMatch(lyrics) || GermanWord().IsMatch(lyrics);

    [System.Text.RegularExpressions.GeneratedRegex(@"\s+")]
    private static partial System.Text.RegularExpressions.Regex Whitespace();

    [System.Text.RegularExpressions.GeneratedRegex(@"[.!?…][""'”’]?$")]
    private static partial System.Text.RegularExpressions.Regex SentenceEnd();

    [System.Text.RegularExpressions.GeneratedRegex(@"^I(['’]|$)")]
    private static partial System.Text.RegularExpressions.Regex LoneI();

    [System.Text.RegularExpressions.GeneratedRegex("[äöüÄÖÜß]")]
    private static partial System.Text.RegularExpressions.Regex Umlaut();

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(und|ich|nicht|ist|wir|mein|dein|auf|kein)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex GermanWord();

    /// <summary>
    /// Whether a timing was made from other lines than the lyrics now give: before the lines were cut as the player
    /// shows them, or for lyrics changed since. Such a song is heard again.
    /// </summary>
    public static bool Stale(LyricsTiming timing, string lyrics) =>
        !timing.Lines.Select(line => line.Text).SequenceEqual(SungLines(lyrics).Select(line => line.Text));

    public static LyricsTiming Align(string lyrics, IReadOnlyList<HeardWord> heard, double? duration, string source, DateTimeOffset now)
    {
        var lines = SungLines(lyrics);
        // Each lyric word with the line it belongs to.
        List<(string Word, int Line)> words = [];
        for (var i = 0; i < lines.Count; i++)
        {
            words.AddRange(Words(lines[i].Text).Select(word => (word, i)));
        }
        var heardWords = heard.Select(h => (Word: Normalize(h.Word), h.Start, h.End)).Where(h => h.Word.Length > 0).ToList();
        var pairs = Lcs(words.Select(w => w.Word).ToList(), heardWords.Select(h => h.Word).ToList());

        var starts = new double?[lines.Count];
        var ends = new double?[lines.Count];
        var counts = new int[lines.Count];
        foreach (var (lyric, at) in pairs)
        {
            var line = words[lyric].Line;
            starts[line] ??= heardWords[at].Start;
            ends[line] = heardWords[at].End;
            counts[line]++;
        }

        var end = duration ?? (heardWords.Count > 0 ? heardWords[^1].End + 2 : lines.Count * 3.0);
        var timed = new TimedLine[lines.Count];
        for (var i = 0; i < lines.Count; i++)
        {
            if (starts[i] is null)
            {
                continue;
            }
            timed[i] = new TimedLine(lines[i].Text, lines[i].Section, starts[i]!.Value, Math.Max(ends[i]!.Value, starts[i]!.Value + MinLineSeconds), counts[i]);
        }
        // Runs of unheard lines share the gap between the heard lines around them.
        for (var i = 0; i < lines.Count;)
        {
            if (timed[i] is not null)
            {
                i++;
                continue;
            }
            var first = i;
            while (i < lines.Count && timed[i] is null)
            {
                i++;
            }
            var from = first > 0 ? timed[first - 1].End : 0;
            var to = i < lines.Count ? timed[i].Start : end;
            var step = Math.Max(to - from, 0) / (i - first);
            for (var j = first; j < i; j++)
            {
                timed[j] = new TimedLine(lines[j].Text, lines[j].Section, from + step * (j - first), from + step * (j - first + 1), 0);
            }
        }
        // A line runs until the next one starts at most, so two are never lit at once.
        for (var i = 0; i + 1 < timed.Length; i++)
        {
            if (timed[i].End > timed[i + 1].Start)
            {
                timed[i] = timed[i] with { End = Math.Max(timed[i + 1].Start, timed[i].Start) };
            }
        }
        var coverage = words.Count == 0 ? 0 : Math.Round((double)pairs.Count / words.Count, 2);
        return new LyricsTiming([.. timed.Select(Round)], source, coverage, now);
    }

    private static TimedLine Round(TimedLine line) => line with { Start = Math.Round(line.Start, 2), End = Math.Round(line.End, 2) };

    /// <summary>SubRip subtitles, one per line, for a video upload (YouTube takes them beside the video).</summary>
    public static string Srt(LyricsTiming timing)
    {
        var text = new StringBuilder();
        for (var i = 0; i < timing.Lines.Count; i++)
        {
            var line = timing.Lines[i];
            text.Append(CultureInfo.InvariantCulture, $"{i + 1}\n{SrtTime(line.Start)} --> {SrtTime(line.End)}\n{line.Text}\n\n");
        }
        return text.ToString();
    }

    private static string SrtTime(double seconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Round(Math.Max(seconds, 0) * 1000));
        return string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00},{time.Milliseconds:000}");
    }

    /// <summary>The words of a line, normalized; a (YuE2 style) parenthesis like "(oh)" counts too, it is sung.</summary>
    public static List<string> Words(string line) =>
        [.. line.Split([' ', '\t', '-', '/'], StringSplitOptions.RemoveEmptyEntries).Select(Normalize).Where(w => w.Length > 0)];

    /// <summary>Lower case letters and digits only; umlauts stay, since Whisper writes them.</summary>
    public static string Normalize(string word)
    {
        var text = new StringBuilder(word.Length);
        foreach (var c in word.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                text.Append(c);
            }
        }
        return text.ToString();
    }

    /// <summary>Nearly the same word: equal, or one edit apart in a word of five letters or more ("tonight"/"tonite").</summary>
    public static bool Alike(string a, string b)
    {
        if (a == b)
        {
            return true;
        }
        if (Math.Min(a.Length, b.Length) < 5 || Math.Abs(a.Length - b.Length) > 2)
        {
            return false;
        }
        return Distance(a, b) <= Math.Max(1, Math.Min(a.Length, b.Length) / 4);
    }

    private static int Distance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                current[j] = Math.Min(Math.Min(current[j - 1], previous[j]) + 1, previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            }
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }

    /// <summary>The matched pairs (lyric index, heard index) of a longest common subsequence, in order.</summary>
    private static List<(int Lyric, int Heard)> Lcs(IReadOnlyList<string> lyric, IReadOnlyList<string> heard)
    {
        // A song has a few hundred words each way; the table stays small.
        var table = new int[lyric.Count + 1, heard.Count + 1];
        for (var i = lyric.Count - 1; i >= 0; i--)
        {
            for (var j = heard.Count - 1; j >= 0; j--)
            {
                table[i, j] = Alike(lyric[i], heard[j]) ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);
            }
        }
        List<(int, int)> pairs = [];
        for (int i = 0, j = 0; i < lyric.Count && j < heard.Count;)
        {
            if (Alike(lyric[i], heard[j]) && table[i, j] == table[i + 1, j + 1] + 1)
            {
                pairs.Add((i, j));
                i++;
                j++;
            }
            else if (table[i + 1, j] >= table[i, j + 1])
            {
                i++;
            }
            else
            {
                j++;
            }
        }
        return pairs;
    }
}
