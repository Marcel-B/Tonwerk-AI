using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using YueUI.Api.Worker;

namespace YueUI.Api.Lyrics;

/// <summary>
/// LM Studio's server as everything that writes with a local language model uses it (lyrics drafts, the writing
/// page, sketches planned locally): started when it does not answer, a model loaded with a context of our choosing
/// rather than LM Studio's just-in-time default, and unloaded again by whoever loaded it.
/// </summary>
public sealed class LmStudio(
    IHttpClientFactory httpClients,
    IOptions<LyricsOptions> options,
    ILmStudioStarter starter,
    WorkerHost worker,
    ILogger<LmStudio> logger)
{
    /// <summary>
    /// What LM Studio has downloaded, loaded or not, without embedding models. Starts the server, which loads no model.
    /// </summary>
    /// <exception cref="LyricsUnavailableException">LM Studio could not be reached or started.</exception>
    public async Task<List<LyricsModel>> ListAsync(CancellationToken cancellationToken)
    {
        using var http = CreateClient();
        await EnsureServerAsync(http, cancellationToken);
        return await NativeModelsAsync(http, cancellationToken) ?? await OpenAiModelsAsync(http, cancellationToken);
    }

    /// <summary>
    /// Asks <see cref="LoadedModel"/> through the OpenAI-compatible chat endpoint, streamed: a long answer at a few
    /// tokens a second (Gemma 4 31B writes about eight) takes many minutes, and the client's timeout only covers the
    /// wait for the first byte of a streamed answer. <paramref name="onPiece"/> hears every piece as it comes, with
    /// whether it is reasoning. <c>model</c> and <c>stream</c> are set here.
    /// </summary>
    /// <exception cref="LyricsUnavailableException">LM Studio refused or stopped answering.</exception>
    public static async Task<ChatAnswer> StreamChatAsync(
        LoadedModel loaded, JsonObject request, Action<string, bool>? onPiece, CancellationToken cancellationToken)
    {
        request["model"] = loaded.Instance;
        request["stream"] = true;
        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions") { Content = Body(request) };
        HttpResponseMessage response;
        try
        {
            response = await loaded.Http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new LyricsUnavailableException($"LM Studio stopped answering: {exception.Message}");
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var json = await ReadAsync(response, cancellationToken);
                throw new LyricsUnavailableException($"LM Studio refused ({(int)response.StatusCode}): {ErrorOf(json)}");
            }
            var content = new System.Text.StringBuilder();
            var reasoning = new System.Text.StringBuilder();
            string? finish = null;
            try
            {
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken));
                while (await reader.ReadLineAsync(cancellationToken) is { } line)
                {
                    if (!line.StartsWith("data:", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    var data = line[5..].Trim();
                    if (data == "[DONE]")
                    {
                        break;
                    }
                    JsonNode? chunk;
                    try
                    {
                        chunk = JsonNode.Parse(data);
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        continue;
                    }
                    if (chunk?["error"] is { } error)
                    {
                        throw new LyricsUnavailableException($"LM Studio: {ErrorOf(new JsonObject { ["error"] = error.DeepClone() })}");
                    }
                    var choice = chunk?["choices"]?[0];
                    finish = choice?["finish_reason"]?.ToString() ?? finish;
                    var delta = choice?["delta"];
                    // LM Studio hands reasoning apart as reasoning_content (or reasoning) when its setting says so.
                    if ((delta?["reasoning_content"] ?? delta?["reasoning"]) is JsonValue thought && thought.ToString() is { Length: > 0 } piece)
                    {
                        reasoning.Append(piece);
                        onPiece?.Invoke(piece, true);
                    }
                    if (delta?["content"] is JsonValue text && text.ToString() is { Length: > 0 } said)
                    {
                        content.Append(said);
                        onPiece?.Invoke(said, false);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or HttpRequestException)
            {
                throw new LyricsUnavailableException($"LM Studio stopped answering: {exception.Message}");
            }
            return new ChatAnswer(content.ToString(), reasoning.ToString(), finish);
        }
    }

    /// <summary>
    /// Asks <see cref="LoadedModel"/> through LM Studio's native chat API, streamed, the only one of its APIs that
    /// takes <c>reasoning</c>. It takes no earlier answers either, so a conversation goes in as text (see
    /// <c>WritingDesk.Transcript</c>). Nothing is stored in LM Studio.
    /// </summary>
    /// <returns>The answer, or null if LM Studio refused the request (a setting the model does not support, a version
    /// without this API), so that the caller asks the OpenAI-compatible way instead.</returns>
    /// <exception cref="LyricsUnavailableException">LM Studio failed or stopped answering.</exception>
    public static async Task<ChatAnswer?> StreamNativeChatAsync(
        LoadedModel loaded, string systemPrompt, string input, double temperature, int maxTokens, LyricsReasoning reasoning,
        Action<string, bool>? onPiece, ILogger logger, CancellationToken cancellationToken)
    {
        var request = new JsonObject
        {
            ["model"] = loaded.Instance,
            ["system_prompt"] = systemPrompt,
            ["input"] = input,
            ["temperature"] = temperature,
            ["max_output_tokens"] = maxTokens,
            ["reasoning"] = reasoning.ToString().ToLowerInvariant(),
            ["store"] = false,
            ["stream"] = true,
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, "api/v1/chat") { Content = Body(request) };
        HttpResponseMessage response;
        try
        {
            response = await loaded.Http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new LyricsUnavailableException($"LM Studio stopped answering: {exception.Message}");
        }
        using (response)
        {
            if ((int)response.StatusCode is >= 400 and < 500)
            {
                var refused = await ReadAsync(response, cancellationToken);
                logger.LogWarning("LM Studio refused reasoning {Reasoning} for {Model} ({Status}: {Error}); asking without it",
                    request["reasoning"], loaded.Model, (int)response.StatusCode, ErrorOf(refused));
                return null;
            }
            if (!response.IsSuccessStatusCode)
            {
                var json = await ReadAsync(response, cancellationToken);
                throw new LyricsUnavailableException($"LM Studio refused ({(int)response.StatusCode}): {ErrorOf(json)}");
            }
            var content = new System.Text.StringBuilder();
            var reasoningText = new System.Text.StringBuilder();
            string? error = null;
            var ended = false;
            try
            {
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken));
                while (!ended && await reader.ReadLineAsync(cancellationToken) is { } line)
                {
                    if (!line.StartsWith("data:", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    JsonNode? data;
                    try
                    {
                        data = JsonNode.Parse(line[5..].Trim());
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        continue;
                    }
                    // Every event names itself in its data as well as in its event: line.
                    switch (data?["type"]?.ToString())
                    {
                        case "reasoning.delta" when data["content"]?.ToString() is { Length: > 0 } piece:
                            reasoningText.Append(piece);
                            onPiece?.Invoke(piece, true);
                            break;
                        case "message.delta" when data["content"]?.ToString() is { Length: > 0 } said:
                            content.Append(said);
                            onPiece?.Invoke(said, false);
                            break;
                        // An error does not end the stream; chat.end still follows with what was written.
                        case "error":
                            error = data["error"]?["message"]?.ToString() ?? data["error"]?.ToString();
                            break;
                        case "chat.end":
                            ended = true;
                            break;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or HttpRequestException)
            {
                throw new LyricsUnavailableException($"LM Studio stopped answering: {exception.Message}");
            }
            if (content.Length == 0 && error is not null)
            {
                throw new LyricsUnavailableException($"LM Studio: {error}");
            }
            return new ChatAnswer(content.ToString(), reasoningText.ToString(), null);
        }
    }

    /// <summary>
    /// Stops an idle YuE worker (24 or 32 GB do not hold YuE2 beside a large text model), makes sure the server
    /// answers and loads <paramref name="model"/>. The caller holds <see cref="Memory.LargeModel.Lyrics"/> before.
    /// Disposing the result unloads the model if it was loaded here.
    /// </summary>
    public async Task<LoadedModel> OpenAsync(string model, int contextLength, CancellationToken cancellationToken)
    {
        var http = CreateClient();
        try
        {
            if (worker.Info().Status != WorkerStatus.Stopped)
            {
                logger.LogInformation("Stopping the idle YuE worker to make room for {Model}", model);
                await worker.ShutdownWorkerAsync();
            }
            await EnsureServerAsync(http, cancellationToken);
            var (instance, loadedHere, context) = await LoadAsync(
                http, model, contextLength, Math.Min(contextLength, options.Value.MinContextLength), cancellationToken);
            return new LoadedModel(this, http, model, instance, loadedHere, context);
        }
        catch
        {
            http.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Loads the model with the given context instead of leaving it to LM Studio's
    /// just-in-time loading, whose context can be the model's maximum.
    /// </summary>
    /// <remarks>
    /// LM Studio's guardrails refuse a load whose estimate (weights plus the cache for the whole context) exceeds the
    /// free memory. A dense model loads less easily than Gemma 4's mixture of experts: Qwen 3.8 27B needs all its
    /// weights and a cache of several GB for 30000 tokens, which on 24 GB is refused as soon as a few apps are open.
    /// The guardrail is right to refuse (it guards against the swap freeze), so it is not switched off; instead what
    /// is in the way goes first: other models still loaded in LM Studio, then half the context each time down to
    /// <paramref name="minContextLength"/>. The draft then has less room for reasoning, which is still
    /// better than none.
    /// </remarks>
    /// <returns>The instance to address, whether it was loaded here (and so is unloaded after), and its context.</returns>
    internal async Task<(string Instance, bool LoadedHere, int Context)> LoadAsync(HttpClient http, string model, int contextLength, int minContextLength, CancellationToken cancellationToken)
    {
        var loaded = await LoadedInstancesAsync(http, cancellationToken);
        if (loaded.FirstOrDefault(i => i.Model == model) is { Instance: { } instance })
        {
            logger.LogInformation("Using {Instance}, which is already loaded in LM Studio", instance);
            return (instance, false, contextLength);
        }

        var context = contextLength;
        var othersUnloaded = false;
        while (true)
        {
            var (loadedInstance, loadedContext, refusal) = await TryLoadAsync(http, model, context, cancellationToken);
            if (loadedInstance is not null)
            {
                return (loadedInstance, true, loadedContext);
            }
            if (!refusal!.Contains("insufficient system resources", StringComparison.OrdinalIgnoreCase))
            {
                throw new LyricsUnavailableException($"LM Studio could not load {model}: {refusal}");
            }
            if (!othersUnloaded && loaded.Count > 0)
            {
                othersUnloaded = true;
                foreach (var other in loaded)
                {
                    logger.LogInformation("Unloading {Instance} to make room for {Model}", other.Instance, model);
                    await UnloadAsync(http, other.Instance);
                }
                continue;
            }
            var smaller = Math.Max(minContextLength, context / 2);
            if (smaller >= context)
            {
                throw new LyricsUnavailableException($"LM Studio could not load {model}, not even with a context of {context} tokens; close other apps or pick a smaller model. {refusal}");
            }
            logger.LogInformation("LM Studio refused {Model} with a context of {Context} tokens for lack of memory; trying {Smaller}", model, context, smaller);
            context = smaller;
        }
    }

    /// <returns>The instance and its context if LM Studio loaded the model, else its refusal.</returns>
    internal async Task<(string? Instance, int Context, string? Refusal)> TryLoadAsync(HttpClient http, string model, int context, CancellationToken cancellationToken)
    {
        var request = new JsonObject
        {
            ["model"] = model,
            ["context_length"] = context,
            ["echo_load_config"] = true,
        };
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync("api/v1/models/load", Body(request), cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new LyricsUnavailableException($"LM Studio stopped answering while loading {model}: {exception.Message}");
        }
        using (response)
        {
            var json = await ReadAsync(response, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // E.g. LM Studio's guardrails: "Model loading was stopped due to insufficient system resources …".
                return (null, 0, $"({(int)response.StatusCode}) {ErrorOf(json)}");
            }
            var instance = (json?["instance_id"] as JsonValue)?.GetValue<string>() ?? model;
            // The answer is sized by the context asked for, not by the one LM Studio echoes: on the Mac it echoed
            // 4096 for Gemma 4 loaded with 30000, and drafts that had worked ran out of tokens while thinking.
            var echoed = json?["load_config"]?["context_length"]?.ToString();
            logger.LogInformation("LM Studio loaded {Instance} with a context of {Context} tokens (echoed: {Echoed}) in {Seconds} s",
                instance, context, echoed ?? "?", json?["load_time_seconds"]?.ToString() ?? "?");
            return (instance, context, null);
        }
    }

    /// <summary>
    /// LM Studio's own list, with names and sizes: the size is what decides whether a model fits beside the apps on
    /// 24 GB. Null for a server without it (another OpenAI-compatible one).
    /// </summary>
    internal static async Task<List<LyricsModel>?> NativeModelsAsync(HttpClient http, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync("api/v1/models", cancellationToken);
            if (!response.IsSuccessStatusCode || await ReadAsync(response, cancellationToken) is not JsonObject { } json
                || json["models"] is not JsonArray models)
            {
                return null;
            }
            return [.. models.OfType<JsonObject>()
                .Where(m => m["key"] is JsonValue && m["type"]?.ToString() != "embedding")
                .Select(m => new LyricsModel(
                    m["key"]!.ToString(),
                    m["display_name"]?.ToString() is { Length: > 0 } name ? name : m["key"]!.ToString(),
                    (m["size_bytes"] as JsonValue)?.TryGetValue<long>(out var size) == true ? size : null,
                    (m["loaded_instances"] as JsonArray)?.Count > 0,
                    (m["capabilities"]?["vision"] as JsonValue)?.TryGetValue<bool>(out var vision) == true ? vision : null))];
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>The OpenAI-compatible list: ids only. LM Studio lists downloaded models there too.</summary>
    internal static async Task<List<LyricsModel>> OpenAiModelsAsync(HttpClient http, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync("v1/models", cancellationToken);
            var json = await ReadAsync(response, cancellationToken);
            return [.. ((json?["data"] as JsonArray)?.OfType<JsonObject>() ?? [])
                .Select(m => m["id"]?.ToString())
                .OfType<string>()
                .Where(id => !id.Contains("embed", StringComparison.OrdinalIgnoreCase))
                .Select(id => new LyricsModel(id, id, null, false, null))];
        }
        catch (HttpRequestException)
        {
            return [];
        }
    }

    internal HttpClient CreateClient()
    {
        var settings = options.Value;
        var http = httpClients.CreateClient(LyricsWriter.HttpClientName);
        http.BaseAddress = new Uri(settings.BaseUrl.TrimEnd('/') + "/");
        if (!string.IsNullOrWhiteSpace(settings.ApiToken))
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiToken);
        }
        return http;
    }

    /// <summary>The language models in memory, from LM Studio's model list; empty if none or unknown.</summary>
    internal static async Task<List<(string Model, string Instance)>> LoadedInstancesAsync(HttpClient http, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync("api/v1/models", cancellationToken);
            var json = await ReadAsync(response, cancellationToken);
            return [.. ((json?["models"] as JsonArray)?.OfType<JsonObject>() ?? [])
                .Where(m => m["key"] is JsonValue && m["type"]?.ToString() != "embedding")
                .SelectMany(m => ((m["loaded_instances"] as JsonArray)?.OfType<JsonObject>() ?? [])
                    .Select(i => i["id"]?.ToString())
                    .OfType<string>()
                    .Select(id => (m["key"]!.ToString(), id)))];
        }
        catch (HttpRequestException)
        {
            return [];
        }
    }

    internal async Task EnsureServerAsync(HttpClient http, CancellationToken cancellationToken)
    {
        if (await AnswersAsync(http, cancellationToken))
        {
            return;
        }
        if (!await starter.StartAsync(cancellationToken))
        {
            throw new LyricsUnavailableException(
                $"LM Studio does not answer at {options.Value.BaseUrl}, and its command line tool was not found at {options.Value.LmsPath} to start it.");
        }
        if (!await AnswersAsync(http, cancellationToken))
        {
            throw new LyricsUnavailableException($"LM Studio was started but does not answer at {options.Value.BaseUrl}.");
        }
    }

    internal static async Task<bool> AnswersAsync(HttpClient http, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var response = await http.GetAsync("v1/models", timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return false;
        }
    }

    internal static async Task<JsonNode?> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            return JsonNode.Parse(body);
        }
        catch (System.Text.Json.JsonException)
        {
            return body.Length > 0 ? JsonValue.Create(body) : null;
        }
    }

    /// <summary>LM Studio names the problem, e.g. a model id it does not know or too little memory.</summary>
    internal static string ErrorOf(JsonNode? json) => json switch
    {
        JsonObject { } body when body["error"] is JsonObject error => error["message"]?.ToString() ?? error.ToJsonString(),
        JsonObject { } body when body["error"] is JsonNode error => error.ToString(),
        JsonNode node => node.ToString(),
        null => "no details",
    };

    internal static string Truncate(string text, int length) => text.Length <= length ? text : text[..length] + " …";

    /// <summary>Best effort; a model loaded through the API has no idle TTL, so a failure is logged as a warning.</summary>
    internal async Task UnloadAsync(HttpClient http, string model)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var response = await http.PostAsync("api/v1/models/unload", Body(new JsonObject { ["instance_id"] = model }), timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("LM Studio did not unload {Model} ({Status}); it stays in memory until unloaded in LM Studio", model, (int)response.StatusCode);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            logger.LogWarning("LM Studio did not unload {Model} ({Message}); it stays in memory until unloaded in LM Studio", model, exception.Message);
        }
    }

    /// <summary>With a Content-Length: JsonContent would stream it chunked, which not every local server accepts.</summary>
    internal static StringContent Body(JsonObject json) => new(json.ToJsonString(), System.Text.Encoding.UTF8, "application/json");

}

/// <param name="Content">The answer, with <c>&lt;think&gt;</c> where the model writes its reasoning inline.</param>
/// <param name="Reasoning">The reasoning LM Studio handed apart.</param>
/// <param name="FinishReason"><c>stop</c>, or <c>length</c> when the tokens ran out.</param>
public sealed record ChatAnswer(string Content, string Reasoning, string? FinishReason);

/// <summary>A model in LM Studio's memory, unloaded on disposal if <see cref="LoadedHere"/>.</summary>
public sealed class LoadedModel(LmStudio lmStudio, HttpClient http, string model, string instance, bool loadedHere, int context) : IAsyncDisposable
{
    public HttpClient Http => http;

    /// <summary>The key the model was asked by.</summary>
    public string Model => model;

    /// <summary>The instance to address in requests.</summary>
    public string Instance => instance;

    public bool LoadedHere => loadedHere;

    /// <summary>The context it was loaded with (or asked for, when it was loaded already).</summary>
    public int Context => context;

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }
        if (loadedHere)
        {
            await lmStudio.UnloadAsync(http, instance);
        }
        http.Dispose();
    }
}
