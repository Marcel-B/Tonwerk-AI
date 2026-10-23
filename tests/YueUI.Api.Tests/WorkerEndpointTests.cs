using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using YueUI.Api.Library;
using YueUI.Api.Worker;

namespace YueUI.Api.Tests;

public sealed class WorkerEndpointTests : IDisposable
{
    private const string Run = "20260922-101500-Neon-Night";

    private readonly TestApp _app = new();
    private readonly HttpClient _client;

    public WorkerEndpointTests() => _client = _app.CreateClient();

    [Fact]
    public async Task Generate_starts_the_worker_and_sends_the_command()
    {
        var response = await _client.PostAsJsonAsync("/api/generate", new
        {
            style = " Dark synthwave ",
            lyrics = "[verse]\nNeon lights",
            title = "Neon Night",
            batch = 2,
            quality = "full",
            draftSteps = 12,
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var command = await _app.Worker.NextCommand();
        Assert.Equal("generate", (string?)command["cmd"]);
        Assert.Equal("Dark synthwave", (string?)command["style"]);
        Assert.Equal("Neon Night", (string?)command["title"]);
        Assert.Equal(2, (int?)command["batch"]);
        Assert.Equal("full", (string?)command["quality"]);
        Assert.Equal(12, (int?)command["draft_steps"]);
        // No seed given: the worker picks one.
        Assert.True((bool?)command["random_seed"]);
        Assert.Null(command["engines"]);
    }

    [Fact]
    public async Task A_score_and_a_length_limit_reach_the_worker()
    {
        var response = await _client.PostAsJsonAsync("/api/generate", new
        {
            style = "English, piano pop",
            lyrics = "[verse]\nNeon lights",
            cot = "melody",
            maxTokens = 3000,
            abc = "\nX:1\nK:C\nE2G2A2G2|\n",
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var command = await _app.Worker.NextCommand();
        Assert.Equal("melody", (string?)command["cot"]);
        Assert.Equal(3000, (int?)command["max_tokens"]);
        Assert.Equal("X:1\nK:C\nE2G2A2G2|", (string?)command["abc"]);
    }

    [Fact]
    public async Task Defaults_leave_length_and_score_to_the_worker()
    {
        var command = await Generate();

        Assert.Null(command["max_tokens"]);
        Assert.Null(command["abc"]);
    }

    [Fact]
    public async Task An_instrumental_needs_no_lyrics()
    {
        var response = await _client.PostAsJsonAsync("/api/generate", new { style = "Ambient", lyrics = "", instrumental = true });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("", (string?)(await _app.Worker.NextCommand())["lyrics"]);
    }

    [Fact]
    public async Task A_score_without_planning_and_an_impossible_length_are_refused()
    {
        var response = await _client.PostAsJsonAsync("/api/generate", new { style = "Pop", lyrics = "x", cot = "off", abc = "X:1", maxTokens = 100 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonObject>())!["errors"]!.AsObject();
        Assert.Equal(["maxTokens", "abc"], errors.Select(e => e.Key));
    }

    [Fact]
    public async Task A_seed_whose_batch_would_pass_the_models_limit_is_refused()
    {
        var response = await _client.PostAsJsonAsync("/api/generate", new { style = "Pop", lyrics = "x", batch = 2, seed = long.MaxValue });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonObject>())!["errors"]!.AsObject();
        Assert.Equal(["seed"], errors.Select(e => e.Key));
    }

    [Fact]
    public async Task Sampling_full_steps_and_long_songs_reach_the_worker_extension()
    {
        var response = await _client.PostAsJsonAsync("/api/generate", new
        {
            style = "English, piano pop",
            lyrics = "[verse]\nNeon lights",
            quality = "full",
            fullSteps = 48,
            maxTokens = 15000,
            abcSampling = new { temperature = 0.6, topK = 20 },
            semanticSampling = new { topP = 0.9, repetitionPenalty = 1.1, penaltyWindow = 40 },
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var command = await _app.Worker.NextCommand();
        Assert.Equal(48, (int?)command["full_steps"]);
        Assert.Equal(15000, (int?)command["max_tokens"]);
        Assert.Equal("""{"temperature":0.6,"top_k":20}""", command["abc_sampling"]!.ToJsonString());
        Assert.Equal("""{"top_p":0.9,"repetition_penalty":1.1,"penalty_window":40}""", command["semantic_sampling"]!.ToJsonString());
    }

    [Fact]
    public async Task Empty_sampling_overrides_are_left_out()
    {
        var response = await _client.PostAsJsonAsync("/api/generate", new { style = "Pop", lyrics = "x", semanticSampling = new { } });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Null((await _app.Worker.NextCommand())["semantic_sampling"]);
    }

    [Fact]
    public async Task Sampling_outside_the_models_ranges_is_refused()
    {
        var response = await _client.PostAsJsonAsync("/api/generate", new
        {
            style = "Pop",
            lyrics = "x",
            fullSteps = 65,
            abcSampling = new { temperature = 6.0, topP = 0.0 },
            semanticSampling = new { topK = 0, repetitionPenalty = 0.0, penaltyWindow = 101 },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonObject>())!["errors"]!.AsObject();
        Assert.Equal(
            ["fullSteps", "abcSampling.temperature", "abcSampling.topP", "semanticSampling.topK", "semanticSampling.repetitionPenalty", "semanticSampling.penaltyWindow"],
            errors.Select(e => e.Key));
    }

    [Fact]
    public async Task The_status_tells_whether_the_worker_runs_with_the_extension()
    {
        Assert.Null((await _client.GetFromJsonAsync<StatusSnapshot>("/api/status", TestApp.Json))!.Worker.Extensions);

        await Generate();
        _app.Worker.Emit(new { @event = "ready", yueui_extensions = true });

        var status = await _app.WaitForStatus(_client, s => s.Worker.Status == WorkerStatus.Ready);
        Assert.True(status.Worker.Extensions);
    }

    [Fact]
    public async Task A_worker_without_the_extension_is_reported_as_such()
    {
        await Generate();
        _app.Worker.Emit(new { @event = "ready" });

        var status = await _app.WaitForStatus(_client, s => s.Worker.Status == WorkerStatus.Ready);
        Assert.False(status.Worker.Extensions);
    }

    [Fact]
    public async Task Why_the_extension_is_off_is_logged_as_a_warning()
    {
        await Generate();
        _app.Worker.Emit(new { @event = "ready", yueui_extensions = false, yueui_problem = "yue2_worker.run_remote is gone" });

        await _app.WaitForStatus(_client, s => s.Worker.Status == WorkerStatus.Ready);
        var report = await _client.GetStringAsync("/api/logs/report");
        Assert.Contains("yue2_worker.run_remote is gone", report);
    }

    [Fact]
    public void The_worker_extension_ships_with_the_app()
    {
        Assert.True(File.Exists(PythonWorkerLauncher.ExtensionScript), PythonWorkerLauncher.ExtensionScript);
    }

    [Fact]
    public async Task A_second_command_reuses_the_running_worker()
    {
        await Generate();
        await Generate();

        Assert.Equal(1, _app.Launcher.Launches);
    }

    [Fact]
    public async Task A_lora_reaches_the_worker_as_its_file()
    {
        var path = LoraEndpointTests.WriteLora(_app, "dubstep");

        var response = await _client.PostAsJsonAsync("/api/generate", new { style = "dubstep", lyrics = "x", lora = "dubstep", loraStrength = 1.5 });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var command = await _app.Worker.NextCommand();
        Assert.Equal(path, (string?)command["lora"]);
        Assert.Equal(1.5, (double?)command["lora_strength"]);
    }

    [Fact]
    public async Task An_unknown_lora_or_strength_is_refused()
    {
        var unknown = await _client.PostAsJsonAsync("/api/generate", new { style = "dubstep", lyrics = "x", lora = "dubstep" });
        var traversal = await _client.PostAsJsonAsync("/api/generate", new { style = "dubstep", lyrics = "x", lora = "../yueui" });
        var strong = await _client.PostAsJsonAsync("/api/generate", new { style = "dubstep", lyrics = "x", loraStrength = 4 });

        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal(["lora"], (await unknown.Content.ReadFromJsonAsync<JsonObject>())!["errors"]!.AsObject().Select(e => e.Key));
        Assert.Equal(["lora"], (await traversal.Content.ReadFromJsonAsync<JsonObject>())!["errors"]!.AsObject().Select(e => e.Key));
        Assert.Equal(["loraStrength"], (await strong.Content.ReadFromJsonAsync<JsonObject>())!["errors"]!.AsObject().Select(e => e.Key));
        Assert.Equal(0, _app.Launcher.Launches);
    }

    [Fact]
    public async Task Ten_songs_go_to_the_worker_as_one_batch_and_eleven_are_refused()
    {
        var refused = await _client.PostAsJsonAsync("/api/generate", new { style = "pop", lyrics = "[verse]\nla", batch = 11 });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        // The worker composes four at a time and queues the rest itself, so ten simply run through one after another.
        var response = await _client.PostAsJsonAsync("/api/generate", new { style = "pop", lyrics = "[verse]\nla", batch = 10 });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var command = await _app.Worker.NextCommand();
        Assert.Equal(10, (int?)command["batch"]);
    }

    [Fact]
    public async Task Invalid_requests_are_refused_without_starting_a_worker()
    {
        var response = await _client.PostAsJsonAsync("/api/generate", new { style = "", lyrics = "x", batch = 0, quality = "best", cot = "maybe" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonObject>())!["errors"]!.AsObject();
        Assert.Equal(["style", "batch", "quality", "cot"], errors.Select(e => e.Key));
        Assert.Equal(0, _app.Launcher.Launches);
    }

    [Fact]
    public async Task Worker_events_become_song_states()
    {
        await StartSong();
        _app.Worker.Emit(new { @event = "stage", path = _app.AudioPath(Run, "song1"), stage = "tokens", detail = "writing", engine = "gpu" });
        _app.Worker.Emit(new { @event = "progress", path = _app.AudioPath(Run, "song1"), fraction = 0.5, detail = "4000 tokens" });

        var status = await _app.WaitForStatus(_client, s => s.Songs.Any(song => song.Fraction == 0.5));

        Assert.Equal(WorkerStatus.Ready, status.Worker.Status);
        Assert.True(status.Worker.Busy);
        var song = Assert.Single(status.Songs);
        Assert.Equal($"{Run}/song1", song.Id);
        Assert.Equal("Neon Night", song.Title);
        Assert.Equal(831001, song.Seed);
        Assert.Equal("tokens", song.Stage);
        Assert.Equal("4000 tokens", song.Detail);
        Assert.Equal("gpu", song.Engine);
    }

    [Fact]
    public async Task A_finished_song_carries_its_length_and_quality()
    {
        await StartSong();
        _app.Worker.Emit(new { @event = "song", index = 1, path = _app.AudioPath(Run, "song1"), seconds = 187.5, quality = "draft", seed = 831001 });
        _app.Worker.Emit(new { @event = "stage", path = _app.AudioPath(Run, "song1"), stage = "ready", detail = "" });

        var status = await _app.WaitForStatus(_client, s => s.Songs.Any(song => song.Finished));

        var song = Assert.Single(status.Songs);
        Assert.Equal(187.5, song.Seconds);
        Assert.Equal("draft", song.Quality);
        Assert.Equal(1, song.Fraction);
        Assert.False(status.Worker.Busy);
    }

    [Fact]
    public async Task A_finished_song_keeps_the_parameters_it_was_asked_for()
    {
        var response = await _client.PostAsJsonAsync("/api/generate", new
        {
            title = "Neon Night",
            style = "English, piano pop",
            lyrics = "[verse]\nNeon lights",
            quality = "full",
            batch = 2,
            seed = 7,
            maxTokens = 3000,
            semanticSampling = new { topK = 120 },
        });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await _app.Worker.NextCommand();
        _app.Worker.Emit(new { @event = "ready" });
        _app.Worker.Emit(new
        {
            @event = "started",
            job = Run,
            title = "Neon Night",
            songs = new[] { new { index = 1, seed = 7, path = _app.AudioPath(Run, "song1"), priority = 1 } },
        });
        await _app.WaitForStatus(_client, s => s.Songs.Count == 1);
        var directory = _app.AddSong(Run, "song1");
        _app.Worker.Emit(new { @event = "song", index = 1, path = _app.AudioPath(Run, "song1"), seconds = 120.0, quality = "full", seed = 7 });

        var file = Path.Combine(directory, SongLibrary.AppRequestFile);
        await TestApp.WaitUntil(() => File.Exists(file));
        var saved = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        Assert.Equal("""{"top_k":120}""", saved["semantic_sampling"]!.ToJsonString());
        Assert.Equal(3000, (int?)saved["max_tokens"]);
        Assert.Equal("full", (string?)saved["quality"]);
        // Batch and seed concern the whole command; the song's own seed is in YuE Studio's request.json.
        Assert.Null(saved["batch"]);
        Assert.Null(saved["seed"]);
    }

    [Fact]
    public async Task A_finished_song_gets_its_streaming_copy_right_away()
    {
        await StartSong();
        _app.AddSong(Run, "song1");
        _app.Worker.Emit(new { @event = "song", index = 1, path = _app.AudioPath(Run, "song1"), seconds = 187.5, quality = "draft", seed = 831001 });
        _app.Worker.Emit(new { @event = "stage", path = _app.AudioPath(Run, "song1"), stage = "ready", detail = "" });

        await TestApp.WaitUntil(() => File.Exists(Path.Combine(_app.Root, "stream", Run, "song1.m4a")));
        Assert.Equal(_app.AudioPath(Run, "song1"), _app.Encoder.Source);
    }

    [Fact]
    public async Task A_song_keeps_its_stages_in_order_with_their_start()
    {
        await StartSong();
        var path = _app.AudioPath(Run, "song1");
        _app.Worker.Emit(new { @event = "stage", path, stage = "planning", detail = "" });
        _app.Worker.Emit(new { @event = "stage", path, stage = "tokens", detail = "writing" });
        // Same stage again with a new detail: no new step.
        _app.Worker.Emit(new { @event = "stage", path, stage = "tokens", detail = "writing more" });
        _app.Worker.Emit(new { @event = "failed", path, message = "out of memory" });

        var status = await _app.WaitForStatus(_client, s => s.Songs.Any(song => song.Finished));

        var song = Assert.Single(status.Songs);
        Assert.Equal(["queued", "planning", "tokens", "failed"], song.Stages.Select(s => s.Stage));
        Assert.True(song.Stages.Zip(song.Stages.Skip(1)).All(pair => pair.First.StartedAt <= pair.Second.StartedAt));
        Assert.False(song.Render);
    }

    [Fact]
    public async Task A_rendered_song_is_marked_as_a_render()
    {
        _app.AddSong(Run, "song2", files: "semantic.npy");
        await _client.PostAsJsonAsync($"/api/songs/{Run}/song2/render", new { quality = "full" });
        await _app.Worker.NextCommand();
        _app.Worker.Emit(new { @event = "ready" });
        _app.Worker.Emit(new
        {
            @event = "started",
            job = Run,
            title = "Neon Night",
            songs = new[] { new { index = 2, seed = 831002, path = _app.AudioPath(Run, "song2"), priority = 1 } },
        });

        var status = await _app.WaitForStatus(_client, s => s.Songs.Count == 1);

        Assert.True(Assert.Single(status.Songs).Render);
    }

    [Fact]
    public async Task Renaming_a_run_renames_its_songs_in_the_queue_and_in_later_renders()
    {
        _app.AddSong(Run, "song1");
        await StartSong();

        await _client.PutAsJsonAsync($"/api/runs/{Run}/title", new { title = "Neonnächte" });

        var status = await _app.WaitForStatus(_client, s => s.Songs.All(song => song.Title == "Neonnächte"));
        Assert.Single(status.Songs);
        _app.Worker.Emit(new
        {
            @event = "started",
            job = Run,
            title = "Neon Night",
            songs = new[] { new { index = 2, seed = 831002, path = _app.AudioPath(Run, "song2"), priority = 1 } },
        });
        status = await _app.WaitForStatus(_client, s => s.Songs.Count == 2);
        Assert.All(status.Songs, song => Assert.Equal("Neonnächte", song.Title));
    }

    [Fact]
    public async Task Cancel_addresses_the_song_by_its_audio_path()
    {
        await StartSong();

        var response = await _client.PostAsync($"/api/songs/{Run}/song1/cancel", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var command = await _app.Worker.NextCommand();
        Assert.Equal("cancel", (string?)command["cmd"]);
        Assert.Equal(_app.AudioPath(Run, "song1"), (string?)command["path"]);
    }

    [Fact]
    public async Task Cancelling_a_song_that_is_not_queued_is_not_found()
    {
        var response = await _client.PostAsync($"/api/songs/{Run}/song1/cancel", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task When_the_worker_dies_its_songs_fail_and_the_next_command_starts_a_new_one()
    {
        await StartSong();

        _app.Worker.Exit();
        var status = await _app.WaitForStatus(_client, s => s.Worker.Status == WorkerStatus.Stopped);

        Assert.Equal("failed", Assert.Single(status.Songs).Stage);
        await Generate();
        Assert.Equal(2, _app.Launcher.Launches);
    }

    [Fact]
    public async Task Render_needs_the_saved_tokens()
    {
        _app.AddSong(Run, "song1");

        var response = await _client.PostAsJsonAsync($"/api/songs/{Run}/song1/render", new { quality = "full" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Render_sends_the_song_folder()
    {
        var directory = _app.AddSong(Run, "song2", files: "semantic.npy");

        var response = await _client.PostAsJsonAsync($"/api/songs/{Run}/song2/render", new { quality = "full" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var command = await _app.Worker.NextCommand();
        Assert.Equal("render", (string?)command["cmd"]);
        Assert.Equal(directory, (string?)command["path"]);
        Assert.Equal("full", (string?)command["quality"]);
    }

    [Theory]
    [InlineData("..", "song1")]
    [InlineData("transcriptions", "song1")]
    [InlineData(Run, "audio.flac")]
    public async Task Render_refuses_names_outside_the_library(string run, string song)
    {
        var response = await _client.PostAsJsonAsync($"/api/songs/{run}/{song}/render", new { quality = "full" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_song_the_worker_is_on_cannot_be_deleted_until_it_is_finished()
    {
        _app.AddSong(Run, "song1");
        _app.AddSong(Run, "song2");
        await StartSong();

        Assert.Equal(HttpStatusCode.Conflict, (await _client.DeleteAsync($"/api/songs/{Run}/song1")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.DeleteAsync($"/api/runs/{Run}")).StatusCode);
        // Another song of the run is not in the way.
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/songs/{Run}/song2")).StatusCode);

        _app.Worker.Emit(new { @event = "stage", path = _app.AudioPath(Run, "song1"), stage = "cancelled", detail = "" });
        await _app.WaitForStatus(_client, s => s.Songs.All(song => song.Finished));

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/runs/{Run}")).StatusCode);
        Assert.False(Directory.Exists(Path.Combine(_app.OutputDir, Run)));
    }

    [Fact]
    public async Task Deleting_tells_every_open_library_to_reload()
    {
        _app.AddSong(Run, "song1");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/events");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        Assert.Equal("snapshot", (await NextEvent(reader)).Type);

        await _client.DeleteAsync($"/api/runs/{Run}");

        Assert.Equal("library", (await NextEvent(reader)).Type);
    }

    [Fact]
    public async Task Status_reports_a_running_studio_app()
    {
        _app.Studio.Running = true;

        var status = await _app.WaitForStatus(_client, _ => true);

        Assert.True(status.Worker.StudioRunning);
        Assert.Equal(WorkerStatus.Stopped, status.Worker.Status);
    }

    [Fact]
    public async Task Shutdown_ends_the_worker()
    {
        await StartSong();

        var response = await _client.PostAsync("/api/worker/shutdown", null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(_app.Worker.Disposed);
        await _app.WaitForStatus(_client, s => s.Worker.Status == WorkerStatus.Stopped);
    }

    [Fact]
    public async Task Event_stream_starts_with_a_snapshot_and_follows_the_worker()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/events");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());

        Assert.Equal("snapshot", (await NextEvent(reader)).Type);

        await StartSong();
        (string Type, JsonObject Data) song;
        do
        {
            song = await NextEvent(reader);
        }
        while (song.Type != "song");
        Assert.Equal($"{Run}/song1", (string?)song.Data["id"]);
        Assert.Equal("queued", (string?)song.Data["stage"]);
    }

    public void Dispose()
    {
        _client.Dispose();
        _app.Dispose();
    }

    [Fact]
    public async Task Busy_says_what_a_restart_would_cut_off()
    {
        Assert.Equal(new JsonObject { ["busy"] = false, ["reasons"] = new JsonArray() }.ToJsonString(),
            await _client.GetStringAsync("/api/busy"));

        await Generate();

        Assert.Equal(new JsonObject { ["busy"] = true, ["reasons"] = new JsonArray("songs") }.ToJsonString(),
            await _client.GetStringAsync("/api/busy"));
    }

    [Fact]
    public async Task Idle_counts_quiet_time_and_names_what_runs()
    {
        var idle = (await _client.GetFromJsonAsync<JsonObject>("/api/idle"))!;
        Assert.True(idle["idle"]!.GetValue<bool>());
        Assert.True(idle["idleSeconds"]!.GetValue<double>() >= 0);
        Assert.NotNull(idle["idleSince"]);

        await StartSong();

        idle = (await _client.GetFromJsonAsync<JsonObject>("/api/idle"))!;
        Assert.False(idle["idle"]!.GetValue<bool>());
        Assert.Equal(0, idle["idleSeconds"]!.GetValue<double>());
        Assert.Null(idle["idleSince"]);
        Assert.Contains("songs", idle["busy"]!.AsArray().Select(reason => reason!.GetValue<string>()));
    }

    private async Task<JsonObject> Generate()
    {
        var response = await _client.PostAsJsonAsync("/api/generate", new { style = "Dark synthwave", lyrics = "[verse]\nNeon", title = "Neon Night" });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return await _app.Worker.NextCommand();
    }

    /// <summary>A generate command and the worker's answer to it: ready, then one queued song.</summary>
    private async Task StartSong()
    {
        await Generate();
        _app.Worker.Emit(new { @event = "ready" });
        _app.Worker.Emit(new
        {
            @event = "started",
            job = Run,
            title = "Neon Night",
            songs = new[] { new { index = 1, seed = 831001, path = _app.AudioPath(Run, "song1"), priority = 1 } },
        });
        await _app.WaitForStatus(_client, s => s.Songs.Count == 1);
    }

    private static async Task<(string Type, JsonObject Data)> NextEvent(StreamReader reader)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        string? type = null, data = null;
        while (await reader.ReadLineAsync(timeout.Token) is { } line)
        {
            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                type = line[7..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                data = line[6..];
            }
            else if (line.Length == 0 && type is not null)
            {
                return (type, JsonNode.Parse(data ?? "{}")!.AsObject());
            }
        }
        throw new EndOfStreamException();
    }
}
