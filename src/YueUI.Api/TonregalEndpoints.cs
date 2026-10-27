using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using YueUI.Api.Data;
using YueUI.Api.Export;
using YueUI.Api.Library;
using YueUI.Api.Share;
using YueUI.Api.Tonregal;

namespace YueUI.Api;

/// <summary>Whether songs can be handed to Tonregal; the export dialog shows its button only then.</summary>
/// <param name="Url">The address in use, for the settings; the key itself never leaves the server.</param>
/// <param name="KeySet">A key is set.</param>
/// <param name="Editable">The settings can change address and key: there is an env file and no environment variable
/// that would win over it.</param>
public sealed record TonregalStatus(bool Configured, string? Url, bool KeySet, bool Editable);

/// <summary>Address and key from the settings. An empty key keeps the one set; an empty address turns the handover off.</summary>
public sealed record TonregalSettings(string? Url, string? ApiKey);

/// <summary>
/// A song handed to Tonregal. <see cref="State"/> is Tonregal's: <c>imported</c> (filed under <see cref="LibraryPath"/>),
/// <c>matched</c> (accepted, filed shortly), <c>rejected</c> (the same file is there already, see <see cref="Error"/>).
/// </summary>
/// <param name="Repeated">Tonregal knew the song from an earlier handover and kept that one.</param>
public sealed record TonregalTransfer(int JobId, string State, string? LibraryPath, string? Error, bool Repeated);

/// <summary>
/// Hands a song to Tonregal: the export's FLAC with the same tags (title, album, lyrics, cover, the AI mark) plus artist
/// and genre from the dialog, tempo and key as heard, under the song's id as Tonregal's <c>externalId</c>.
/// </summary>
public static class TonregalEndpoints
{
    public static RouteGroupBuilder MapTonregalEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/tonregal", (IOptions<TonregalOptions> options) => StatusOf(options.Value))
            .WithName("GetTonregalStatus")
            .Produces<TonregalStatus>(StatusCodes.Status200OK);

        api.MapPut("/tonregal", Save)
            .WithName("SetTonregalSettings")
            .Produces<TonregalStatus>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        api.MapPost("/tonregal/check", (TonregalClient tonregal, CancellationToken cancellationToken) => tonregal.CheckAsync(cancellationToken))
            .WithName("CheckTonregal")
            .Produces<TonregalCheck>(StatusCodes.Status200OK);

