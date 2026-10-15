using YueUI.Api.Logs;

namespace YueUI.Api;

/// <summary>The collected log (<see cref="LogStore"/>) and the report the log page copies for a chat.</summary>
public static class LogEndpoints
{
    public static RouteGroupBuilder MapLogEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/logs", Query)
            .WithName("ListLogs")
            .Produces<LogPage>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);
        api.MapGet("/logs/report", (LogReport report, double? hours) =>
            Results.Text(report.Build(TimeSpan.FromHours(Math.Clamp(hours ?? 24, 1, 24 * 14))), "text/plain; charset=utf-8"))
            .WithName("GetLogReport")
            .Produces<string>(StatusCodes.Status200OK, "text/plain");
        return api;
    }

    /// <param name="source">Comma-separated sources (<see cref="LogSources.All"/>); empty for all.</param>
    /// <param name="level">The lowest level shown: debug, info, warning or error.</param>
    /// <param name="q">Text the message or exception contains.</param>
    /// <param name="before">Lines older than this, for the next page.</param>
    private static IResult Query(LogStore store, string? source, string? level, string? q, DateTimeOffset? before, int? limit)
    {
        var sources = (source ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
        if (sources.FirstOrDefault(s => !LogSources.All.Contains(s)) is { } unknown)
        {
            return Results.Problem(title: $"Unknown source {unknown}.", statusCode: StatusCodes.Status400BadRequest);
        }
        if (level is not null && !LogLevels.All.Contains(level))
        {
            return Results.Problem(title: $"Unknown level {level}.", statusCode: StatusCodes.Status400BadRequest);
        }
        return Results.Ok(store.Query(new LogQuery(sources, level, q, before, Limit: limit ?? 200)));
    }
}
