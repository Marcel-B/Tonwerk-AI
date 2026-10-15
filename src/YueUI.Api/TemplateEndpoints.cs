using System.Text.Json;
using YueUI.Api.Data;

namespace YueUI.Api;

/// <param name="Settings">A JSON object; required when creating, optional when renaming.</param>
public sealed record TemplateRequest(string? Name, JsonElement? Settings);

/// <summary>
/// Prompt templates for the create page: list them, save one under a new name, overwrite or rename one, delete one.
/// The settings are the form's business; the server checks only that they are an object and the name is free.
/// </summary>
public static class TemplateEndpoints
{
    public const int MaxNameLength = 100;

    /// <summary>A style is a few hundred characters; this leaves room for every parameter without taking a whole song.</summary>
    public const int MaxSettingsLength = 20_000;

    public static RouteGroupBuilder MapTemplateEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/templates", (SqlitePromptTemplateStore store) => store.List())
            .WithName("ListTemplates");

        api.MapPost("/templates", (TemplateRequest request, SqlitePromptTemplateStore store) =>
        {
            var errors = new Dictionary<string, string[]>();
            var name = ValidName(request.Name, errors);
            var settings = ValidSettings(request.Settings, required: true, errors);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }
            return store.Create(name!, settings!.Value) is { } created
                ? Results.Created($"/api/templates/{created.Id}", created)
                : NameTaken(name!);
        })
            .WithName("CreateTemplate")
            .Produces<PromptTemplate>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapPut("/templates/{id:long}", (long id, TemplateRequest request, SqlitePromptTemplateStore store) =>
        {
            var errors = new Dictionary<string, string[]>();
            var name = request.Name is null ? null : ValidName(request.Name, errors);
            var settings = ValidSettings(request.Settings, required: false, errors);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }
            return store.Update(id, name, settings) switch
            {
                TemplateChange.Changed => Results.Ok(store.Find(id)),
                TemplateChange.NameTaken => NameTaken(name!),
                _ => Results.NotFound(),
            };
        })
            .WithName("UpdateTemplate")
            .Produces<PromptTemplate>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapDelete("/templates/{id:long}", (long id, SqlitePromptTemplateStore store) =>
            store.Delete(id) ? Results.NoContent() : Results.NotFound())
            .WithName("DeleteTemplate")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);
        return api;
    }

    private static string? ValidName(string? name, Dictionary<string, string[]> errors)
    {
        if (name?.Trim() is { Length: > 0 and <= MaxNameLength } trimmed)
        {
            return trimmed;
        }
        errors["name"] = [$"A template needs a name of 1 to {MaxNameLength} characters."];
        return null;
    }

    private static JsonElement? ValidSettings(JsonElement? settings, bool required, Dictionary<string, string[]> errors)
    {
        // An explicit null in the JSON arrives as an element of kind Null rather than as a missing value.
        if (settings is null or { ValueKind: JsonValueKind.Null })
        {
            if (required)
            {
                errors["settings"] = ["A template needs its settings."];
            }
            return null;
        }
        if (settings.Value.ValueKind != JsonValueKind.Object)
        {
            errors["settings"] = ["The settings must be a JSON object."];
            return null;
        }
        if (settings.Value.GetRawText().Length > MaxSettingsLength)
        {
            errors["settings"] = [$"The settings may be at most {MaxSettingsLength} characters of JSON."];
            return null;
        }
        return settings;
    }

    private static IResult NameTaken(string name) =>
        Results.Problem(title: "Name taken", detail: $"There is already a template named '{name}'.", statusCode: StatusCodes.Status409Conflict);
}
