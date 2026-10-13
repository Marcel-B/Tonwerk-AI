using YueUI.Api.Library;
using YueUI.Api.Timing;

namespace YueUI.Api;

/// <param name="Timing">The lyrics' lines with their times; null while there is none.</param>
/// <param name="Info">Whether Whisper is installed and how far a song without timing is.</param>
public sealed record TimingAnswer(LyricsTiming? Timing, TimingInfo Info);

/// <summary>When each lyrics line is sung (<see cref="LyricsTimer"/>): for the full-screen player and video subtitles.</summary>
public static class TimingEndpoints
{
    public static RouteGroupBuilder MapTimingEndpoints(this RouteGroupBuilder api)
    {
        // Where the environment is looked for, for deploy/install-whisper.sh.
        api.MapGet("/timing", (LyricsTimer timer, Microsoft.Extensions.Options.IOptions<TimingOptions> options) =>
            new { installed = timer.Installed, python = options.Value.ResolvedPython, model = options.Value.Model });

        api.MapGet("/songs/{run}/{song}/timing", (string run, string song, SongLibrary library, LyricsTimer timer) =>
            library.SongDirectory(run, song) is null
                ? Results.NotFound()
                : Results.Ok(new TimingAnswer(timer.Get(run, song), timer.State($"{run}/{song}"))));

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
        });

        // SubRip for a video upload: YouTube takes it beside the video as subtitles.
        api.MapGet("/songs/{run}/{song}/timing.srt", (string run, string song, SongLibrary library, LyricsTimer timer) =>
            library.SongDirectory(run, song) is { } directory && timer.Get(run, song) is { } timing
                ? Results.File(
                    System.Text.Encoding.UTF8.GetBytes(LyricsAligner.Srt(timing)),
                    "application/x-subrip",
                    $"{LibraryEndpoints.FileName(library.TitleOf(run, directory), run)}-{song}.srt")
                : Results.NotFound());
        return api;
    }
}
