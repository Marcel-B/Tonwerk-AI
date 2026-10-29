using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace YueUI.Api.Images;

/// <summary>
/// Paints through img-o-mat (repository Marcel-B/img-o-mat, <see cref="ImageOptions.Service"/>), the service that runs
/// the image models for every app on the Macs: it keeps the catalogue's rules (content filter, the memory a model
/// needs), queues pictures one at a time and paints them on oi-o-mat. Tonwerk asks with priority 0, so its covers pass
/// other apps' pictures waiting there.
/// </summary>
/// <remarks>
/// <see cref="IImageEngine"/>'s questions are synchronous, so the models' list is cached and refreshed in the
/// background every <see cref="ImageOptions.ServiceRefresh"/>; only the very first question waits for an answer. LoRA
/// files and reference pictures go up once by their SHA-256 (<c>PUT /v1/files/&lt;sha&gt;</c>), so a LoRA used for
/// every cover travels once. Closing the stream cancels the picture there.
/// </remarks>
public sealed class ImgOMatEngine(IHttpClientFactory clients, IOptions<ImageOptions> options, ILogger<ImgOMatEngine> logger) : IImageEngine
{
    public const string HttpClientName = "img-o-mat";

    /// <summary>The name img-o-mat files Tonwerk's pictures under, and the one <see cref="ImageServiceWatch"/> leaves out.</summary>
    public const string AppName = "tonwerk-ai";

    private readonly Lock _gate = new();
    private Dictionary<string, JsonObject>? _models;
    private string? _error;
    private DateTimeOffset _at = DateTimeOffset.MinValue;
    private Task? _refreshing;

    private string Base => options.Value.Service!.TrimEnd('/');

    public bool Installed => Models() is not null;

    public bool TokenFound => Models()?.Values.Any(m => (bool?)m["tokenFound"] == true) == true;

    public string? Unavailable(ImageModel model)
    {
        var models = Models();
        if (models is null)
        {
            return $"img-o-mat ({Base}) does not answer: {_error ?? "no answer yet"}.";
        }
        if (!models.TryGetValue(model.Id, out var offered))
        {
            return $"img-o-mat does not offer {model.Label} ({model.Id}).";
        }
        return (bool?)offered["available"] == true ? null : (string?)offered["unavailable"] ?? $"img-o-mat cannot paint {model.Label}.";
    }

    public bool Downloaded(ImageModel model) => Models()?.GetValueOrDefault(model.Id) is { } offered && (bool?)offered["downloaded"] == true;

    /// <summary>The cached list; refreshed in the background when old, waited for (5 s at most) only the first time.</summary>
    private Dictionary<string, JsonObject>? Models()
    {
        Task? first = null;
        lock (_gate)
        {
            if (DateTimeOffset.UtcNow - _at > options.Value.ServiceRefresh && _refreshing is null)
            {
                _refreshing = RefreshAsync();
            }
            if (_models is null && _error is null)
            {
                first = _refreshing;
            }
        }
        first?.Wait(TimeSpan.FromSeconds(5));
        lock (_gate)
        {
            return _models;
        }
    }

