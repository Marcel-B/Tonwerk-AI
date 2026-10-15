using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using YueUI.Api.Lyrics;
using YueUI.Api.Sketches;
using YueUI.Api.Worker;

namespace YueUI.Api.Tests;

/// <summary>Lyrics written by Claude instead of LM Studio: offered in the picker, run at once, beside songs.</summary>
public sealed class ClaudeLyricsTests : IDisposable
{
    private readonly TestApp _app = new();
    private readonly HttpClient _client;

    public ClaudeLyricsTests()
    {
        _client = _app.CreateClient();
        _app.CloudLyrics.Configured = true;
    }

    [Fact]
    public async Task The_picker_offers_Claude_after_LM_Studios_models_while_a_key_is_set()
    {
        var list = (await _client.GetFromJsonAsync<JsonObject>("/api/lyrics/models"))!["models"]!.AsArray();

        Assert.Equal(["google/gemma-4-e4b", "qwen/qwen3-8b", "claude-opus-5-5"], list.Select(m => (string?)m!["id"]));
        Assert.Equal("Claude Opus 5.5", (string?)list[2]!["name"]);
        Assert.Equal([false, false, true], list.Select(m => (bool?)m!["cloud"]));
        Assert.True((bool?)list[2]!["vision"]);

        _app.CloudLyrics.Configured = false;
        var withoutKey = (await _client.GetFromJsonAsync<JsonObject>("/api/lyrics/models"))!["models"]!.AsArray();
        Assert.DoesNotContain(withoutKey, m => (bool?)m!["cloud"] == true);
    }

    [Fact]
    public async Task Without_LM_Studio_the_picker_still_offers_Claude()
    {
        _app.LmStudio.Running = false;
        _app.LmStudio.CanStart = false;

        var models = (await _client.GetFromJsonAsync<JsonObject>("/api/lyrics/models"))!;

        Assert.Equal("google/gemma-4-e4b", (string?)models["default"]);
        Assert.Equal(["google/gemma-4-e4b", "claude-opus-5-5"], models["models"]!.AsArray().Select(m => (string?)m!["id"]));
    }

    [Fact]
    public async Task A_Claude_draft_gets_the_same_prompts_and_never_touches_LM_Studio()
    {
        var draft = await Finished(new { keywords = "Nachtzug", style = "German, synthwave", language = "german", model = "claude-opus-5-5" });

        Assert.Equal("done", draft.Stage);
        Assert.Equal("[Verse]\nCloud line\n\n[Chorus]\nSky line", draft.Lyrics);
        var call = Assert.Single(_app.CloudLyrics.Calls);
        Assert.Equal("claude-opus-5-5", call.Model);
        Assert.Contains("Write in German", call.System, StringComparison.Ordinal);
        Assert.Contains("Nachtzug", call.Prompt, StringComparison.Ordinal);
        Assert.Null(call.Image);
        Assert.Empty(_app.LmStudio.Requests);
    }

