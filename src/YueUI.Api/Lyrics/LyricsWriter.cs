using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using YueUI.Api.Memory;
using YueUI.Api.Worker;

namespace YueUI.Api.Lyrics;

/// <summary>LM Studio could not be reached or started, or it refused the request.</summary>
public sealed class LyricsUnavailableException(string message) : Exception(message);

/// <summary>Another draft or a song is in progress; there is memory for one model at a time.</summary>
public sealed class LyricsBusyException(string message) : Exception(message);

/// <summary>
/// Drafts English or German lyrics in YuE2's format from a few keywords, with a language model in LM Studio (or any
/// OpenAI-compatible server).
/// </summary>
/// <remarks>
/// On 24 GB a lyrics model and YuE2 do not fit side by side. So the YuE worker is stopped first when it idles
/// with its model loaded, the lyrics model is loaded for the request with a small context and unloaded right
/// after. Meanwhile <see cref="IsWriting"/> keeps new songs from starting. A model someone loaded in LM Studio
/// themselves is used as it is and left loaded.
/// A Claude model (<see cref="LyricsOptions.ClaudeModels"/>) runs at Anthropic instead: no memory is claimed, no
/// worker stopped, and songs go on while it writes.
/// </remarks>
public sealed partial class LyricsWriter(
    IHttpClientFactory httpClients,
    IOptions<LyricsOptions> options,
    ILmStudioStarter starter,
    ICloudLyricsModel cloud,
    WorkerHost worker,
    Status.StatusHub hub,
    ModelMemory memory,
    Machines.Machines machines,
    Machines.RemoteEngines remote,
    TimeProvider time,
    ILogger<LyricsWriter> logger)
{
    public const string HttpClientName = "lyrics";

    /// <summary>What YuE2 sings well, from its documentation and the YuE v1 prompt guide.</summary>
    internal static string SystemPrompt(LyricsLanguage language) => $"""
        You write song lyrics for YuE2, a model that turns lyrics and a style description into a sung song.
        {LanguageRule(language)} Answer with the lyrics only: no title, no explanations, no Markdown.

        Format:
        - Every section starts with its tag on a line of its own: [Verse], [Pre-Chorus], [Chorus], [Bridge] or [Outro].
        - Keep these tags in English whatever the language of the lyrics; YuE2 reads the song's form from them.
        - Start with [Verse]. Do not use [Intro]; YuE2 handles it poorly.
        - One sung line per line. Exactly one empty line between sections.
        - No stage directions, no parentheses, no speaker names, no line numbers.

        Singability:
        - A section is sung in about 30 seconds: 4 lines, at most 6.
        - Keep the syllable count of the lines within a section even (6 to 10 syllables), with a clear stress pattern.
        - Plain, concrete words and open vowels on long notes; avoid tongue twisters and dense consonant clusters.
        - Rhyme the lines of a section. Repeat the chorus word for word each time it returns.

        Form: [Verse], [Chorus], [Verse], [Chorus], [Bridge], [Chorus] unless the request suggests another.
        """;

    /// <remarks>
    /// German gets its own hints: small models tend to pad lines with long compounds that do not fit a melody, and
    /// to fall back into English phrases.
    /// </remarks>
    private static string LanguageRule(LyricsLanguage language) => language switch
    {
        LyricsLanguage.German => "Write in German, every sung line, with natural German word order and correct umlauts (ä, ö, ü, ß); " +
            "no English phrases. Prefer short words to long compounds, and rhymes that sound natural in German.",
        _ => "Write in English.",
    };

    /// <summary>Room for system prompt, keywords and style; the system prompt alone is about 300 tokens.</summary>
    private const int PromptTokens = 1024;

    /// <summary>
    /// What a photo adds to the prompt: Gemma 4 turns an image into up to 1120 tokens, depending on the budget the
    /// server picks; the browser sends it at most 1024 pixels wide.
    /// </summary>
    private const int ImageTokens = 1536;

    /// <summary>A draft is in progress, so the lyrics model may be in memory: no song should start now.</summary>
    public bool IsWriting => memory.Holds(LargeModel.Lyrics);

    /// <summary>Whether <paramref name="model"/> is one of the Claude models, which run in the cloud.</summary>
    public bool IsCloud(string? model) =>
        !string.IsNullOrWhiteSpace(model) && options.Value.ClaudeModels.Contains(model.Trim(), StringComparer.Ordinal);

    /// <summary>
    /// Starts a draft in the background; its progress and result arrive as <c>lyrics</c> events (see
    /// <see cref="LyricsState"/>), so a phone that locks meanwhile still gets it.
    /// </summary>
    /// <param name="model">A model id from <see cref="ListModelsAsync"/>; null for <see cref="LyricsOptions.Model"/>.</param>
    /// <param name="image">A photo as a <c>data:image/…;base64,</c> URL for a model that can see; the lyrics are about it.</param>
    /// <param name="revision">Lyrics to change as instructed, instead of new ones.</param>
    /// <param name="id">The id the draft was promised under while it waited in the queue; a new one otherwise.</param>
    /// <param name="machine">
    /// The Mac whose LM Studio writes it (<see cref="Machines.Machines.Normalize"/>): null for this one if its memory is
    /// free, else a free helper. Ignored for a Claude model.
    /// </param>
    /// <param name="reasoning">How much a thinking model thinks first; null leaves it to LM Studio. Ignored for Claude.</param>
    /// <exception cref="LyricsBusyException">Another draft is in progress, YuE2 is generating, or another model holds the memory.</exception>
    public LyricsState Start(
        string? keywords,
        string? style,
        LyricsLanguage language = LyricsLanguage.English,
        string? model = null,
        string? image = null,
        LyricsRevision? revision = null,
        string? id = null,
        string? machine = null,
        LyricsReasoning? reasoning = null)
    {
        var asked = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
        model = asked ?? options.Value.Model;
        var cloudModel = IsCloud(model);
        // From here on no song can start (see IsWriting); a cloud draft or one on a helper takes nothing from this Mac's memory.
        var placement = cloudModel ? null : Place(machine);
        var state = new LyricsState
        {
            Id = id ?? NewId(),
            Machine = cloudModel ? null : machine,
            RanOn = cloudModel ? null : placement!.Name,
            UpdatedAt = time.GetUtcNow(),
        };
        hub.UpdateLyrics(state);
        var brief = new Brief(keywords?.Trim() ?? "", style, language, image, revision, reasoning);
        // A helper drafts with its own default when none was chosen: this Mac's may not be loaded there.
        _ = Task.Run(() => RunAsync(state, brief, placement?.Helper is null ? model : asked, placement));
        return state;
    }

    /// <exception cref="LyricsBusyException">Nowhere to write it now.</exception>
    private Machines.Placement Place(string? machine)
    {
        if (machine is not null && machine != Machines.Machines.Local)
        {
            return machines.Find(machine) is { } helper
                ? machines.Remote(helper)
                : throw new LyricsBusyException($"There is no Mac {machine} any more.");
        }
        if (memory.TryClaim(LargeModel.Lyrics))
        {
            return new Machines.Placement(null, machines.LocalName, () => memory.Release(LargeModel.Lyrics));
        }
        if (machine is null && machines.FreeHelper(Machines.Engines.Lyrics) is { } free)
        {
            return machines.Remote(free);
        }
        throw new LyricsBusyException(
            IsWriting ? "Lyrics are already being written."
            : worker.IsBusy ? "YuE2 is generating; the lyrics model would not fit into memory beside it."
            // The worker is shut down below to make room; a transcription in it would end with it.
            : worker.IsTranscribing ? "A transcription is running in the worker."
            : "Another model holds the memory.");
    }

    /// <summary>
    /// A draft another Mac asked for (Machines/HelperRuns.cs), with this Mac's LM Studio. The caller holds the memory;
    /// the worker is shut down and the model loaded and unloaded as for a draft of this Mac's own.
    /// </summary>
    /// <exception cref="LyricsUnavailableException">LM Studio could not be reached or refused.</exception>
    public Task<string> WriteForAsync(Machines.LyricsHelperJob job, CancellationToken cancellationToken) =>
        WriteAsync(string.IsNullOrWhiteSpace(job.Model) ? options.Value.Model : job.Model.Trim(), new Brief(job.Keywords.Trim(), job.Style, job.Language, job.Image, job.Revision, job.Reasoning), cancellationToken);

    /// <summary>The id of a <see cref="LyricsState"/>, which the form keeps to take the result.</summary>
    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>What the song is to be about, as the form sent it.</summary>
    private sealed record Brief(string Keywords, string? Style, LyricsLanguage Language, string? Image, LyricsRevision? Revision, LyricsReasoning? Reasoning = null);

    /// <param name="placement">Null for Claude.</param>
    /// <param name="model">Null only on a helper, which then writes with its own default.</param>
    private async Task RunAsync(LyricsState state, Brief brief, string? model, Machines.Placement? placement)
    {
        LyricsState result;
        try
        {
            var lyrics = placement is null ? await WriteInCloudAsync(model!, brief, CancellationToken.None)
                : placement.Helper is { } helper ? await remote.WriteLyricsAsync(
                    helper, new Machines.LyricsHelperJob(model, brief.Keywords, brief.Style, brief.Language, brief.Image, brief.Revision, brief.Reasoning), CancellationToken.None)
                : await WriteAsync(model!, brief, CancellationToken.None);
            result = state with { Stage = "done", Lyrics = lyrics };
        }
        catch (Exception exception) when (exception is LyricsUnavailableException or Machines.HelperException)
        {
            result = state with { Stage = "failed", Message = exception.Message };
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Drafting lyrics failed");
            result = state with { Stage = "failed", Message = exception.Message };
        }
        finally
        {
            // Before the result goes out, so that a song started in answer to it is not refused.
            placement?.Release();
        }
        hub.UpdateLyrics(result with { UpdatedAt = time.GetUtcNow() });
    }

    /// <summary>The same prompts as LM Studio gets, so a draft reads alike whichever writes it.</summary>
    /// <exception cref="LyricsUnavailableException">No key, or Claude refused or failed.</exception>
    private async Task<string> WriteInCloudAsync(string model, Brief brief, CancellationToken cancellationToken)
    {
        var answer = await cloud.WriteAsync(model, SystemPrompt(brief.Language), PromptOf(brief), brief.Image, cancellationToken);
        var lyrics = Clean(answer);
        return lyrics.Length > 0 ? lyrics : throw new LyricsUnavailableException("Claude returned no lyrics.");
    }

    private static string PromptOf(Brief brief) => brief.Revision is { } revision
        ? RevisionPrompt(revision, brief.Keywords, brief.Style)
        : UserPrompt(brief.Keywords, brief.Style, brief.Image is not null);

    /// <exception cref="LyricsUnavailableException">LM Studio could not be reached or refused.</exception>
    private async Task<string> WriteAsync(string model, Brief brief, CancellationToken cancellationToken)
    {
        // Set once this call has loaded the model, so that it is unloaded again whatever happens next.
        string? unload = null;
        var settings = options.Value;
        using var http = CreateClient();
        try
        {
            if (worker.Info().Status != WorkerStatus.Stopped)
            {
                logger.LogInformation("Stopping the idle YuE worker to make room for the lyrics model");
                await worker.ShutdownWorkerAsync();
            }
            await EnsureServerAsync(http, cancellationToken);
            var (instance, loadedHere, context) = await LoadAsync(http, model, settings, cancellationToken);
            unload = loadedHere ? instance : null;
            return await CompleteAsync(http, settings, instance, context, brief, logger, cancellationToken);
        }
        finally
        {
            if (unload is not null)
            {
                await UnloadAsync(http, unload);
            }
        }
    }

    /// <summary>
    /// Loads the model with <see cref="LyricsOptions.ContextLength"/> instead of leaving it to LM Studio's
    /// just-in-time loading, whose context can be the model's maximum.
    /// </summary>
    /// <remarks>
    /// LM Studio's guardrails refuse a load whose estimate (weights plus the cache for the whole context) exceeds the
    /// free memory. A dense model loads less easily than Gemma 4's mixture of experts: Qwen 3.8 27B needs all its
    /// weights and a cache of several GB for 30000 tokens, which on 24 GB is refused as soon as a few apps are open.
    /// The guardrail is right to refuse (it guards against the swap freeze), so it is not switched off; instead what
    /// is in the way goes first: other models still loaded in LM Studio, then half the context each time down to
    /// <see cref="LyricsOptions.MinContextLength"/>. The draft then has less room for reasoning, which is still
    /// better than none.
    /// </remarks>
    /// <returns>The instance to address, whether it was loaded here (and so is unloaded after), and its context.</returns>
    private async Task<(string Instance, bool LoadedHere, int Context)> LoadAsync(HttpClient http, string model, LyricsOptions settings, CancellationToken cancellationToken)
    {
        var loaded = await LoadedInstancesAsync(http, cancellationToken);
        if (loaded.FirstOrDefault(i => i.Model == model) is { Instance: { } instance })
        {
            logger.LogInformation("Using {Instance}, which is already loaded in LM Studio", instance);
            return (instance, false, settings.ContextLength);
        }

        var context = settings.ContextLength;
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
            var smaller = Math.Max(settings.MinContextLength, context / 2);
            if (smaller >= context)
            {
                throw new LyricsUnavailableException($"LM Studio could not load {model}, not even with a context of {context} tokens; close other apps or pick a smaller model. {refusal}");
            }
            logger.LogInformation("LM Studio refused {Model} with a context of {Context} tokens for lack of memory; trying {Smaller}", model, context, smaller);
            context = smaller;
        }
    }

    /// <returns>The instance and its context if LM Studio loaded the model, else its refusal.</returns>
    private async Task<(string? Instance, int Context, string? Refusal)> TryLoadAsync(HttpClient http, string model, int context, CancellationToken cancellationToken)
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
    /// The models the picker offers: what LM Studio has downloaded, whether loaded or not, without embedding models.
    /// Starts LM Studio's server like a draft would; that loads no model. The Claude models follow while a key is set.
    /// </summary>
    /// <exception cref="LyricsUnavailableException">LM Studio could not be reached or started, and there is no Claude to offer.</exception>
    public async Task<LyricsModels> ListModelsAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        List<LyricsModel> claude = cloud.Configured
            ? [.. settings.ClaudeModels.Distinct().Select(id => new LyricsModel(id, Sketches.ClaudeModelChoice.NameOf(id), null, false, true, Cloud: true))]
            : [];
        List<LyricsModel> models;
        try
        {
            using var http = CreateClient();
            await EnsureServerAsync(http, cancellationToken);
            models = await NativeModelsAsync(http, cancellationToken) ?? await OpenAiModelsAsync(http, cancellationToken);
        }
        catch (LyricsUnavailableException exception) when (claude.Count > 0)
        {
            // Claude can still write; the configured model is kept as the default, as the form would use it anyway.
            logger.LogInformation("LM Studio's models are not listed: {Message}", exception.Message);
            models = [];
        }
        // The configured model stays choosable even when LM Studio does not list it, e.g. while it is still downloading.
        if (!models.Any(m => m.Id == settings.Model))
        {
            models.Insert(0, new LyricsModel(settings.Model, settings.Model, null, false, null));
        }
        models.AddRange(claude);
        return new LyricsModels(settings.Model, models);
    }

    /// <summary>
    /// What a draft on <paramref name="helper"/> can be written with: its LM Studio's models and its default, plus the
    /// Claude models, which write from here whichever Mac is chosen.
    /// </summary>
    /// <exception cref="LyricsUnavailableException">The helper could not be reached, or its LM Studio not.</exception>
    public async Task<LyricsModels> ListModelsAsync(Machines.HelperMachine helper, CancellationToken cancellationToken)
    {
        LyricsModels listed;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            listed = await remote.LyricsModelsAsync(helper, timeout.Token);
        }
        catch (Exception exception) when (exception is Machines.HelperException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new LyricsUnavailableException($"{helper.Label}: {exception.Message}");
        }
        List<LyricsModel> claude = cloud.Configured
            ? [.. options.Value.ClaudeModels.Distinct().Select(id => new LyricsModel(id, Sketches.ClaudeModelChoice.NameOf(id), null, false, true, Cloud: true))]
            : [];
        return listed with { Models = [.. listed.Models.Where(m => !m.Cloud), .. claude] };
    }


    /// <summary>
    /// LM Studio's own list, with names and sizes: the size is what decides whether a model fits beside the apps on
    /// 24 GB. Null for a server without it (another OpenAI-compatible one).
    /// </summary>
    private static async Task<List<LyricsModel>?> NativeModelsAsync(HttpClient http, CancellationToken cancellationToken)
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
    private static async Task<List<LyricsModel>> OpenAiModelsAsync(HttpClient http, CancellationToken cancellationToken)
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

    private HttpClient CreateClient()
    {
        var settings = options.Value;
        var http = httpClients.CreateClient(HttpClientName);
        http.BaseAddress = new Uri(settings.BaseUrl.TrimEnd('/') + "/");
        if (!string.IsNullOrWhiteSpace(settings.ApiToken))
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiToken);
        }
        return http;
    }

    /// <summary>The language models in memory, from LM Studio's model list; empty if none or unknown.</summary>
    private static async Task<List<(string Model, string Instance)>> LoadedInstancesAsync(HttpClient http, CancellationToken cancellationToken)
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

    private async Task EnsureServerAsync(HttpClient http, CancellationToken cancellationToken)
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

    private static async Task<bool> AnswersAsync(HttpClient http, CancellationToken cancellationToken)
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

    private static async Task<string> CompleteAsync(
        HttpClient http, LyricsOptions settings, string model, int context, Brief brief, ILogger logger, CancellationToken cancellationToken)
    {
        // Thinking models reason before the lyrics, and at length: Gemma 4 26B counts every line's syllables,
        // well over 4000 tokens. So the answer gets the whole context it was loaded with except the prompt,
        // which may be less than configured (see LoadAsync).
        var maxTokens = Math.Max(1024, context - PromptTokens - (brief.Image is null ? 0 : ImageTokens) - RevisionTokens(brief.Revision));
        if (brief.Reasoning is { } effort
            && await CompleteNativeAsync(http, settings, model, maxTokens, brief, effort, logger, cancellationToken) is { } steered)
        {
            return steered;
        }
        var text = PromptOf(brief);
        // The OpenAI form for images, which LM Studio passes to a vision model; a model without vision refuses it.
        JsonNode prompt = brief.Image is null
            ? text
            : new JsonArray(
                new JsonObject { ["type"] = "text", ["text"] = text },
                new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = brief.Image } });
        var request = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = SystemPrompt(brief.Language) },
                new JsonObject { ["role"] = "user", ["content"] = prompt }),
            ["temperature"] = settings.Temperature,
            // LM Studio's reasoning "off" is no way out for Gemma 4: it thinks anyway, unmarked in the answer.
            ["max_tokens"] = maxTokens,
            ["stream"] = false,
            // LM Studio's own field: unload after this many idle seconds, should the model be loaded just in time.
            ["ttl"] = settings.IdleTtlSeconds,
        };

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync("v1/chat/completions", Body(request), cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new LyricsUnavailableException($"LM Studio stopped answering: {exception.Message}");
        }
        using (response)
        {
            var json = await ReadAsync(response, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new LyricsUnavailableException($"LM Studio refused ({(int)response.StatusCode}): {ErrorOf(json)}");
            }
            var choice = json?["choices"]?[0];
            var content = (choice?["message"]?["content"] as JsonValue)?.GetValue<string>() ?? "";
            var lyrics = Clean(content);
            if (lyrics.Length > 0)
            {
                return lyrics;
            }

            // Thinking models (Gemma 4, Qwen 3) reason first; LM Studio returns that apart (reasoning_content) or
            // as <think> in the content. If the tokens run out while thinking, no lyrics are left.
            var finish = choice?["finish_reason"]?.ToString();
            var reasoning = (choice?["message"]?["reasoning_content"] ?? choice?["message"]?["reasoning"])?.ToString() ?? "";
            logger.LogWarning("No lyrics in the answer of {Model} (finish_reason {Finish}): {Answer}",
                model, finish, Truncate(json?.ToJsonString() ?? "", 2000));
            throw new LyricsUnavailableException(finish == "length" && (reasoning.Length > 0 || content.Contains("<think>", StringComparison.Ordinal))
                ? $"The model spent all its {request["max_tokens"]} tokens thinking and wrote no lyrics. Raise Lyrics:ContextLength, or pick a model that thinks less."
                : $"The model returned no lyrics (finish_reason: {finish ?? "none"}{(reasoning.Length > 0 ? ", only reasoning" : "")}).");
        }
    }

    /// <summary>
    /// A draft through LM Studio's native chat API, the only one of its APIs that takes <c>reasoning</c> (the
    /// OpenAI-compatible one has no such field). Same prompts, same token budget.
    /// </summary>
    /// <returns>The lyrics, or null if the model refused the setting, so that the draft is written without it.</returns>
    /// <exception cref="LyricsUnavailableException">LM Studio could not be reached, failed, or wrote no lyrics.</exception>
    private static async Task<string?> CompleteNativeAsync(
        HttpClient http, LyricsOptions settings, string model, int maxTokens, Brief brief, LyricsReasoning reasoning, ILogger logger,
        CancellationToken cancellationToken)
    {
        var text = PromptOf(brief);
        JsonNode input = brief.Image is null
            ? text
            : new JsonArray(
                new JsonObject { ["type"] = "text", ["content"] = text },
                new JsonObject { ["type"] = "image", ["data_url"] = brief.Image });
        var request = new JsonObject
        {
            ["model"] = model,
            ["system_prompt"] = SystemPrompt(brief.Language),
            ["input"] = input,
            ["temperature"] = settings.Temperature,
            ["max_output_tokens"] = maxTokens,
            ["reasoning"] = reasoning.ToString().ToLowerInvariant(),
            // Without it LM Studio keeps every draft as a chat of its own.
            ["store"] = false,
            ["stream"] = false,
        };

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync("api/v1/chat", Body(request), cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new LyricsUnavailableException($"LM Studio stopped answering: {exception.Message}");
        }
        using (response)
        {
            var json = await ReadAsync(response, cancellationToken);
            // LM Studio answers a setting the model does not support with an error; so does a version without this API.
            if ((int)response.StatusCode is >= 400 and < 500)
            {
                logger.LogWarning("LM Studio refused reasoning {Reasoning} for {Model} ({Status}: {Error}); drafting without it",
                    request["reasoning"], model, (int)response.StatusCode, ErrorOf(json));
                return null;
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new LyricsUnavailableException($"LM Studio refused ({(int)response.StatusCode}): {ErrorOf(json)}");
            }
            var output = json?["output"] as JsonArray ?? [];
            var content = string.Concat(output
                .Where(item => item?["type"]?.ToString() == "message")
                .Select(item => item?["content"]?.ToString() ?? ""));
            var lyrics = Clean(content);
            if (lyrics.Length > 0)
            {
                return lyrics;
            }
            var thought = output.Any(item => item?["type"]?.ToString() == "reasoning");
            logger.LogWarning("No lyrics in the answer of {Model} (reasoning {Reasoning}): {Answer}",
                model, request["reasoning"], Truncate(json?.ToJsonString() ?? "", 2000));
            throw new LyricsUnavailableException(thought
                ? $"The model wrote only its reasoning, no lyrics (at most {maxTokens} tokens). Think less, or raise Lyrics:ContextLength."
                : "The model returned no lyrics.");
        }
    }

    private static async Task<JsonNode?> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
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
    private static string ErrorOf(JsonNode? json) => json switch
    {
        JsonObject { } body when body["error"] is JsonObject error => error["message"]?.ToString() ?? error.ToJsonString(),
        JsonObject { } body when body["error"] is JsonNode error => error.ToString(),
        JsonNode node => node.ToString(),
        null => "no details",
    };

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length] + " …";

    /// <remarks>
    /// A photo is the song's subject; keywords given with it steer what the lyrics take from it. The photo is not to be
    /// described line by line: a song about a picture tells what it feels like, not what is where.
    /// </remarks>
    internal static string UserPrompt(string keywords, string? style, bool image = false)
    {
        var about = (image, keywords.Trim()) switch
        {
            (false, var words) => $"Write lyrics about: {words}",
            (true, "") => "Write lyrics inspired by this photo: its mood, its place and the story it could tell. Do not describe it line by line.",
            (true, var words) => $"Write lyrics inspired by this photo: its mood, its place and the story it could tell. Do not describe it line by line.\nFocus on: {words}",
        };
        return string.IsNullOrWhiteSpace(style)
            ? about
            : $"{about}\nThe song's style (for mood and pacing, do not mention it): {style.Trim()}";
    }

    /// <remarks>
    /// The point of a revision is that what worked stays: a model asked to "improve" lyrics rewrites every line, so it
    /// is told to change only what the instruction asks for. The whole text comes back, since it replaces the field.
    /// </remarks>
    internal static string RevisionPrompt(LyricsRevision revision, string keywords, string? style)
    {
        var prompt = $"""
            Revise these lyrics:

            {revision.Lyrics.Trim()}

            What to change: {revision.Instruction.Trim()}
            Change only what this asks for and keep every other line word for word. Answer with the complete revised lyrics.
            """;
        if (keywords.Trim() is { Length: > 0 } about)
        {
            prompt += $"\nThe song is about: {about}";
        }
        return string.IsNullOrWhiteSpace(style)
            ? prompt
            : $"{prompt}\nThe song's style (for mood and pacing, do not mention it): {style.Trim()}";
    }

    /// <summary>
    /// The lyrics to revise are part of the prompt, beyond <see cref="PromptTokens"/>. Lyrics take about four characters
    /// a token; three is counted, since an answer cut short loses the song's end.
    /// </summary>
    private static int RevisionTokens(LyricsRevision? revision) => revision is null ? 0 : revision.Lyrics.Length / 3 + 1;

    /// <summary>What models add around the lyrics despite the instructions: reasoning, code fences, bold tags.</summary>
    internal static string Clean(string text)
    {
        text = Thinking().Replace(text.Replace("\r\n", "\n"), "");
        var lines = text.Split('\n')
            .Where(line => !line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            .Select(line => Tag().Replace(line.Replace("**", "").TrimEnd(), "[$1]"));
        return BlankLines().Replace(string.Join('\n', lines), "\n\n").Trim();
    }

    /// <summary>Best effort; a model loaded through the API has no idle TTL, so a failure is logged as a warning.</summary>
    private async Task UnloadAsync(HttpClient http, string model)
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
    private static StringContent Body(JsonObject json) => new(json.ToJsonString(), System.Text.Encoding.UTF8, "application/json");

    [GeneratedRegex(@"<think>.*?(</think>|$)", RegexOptions.Singleline)]
    private static partial Regex Thinking();

    /// <summary>"Chorus:" or "[chorus]" on a line of its own → "[Chorus]"-style tag, kept as the model wrote the word.</summary>
    [GeneratedRegex(@"^\s*\[?\s*((?:Verse|Pre-Chorus|Chorus|Bridge|Outro|Intro|Hook)(?:\s*\d+)?)\s*\]?\s*:?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Tag();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();
}
