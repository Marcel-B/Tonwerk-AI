using System.Globalization;

namespace YueUI.Api.Library;

/// <param name="Value">The style block, engine, LoRA, … as the request said it.</param>
/// <param name="Count">Rated songs made with it.</param>
/// <param name="Average">Their mean rating.</param>
/// <param name="Score">
/// The mean pulled towards the overall one by <see cref="Insights.Prior"/> imagined songs, so a block on one
/// five-star song does not outrank one on twenty four-star songs; the lists are sorted by it.
/// </param>
public sealed record InsightEntry(string Value, int Count, double Average, double Score);

/// <param name="Key"><c>style</c>, <c>engine</c>, <c>quality</c>, <c>lora</c>, <c>instrumental</c>, <c>length</c>, <c>steps</c>.</param>
public sealed record InsightGroup(string Key, IReadOnlyList<InsightEntry> Entries);

/// <param name="Rated">Songs with stars; only they count.</param>
/// <param name="Average">Their mean rating, the baseline every entry is compared to.</param>
public sealed record InsightReport(int Rated, double Average, IReadOnlyList<InsightGroup> Groups);

/// <summary>
/// What the rated songs were made with, against their stars: which style blocks, engines and settings end up with
/// four or five. The ratings are in the database, the rest in each song's request, so this reads every rated song's
/// request once per call; a library of a few thousand songs takes well under a second.
/// </summary>
public static class Insights
{
    /// <summary>How many songs of average rating every entry starts with.</summary>
    public const double Prior = 3;

    /// <summary>An entry on fewer songs says nothing yet.</summary>
    public const int MinCount = 2;

    public static InsightReport Build(SongLibrary library)
    {
        List<(int Rating, List<(string Key, string Value)> Features)> songs = [];
        foreach (var run in library.ListRuns())
        {
            foreach (var song in run.Songs)
            {
                if (song.Rating is not { } rating)
                {
                    continue;
                }
                var name = song.Id[(song.Id.IndexOf('/') + 1)..];
                var request = library.ReadRequest(run.Id, name);
                songs.Add((rating, Features(run, song, request)));
            }
        }
        return Report(songs);
    }

    /// <summary>The features of one song; a style block counts once per song even if written twice.</summary>
    public static List<(string Key, string Value)> Features(RunInfo run, SongInfo song, SongRequest? request)
    {
        List<(string, string)> features = [];
        var style = request?.Style is { Length: > 0 } own ? own : run.Style;
        foreach (var block in StyleBlocks(style))
        {
            features.Add(("style", block));
        }
        features.Add(("engine", song.Engine ?? "yue2"));
        if ((song.Quality ?? request?.Quality) is { Length: > 0 } quality)
        {
            features.Add(("quality", quality));
        }
        if (song.Engine is null)
        {
            features.Add(("lora", request?.Lora is { Length: > 0 } lora ? Path.GetFileNameWithoutExtension(lora) : ""));
        }
        if (request?.Instrumental is { } instrumental)
        {
            features.Add(("instrumental", instrumental ? "yes" : "no"));
        }
        if (song.Seconds is { } seconds)
        {
            features.Add(("length", seconds switch { < 120 => "<2", < 180 => "2-3", < 240 => "3-4", _ => "4+" }));
        }
        if (request?.FullSteps is { } steps)
        {
            features.Add(("steps", steps.ToString(CultureInfo.InvariantCulture)));
        }
        return features;
    }

    /// <summary>The comma-separated blocks of a style, as the form's building blocks write them.</summary>
    public static IEnumerable<string> StyleBlocks(string style) => style
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(block => block.ToLowerInvariant())
        .Distinct();

    public static InsightReport Report(IReadOnlyList<(int Rating, List<(string Key, string Value)> Features)> songs)
    {
        if (songs.Count == 0)
        {
            return new InsightReport(0, 0, []);
        }
        var average = songs.Average(s => s.Rating);
        var groups = songs
            .SelectMany(s => s.Features.Distinct().Select(f => (f.Key, f.Value, s.Rating)))
            .GroupBy(f => f.Key)
            .Select(group => new InsightGroup(
                group.Key,
                group.GroupBy(f => f.Value)
                    .Select(entry =>
                    {
                        var count = entry.Count();
                        var sum = entry.Sum(f => f.Rating);
                        return new InsightEntry(entry.Key, count, Math.Round((double)sum / count, 2), Math.Round((sum + Prior * average) / (count + Prior), 2));
                    })
                    .Where(entry => entry.Count >= MinCount)
                    .OrderByDescending(entry => entry.Score)
                    .ThenByDescending(entry => entry.Count)
                    .ToList()))
            .Where(group => group.Entries.Count > 0)
            .OrderBy(group => Array.IndexOf(Order, group.Key))
            .ToList();
        return new InsightReport(songs.Count, Math.Round(average, 2), groups);
    }

    private static readonly string[] Order = ["style", "engine", "quality", "lora", "steps", "length", "instrumental"];
}
