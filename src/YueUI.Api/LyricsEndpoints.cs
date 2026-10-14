using YueUI.Api.Lyrics;
using YueUI.Api.Queue;
using YueUI.Api.Worker;

namespace YueUI.Api;

/// <param name="Keywords">Topic, images, story: what the song is about. Optional with a photo.</param>
/// <param name="Style">The form's style prompt, so the words fit the mood; optional.</param>
/// <param name="Language">The language the lyrics are written in; English when left out.</param>
/// <param name="Model">A model id from <c>GET /api/lyrics/models</c>; the configured one when left out.</param>
/// <param name="Image">
/// A photo the song is about, as a <c>data:image/jpeg;base64,…</c> URL (PNG and WebP too); the model must be able to see.
/// </param>
/// <param name="Lyrics">
/// Lyrics to revise instead of writing new ones, typically the last draft; <paramref name="Instruction"/> says how.
/// </param>
/// <param name="Instruction">What to change in <paramref name="Lyrics"/>, e.g. "make the chorus catchier".</param>
public sealed record LyricsRequest(
    string? Keywords,
    string? Style,
    LyricsLanguage? Language = null,
    string? Model = null,
    string? Image = null,
    string? Lyrics = null,
    string? Instruction = null);

/// <summary>
/// Drafting lyrics with a local language model (LM Studio), see <see cref="LyricsWriter"/>. Like the worker's
/// commands it answers 202; the draft arrives as a <c>lyrics</c> event. While another draft, songs or a voice
/// conversion hold the memory it waits in the <see cref="JobQueue"/> (stage <c>queued</c> in the answer).
/// </summary>
public static class LyricsEndpoints
{
    public const int MaxKeywordsLength = 1000;

    /// <summary>LM Studio's keys are publisher/name, far shorter; this only bounds what is passed on.</summary>
    public const int MaxModelLength = 200;

    /// <summary>A song YuE2 can sing is far shorter (its token limit ends it after a few minutes).</summary>
    public const int MaxLyricsLength = 10_000;

    /// <summary>
    /// The browser scales a photo down to 1024 pixels before sending, some 200 KB; this leaves room for a PNG
    /// sent as it is, and still keeps a phone's 12-megapixel original out.
    /// </summary>
    public const int MaxImageLength = 8 * 1024 * 1024;

