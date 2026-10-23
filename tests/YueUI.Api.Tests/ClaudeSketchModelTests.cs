using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using YueUI.Api.Monitoring;
using YueUI.Api.Sketches;

namespace YueUI.Api.Tests;

/// <summary>The real adapter and the Anthropic SDK, with Anthropic's streaming answer played by a handler.</summary>
public sealed class ClaudeSketchModelTests
{
    [Fact]
    public async Task The_request_asks_for_a_plan_and_the_streamed_json_becomes_one()
    {
        var plan = JsonSerializer.Serialize(SketchSamples.Plan(), JsonSerializerOptions.Web);
        var handler = new FakeAnthropic(Stream(plan, "end_turn"));

        var answer = await Model(handler).PlanAsync("# Before the Silence\n96 BPM", "keep the hook", "claude-opus-5-5", CancellationToken.None);

        Assert.Equal("Before the Silence", answer.Plan.Title);
        Assert.Equal(7, answer.Plan.Sections.Sum(s => s.Bars.Count));
        Assert.Equal(("claude-opus-5-5", 1200, 9000), (answer.Model, answer.InputTokens, answer.OutputTokens));

        var request = handler.Request!;
        Assert.Equal("https://api.anthropic.com/v1/messages", handler.Uri);
        Assert.Equal("test-key", handler.Key);
        Assert.Equal("claude-opus-5-5", (string?)request["model"]);
        Assert.True((bool?)request["stream"]);
        Assert.Equal("adaptive", (string?)request["thinking"]!["type"]);
        Assert.Equal("high", (string?)request["output_config"]!["effort"]);
        Assert.Equal("json_schema", (string?)request["output_config"]!["format"]!["type"]);
        Assert.Equal("object", (string?)request["output_config"]!["format"]!["schema"]!["type"]);
        var user = (string?)request["messages"]![0]!["content"];
        Assert.Contains("<sketch>\n# Before the Silence\n96 BPM\n</sketch>", user);
        Assert.Contains("<wishes>\nkeep the hook\n</wishes>", user);
        Assert.StartsWith("You turn a songwriter's written sketch", (string?)request["system"]);
    }

    [Theory]
    [InlineData("refusal", "Claude declined to plan this sketch.")]
    [InlineData("max_tokens", "Claude's answer outgrew 64000 tokens (Sketch:MaxTokens).")]
    public async Task An_answer_that_is_not_a_plan_says_why(string stopReason, string message)
    {
        var handler = new FakeAnthropic(Stream("{\"title\":", stopReason));

        var error = await Assert.ThrowsAsync<SketchModelException>(() => Model(handler).PlanAsync("x", null, "claude-opus-5-5", CancellationToken.None));

        Assert.Equal(message, error.Message);
    }

    [Fact]
    public async Task An_api_error_names_itself()
    {
        var handler = new FakeAnthropic("""{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""", HttpStatusCode.Unauthorized);

        var error = await Assert.ThrowsAsync<SketchModelException>(() => Model(handler).PlanAsync("x", null, "claude-opus-5-5", CancellationToken.None));

        Assert.StartsWith("Claude:", error.Message);
    }

    private static ClaudeSketchModel Model(FakeAnthropic handler)
    {
        var services = new ServiceCollection();
        services.AddHttpClient(ClaudeSketchModel.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
        var provider = services.BuildServiceProvider();
        return new ClaudeSketchModel(
            Options.Create(new SketchOptions { ApiKey = "test-key" }),
            provider.GetRequiredService<IHttpClientFactory>(),
            new CloudUsage());
    }

    /// <summary>Server-sent events as the Messages API streams them, the text cut into pieces.</summary>
    internal static string Stream(string text, string stopReason)
    {
        var events = new StringBuilder();
        void Event(string name, object data) =>
            events.Append($"event: {name}\ndata: {JsonSerializer.Serialize(data)}\n\n");
        Event("message_start", new
        {
            type = "message_start",
            message = new
            {
                id = "msg_1", type = "message", role = "assistant", model = "claude-opus-5-5", content = Array.Empty<object>(),
                stop_reason = (string?)null, stop_sequence = (string?)null,
                usage = new { input_tokens = 1200, output_tokens = 1 },
            },
        });
        Event("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } });
        for (var at = 0; at < text.Length; at += 200)
        {
            Event("content_block_delta", new
            {
                type = "content_block_delta",
                index = 0,
                delta = new { type = "text_delta", text = text[at..Math.Min(text.Length, at + 200)] },
            });
        }
        Event("content_block_stop", new { type = "content_block_stop", index = 0 });
        Event("message_delta", new
        {
            type = "message_delta",
            delta = new { stop_reason = stopReason, stop_sequence = (string?)null },
            usage = new { output_tokens = 9000 },
        });
        Event("message_stop", new { type = "message_stop" });
        return events.ToString();
    }

    internal sealed class FakeAnthropic(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public JsonObject? Request { get; private set; }

        public string? Uri { get; private set; }

        public string? Key { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri?.ToString();
            Key = request.Headers.TryGetValues("x-api-key", out var keys) ? keys.Single() : null;
            Request = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, status == HttpStatusCode.OK ? "text/event-stream" : "application/json"),
            };
        }
    }
}