        api.MapPost("/songs/{run}/{song}/tonregal", SendAsync).DisableAntiforgery()
            .WithName("SendSongToTonregal")
            .Produces<TonregalTransfer>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .ProducesProblem(StatusCodes.Status502BadGateway);
        return api;
    }

    private static TonregalStatus StatusOf(TonregalOptions options) => new(
        options.Configured,
        options.BaseUri?.ToString().TrimEnd('/'),
        options.ResolvedKey is not null,
        !string.IsNullOrWhiteSpace(options.EnvFile) && !TonregalOptions.SetByEnvironment);

    private static IResult Save(TonregalSettings settings, IOptions<TonregalOptions> options)
    {
        var current = options.Value;
        if (string.IsNullOrWhiteSpace(current.EnvFile) || TonregalOptions.SetByEnvironment)
        {
            return Results.Problem(
                title: "Tonregal is set by environment variables on the server, which the settings cannot change.",
                statusCode: StatusCodes.Status409Conflict);
        }
        var url = settings.Url?.Trim() ?? "";
        var key = settings.ApiKey?.Trim() ?? "";
        if (url.Length > 0 && !(Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["url"] = ["An http or https address."] });
        }
        if (key.Any(char.IsWhiteSpace) || key.Contains('"') || key.Contains('\''))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["apiKey"] = ["A key without spaces or quotes."] });
        }
        var values = new Dictionary<string, string?> { [TonregalOptions.UrlVariable] = url.Length > 0 ? url.TrimEnd('/') : null };
        if (key.Length > 0)
        {
            values[TonregalOptions.KeyVariable] = key;
        }
        try
        {
            EnvValues.Write(current.EnvFile, values);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Results.Problem(title: "The settings could not be written.", detail: exception.Message, statusCode: StatusCodes.Status500InternalServerError);
        }
        // The options read the file on every use, so this already answers with what was written.
        return Results.Ok(StatusOf(current));
    }

    private static async Task<IResult> SendAsync(
        string run,
        string song,
        [FromForm] string? artist,
        [FromForm] string? genre,
        IFormFile? cover,
        SongLibrary library,
        SqliteSongTraitStore traits,
        IAudioEncoder encoder,
        IAudioTagger tagger,
        TonregalClient tonregal,
        IOptions<TonregalOptions> options,
        CancellationToken cancellationToken)
    {
        if (library.SongDirectory(run, song) is not { } directory || !File.Exists(Path.Combine(directory, "audio.flac")))
        {
            return Results.NotFound();
        }
        if (!options.Value.Configured)
        {
            return Results.Problem(
                title: "Tonregal is not configured (TONREGAL_URL and TONREGAL_API_KEY in ~/.config/tonwerk/tonregal.env).",
                statusCode: StatusCodes.Status501NotImplemented);
        }
        var errors = new Dictionary<string, string[]>();
        // Tonregal files by artist, so it needs one.
        if (string.IsNullOrWhiteSpace(artist))
        {
            errors["artist"] = ["Tonregal files songs by artist."];
        }
        else if (artist.Length > ExportEndpoints.MaxFieldLength)
        {
            errors["artist"] = [$"At most {ExportEndpoints.MaxFieldLength} characters."];
        }
        if (genre?.Length > ExportEndpoints.MaxFieldLength)
        {
            errors["genre"] = [$"At most {ExportEndpoints.MaxFieldLength} characters."];
        }
        CoverImage? image = null;
        if (cover is not null && (image = await ExportEndpoints.ReadCoverAsync(cover, cancellationToken)) is null)
        {
            errors["cover"] = [$"A JPEG or PNG of at most {ExportEndpoints.MaxCoverBytes / 1024 / 1024} MB."];
        }
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }
        if (cover is null && library.CoverOf(run, song) is { } own)
        {
            image = new CoverImage(await File.ReadAllBytesAsync(own.Path, cancellationToken), own.ContentType);
        }

        var temp = Path.Combine(Path.GetTempPath(), $"yueui-tonregal-{Guid.NewGuid():N}.flac");
        try
        {
            // The FLAC as an export makes it, so the library holds what a download would.
            var flac = Path.Combine(directory, "audio.flac");
            if (!await encoder.EncodeAsync(flac, temp, AudioFormat.Flac, 0, cancellationToken))
            {
                File.Copy(flac, temp, overwrite: true);
            }
            var tags = ExportEndpoints.Tags(library, run, song, directory, artist!.Trim(), genre?.Trim() is { Length: > 0 } g ? g : null, image);
            tagger.Write(temp, tags);
            var heard = traits.All().GetValueOrDefault($"{run}/{song}");
            var metadata = new TonregalMetadata(
                $"{run}/{song}", tags.Title, tags.Artist!, tags.Album, tags.Year, tags.Track, tags.Genre, heard?.Tempo, heard?.Key);
            var fileName = $"{LibraryEndpoints.FileName(tags.Title, run)}-{song}.flac";
            var (job, repeated) = await tonregal.SendAsync(temp, "audio/flac", fileName, metadata, image, cancellationToken);

            // Tonregal files it in the background, usually within a second or two; wait a little to say where.
            var deadline = DateTime.UtcNow + options.Value.Wait;
            while (job.State == "matched" && job.Error is null && DateTime.UtcNow < deadline)
            {
                await Task.Delay(options.Value.PollInterval, cancellationToken);
                job = await tonregal.GetAsync(job.Id, cancellationToken);
            }
            return Results.Ok(new TonregalTransfer(job.Id, job.State, job.LibraryPath, job.Error, repeated));
        }
        catch (TonregalException exception)
        {
            // Tonregal's status goes along so the apps can say in their language what to fix (a key that does not match).
            return Results.Problem(
                title: "Tonregal did not take the song.",
                detail: exception.Message,
                statusCode: StatusCodes.Status502BadGateway,
                extensions: exception.Status is { } status ? new Dictionary<string, object?> { ["tonregalStatus"] = status } : null);
        }
        catch (InvalidOperationException exception)
        {
            return Results.Problem(title: "The song could not be prepared.", detail: exception.Message, statusCode: StatusCodes.Status500InternalServerError);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