    public static RouteGroupBuilder MapLyricsEndpoints(this RouteGroupBuilder api)
    {
        // 503 when LM Studio neither answers nor can be started: the form then drafts with the configured model.
        api.MapGet("/lyrics/models", async (LyricsWriter writer, CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await writer.ListModelsAsync(cancellationToken));
            }
            catch (LyricsUnavailableException exception)
            {
                return Results.Problem(title: exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        api.MapPost("/lyrics", async (LyricsRequest request, JobQueue queue, CancellationToken cancellationToken) =>
        {
            if (request.Image is { } image && !IsImage(image))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["image"] = [$"A JPEG, PNG or WebP photo as a base64 data URL, up to {MaxImageLength / 1024 / 1024} MB."],
                });
            }
            if (request.Lyrics is not null || request.Instruction is not null)
            {
                return Revise(request) ?? Results.Accepted(value: await queue.LyricsAsync(request, cancellationToken));
            }
            if ((string.IsNullOrWhiteSpace(request.Keywords) && request.Image is null) || request.Keywords?.Length > MaxKeywordsLength)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["keywords"] = [$"What the song is about, up to {MaxKeywordsLength} characters; optional with a photo."],
                });
            }
            if (InvalidModel(request.Model) is { } invalid)
            {
                return invalid;
            }
            return Results.Accepted(value: await queue.LyricsAsync(request with { Lyrics = null, Instruction = null }, cancellationToken));
        });

        // A song from a few words in one call, for an iPhone shortcut: the draft first, the song once it is done.
        api.MapPost("/quick", async (QuickSongRequest request, JobQueue queue, QuickSongs quick, Library.SongLibrary library, CancellationToken cancellationToken) =>
        {
            var errors = new Dictionary<string, string[]>();
            if (string.IsNullOrWhiteSpace(request.Keywords) || request.Keywords.Length > MaxKeywordsLength)
            {
                errors["keywords"] = [$"What the song is about, up to {MaxKeywordsLength} characters."];
            }
            var language = QuickSongs.LanguageFrom(request.Language);
            if (language is null)
            {
                errors["language"] = ["de or en."];
            }
            var style = string.IsNullOrWhiteSpace(request.Style) ? NewestStyle(library) : request.Style.Trim();
            if (style is null)
            {
                errors["style"] = ["No song to take the style from yet; send one."];
            }
            var title = string.IsNullOrWhiteSpace(request.Title) ? QuickSongs.TitleFrom(request.Keywords ?? "") : request.Title.Trim();
            // Checked now with stand-in lyrics, so a wrong batch or quality is refused before the draft is written.
            var song = new GenerateRequest(style ?? "", "[verse]\n…", title, Batch: request.Batch ?? 1, Quality: request.Quality ?? "draft");
            foreach (var (key, messages) in song.Validate())
            {
                errors[key] = messages;
            }
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }
            // Expected before the draft may start: one that starts at once can be done before LyricsAsync returns.
            string? expected = null;
            LyricsState draft;
            try
            {
                draft = await queue.LyricsAsync(
                    new LyricsRequest(request.Keywords!.Trim(), style, language),
                    cancellationToken,
                    id => quick.Expect(expected = id, song));
            }
            catch when (expected is not null)
            {
                quick.Forget(expected);
                throw;
            }
            return Results.Accepted(value: new QuickSongStarted(draft.Id, draft.Stage, title, style!));
        });
        return api;
    }

    /// <summary>
    /// A revision works on the lyrics as they are rather than drafting anew; the keywords, if sent, tell the model what
    /// the song is about. A photo would add nothing the lyrics do not already carry, and cost a vision model.
    /// </summary>
    /// <returns>Why the revision cannot be made, or null.</returns>
    private static IResult? Revise(LyricsRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Lyrics) || request.Lyrics.Length > MaxLyricsLength)
        {
            errors["lyrics"] = [$"The lyrics to revise, up to {MaxLyricsLength} characters."];
        }
        if (string.IsNullOrWhiteSpace(request.Instruction) || request.Instruction.Length > MaxKeywordsLength)
        {
            errors["instruction"] = [$"What to change in the lyrics, up to {MaxKeywordsLength} characters."];
        }
        if (request.Keywords?.Length > MaxKeywordsLength)
        {
            errors["keywords"] = [$"What the song is about, up to {MaxKeywordsLength} characters."];
        }
        if (request.Image is not null)
        {
            errors["image"] = ["A revision works on the lyrics alone; send the photo with a new draft."];
        }
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }
        return InvalidModel(request.Model);
    }

    /// <summary>Unknown ids are left to LM Studio, which refuses them with its own message.</summary>
    private static IResult? InvalidModel(string? model) =>
        model is not null && (model.Length > MaxModelLength || model.Any(char.IsControl))
            ? Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["model"] = [$"A model id as GET /api/lyrics/models lists it, up to {MaxModelLength} characters."],
            })
            : null;

    /// <summary>The style of the newest run that has one: what is being made these days.</summary>
    private static string? NewestStyle(Library.SongLibrary library) =>
        library.ListRuns().Select(run => run.Style).FirstOrDefault(style => !string.IsNullOrWhiteSpace(style));

    /// <summary>Checked here so that LM Studio is not loaded for a request it cannot read.</summary>
    private static bool IsImage(string image)
    {
        if (image.Length > MaxImageLength
            || !(image.StartsWith("data:image/jpeg;base64,", StringComparison.Ordinal)
                || image.StartsWith("data:image/png;base64,", StringComparison.Ordinal)
                || image.StartsWith("data:image/webp;base64,", StringComparison.Ordinal)))
        {
            return false;
        }
        var data = image.AsSpan(image.IndexOf(',') + 1);
        return data.Length > 0 && System.Buffers.Text.Base64.IsValid(data);
    }
}
