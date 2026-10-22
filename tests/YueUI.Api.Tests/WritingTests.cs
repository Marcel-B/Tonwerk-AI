using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using YueUI.Api.Memory;

namespace YueUI.Api.Tests;

public sealed class WritingTests : IDisposable
{
    private readonly TestApp _app = new();
    private HttpClient? _client;

    private HttpClient Client => _client ??= _app.CreateClient();

    private ModelMemory Memory => _app.Services.GetRequiredService<ModelMemory>();

    [Fact]
    public async Task A_message_is_answered_by_the_model_which_stays_loaded_for_the_next()
    {
        _app.LmStudio.Answer = "1. Nachtzug\n2. Abfahrt";
        var chat = await NewChat();

        var reply = await Send(chat, "Titelideen für einen Song über einen Nachtzug");
        Assert.Equal(HttpStatusCode.Accepted, reply.StatusCode);
        var done = await Answered(chat, 2);

        var messages = done["messages"]!.AsArray();
        Assert.Equal("Titelideen für einen Song über einen Nachtzug", (string?)done["title"]);
        Assert.Equal("1. Nachtzug\n2. Abfahrt", (string?)messages[1]!["text"]);
        Assert.Equal("assistant", (string?)messages[1]!["role"]);
        Assert.Equal("google/gemma-4-e4b", (string?)messages[1]!["model"]);
        var load = Assert.Single(_app.LmStudio.Requests, r => r.Path == "/api/v1/models/load").Body!;
        Assert.Equal(32768, (int?)load["context_length"]);
        var completion = Assert.Single(_app.LmStudio.Requests, r => r.Path == "/v1/chat/completions").Body!;
        Assert.True((bool?)completion["stream"]);
        Assert.Equal("system", (string?)completion["messages"]![0]!["role"]);
        Assert.Contains("songwriting partner", (string?)completion["messages"]![0]!["content"], StringComparison.Ordinal);
        // Kept for the next message, holding the memory songs would need.
        Assert.DoesNotContain(_app.LmStudio.Requests, r => r.Path == "/api/v1/models/unload");
        Assert.Equal(LargeModel.Lyrics, Memory.Holder);

        _app.LmStudio.Answer = "Gleis neun";
        await Send(chat, "Noch eine?");
        await Answered(chat, 4);

        Assert.Single(_app.LmStudio.Requests, r => r.Path == "/api/v1/models/load");
        var second = _app.LmStudio.Requests.Last(r => r.Path == "/v1/chat/completions").Body!["messages"]!.AsArray();
        Assert.Equal(["system", "user", "assistant", "user"], second.Select(m => (string?)m!["role"]));

        Assert.Equal(HttpStatusCode.NoContent, (await Client.PostAsync("/api/writing/release", null)).StatusCode);
        Assert.Equal("/api/v1/models/unload", _app.LmStudio.Requests[^1].Path);
        Assert.Null(Memory.Holder);
    }

    [Fact]
    public async Task The_kept_model_is_unloaded_after_a_while()
    {
        _app.WritingKeepLoaded = TimeSpan.FromMilliseconds(100);
        var chat = await NewChat();

        await Send(chat, "Ein Reim auf Herz");
        await Answered(chat, 2);

        await TestApp.WaitUntil(() => Memory.Holder is null);
        Assert.Contains(_app.LmStudio.Requests, r => r.Path == "/api/v1/models/unload");
    }

    [Fact]
    public async Task The_models_reasoning_is_left_out_of_the_answer()
    {
        _app.LmStudio.StreamReasoning = "Silben zählen";
        _app.LmStudio.Answer = "<think>noch mehr</think>Schmerz, März, Scherz";
        var chat = await NewChat();

        await Send(chat, "Ein Reim auf Herz");
        var done = await Answered(chat, 2);

        Assert.Equal("Schmerz, März, Scherz", (string?)done["messages"]![1]!["text"]);
    }

