using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using YueUI.Api.Lyrics;

namespace YueUI.Api.Sketches;

/// <summary>
/// Plans a sketch with a model in LM Studio instead of Claude, at Marcel's wish to try the large local models
/// (Gemma 4 31B, Qwen 3.6 35B A3B) on the new Mac. Same system prompt and schema as <see cref="ClaudeSketchModel"/>,
/// the schema given to LM Studio as structured output (<c>response_format</c>), which holds the model to valid JSON
/// of that shape but not to the musical rules: bar sums and syllable counts are what a local model gets wrong most,
/// and <see cref="SketchScore"/> repairs or reports them. A song of a hundred bars is about 20000 tokens, at eight
/// tokens a second the better part of an hour, so this has its own, longer time limit.
/// </summary>
/// <remarks>
/// The caller (<see cref="SketchMaker"/>) holds <see cref="Memory.LargeModel.Lyrics"/>; the model is loaded here and
/// unloaded after, as a lyrics draft does.
/// </remarks>
public sealed partial class LocalSketchModel(LmStudio lmStudio, IOptions<SketchOptions> options, ILogger<LocalSketchModel> logger)
{
    public async Task<SketchAnswer> PlanAsync(string sketch, string? instruction, string model, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var user = ClaudeSketchModel.UserPrompt(sketch, instruction);
        try
        {
            await using var loaded = await lmStudio.OpenAsync(model, settings.LocalContextLength, cancellationToken);
            // About three characters a token for the prompts; the rest of the context is the plan's.
            var promptTokens = (ClaudeSketchModel.SystemPrompt.Length + user.Length) / 3 + 256;
            var maxTokens = loaded.Context - promptTokens;
            if (maxTokens < 4096)
            {
                throw new SketchModelException(
                    $"{model} was loaded with only {loaded.Context} tokens of context, too few for this sketch; close other apps or raise Sketch:LocalContextLength.");
            }
            var request = new JsonObject
            {
                ["messages"] = new JsonArray(
                    new JsonObject { ["role"] = "system", ["content"] = ClaudeSketchModel.SystemPrompt },
                    new JsonObject { ["role"] = "user", ["content"] = user }),
                ["temperature"] = settings.LocalTemperature,
                ["max_tokens"] = maxTokens,
                ["response_format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JsonObject
                    {
                        ["name"] = "song_plan",
                        ["strict"] = true,
                        ["schema"] = JsonSerializer.SerializeToNode(ClaudeSketchModel.Schema),
                    },
                },
            };
            var started = DateTimeOffset.UtcNow;
            var answer = await LmStudio.StreamChatAsync(loaded, request, null, cancellationToken);
            logger.LogInformation("{Model} planned a sketch in {Minutes:0.0} min ({Characters} characters, finish {Finish})",
                model, (DateTimeOffset.UtcNow - started).TotalMinutes, answer.Content.Length, answer.FinishReason);
            if (answer.FinishReason == "length")
            {
                throw new SketchModelException($"{model}'s plan outgrew its {maxTokens} tokens; raise Sketch:LocalContextLength or shorten the sketch.");
            }
            return new SketchAnswer(Parse(answer.Content, model), model, promptTokens, null);
        }
        catch (LyricsUnavailableException exception)
        {
            throw new SketchModelException(exception.Message, exception);
        }
    }

    /// <summary>The plan in the answer, past any reasoning or code fence a model writes around it.</summary>
    internal static SketchPlan Parse(string content, string model)
    {
        var text = Thinking().Replace(content, "");
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        SketchPlan? plan = null;
        if (start >= 0 && end > start)
        {
            try
            {
                plan = JsonSerializer.Deserialize<SketchPlan>(text[start..(end + 1)], JsonSerializerOptions.Web);
            }
            catch (JsonException error)
            {
                throw new SketchModelException($"{model}'s answer was not a song plan: {error.Message}", error);
            }
        }
        return plan is { Sections.Count: > 0 } ? plan : throw new SketchModelException($"{model}'s answer held no sections.");
    }

    [GeneratedRegex(@"<think>.*?(</think>|$)", RegexOptions.Singleline)]
    private static partial Regex Thinking();
}
