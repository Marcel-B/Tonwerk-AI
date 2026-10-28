using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using YueUI.Api.Lyrics;

namespace YueUI.Api.Tests;

public sealed class LlmGateTests : IDisposable
{
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    private void Start() => _app.Services.GetRequiredService<LlmGateRegistration>();

    private static string[] Keys(JsonObject body) =>
        [.. body["models"]!.AsArray().Select(model => model!["model"]!.GetValue<string>())];

    private static JsonObject Model(JsonObject body, string key) =>
        body["models"]!.AsArray().Single(model => model!["model"]!.GetValue<string>() == key)!.AsObject();

    [Fact]
    public async Task Tonwerk_lists_its_models_at_the_gate_as_a_direct_app()
    {
        _app.LlmGateUrl = "http://gate.test:1235";
        _app.LmStudio.MoreModels.Add(new
        {
            type = "llm",
            key = "lmstudio-community/gemma-4-31b-it-mlx-4bit",
            display_name = "Gemma 4 31B",
            size_bytes = 19_800_000_000L,
            loaded_instances = Array.Empty<object>(),
        });
        _app.LlmGateModels.Add(new LlmGateModel("qwen3.6-35b-a3b", Source: "lmstudio-community/Qwen3.6-35B-A3B-MLX-4bit", SizeGb: 23));
        Start();

        await TestApp.WaitUntil(() => _app.LlmGate.Registrations().Length > 0);

        var (path, body) = _app.LlmGate.Registrations()[0];
        Assert.Equal("/gate/apps/tonwerk-ai", path);
        Assert.True(body["direct"]!.GetValue<bool>());
        Assert.Equal(0, body["priority"]!.GetValue<int>());
        // The default lyrics model and the installed one that matches a writing role; qwen3-8b matches none.
        Assert.Equal(["google/gemma-4-e4b", "lmstudio-community/gemma-4-31b-it-mlx-4bit", "qwen3.6-35b-a3b"], Keys(body));
        // Lyrics load it with 8192 here, the writing page and sketches with 32768: the gate hears the larger.
        Assert.Equal(32768, Model(body, "google/gemma-4-e4b")["context"]!.GetValue<int>());
        Assert.Equal(19.8, Model(body, "lmstudio-community/gemma-4-31b-it-mlx-4bit")["sizeGb"]!.GetValue<double>());
        Assert.Equal("lmstudio-community/Qwen3.6-35B-A3B-MLX-4bit", Model(body, "qwen3.6-35b-a3b")["source"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_writing_model_too_large_for_the_mac_is_not_listed()
    {
        _app.LlmGateUrl = "http://gate.test:1235";
        _app.LyricsMemoryGb = 24;
        _app.LmStudio.MoreModels.Add(new
        {
            type = "llm",
            key = "lmstudio-community/gemma-4-31b-it-mlx-4bit",
            display_name = "Gemma 4 31B",
            size_bytes = 19_800_000_000L,
            loaded_instances = Array.Empty<object>(),
        });
        Start();

        await TestApp.WaitUntil(() => _app.LlmGate.Registrations().Length > 0);

        Assert.Equal(["google/gemma-4-e4b"], Keys(_app.LlmGate.Registrations()[0].Body));
    }

    [Fact]
    public async Task A_gate_that_does_not_answer_yet_is_tried_again()
    {
        _app.LlmGateUrl = "http://gate.test:1235";
        _app.LlmGate.Unreachable = 3;
        var registration = _app.Services.GetRequiredService<LlmGateRegistration>();

        await TestApp.WaitUntil(() => registration.Registered);

        Assert.Single(_app.LlmGate.Registrations());
        Assert.Equal(0, _app.LlmGate.Unreachable);
    }

    [Fact]
    public async Task Without_an_address_nothing_is_sent()
    {
        var registration = _app.Services.GetRequiredService<LlmGateRegistration>();
        await Task.Delay(200);

        Assert.False(registration.Registered);
        Assert.Empty(_app.LlmGate.Registrations());
    }
}
