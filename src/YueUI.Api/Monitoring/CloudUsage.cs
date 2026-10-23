using System.Collections.Concurrent;

namespace YueUI.Api.Monitoring;

/// <summary>
/// What the calls to Claude used since the server started, by what they were for and the model, for <c>/metrics</c>:
/// they are the only part of Tonwerk that costs money per use, so the tokens are worth seeing next to the jobs.
/// </summary>
/// <remarks>
/// Uses: <c>lyrics</c> (drafts and revisions), <c>sketch</c> (the Ausarbeitung's plans) and <c>translate</c> (a
/// cover's description into English). A call that failed before Anthropic answered counts as a request without tokens.
/// </remarks>
public sealed class CloudUsage
{
    private readonly ConcurrentDictionary<(string Use, string Model), Counts> _totals = new();

    /// <summary>Counts one call; with a logger also writes a line with what it took.</summary>
    public void Record(string use, string model, long? inputTokens, long? outputTokens, bool success, TimeSpan took, ILogger? logger = null)
    {
        var totals = _totals.GetOrAdd((use, model), _ => new Counts());
        lock (totals)
        {
            if (success)
            {
                totals.Succeeded++;
            }
            else
            {
                totals.Failed++;
            }
            totals.Input += inputTokens ?? 0;
            totals.Output += outputTokens ?? 0;
        }
        logger?.LogInformation(
            "Claude {Model} for {Use}: {Outcome} in {Seconds:0.0} s, {Input} tokens in, {Output} out",
            model, use, success ? "answered" : "failed", took.TotalSeconds, inputTokens, outputTokens);
    }

    /// <summary>A copy of the totals per use and model.</summary>
    public IReadOnlyList<(string Use, string Model, long Succeeded, long Failed, long Input, long Output)> Totals()
    {
        List<(string, string, long, long, long, long)> list = [];
        foreach (var ((use, model), totals) in _totals)
        {
            lock (totals)
            {
                list.Add((use, model, totals.Succeeded, totals.Failed, totals.Input, totals.Output));
            }
        }
        return [.. list.OrderBy(entry => entry.Item1, StringComparer.Ordinal).ThenBy(entry => entry.Item2, StringComparer.Ordinal)];
    }

    private sealed class Counts
    {
        public long Succeeded;
        public long Failed;
        public long Input;
        public long Output;
    }
}