    private async Task RefreshAsync()
    {
        try
        {
            using var client = Client(TimeSpan.FromSeconds(10));
            var answer = JsonNode.Parse(await client.GetStringAsync($"{Base}/v1/models"));
            var models = (answer?["data"]?.AsArray() ?? []).OfType<JsonObject>()
                .Where(m => m["id"] is not null)
                .ToDictionary(m => (string)m["id"]!, m => m);
            lock (_gate)
            {
                _models = models;
                _error = null;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            lock (_gate)
            {
                if (_error is null)
                {
                    logger.LogWarning("img-o-mat at {Url} does not answer: {Message}", Base, exception.Message);
                }
                _models = null;
                _error = exception.Message;
            }
        }
        finally
        {
            lock (_gate)
            {
                _at = DateTimeOffset.UtcNow;
                _refreshing = null;
            }
        }
    }

    public async Task<ImageResult> PaintAsync(ImageJob job, Action<string, double> progress, CancellationToken cancellationToken)
    {
        if (Unavailable(job.Model) is { } reason)
        {
            throw new ImageException(reason, StatusCodes.Status501NotImplemented);
        }
        using var client = Client(options.Value.Timeout);
        progress("loading", 0);
        var loras = new JsonArray();
        foreach (var lora in job.Loras ?? [])
        {
            loras.Add(new JsonObject { ["file"] = await SendFileAsync(client, lora.Path, cancellationToken), ["strength"] = lora.Strength });
        }
        var references = new JsonArray();
        foreach (var reference in job.References ?? [])
        {
            references.Add(new JsonObject
            {
                ["file"] = await SendFileAsync(client, reference.Path, cancellationToken),
                ["role"] = reference.Role,
                ["strength"] = reference.Strength,
            });
        }
        var body = new JsonObject
        {
            ["model"] = job.Model.Id,
            ["prompt"] = job.Prompt,
            ["size"] = $"{job.Width}x{job.Height}",
            ["seed"] = job.Seed,
            ["n"] = 1,
            ["stream"] = true,
            ["creator"] = "Tonwerk-AI",
            ["loras"] = loras,
            ["references"] = references,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Base}/v1/images/generations")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AppName);
        request.Headers.Add("X-Img-Priority", "0");
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new ImageException($"img-o-mat does not answer: {exception.Message}", StatusCodes.Status502BadGateway);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw Failure((int)response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
            }
            logger.LogInformation("Painting with {Model} on img-o-mat (job {Job})", job.Model.Id,
                response.Headers.TryGetValues("X-Img-Job", out var ids) ? ids.FirstOrDefault() : "?");
            ImageResult? result = null;
            await foreach (var (name, data) in EventsAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken))
            {
                switch (name)
                {
                    case "progress":
                        // sending and loading are the model coming up; checking is the content filter after the steps.
                        var stage = (string?)data?["stage"] is "painting" or "checking" ? "painting" : "loading";
                        progress(stage, (double?)data?["fraction"] ?? 0);
                        break;
                    case "image":
                        await File.WriteAllBytesAsync(job.Output, Convert.FromBase64String((string?)data?["b64_json"] ?? ""), cancellationToken);
                        result = new ImageResult(
                            (double?)data?["loadSeconds"], (double?)data?["paintSeconds"], (double?)data?["peakMemoryGb"], (string?)data?["revised_prompt"]);
                        break;
                    case "error":
                        throw Failure((int?)data?["error"]?["code"] ?? 500, data?.ToJsonString() ?? "");
                }
            }
            return result ?? throw new ImageException("img-o-mat ended without a picture.", StatusCodes.Status502BadGateway);
        }
    }

    /// <summary>img-o-mat's refusal (OpenAI's error form) as this app's: 422 the content filter, 501 a model it cannot paint.</summary>
    public static ImageException Failure(int status, string text)
    {
        string? message = null;
        try
        {
            message = (string?)JsonNode.Parse(text)?["error"]?["message"];
        }
        catch (System.Text.Json.JsonException)
        {
        }
        message ??= string.IsNullOrWhiteSpace(text) ? $"img-o-mat answered {status}." : text.Trim();
        return new ImageException(message, status is 400 or 422 or 501 ? status : StatusCodes.Status502BadGateway);
    }

    /// <summary>Uploads a file unless img-o-mat has it already; answers its SHA-256, by which the picture names it.</summary>
    private async Task<string> SendFileAsync(HttpClient client, string path, CancellationToken cancellationToken)
    {
        string digest;
        await using (var file = File.OpenRead(path))
        {
            digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken));
        }
        var url = $"{Base}/v1/files/{digest}";
        using (var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, url), cancellationToken))
        {
            if (head.IsSuccessStatusCode)
            {
                return digest;
            }
        }
        await using var upload = File.OpenRead(path);
        using var put = await client.PutAsync(url, new StreamContent(upload), cancellationToken);
        if (!put.IsSuccessStatusCode)
        {
            throw Failure((int)put.StatusCode, await put.Content.ReadAsStringAsync(cancellationToken));
        }
        return digest;
    }

    /// <summary>Server-sent events as name and JSON data; comments (img-o-mat's pings) are skipped.</summary>
    public static async IAsyncEnumerable<(string Name, JsonNode? Data)> EventsAsync(
        Stream stream, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var name = "message";
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    yield return (name, JsonNode.Parse(data.ToString()));
                }
                name = "message";
                data.Clear();
            }
            else if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                name = line[6..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                data.Append(line[5..].TrimStart());
            }
        }
    }

    private HttpClient Client(TimeSpan timeout)
    {
        var client = clients.CreateClient(HttpClientName);
        client.Timeout = timeout;
        return client;
    }
}