    [Fact]
    public async Task A_Claude_revision_sends_the_lyrics_and_the_instruction()
    {
        var draft = await Finished(new { lyrics = "[Verse]\nOld line", instruction = "sadder", model = "claude-opus-5-5" });

        Assert.Equal("done", draft.Stage);
        var prompt = Assert.Single(_app.CloudLyrics.Calls).Prompt;
        Assert.Contains("Old line", prompt, StringComparison.Ordinal);
        Assert.Contains("What to change: sadder", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Claude_draft_starts_at_once_while_YuE2_generates_and_leaves_the_worker_alone()
    {
        await StartSong();

        var response = await _client.PostAsJsonAsync("/api/lyrics", new { keywords = "rain", model = "claude-opus-5-5" });
        var state = (await response.Content.ReadFromJsonAsync<LyricsState>(TestApp.Json))!;
        Assert.Equal("writing", state.Stage);

        var done = (await _app.WaitForStatus(_client, s => s.Lyrics is { Finished: true } l && l.Id == state.Id)).Lyrics!;
        Assert.Equal("done", done.Stage);
        Assert.False(_app.Worker.Disposed);
        Assert.Empty((await _app.WaitForStatus(_client, _ => true)).Queue ?? []);
    }

    [Fact]
    public async Task A_failure_reaches_the_form()
    {
        _app.CloudLyrics.Failure = "Claude: invalid x-api-key";

        var draft = await Finished(new { keywords = "rain", model = "claude-opus-5-5" });

        Assert.Equal("failed", draft.Stage);
        Assert.Equal("Claude: invalid x-api-key", draft.Message);
    }

    [Theory]
    [InlineData("claude-opus-5-5", "Claude Opus 5.5")]
    [InlineData("claude-sonnet-5", "Claude Sonnet 5")]
    [InlineData("claude-haiku-4-5", "Claude Haiku 4.5")]
    public void Names_are_made_from_the_id(string id, string name) => Assert.Equal(name, LyricsWriter.ClaudeName(id));

    [Fact]
    public async Task The_adapter_asks_Claude_with_the_system_prompt_and_a_photo_as_an_image_block()
    {
        var handler = new ClaudeSketchModelTests.FakeAnthropic(ClaudeSketchModelTests.Stream("[Verse]\nLine", "end_turn"));
        var services = new ServiceCollection();
        services.AddHttpClient(ClaudeSketchModel.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
        var model = new ClaudeLyricsModel(
            Options.Create(new LyricsOptions()),
            Options.Create(new SketchOptions { ApiKey = "test-key" }),
            services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>());

        var answer = await model.WriteAsync("claude-opus-5-5", "You write song lyrics", "Write lyrics about: rain", "data:image/png;base64,iVBORw0KGgo=", CancellationToken.None);

        Assert.Equal("[Verse]\nLine", answer);
        var request = handler.Request!;
        Assert.Equal("test-key", handler.Key);
        Assert.Equal("claude-opus-5-5", (string?)request["model"]);
        Assert.Equal("You write song lyrics", (string?)request["system"]);
        Assert.Equal("adaptive", (string?)request["thinking"]!["type"]);
        Assert.Equal("medium", (string?)request["output_config"]!["effort"]);
        var content = request["messages"]![0]!["content"]!.AsArray();
        Assert.Equal("image", (string?)content[0]!["type"]);
        Assert.Equal("image/png", (string?)content[0]!["source"]!["media_type"]);
        Assert.Equal("iVBORw0KGgo=", (string?)content[0]!["source"]!["data"]);
        Assert.Equal("Write lyrics about: rain", (string?)content[1]!["text"]);
    }

    [Fact]
    public async Task The_adapter_names_a_refusal()
    {
        var handler = new ClaudeSketchModelTests.FakeAnthropic(ClaudeSketchModelTests.Stream("", "refusal"));
        var services = new ServiceCollection();
        services.AddHttpClient(ClaudeSketchModel.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
        var model = new ClaudeLyricsModel(
            Options.Create(new LyricsOptions()),
            Options.Create(new SketchOptions { ApiKey = "test-key" }),
            services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>());

        var error = await Assert.ThrowsAsync<LyricsUnavailableException>(() => model.WriteAsync("claude-opus-5-5", "s", "p", null, CancellationToken.None));

        Assert.Equal("Claude declined to write these lyrics.", error.Message);
    }

    public void Dispose()
    {
        _client.Dispose();
        _app.Dispose();
    }

    private async Task<LyricsState> Finished(object request)
    {
        var response = await _client.PostAsJsonAsync("/api/lyrics", request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<LyricsState>(TestApp.Json))!.Id;
        return (await _app.WaitForStatus(_client, s => s.Lyrics is { Finished: true } lyrics && lyrics.Id == id)).Lyrics!;
    }

    private async Task StartSong()
    {
        Assert.Equal(HttpStatusCode.Accepted, (await _client.PostAsJsonAsync("/api/generate", new { style = "Pop", lyrics = "[verse]\nLa" })).StatusCode);
        await _app.Worker.NextCommand();
        _app.Worker.Emit(new { @event = "ready" });
        _app.Worker.Emit(new
        {
            @event = "started",
            job = "20260922-101500-Neon-Night",
            title = "Neon Night",
            songs = new[] { new { index = 1, seed = 1, path = _app.AudioPath("20260922-101500-Neon-Night", "song1"), priority = 1 } },
        });
        await _app.WaitForStatus(_client, s => s.Worker.Busy);
    }
}
