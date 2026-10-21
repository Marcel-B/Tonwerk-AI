using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using YueUI.Api.Worker;

namespace YueUI.Api.Tests;

public sealed class LyricsEndpointTests : IDisposable
{
    private readonly TestApp _app = new();
    private readonly HttpClient _client;

    public LyricsEndpointTests() => _client = _app.CreateClient();

    [Fact]
    public async Task The_model_is_loaded_with_a_small_context_asked_with_YuE2s_rules_and_unloaded_after()
    {
        var draft = await Finished(new { keywords = "night train, leaving home", style = "English, melancholic folk, 80 BPM" });

        Assert.Equal("done", draft.Stage);
        Assert.Equal("[Verse]\nLine one\n\n[Chorus]\nLine two", draft.Lyrics);

        var load = Assert.Single(_app.LmStudio.Requests, r => r.Path == "/api/v1/models/load").Body!;
        Assert.Equal("google/gemma-4-e4b", (string?)load["model"]);
        Assert.Equal(8192, (int?)load["context_length"]);
        var completion = Assert.Single(_app.LmStudio.Requests, r => r.Path == "/v1/chat/completions").Body!;
        Assert.Equal("google/gemma-4-e4b:1", (string?)completion["model"]);
        Assert.Equal(60, (int?)completion["ttl"]);
        Assert.Equal(8192 - 1024, (int?)completion["max_tokens"]);
        var messages = completion["messages"]!.AsArray();
        Assert.Contains("[Verse]", (string?)messages[0]!["content"], StringComparison.Ordinal);
        Assert.Contains("Write in English", (string?)messages[0]!["content"], StringComparison.Ordinal);
        var user = (string)messages[1]!["content"]!;
        Assert.Contains("night train, leaving home", user, StringComparison.Ordinal);
        Assert.Contains("melancholic folk", user, StringComparison.Ordinal);

        Assert.Equal("/api/v1/models/unload", _app.LmStudio.Requests[^1].Path);
        Assert.Equal("google/gemma-4-e4b:1", (string?)_app.LmStudio.Requests[^1].Body!["instance_id"]);
    }

    [Fact]
    public async Task A_reasoning_effort_goes_through_LM_Studios_native_chat_with_the_same_prompts()
    {
        var draft = await Finished(new { keywords = "night train", style = "folk", reasoning = "low" });

        Assert.Equal("done", draft.Stage);
        Assert.Equal("[Verse]\nLine one\n\n[Chorus]\nLine two", draft.Lyrics);
        Assert.DoesNotContain(_app.LmStudio.Requests, r => r.Path == "/v1/chat/completions");
        var chat = Assert.Single(_app.LmStudio.Requests, r => r.Path == "/api/v1/chat").Body!;
        Assert.Equal("low", (string?)chat["reasoning"]);
        Assert.Equal("google/gemma-4-e4b:1", (string?)chat["model"]);
        Assert.Equal(8192 - 1024, (int?)chat["max_output_tokens"]);
        Assert.False((bool?)chat["store"]);
        Assert.Contains("[Verse]", (string?)chat["system_prompt"], StringComparison.Ordinal);
        Assert.Contains("night train", (string?)chat["input"], StringComparison.Ordinal);
        Assert.Equal("/api/v1/models/unload", _app.LmStudio.Requests[^1].Path);
    }

    [Fact]
    public async Task A_model_refusing_the_reasoning_effort_drafts_without_it()
    {
        _app.LmStudio.RefusesReasoning = true;

        var draft = await Finished(new { keywords = "night train", reasoning = "high" });

        Assert.Equal("done", draft.Stage);
        Assert.Single(_app.LmStudio.Requests, r => r.Path == "/api/v1/chat");
        var completion = Assert.Single(_app.LmStudio.Requests, r => r.Path == "/v1/chat/completions").Body!;
        Assert.Null(completion["reasoning"]);
    }

