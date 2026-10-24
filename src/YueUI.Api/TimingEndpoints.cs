using YueUI.Api.Library;
using YueUI.Api.Timing;

namespace YueUI.Api;

/// <param name="Timing">The lyrics' lines with their times; null while there is none.</param>
/// <param name="Info">Whether Whisper is installed and how far a song without timing is.</param>
public sealed record TimingAnswer(LyricsTiming? Timing, TimingInfo Info);

/// <summary>Whether Whisper can listen and with what, for deploy/install-whisper.sh.</summary>
public sealed record TimingSetup(bool Installed, string Python, string Model);

/// <summary>When each lyrics line is sung (<see cref="LyricsTimer"/>): for the full-screen player and video subtitles.</summary>
public static class TimingEndpoints
{
    public static RouteGroupBuilder MapTimingEndpoints(this RouteGroupBuilder api)
    {
        // Where the environment is looked for, for deploy/install-whisper.sh.
        api.MapGet("/timing", (LyricsTimer timer, Microsoft.Extensions.Options.IOptions<TimingOptions> options) =>
            new TimingSetup(timer.Installed, options.Value.ResolvedPython, options.Value.Model))
            .WithName("GetTimingSetup");

        // A timing made from other lines than the lyrics now give (before the lines were cut as the player shows them)
        // is answered as it is and heard again in the background; the state says so, so the player looks again.
        api.MapGet("/songs/{run}/{song}/timing", (string run, string song, SongLibrary library, LyricsTimer timer) =>
        {
            if (library.SongDirectory(run, song) is null)
            {
                return Results.NotFound();
            }
            timer.RefreshIfStale(run, song);
            return Results.Ok(new TimingAnswer(timer.Get(run, song), timer.State($"{run}/{song}")));
        })
            .WithName("GetSongTiming")
            .Produces<TimingAnswer>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        // The player asks for a song that has none yet; answered at once, the result is fetched again later.
        api.MapPost("/songs/{run}/{song}/timing", (string run, string song, SongLibrary library, LyricsTimer timer) =>
        {
            if (library.SongDirectory(run, song) is null)
            {
                return Results.NotFound();
            }
            if (!timer.Enqueue($"{run}/{song}"))
            {
                return Results.Problem(title: "Whisper is not installed; run deploy/install-whisper.sh.", statusCode: StatusCodes.Status501NotImplemented);
            }
            return Results.Accepted(value: new TimingAnswer(timer.Get(run, song), timer.State($"{run}/{song}")));
        })
            .WithName("StartSongTiming")
            .Produces<TimingAnswer>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status501NotImplemented);

        // SubRip for a video upload: YouTube takes it beside the video as subtitles.
        api.MapGet("/songs/{run}/{song}/timing.srt", (string run, string song, SongLibrary library, LyricsTimer timer) =>
            library.SongDirectory(run, song) is { } directory && timer.Get(run, song) is { } timing
                ? Results.File(
                    System.Text.Encoding.UTF8.GetBytes(LyricsAligner.Srt(timing)),
                    "application/x-subrip",
                    $"{LibraryEndpoints.FileName(library.TitleOf(run, directory), run)}-{song}.srt")
                : Results.NotFound())
            .WithName("GetSongTimingSrt")
            .ProducesFile("application/x-subrip")
            .Produces(StatusCodes.Status404NotFound);
        return api;
    }
}
