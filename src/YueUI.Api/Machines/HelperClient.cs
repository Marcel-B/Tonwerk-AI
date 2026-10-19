using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using YueUI.Api.Memory;

namespace YueUI.Api.Machines;

/// <summary>What a helper can run, by the names <see cref="HelperClient"/> sends as a job's kind.</summary>
public static class Engines
{
    public const string Lyrics = "lyrics";
    public const string Speech = "speech";
    public const string Images = "images";
    public const string Ace = "ace";
    public const string Voices = "voices";
    public const string Stems = "stems";
    public const string Singing = "singing";
}

/// <summary>What a helper says about itself, polled by <see cref="MachineMonitor"/>.</summary>
/// <param name="Holder">The large model its memory holds; null while free.</param>
/// <param name="InUse">Its own YuE worker generates or transcribes.</param>
/// <param name="Engines">What is installed there (<see cref="Machines.Engines"/>).</param>
/// <param name="Ace">ACE-Step's model files there, since a job names them.</param>
public sealed record HelperStatus(
    string Name,
    LargeModel? Holder,
    bool InUse,
    IReadOnlyList<string> Engines,
    Ace.AceModelFiles? Ace = null);

/// <summary>A line of a helper's answer to a run: <c>stage</c>, <c>progress</c>, <c>event</c>, <c>ping</c>, <c>done</c> or <c>failed</c>.</summary>
/// <param name="Share">How far the job is, 0 to 1, where it says.</param>
/// <param name="Name">An <c>event</c>'s name, e.g. the singing's <c>aligned</c>.</param>
/// <param name="Data">An <c>event</c>'s data, or a <c>progress</c>'s extra (Seed-VC's estimate).</param>
/// <param name="Run">A <c>done</c>'s run, whose files are fetched next.</param>
/// <param name="Result">A <c>done</c>'s result, the engine's measurements.</param>
/// <param name="Files">A <c>done</c>'s output files by name.</param>
public sealed record HelperEvent(
    string Type,
    string? Stage = null,
    double? Share = null,
    string? Name = null,
    JsonNode? Data = null,
    string? Run = null,
    JsonNode? Result = null,
    IReadOnlyList<string>? Files = null,
    string? Message = null,
    int? Status = null);

/// <summary>A run done on a helper, its files already copied here.</summary>
/// <param name="Files">The local paths of what came back, in the helper's order.</param>
public sealed record HelperOutcome(JsonNode? Result, IReadOnlyList<string> Files);

/// <summary>A helper could not be reached, refused, or its job failed there.</summary>
public sealed class HelperException(string message, int status = StatusCodes.Status502BadGateway) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// Talks to a helper's <c>/api/helper</c> (<see cref="HelperEndpoints"/>): asks how it is, and runs a job there. A
/// run is one request: the job as JSON and its input files go up as multipart, the answer streams the job's progress
/// as JSON lines and ends with what it wrote, which is fetched next and then forgotten there. Cancelling the request
/// cancels the job on the helper.
/// </summary>
public sealed class HelperClient(IHttpClientFactory httpClients, IOptions<MachineOptions> options, ILogger<HelperClient> logger)
{
    public const string HttpClientName = "helper";

    public const string KeyHeader = "X-Tonwerk-Helper-Key";

    /// <summary>The API's own JSON: camelCase, enums as camelCase strings.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<HelperStatus> StatusAsync(HelperMachine machine, CancellationToken cancellationToken) =>
        await GetJsonAsync<HelperStatus>(machine, "api/helper/status", cancellationToken)
            ?? throw new HelperException($"{machine.Label} gave no status.");

    /// <exception cref="HelperException">It could not be reached or refused.</exception>
    public async Task<T?> GetJsonAsync<T>(HelperMachine machine, string path, CancellationToken cancellationToken)
    {
        using var http = Client(machine);
        using var request = Request(machine, HttpMethod.Get, path);
        using var response = await SendAsync(machine, http, request, cancellationToken);
        await EnsureAsync(machine, response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken);
    }