    [Fact]
    public async Task One_answer_at_a_time_and_a_stopped_one_ends()
    {
        _app.LmStudio.Gate = new TaskCompletionSource();
        var chat = await NewChat();
        await Send(chat, "Erste Strophe bitte");
        await TestApp.WaitUntil(() => (string?)Overview()["reply"]?["stage"] == "writing");

        var second = await Send(chat, "Und noch etwas");
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await Client.PostAsync($"/api/writing/{chat}/stop", null)).StatusCode);
        await TestApp.WaitUntil(() => (string?)Overview()["reply"]?["stage"] == "stopped");
        _app.LmStudio.Gate.SetResult();
        Assert.Single((await Chat(chat))["messages"]!.AsArray());
    }

    [Fact]
    public async Task Again_replaces_the_last_answer_with_a_new_one()
    {
        _app.LmStudio.Answer = "Variante A";
        var chat = await NewChat();
        await Send(chat, "Eine Zeile über Regen");
        await Answered(chat, 2);

        _app.LmStudio.Answer = "Variante B";
        Assert.Equal(HttpStatusCode.Accepted, (await Client.PostAsJsonAsync($"/api/writing/{chat}/retry", new { })).StatusCode);
        await TestApp.WaitUntil(() => (string?)Overview()["reply"]?["stage"] == "done"
            && (string?)Chat(chat).GetAwaiter().GetResult()["messages"]![1]!["text"] == "Variante B");

        Assert.Equal(2, (await Chat(chat))["messages"]!.AsArray().Count);
    }

    [Fact]
    public async Task A_failed_load_says_why_and_gives_the_memory_back()
    {
        _app.LmStudio.LoadRefusal = "Model not found";
        var chat = await NewChat();

        await Send(chat, "Hallo");
        await TestApp.WaitUntil(() => (string?)Overview()["reply"]?["stage"] == "failed");

        Assert.Contains("Model not found", (string?)Overview()["reply"]!["message"], StringComparison.Ordinal);
        await TestApp.WaitUntil(() => Memory.Holder is null);
    }

    [Fact]
    public async Task Chats_are_listed_renamed_and_deleted()
    {
        var chat = await NewChat();
        Assert.Equal(HttpStatusCode.OK, (await Client.PutAsJsonAsync($"/api/writing/{chat}", new { title = "Nachtzug" })).StatusCode);

        var overview = Overview();
        Assert.Equal("google/gemma-4-e4b", (string?)overview["model"]);
        Assert.Equal("Nachtzug", (string?)Assert.Single(overview["chats"]!.AsArray())!["title"]);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(chat, " ")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send("nope", "Hallo")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await Client.DeleteAsync($"/api/writing/{chat}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/api/writing/{chat}")).StatusCode);
    }

    [Fact]
    public async Task How_much_to_think_goes_to_the_native_chat_with_the_conversation_as_text()
    {
        var chat = await NewChat();
        _app.LmStudio.Answer = "Schmerz, März";
        await Send(chat, "Ein Reim auf Herz");
        await Answered(chat, 2);

        _app.LmStudio.StreamReasoning = "Silben zählen";
        _app.LmStudio.Answer = "Scherz";
        var reply = await Client.PostAsJsonAsync($"/api/writing/{chat}/messages", new { text = "Noch einer?", reasoning = "off" });
        Assert.Equal("off", (string?)(await reply.Content.ReadFromJsonAsync<JsonObject>())!["reasoning"]);
        var done = await Answered(chat, 4);

        Assert.Equal("Scherz", (string?)done["messages"]![3]!["text"]);
        Assert.Equal("off", (string?)done["reasoning"]);
        var native = Assert.Single(_app.LmStudio.Requests, r => r.Path == "/api/v1/chat").Body!;
        Assert.Equal("off", (string?)native["reasoning"]);
        Assert.False((bool?)native["store"]);
        Assert.True((bool?)native["stream"]);
        Assert.Contains("songwriting partner", (string?)native["system_prompt"], StringComparison.Ordinal);
        var input = (string)native["input"]!;
        Assert.Contains("<songwriter>\nEin Reim auf Herz\n</songwriter>", input, StringComparison.Ordinal);
        Assert.Contains("<you>\nSchmerz, März\n</you>", input, StringComparison.Ordinal);
        Assert.EndsWith("Noch einer?", input, StringComparison.Ordinal);
        // The first answer, without a choice, went the usual way.
        Assert.Single(_app.LmStudio.Requests, r => r.Path == "/v1/chat/completions");
    }

    [Fact]
    public async Task A_model_that_cannot_be_steered_answers_the_usual_way()
    {
        _app.LmStudio.RefusesReasoning = true;
        _app.LmStudio.Answer = "Schmerz";
        var chat = await NewChat();

        await Client.PostAsJsonAsync($"/api/writing/{chat}/messages", new { text = "Ein Reim auf Herz", reasoning = "low" });
        var done = await Answered(chat, 2);

        Assert.Equal("Schmerz", (string?)done["messages"]![1]!["text"]);
        Assert.Single(_app.LmStudio.Requests, r => r.Path == "/api/v1/chat");
        Assert.Single(_app.LmStudio.Requests, r => r.Path == "/v1/chat/completions");
    }

    private async Task<string> NewChat()
    {
        var response = await Client.PostAsJsonAsync("/api/writing", new { });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (string)(await response.Content.ReadFromJsonAsync<JsonObject>())!["id"]!;
    }

    private Task<HttpResponseMessage> Send(string chat, string text) =>
        Client.PostAsJsonAsync($"/api/writing/{chat}/messages", new { text });

    private async Task<JsonObject> Chat(string chat) => (await Client.GetFromJsonAsync<JsonObject>($"/api/writing/{chat}"))!;

    private JsonObject Overview() => Client.GetFromJsonAsync<JsonObject>("/api/writing").GetAwaiter().GetResult()!;

    private async Task<JsonObject> Answered(string chat, int messages)
    {
        await TestApp.WaitUntil(() =>
            Chat(chat).GetAwaiter().GetResult()["messages"]!.AsArray().Count == messages
            && (string?)Overview()["reply"]?["stage"] == "done");
        return await Chat(chat);
    }

    public void Dispose()
    {
        _client?.Dispose();
        _app.Dispose();
    }
}
