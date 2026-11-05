using Microsoft.Extensions.Options;
using YueUI.Api.OnkelBummel;

namespace YueUI.Api;

/// <summary>Whether finished jobs go to Onkel Bummel, for the settings.</summary>
/// <param name="Configured">Address and token are set.</param>
/// <param name="Enabled">Not switched off; messages go out when this and <see cref="Configured"/> hold.</param>
/// <param name="Url">The address in use; the token itself never leaves the server.</param>
/// <param name="TokenSet">A token is set.</param>
/// <param name="Topic">Where the messages go.</param>
/// <param name="Link">Tonwerk's address that a message opens, if any.</param>
/// <param name="Editable">The settings can change these: there is an env file and no environment variable that would
/// win over it.</param>
public sealed record OnkelBummelStatus(bool Configured, bool Enabled, string? Url, bool TokenSet, string Topic, string? Link, bool Editable);

/// <summary>From the settings. An empty token keeps the one set, an empty address turns the messages off for good, an
/// empty topic means <c>tonwerk</c>.</summary>
public sealed record OnkelBummelSettings(string? Url, string? Token, string? Topic, string? Link, bool Enabled = true);

public static class OnkelBummelEndpoints
{
    public static RouteGroupBuilder MapOnkelBummelEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/onkel-bummel", (IOptions<OnkelBummelOptions> options) => StatusOf(options.Value))
            .WithName("GetOnkelBummelStatus")
            .Produces<OnkelBummelStatus>(StatusCodes.Status200OK);

        api.MapPut("/onkel-bummel", Save)
            .WithName("SetOnkelBummelSettings")
            .Produces<OnkelBummelStatus>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        api.MapPost("/onkel-bummel/test", (OnkelBummelClient client, CancellationToken cancellationToken) => client.TestAsync(cancellationToken))
            .WithName("TestOnkelBummel")
            .Produces<OnkelBummelCheck>(StatusCodes.Status200OK);
        return api;
    }

    private static OnkelBummelStatus StatusOf(OnkelBummelOptions options) => new(
        options.Configured,
        !options.Off,
        options.BaseUri?.ToString().TrimEnd('/'),
        options.ResolvedToken is not null,
        options.ResolvedTopic,
        options.LinkUri?.ToString().TrimEnd('/'),
        !string.IsNullOrWhiteSpace(options.EnvFile) && !OnkelBummelOptions.SetByEnvironment);

    private static IResult Save(OnkelBummelSettings settings, IOptions<OnkelBummelOptions> options)
    {
        var current = options.Value;
        if (string.IsNullOrWhiteSpace(current.EnvFile) || OnkelBummelOptions.SetByEnvironment)
        {
            return Results.Problem(
                title: "Onkel Bummel is set by environment variables on the server, which the settings cannot change.",
                statusCode: StatusCodes.Status409Conflict);
        }
        var url = settings.Url?.Trim() ?? "";
        var token = settings.Token?.Trim() ?? "";
        var topic = settings.Topic?.Trim() ?? "";
        var link = settings.Link?.Trim() ?? "";
        var errors = new Dictionary<string, string[]>();
        if (url.Length > 0 && !IsHttp(url))
        {
            errors["url"] = ["An http or https address."];
        }
        if (link.Length > 0 && !IsHttp(link))
        {
            errors["link"] = ["An http or https address."];
        }
        if (token.Any(char.IsWhiteSpace) || token.Contains('"') || token.Contains('\''))
        {
            errors["token"] = ["A token without spaces or quotes."];
        }
        // Onkel Bummel's topics are names in a URL path.
        if (topic.Length > 64 || !topic.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
        {
            errors["topic"] = ["Letters, digits, '-', '_' or '.', at most 64."];
        }
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }
        var values = new Dictionary<string, string?>
        {
            [OnkelBummelOptions.UrlVariable] = url.Length > 0 ? url.TrimEnd('/') : null,
            [OnkelBummelOptions.TopicVariable] = topic.Length > 0 && topic != OnkelBummelOptions.DefaultTopic ? topic : null,
            [OnkelBummelOptions.LinkVariable] = link.Length > 0 ? link.TrimEnd('/') : null,
            [OnkelBummelOptions.OffVariable] = settings.Enabled ? null : "1",
        };
        if (token.Length > 0)
        {
            values[OnkelBummelOptions.TokenVariable] = token;
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

    private static bool IsHttp(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
}