    /// <summary>Runs a job on <paramref name="machine"/> and copies what it wrote here.</summary>
    /// <param name="kind">One of <see cref="Engines"/>.</param>
    /// <param name="inputs">Files to send, by the names the helper reads them under.</param>
    /// <param name="outputs">Where each file the helper names goes; the rest go into <paramref name="directory"/>.</param>
    /// <param name="directory">Where files <paramref name="outputs"/> does not name go, under their own names; skipped without one.</param>
    /// <param name="progress">Hears every line but pings and the end.</param>
    /// <exception cref="HelperException">It could not be reached, refused, or the job failed there.</exception>
    public async Task<HelperOutcome> RunAsync(
        HelperMachine machine,
        string kind,
        object job,
        IReadOnlyDictionary<string, string> inputs,
        IReadOnlyDictionary<string, string>? outputs,
        string? directory,
        Action<HelperEvent>? progress,
        CancellationToken cancellationToken)
    {
        using var http = Client(machine);
        using var form = new MultipartFormDataContent
        {
            { new StringContent(JsonSerializer.Serialize(job, Json), System.Text.Encoding.UTF8, "application/json"), "job" },
        };
        List<Stream> opened = [];
        try
        {
            foreach (var (name, path) in inputs)
            {
                var stream = File.OpenRead(path);
                opened.Add(stream);
                var content = new StreamContent(stream);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                form.Add(content, name, Path.GetFileName(path));
            }
            using var request = Request(machine, HttpMethod.Post, $"api/helper/runs/{kind}");
            request.Content = form;
            logger.LogInformation("Handing a {Kind} job to {Machine}", kind, machine.Label);
            using var response = await SendAsync(machine, http, request, cancellationToken, HttpCompletionOption.ResponseHeadersRead);
            await EnsureAsync(machine, response, cancellationToken);

            HelperEvent? done = null;
            using (var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken)))
            {
                while (await ReadLineAsync(machine, reader, cancellationToken) is { } line)
                {
                    if (line.Length == 0)
                    {
                        continue;
                    }
                    var read = JsonSerializer.Deserialize<HelperEvent>(line, Json);
                    switch (read?.Type)
                    {
                        case "ping" or null:
                            break;
                        case "done":
                            done = read;
                            break;
                        case "failed":
                            throw new HelperException(read.Message ?? $"The job failed on {machine.Label}.", read.Status ?? StatusCodes.Status500InternalServerError);
                        default:
                            progress?.Invoke(read);
                            break;
                    }
                }
            }
            if (done?.Run is not { } run)
            {
                throw new HelperException($"{machine.Label} stopped answering before the job was done.");
            }

            List<string> files = [];
            try
            {
                foreach (var name in done.Files ?? [])
                {
                    var target = outputs is not null && outputs.TryGetValue(name, out var named) ? named
                        : directory is not null ? Path.Combine(directory, Path.GetFileName(name))
                        : null;
                    if (target is null)
                    {
                        continue;
                    }
                    await DownloadAsync(machine, http, run, name, target, cancellationToken);
                    files.Add(target);
                }
            }
            finally
            {
                await ForgetAsync(machine, http, run);
            }
            return new HelperOutcome(done.Result, files);
        }
        finally
        {
            foreach (var stream in opened)
            {
                await stream.DisposeAsync();
            }
        }
    }

    private async Task DownloadAsync(HelperMachine machine, HttpClient http, string run, string name, string target, CancellationToken cancellationToken)
    {
        using var request = Request(machine, HttpMethod.Get, $"api/helper/runs/{run}/files/{Uri.EscapeDataString(name)}");
        using var response = await SendAsync(machine, http, request, cancellationToken, HttpCompletionOption.ResponseHeadersRead);
        await EnsureAsync(machine, response, cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
        var partial = target + ".part";
        await using (var file = File.Create(partial))
        {
            await response.Content.CopyToAsync(file, cancellationToken);
        }
        File.Move(partial, target, overwrite: true);
    }

    /// <summary>Lets the helper delete the run's files; it clears forgotten ones itself after a while.</summary>
    private async Task ForgetAsync(HelperMachine machine, HttpClient http, string run)
    {
        try
        {
            using var request = Request(machine, HttpMethod.Delete, $"api/helper/runs/{run}");
            using var response = await http.SendAsync(request, CancellationToken.None);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            logger.LogInformation("Could not tell {Machine} to delete the run {Run}: {Message}", machine.Label, run, exception.Message);
        }
    }

    private HttpClient Client(HelperMachine machine)
    {
        var http = httpClients.CreateClient(HttpClientName);
        http.BaseAddress = new Uri(machine.Url.EndsWith('/') ? machine.Url : machine.Url + "/");
        return http;
    }

    private HttpRequestMessage Request(HelperMachine machine, HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        if ((string.IsNullOrWhiteSpace(machine.Key) ? options.Value.ResolveKey() : machine.Key.Trim()) is { } key)
        {
            request.Headers.Add(KeyHeader, key);
        }
        return request;
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HelperMachine machine,
        HttpClient http,
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        try
        {
            return await http.SendAsync(request, completion, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new HelperException($"{machine.Label} does not answer: {exception.Message}", StatusCodes.Status503ServiceUnavailable);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HelperException($"{machine.Label} did not answer in time.", StatusCodes.Status504GatewayTimeout);
        }
    }

    private static async Task<string?> ReadLineAsync(HelperMachine machine, StreamReader reader, CancellationToken cancellationToken)
    {
        try
        {
            return await reader.ReadLineAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException)
        {
            throw new HelperException($"The connection to {machine.Label} broke off: {exception.Message}", StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task EnsureAsync(HelperMachine machine, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        string? detail = null;
        try
        {
            detail = JsonNode.Parse(text)?["detail"]?.GetValue<string>();
        }
        catch (JsonException)
        {
        }
        var status = (int)response.StatusCode;
        throw new HelperException(status switch
        {
            401 => $"{machine.Label} refused the key.",
            404 when detail is null => $"{machine.Label} does not compute for other Macs (Machines:Serve is off there).",
            _ => $"{machine.Label}: {detail ?? $"HTTP {status}"}",
        }, status is 401 or 404 ? StatusCodes.Status502BadGateway : status);
    }
}
