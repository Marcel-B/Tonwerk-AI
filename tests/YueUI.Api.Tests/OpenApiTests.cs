using System.Text.Json;
using System.Text.Json.Nodes;

namespace YueUI.Api.Tests;

/// <summary>
/// The OpenAPI description is the native apps' contract: their Swift client is generated from the copy in
/// <c>apple/TonwerkKit</c>. Run with <c>TONWERK_UPDATE_OPENAPI=1</c> to write that copy anew after changing an endpoint.
/// </summary>
public sealed class OpenApiTests : IDisposable
{
    private static readonly string[] Methods = ["get", "put", "post", "delete", "patch", "head"];

    private readonly TestApp _app = new();

    [Fact]
    public async Task Every_operation_has_a_name_of_its_own()
    {
        var operations = Operations(await Document()).ToList();

        var unnamed = operations.Where(o => o.Operation["operationId"] is null).Select(o => o.Name).ToList();
        Assert.True(unnamed.Count == 0, "Without WithName: " + string.Join(", ", unnamed));
        var twice = operations.GroupBy(o => (string)o.Operation["operationId"]!).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(twice.Count == 0, "Named twice: " + string.Join(", ", twice));
    }

    [Fact]
    public async Task Every_success_says_what_it_answers()
    {
        List<string> vague = [];
        foreach (var (name, operation) in Operations(await Document()))
        {
            var responses = operation["responses"]!.AsObject();
            if (!responses.Any(r => r.Key.StartsWith('2')))
            {
                vague.Add($"{name}: no success");
            }
            // 200 and 201 without a body is what ASP.NET writes when nothing was declared.
            vague.AddRange(responses
                .Where(r => r.Key is "200" or "201" && r.Value?["content"] is null)
                .Select(r => $"{name}: {r.Key} without content"));
        }
        Assert.True(vague.Count == 0, string.Join(Environment.NewLine, vague));
    }

    [Fact]
    public async Task The_apps_copy_is_the_servers_description()
    {
        var served = (await Document()).ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        var copy = Path.Combine(RepositoryRoot(), "apple", "TonwerkKit", "Sources", "TonwerkAPI", "openapi.json");

        if (Environment.GetEnvironmentVariable("TONWERK_UPDATE_OPENAPI") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            await File.WriteAllTextAsync(copy, served);
        }

        Assert.True(File.Exists(copy), $"{copy} is missing; run the tests with TONWERK_UPDATE_OPENAPI=1.");
        Assert.True(
            served == (await File.ReadAllTextAsync(copy)).ReplaceLineEndings("\n"),
            "The API changed; run the tests with TONWERK_UPDATE_OPENAPI=1 and commit apple/TonwerkKit's openapi.json.");
    }

    private async Task<JsonNode> Document() =>
        JsonNode.Parse(await _app.CreateClient().GetStringAsync("/api/openapi"))!;

    private static IEnumerable<(string Name, JsonNode Operation)> Operations(JsonNode document) =>
        document["paths"]!.AsObject().SelectMany(path => Methods
            .Where(method => path.Value?[method] is not null)
            .Select(method => ($"{method.ToUpperInvariant()} {path.Key}", path.Value![method]!)));

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "YueUI.slnx")))
            {
                return directory.FullName;
            }
        }
        throw new InvalidOperationException("YueUI.slnx not found above " + AppContext.BaseDirectory);
    }

    public void Dispose() => _app.Dispose();
}
