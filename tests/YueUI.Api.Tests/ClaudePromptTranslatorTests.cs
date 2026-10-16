using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using YueUI.Api.Images;
using YueUI.Api.Sketches;

namespace YueUI.Api.Tests;

/// <summary>The real translator and the Anthropic SDK, with Anthropic's answer played by a handler.</summary>
public sealed class ClaudePromptTranslatorTests
{
    [Fact]
    public async Task A_german_description_comes_back_in_english()
    {
        var handler = new Handler(Answer("Someone stands in the forest, the morning sun shines through the leaves."));

        var english = await Translator(handler).TranslateAsync("jemand steht im Wals, die morgensonne strahlt durch die blätter.", CancellationToken.None);

        Assert.Equal("Someone stands in the forest, the morning sun shines through the leaves.", english);
        var request = handler.Request!;
        Assert.Equal("https://api.anthropic.com/v1/messages", handler.Uri);
        Assert.Equal("claude-haiku-5-5", (string?)request["model"]);
        Assert.Equal("jemand steht im Wals, die morgensonne strahlt durch die blätter.", (string?)request["messages"]![0]!["content"]);
        Assert.Contains("exactly as given", (string?)request["system"]);
    }

    [Fact]
    public async Task An_english_description_is_not_rewritten()
    {
        var handler = new Handler(Answer("Neon city at night"));

        Assert.Null(await Translator(handler).TranslateAsync(" Neon city at night ", CancellationToken.None));
    }

    [Fact]
    public async Task A_failure_leaves_the_description_as_written()
    {
        var handler = new Handler("""{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}""", HttpStatusCode.BadRequest);

        Assert.Null(await Translator(handler).TranslateAsync("ein Wald", CancellationToken.None));
    }

    [Fact]
    public async Task Without_a_key_or_switched_off_nothing_is_asked()
    {
        var handler = new Handler(Answer("A forest"));

        var noKey = Translator(handler, key: null);
        var off = Translator(handler, translate: false);

        Assert.False(noKey.Configured);
        Assert.False(off.Configured);
        Assert.Null(await noKey.TranslateAsync("ein Wald", CancellationToken.None));
        Assert.Null(await off.TranslateAsync("ein Wald", CancellationToken.None));
        Assert.Null(handler.Request);
    }

    private static ClaudePromptTranslator Translator(Handler handler, string? key = "test-key", bool translate = true)
    {
        var services = new ServiceCollection();
        services.AddHttpClient(ClaudeSketchModel.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
        var provider = services.BuildServiceProvider();
        return new ClaudePromptTranslator(
            Options.Create(new ImageOptions { Translate = translate }),
            Options.Create(new SketchOptions { ApiKey = key, KeyFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.key") }),
            provider.GetRequiredService<IHttpClientFactory>(),
            NullLogger<ClaudePromptTranslator>.Instance);
    }

    private static string Answer(string text) => JsonSerializer.Serialize(new
    {
        id = "msg_1",
        type = "message",
        role = "assistant",
        model = "claude-haiku-5-5",
        content = new[] { new { type = "text", text } },
        stop_reason = "end_turn",
        stop_sequence = (string?)null,
        usage = new { input_tokens = 120, output_tokens = 20 },
    });

    private sealed class Handler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public JsonObject? Request { get; private set; }

        public string? Uri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri?.ToString();
            Request = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
