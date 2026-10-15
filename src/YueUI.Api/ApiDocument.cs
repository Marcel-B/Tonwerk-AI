using Microsoft.OpenApi;

namespace YueUI.Api;

/// <summary>
/// The OpenAPI description at <c>/api/openapi</c>, the contract the native apps generate their client from
/// (<c>apple/TonwerkKit</c>, checked against it by <c>OpenApiTests</c>). Every endpoint names its operation
/// (<c>WithName</c>) and declares what it answers (<c>Produces</c>), since an <see cref="IResult"/> says nothing about
/// its body.
/// </summary>
public static class ApiDocument
{
    public const string Path = "/api/openapi";

    public static IServiceCollection AddApiDocument(this IServiceCollection services) => services.AddOpenApi(options =>
    {
        options.AddSchemaTransformer((schema, _, _) =>
        {
            NumbersOnly(schema);
            return Task.CompletedTask;
        });
        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info = new OpenApiInfo
            {
                Title = "Tonwerk",
                Version = "1",
                Description = "The server behind Tonwerk's web page and apps. No login: it is reached through Tailscale only.",
            };
            // One tag per assembly says nothing; the generated clients would only carry it around.
            document.Tags?.Clear();
            foreach (var path in (document.Paths ?? []).Values)
            {
                // HEAD only answers uptime monitors, and would share its GET's name.
                path.Operations?.Remove(HttpMethod.Head);
            }
            foreach (var operation in (document.Paths ?? []).Values.SelectMany(path => path.Operations?.Values ?? Enumerable.Empty<OpenApiOperation>()))
            {
                operation.Tags?.Clear();
                FlattenForm(operation);
            }
            return Task.CompletedTask;
        });
    });

    /// <summary>
    /// The web defaults read numbers from strings too, so ASP.NET describes every number as "integer or string" with a
    /// pattern. The server only ever writes numbers, and Swift's generator turns a schema of two types into an untyped
    /// value container, so the string is left out.
    /// </summary>
    private static void NumbersOnly(OpenApiSchema schema)
    {
        if (schema.Type is { } type && (type & (JsonSchemaType.Integer | JsonSchemaType.Number)) != 0)
        {
            schema.Type = type & ~JsonSchemaType.String;
            schema.Pattern = null;
        }
    }

    /// <summary>
    /// A body that is not JSON (audio, pictures, ZIPs, MIDI, text files): described as binary, so a generated client
    /// hands it over as bytes. Without a type, <c>Produces</c> would leave the content out altogether.
    /// </summary>
    public static RouteHandlerBuilder ProducesFile(this RouteHandlerBuilder builder, string contentType, params string[] moreContentTypes) =>
        builder.Produces(StatusCodes.Status200OK, typeof(Stream), contentType, moreContentTypes);

    /// <summary>
    /// ASP.NET describes a form of several <c>[FromForm]</c> parameters as an <c>allOf</c> of one-property objects;
    /// Swift's OpenAPI generator only understands a multipart body that is one object, so they are merged.
    /// </summary>
    private static void FlattenForm(OpenApiOperation operation)
    {
        if (operation.RequestBody?.Content is not { } content
            || !content.TryGetValue("multipart/form-data", out var form)
            || form.Schema is not OpenApiSchema { AllOf: { Count: > 0 } parts } schema
            || parts.Any(part => part is not OpenApiSchema { Properties: not null }))
        {
            return;
        }
        var properties = new Dictionary<string, IOpenApiSchema>();
        foreach (var part in parts.Cast<OpenApiSchema>())
        {
            foreach (var (name, property) in part.Properties!)
            {
                properties[name] = property;
            }
        }
        schema.AllOf = null;
        schema.Type = JsonSchemaType.Object;
        schema.Properties = properties;
    }
}