    [Fact]
    public async Task An_unknown_reasoning_effort_is_refused()
    {
        var response = await _client.PostAsJsonAsync("/api/lyrics", new { keywords = "night train", reasoning = "xhigh" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task German_lyrics_are_asked_for_in_German_with_the_tags_kept_in_English()
    {
        var draft = await Finished(new { keywords = "Nachtzug, Abschied", style = "German, synthwave", language = "german" });

        Assert.Equal("done", draft.Stage);
        var system = (string)Assert.Single(_app.LmStudio.Requests, r => r.Path == "/v1/chat/completions").Body!["messages"]![0]!["content"]!;
        Assert.Contains("Write in German", system, StringComparison.Ordinal);
        Assert.DoesNotContain("Write in English", system, StringComparison.Ordinal);
        Assert.Contains("Keep these tags in English", system, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_picker_lists_LM_Studios_language_models_and_the_default()
    {
        var models = (await _client.GetFromJsonAsync<JsonObject>("/api/lyrics/models"))!;

        Assert.Equal("google/gemma-4-e4b", (string?)models["default"]);
        var list = models["models"]!.AsArray();
        Assert.Equal(["google/gemma-4-e4b", "qwen/qwen3-8b"], list.Select(m => (string?)m!["id"]));
        Assert.Equal("Qwen3 8B", (string?)list[1]!["name"]);
        Assert.Equal(5_500_000_000L, (long?)list[1]!["sizeBytes"]);
        Assert.DoesNotContain(_app.LmStudio.Requests, r => r.Path == "/api/v1/models/load");
    }

    [Fact]
    public async Task The_picker_says_which_models_can_read_a_photo()
    {
        var list = (await _client.GetFromJsonAsync<JsonObject>("/api/lyrics/models"))!["models"]!.AsArray();

        Assert.Equal([true, false], list.Select(m => (bool?)m!["vision"]));
    }

    /// <summary>A 1×1 JPEG's worth of base64; the server only checks the form, LM Studio reads the picture.</summary>
    private const string Photo = "data:image/jpeg;base64,/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8=";

    [Fact]
    public async Task A_photo_goes_to_the_model_as_an_image_beside_the_keywords()
    {
        var draft = await Finished(new { keywords = "summer", style = "Pop", image = Photo });

        Assert.Equal("done", draft.Stage);
        var completion = Assert.Single(_app.LmStudio.Requests, r => r.Path == "/v1/chat/completions").Body!;
        var content = completion["messages"]![1]!["content"]!.AsArray();
        Assert.Equal("text", (string?)content[0]!["type"]);
        var text = (string)content[0]!["text"]!;
        Assert.Contains("photo", text, StringComparison.Ordinal);
        Assert.Contains("summer", text, StringComparison.Ordinal);
        Assert.Contains("Pop", text, StringComparison.Ordinal);
        Assert.Equal("image_url", (string?)content[1]!["type"]);
        Assert.Equal(Photo, (string?)content[1]!["image_url"]!["url"]);
        Assert.Equal(8192 - 1024 - 1536, (int?)completion["max_tokens"]);
    }

    [Fact]
    public async Task A_photo_needs_no_keywords()
    {
        var draft = await Finished(new { image = Photo });

        Assert.Equal("done", draft.Stage);
        var text = (string)Assert.Single(_app.LmStudio.Requests, r => r.Path == "/v1/chat/completions").Body!["messages"]![1]!["content"]![0]!["text"]!;
        Assert.DoesNotContain("Focus on", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://example.com/photo.jpg")]
    [InlineData("data:image/gif;base64,R0lGODlh")]
    [InlineData("data:image/jpeg;base64,not base64!")]
    [InlineData("data:image/jpeg;base64,")]
    public async Task Anything_but_a_photo_as_a_data_url_is_refused(string image)
    {
        var response = await _client.PostAsJsonAsync("/api/lyrics", new { keywords = "summer", image });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("image", (await response.Content.ReadFromJsonAsync<JsonObject>())!["errors"]!.AsObject().Select(e => e.Key));
        Assert.Empty(_app.LmStudio.Requests);
    }

    [Fact]
    public async Task A_revision_sends_the_lyrics_and_the_instruction_and_keeps_the_rest()
    {
        const string lyrics = "[Verse]\nOld line one\n\n[Chorus]\nOld chorus";
        var draft = await Finished(new
        {
            lyrics,
            instruction = "make the chorus catchier",
            keywords = "night train",
            style = "Pop",
            language = "german",
        });

        Assert.Equal("done", draft.Stage);
        Assert.Equal("[Verse]\nLine one\n\n[Chorus]\nLine two", draft.Lyrics);
        var completion = Assert.Single(_app.LmStudio.Requests, r => r.Path == "/v1/chat/completions").Body!;
        var messages = completion["messages"]!.AsArray();
        Assert.Contains("Write in German", (string?)messages[0]!["content"], StringComparison.Ordinal);
        var user = (string)messages[1]!["content"]!;
        Assert.Contains(lyrics, user, StringComparison.Ordinal);
        Assert.Contains("What to change: make the chorus catchier", user, StringComparison.Ordinal);
        Assert.Contains("keep every other line word for word", user, StringComparison.Ordinal);
        Assert.Contains("night train", user, StringComparison.Ordinal);
        Assert.Contains("Pop", user, StringComparison.Ordinal);
        Assert.Equal(8192 - 1024 - (lyrics.Length / 3 + 1), (int?)completion["max_tokens"]);
        Assert.Equal("/api/v1/models/unload", _app.LmStudio.Requests[^1].Path);
    }

    [Theory]
    [InlineData("[Verse]\nLa", "", "instruction")]
    [InlineData("", "make it sadder", "lyrics")]
    [InlineData(null, "make it sadder", "lyrics")]
    [InlineData("[Verse]\nLa", null, "instruction")]
    public async Task A_revision_needs_lyrics_and_an_instruction(string? lyrics, string? instruction, string field)
    {
        var response = await _client.PostAsJsonAsync("/api/lyrics", new { keywords = "summer", lyrics, instruction });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field, (await response.Content.ReadFromJsonAsync<JsonObject>())!["errors"]!.AsObject().Select(e => e.Key));
        Assert.Empty(_app.LmStudio.Requests);
    }

    [Fact]
    public async Task A_revision_takes_no_photo_and_no_overlong_lyrics()
    {
        var photo = await _client.PostAsJsonAsync("/api/lyrics", new { lyrics = "[Verse]\nLa", instruction = "sadder", image = Photo });
        var overlong = await _client.PostAsJsonAsync("/api/lyrics", new { lyrics = new string('a', LyricsEndpoints.MaxLyricsLength + 1), instruction = "sadder" });

        Assert.Equal(HttpStatusCode.BadRequest, photo.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, overlong.StatusCode);
        Assert.Empty(_app.LmStudio.Requests);
    }

    [Fact]
    public async Task Listing_models_starts_LM_Studio_and_says_when_it_cannot()
    {
        _app.LmStudio.Running = false;
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/lyrics/models")).StatusCode);
        Assert.Equal(1, _app.LmStudio.Starts);

        _app.LmStudio.Running = false;
        _app.LmStudio.CanStart = false;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await _client.GetAsync("/api/lyrics/models")).StatusCode);
    }

    [Fact]
    public async Task A_chosen_model_is_loaded_asked_and_unloaded_instead_of_the_default()
    {
        var draft = await Finished(new { keywords = "summer", model = "qwen/qwen3-8b" });

        Assert.Equal("done", draft.Stage);
        Assert.Equal("qwen/qwen3-8b", (string?)Assert.Single(_app.LmStudio.Requests, r => r.Path == "/api/v1/models/load").Body!["model"]);
        Assert.Equal("qwen/qwen3-8b:1", (string?)Assert.Single(_app.LmStudio.Requests, r => r.Path == "/v1/chat/completions").Body!["model"]);
        Assert.Equal("qwen/qwen3-8b:1", (string?)_app.LmStudio.Requests[^1].Body!["instance_id"]);
    }

    [Fact]
    public async Task An_overlong_model_id_is_refused()
    {
        var response = await _client.PostAsJsonAsync("/api/lyrics", new { keywords = "summer", model = new string('x', 201) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_app.LmStudio.Requests);
    }

    [Fact]
    public async Task An_unknown_language_is_refused()
    {
        var response = await _client.PostAsJsonAsync("/api/lyrics", new { keywords = "summer", language = "klingon" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_app.LmStudio.Requests);
    }

    [Fact]
    public async Task A_model_already_loaded_in_LM_Studio_is_used_and_left_loaded()
    {
        _app.LmStudio.LoadedInstance = "google/gemma-4-e4b:2";

        await Draft();

        Assert.DoesNotContain(_app.LmStudio.Requests, r => r.Path is "/api/v1/models/load" or "/api/v1/models/unload");
        Assert.Equal("google/gemma-4-e4b:2", (string?)Assert.Single(_app.LmStudio.Requests, r => r.Path == "/v1/chat/completions").Body!["model"]);
    }

    [Fact]
    public async Task A_load_LM_Studio_refuses_for_lack_of_memory_is_explained_without_asking_the_model()
    {
        _app.LmStudio.LoadRefusal = "Model loading was stopped due to insufficient system resources.";

        var draft = await Finished(new { keywords = "summer" });

        Assert.Contains("insufficient system resources", draft.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_app.LmStudio.Requests, r => r.Path is "/v1/chat/completions" or "/api/v1/models/unload");
    }

    [Fact]
    public async Task A_refused_load_is_tried_again_with_half_the_context_down_to_the_minimum()
    {
        _app.LmStudio.LoadRefusal = "Model loading was stopped due to insufficient system resources.";

        var draft = await Finished(new { keywords = "summer" });

        Assert.Contains("not even with a context of 2048 tokens", draft.Message, StringComparison.Ordinal);
        Assert.Equal([8192, 4096, 2048], _app.LmStudio.Requests.Where(r => r.Path == "/api/v1/models/load").Select(r => (int)r.Body!["context_length"]!));
    }

    [Fact]
    public async Task A_smaller_context_LM_Studio_accepts_is_what_the_answer_gets()
    {
        _app.LmStudio.LoadRefusal = "Model loading was stopped due to insufficient system resources.";
        _app.LmStudio.RefusesContext = context => context > 4096;

        Assert.Equal("[Verse]\nLine one\n\n[Chorus]\nLine two", await Draft());

        Assert.Equal([8192, 4096], _app.LmStudio.Requests.Where(r => r.Path == "/api/v1/models/load").Select(r => (int)r.Body!["context_length"]!));
        Assert.Equal(4096 - 1024, (int?)Assert.Single(_app.LmStudio.Requests, r => r.Path == "/v1/chat/completions").Body!["max_tokens"]);
        Assert.Equal("/api/v1/models/unload", _app.LmStudio.Requests[^1].Path);
    }

    [Fact]
    public async Task The_answer_gets_the_context_asked_for_whatever_LM_Studio_echoes()
    {
        _app.LmStudio.EchoedContext = 4096;

        await Draft();

        Assert.Equal(8192 - 1024, (int?)Assert.Single(_app.LmStudio.Requests, r => r.Path == "/v1/chat/completions").Body!["max_tokens"]);
    }

    [Fact]
    public async Task Other_loaded_models_are_unloaded_before_the_context_is_made_smaller()
    {
        _app.LmStudio.OtherLoadedInstance = "qwen/qwen3-8b";
        _app.LmStudio.LoadRefusal = "Model loading was stopped due to insufficient system resources.";
        _app.LmStudio.RefusesContext = _ => _app.LmStudio.OtherLoadedInstance is not null;

        await Draft();

        Assert.Null(_app.LmStudio.OtherLoadedInstance);
        Assert.Equal([8192, 8192], _app.LmStudio.Requests.Where(r => r.Path == "/api/v1/models/load").Select(r => (int)r.Body!["context_length"]!));
    }

    [Fact]
    public async Task Other_loaded_models_stay_when_the_model_loads()
    {
        _app.LmStudio.OtherLoadedInstance = "qwen/qwen3-8b";

        await Draft();

        Assert.Equal("qwen/qwen3-8b", _app.LmStudio.OtherLoadedInstance);
    }

    [Fact]
    public async Task A_load_refused_for_another_reason_is_not_tried_again()
    {
        _app.LmStudio.LoadRefusal = "Model not found";

        var draft = await Finished(new { keywords = "summer" });

        Assert.Contains("Model not found", draft.Message, StringComparison.Ordinal);
        Assert.Single(_app.LmStudio.Requests, r => r.Path == "/api/v1/models/load");
    }

    [Fact]
    public async Task A_model_that_only_thought_until_its_tokens_ran_out_says_so()
    {
        _app.LmStudio.RawAnswer = new
        {
            choices = new[] { new { finish_reason = "length", message = new { role = "assistant", content = "", reasoning_content = "Let me think about trains…" } } },
        };

        var draft = await Finished(new { keywords = "summer" });

        Assert.Equal("failed", draft.Stage);
        Assert.Contains("thinking", draft.Message, StringComparison.Ordinal);
        Assert.Equal("/api/v1/models/unload", _app.LmStudio.Requests[^1].Path);
    }

    [Fact]
    public async Task What_models_add_around_the_lyrics_is_removed()
    {
        _app.LmStudio.Answer = "<think>\nThe user wants a sad song.\n</think>\n```\n**Verse 1:**\nRain on the window\n\n\n\nchorus\nHold on  \n```";

        var draft = await Draft();

        Assert.Equal("[Verse 1]\nRain on the window\n\n[chorus]\nHold on", draft);
    }

    [Fact]
    public async Task A_server_that_does_not_answer_is_started_with_lms()
    {
        _app.LmStudio.Running = false;

        Assert.Equal("done", (await Finished(new { keywords = "summer" })).Stage);
        Assert.Equal(1, _app.LmStudio.Starts);
    }

    [Fact]
    public async Task Without_lms_an_unreachable_server_is_explained()
    {
        _app.LmStudio.Running = false;
        _app.LmStudio.CanStart = false;

        var draft = await Finished(new { keywords = "summer" });

        Assert.Equal("failed", draft.Stage);
        Assert.Contains("does not answer", draft.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refusal_of_LM_Studio_is_passed_on_and_the_model_still_unloaded()
    {
        _app.LmStudio.Status = HttpStatusCode.NotFound;

        var draft = await Finished(new { keywords = "summer" });

        Assert.Equal("failed", draft.Stage);
        Assert.Contains("Model not found", draft.Message, StringComparison.Ordinal);
        Assert.Equal("/api/v1/models/unload", _app.LmStudio.Requests[^1].Path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Keywords_are_required(string keywords)
    {
        var response = await _client.PostAsJsonAsync("/api/lyrics", new { keywords });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_app.LmStudio.Requests);
    }

    [Fact]
    public async Task A_draft_waits_in_the_queue_while_YuE2_generates()
    {
        await StartSong();

        var response = await _client.PostAsJsonAsync("/api/lyrics", new { keywords = "summer" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var queued = (await response.Content.ReadFromJsonAsync<LyricsState>(TestApp.Json))!;
        Assert.Equal("queued", queued.Stage);
        var job = Assert.Single((await _app.WaitForStatus(_client, s => s.Queue is { Count: 1 })).Queue!);
        Assert.Equal((queued.Id, Queue.JobKind.Lyrics, "summer"), (job.Id, job.Kind, job.Title));
        await Task.Delay(100);
        Assert.Empty(_app.LmStudio.Requests);
        Assert.False(_app.Worker.Disposed);

        _app.Worker.Emit(new { @event = "stage", path = _app.AudioPath("20260922-101500-Neon-Night", "song1"), stage = "ready", detail = "" });

        var draft = (await _app.WaitForStatus(_client, s => s.Lyrics is { Finished: true } lyrics && lyrics.Id == queued.Id)).Lyrics!;
        Assert.Equal("done", draft.Stage);
        Assert.True(_app.Worker.Disposed);
        // The queue lets go of the job once the draft has started, which a fast draft can overtake.
        await _app.WaitForStatus(_client, s => s.Queue is { Count: 0 });
    }

    [Fact]
    public async Task An_idle_YuE_worker_is_stopped_to_free_its_memory()
    {
        await StartSong();
        _app.Worker.Emit(new { @event = "stage", path = _app.AudioPath("20260922-101500-Neon-Night", "song1"), stage = "ready", detail = "" });
        await _app.WaitForStatus(_client, s => !s.Worker.Busy);

        await Finished(new { keywords = "summer" });

        Assert.True(_app.Worker.Disposed);
    }

    [Fact]
    public async Task While_lyrics_are_written_songs_and_a_second_draft_wait_in_order()
    {
        _app.LmStudio.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drafting = await _client.PostAsJsonAsync("/api/lyrics", new { keywords = "summer" });
        Assert.Equal(HttpStatusCode.Accepted, drafting.StatusCode);
        var id = (await drafting.Content.ReadFromJsonAsync<LyricsState>(TestApp.Json))!.Id;
        await WaitFor(() => _app.LmStudio.Requests.Any(r => r.Path == "/v1/chat/completions"));
        Assert.Equal("writing", (await _app.WaitForStatus(_client, s => s.Lyrics?.Id == id)).Lyrics!.Stage);

        var song = await _client.PostAsJsonAsync("/api/generate", new { style = "Pop", lyrics = "[verse]\nLa", title = "Sun", batch = 2 });
        var second = await _client.PostAsJsonAsync("/api/lyrics", new { keywords = "winter" });

        Assert.Equal(HttpStatusCode.Accepted, song.StatusCode);
        var songJob = (await song.Content.ReadFromJsonAsync<Queue.QueuedJob>(TestApp.Json))!;
        Assert.Equal((Queue.JobKind.Song, "Sun", 2, "draft"), (songJob.Kind, songJob.Title, songJob.Batch, songJob.Quality));
        var secondId = (await second.Content.ReadFromJsonAsync<LyricsState>(TestApp.Json))!.Id;
        Assert.Equal([songJob.Id, secondId], _app.Snapshot().Queue!.Select(j => j.Id));
        Assert.Equal(0, _app.Launcher.Launches);

        _app.LmStudio.Gate.SetResult();
        await _app.WaitForStatus(_client, s => s.Lyrics is { Stage: "done" } lyrics && lyrics.Id == id);

        // The song goes to the worker; the second draft waits for it, even before the worker has said it started.
        Assert.Equal("generate", (string?)(await (await _app.StartedWorker()).NextCommand())["cmd"]);
        Assert.Equal([secondId], (await _app.WaitForStatus(_client, s => s.Queue is { Count: 1 })).Queue!.Select(j => j.Id));
        await Task.Delay(100);
        Assert.Equal(1, _app.LmStudio.Requests.Count(r => r.Path == "/v1/chat/completions"));
        Assert.False(_app.Worker.Disposed);

        var path = _app.AudioPath("20260927-120000-Sun", "song1");
        _app.Worker.Emit(new { @event = "started", job = "20260927-120000-Sun", title = "Sun", songs = new[] { new { index = 1, path } } });
        await _app.WaitForStatus(_client, s => s.Songs.Count == 1);
        await Task.Delay(100);
        Assert.False(_app.Worker.Disposed);

        _app.Worker.Emit(new { @event = "stage", path, stage = "ready", detail = "" });
        Assert.Equal("done", (await _app.WaitForStatus(_client, s => s.Lyrics is { Finished: true } lyrics && lyrics.Id == secondId)).Lyrics!.Stage);
    }

    public void Dispose()
    {
        _client.Dispose();
        _app.Dispose();
    }

    private async Task<string> Draft()
    {
        var draft = await Finished(new { keywords = "rain" });
        Assert.Equal("done", draft.Stage);
        return draft.Lyrics!;
    }

    /// <summary>Starts a draft (202) and waits for its result in the status, where the browsers get it.</summary>
    private async Task<LyricsState> Finished(object request)
    {
        var response = await _client.PostAsJsonAsync("/api/lyrics", request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<LyricsState>(TestApp.Json))!.Id;
        return (await _app.WaitForStatus(_client, s => s.Lyrics is { Finished: true } lyrics && lyrics.Id == id)).Lyrics!;
    }

    /// <summary>A generate command and one queued song, as in WorkerEndpointTests.</summary>
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

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException();
            }
            await Task.Delay(20);
        }
    }
}
