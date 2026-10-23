using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using YueUI.Api.Memory;
using YueUI.Api.Share;
using YueUI.Api.Speech;
using YueUI.Api.Worker;

namespace YueUI.Api.Tests;

public sealed class SpeechEndpointTests : IDisposable
{
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task The_lab_lists_its_models_and_whether_they_are_downloaded()
    {
        var info = await _app.CreateClient().GetFromJsonAsync<SpeechInfo>("/api/speech", TestApp.Json);

        Assert.True(info!.Installed);
        Assert.EndsWith(Path.Combine("speech", "env", "bin", "python"), info.Python);
        Assert.Equal(["chatterbox", "qwen3-tts", "higgs-v2", "higgs-v3", "higgs-v3-calm", "moss-tts"], info.Models.Select(m => m.Id));
        Assert.Equal(["chatterbox"], info.Models.Where(m => m.Downloaded).Select(m => m.Id));
        Assert.Equal(["mossformer2-se", "deepfilternet3"], info.CleanModels.Select(m => m.Id));
        Assert.Empty(info.Voices);
        Assert.Empty(info.Takes);
    }

    [Fact]
    public async Task A_browser_recording_becomes_a_voice_to_clone()
    {
        var client = _app.CreateClient();

        var response = await client.PostAsync("/api/speech/voices", Recording(" Marcel ", "Hallo, das ist meine Stimme."));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var voice = await response.Content.ReadFromJsonAsync<SpeechVoice>(TestApp.Json);
        Assert.Equal(("Marcel", "Hallo, das ist meine Stimme.", 8.0), (voice!.Label, voice.Transcript, voice.Seconds));
        Assert.Equal("1A45DFA3", Assert.Single(_app.Speech.Recordings));
        var audio = await client.GetAsync($"/api/speech/voices/{voice.Id}/audio");
        Assert.Equal("audio/wav", audio.Content.Headers.ContentType?.MediaType);
        Assert.Single((await client.GetFromJsonAsync<SpeechInfo>("/api/speech", TestApp.Json))!.Voices);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/speech/voices/{voice.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/speech/voices/{voice.Id}/audio")).StatusCode);
    }

    [Fact]
    public async Task A_recording_of_mostly_silence_is_refused()
    {
        _app.Speech.VoiceSeconds = 1.2;
        var client = _app.CreateClient();

        var response = await client.PostAsync("/api/speech/voices", Recording("Marcel", ""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("1.2 seconds", await response.Content.ReadAsStringAsync());
        Assert.Empty((await client.GetFromJsonAsync<SpeechInfo>("/api/speech", TestApp.Json))!.Voices);
    }

    [Fact]
    public async Task Each_model_speaks_the_text_with_the_recorded_voice_one_after_the_other()
    {
        var client = _app.CreateClient();
        var voice = await (await client.PostAsync("/api/speech/voices", Recording("Marcel", "Hallo, das ist meine Stimme.")))
            .Content.ReadFromJsonAsync<SpeechVoice>(TestApp.Json);

        var response = await client.PostAsJsonAsync("/api/speech/takes", new { text = " Willkommen zum Podcast. ", voiceId = voice!.Id, models = new[] { "higgs-v2", "chatterbox" } });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var takes = await response.Content.ReadFromJsonAsync<SpeechTake[]>(TestApp.Json);
        // In the order the lab offers them, whatever order they were ticked in.
        Assert.Equal(["chatterbox", "higgs-v2"], takes!.Select(t => t.ModelId));
        Assert.All(takes!, t => Assert.Equal(("queued", "Willkommen zum Podcast.", "Marcel"), (t.Stage, t.Text, t.VoiceLabel)));

        var done = await WaitForTakes(client, all => all.Count == 2 && all.All(t => t.Finished));
        Assert.All(done, t => Assert.Equal(("done", 2.5, 12.5, 3.25, 4.8), (t.Stage, t.Seconds, t.LoadSeconds, t.SpeakSeconds, t.PeakMemoryGb)));
        Assert.Equal(["mlx-community/chatterbox-multilingual-v3", "mlx-community/higgs-audio-v2-3B-mlx-q8"], _app.Speech.Jobs.Select(j => j.Model.Repo));
        var job = _app.Speech.Jobs[0];
        Assert.Equal(("Willkommen zum Podcast.", "Hallo, das ist meine Stimme.", "de"), (job.Text, job.RefText, job.Model.LangCode));
        Assert.EndsWith(Path.Combine("speech", "voices", $"{voice.Id}.wav"), job.RefAudio);

        var audio = await client.GetAsync($"/api/speech/takes/{takes![0].Id}/audio?download=true");
        Assert.Equal(HttpStatusCode.OK, audio.StatusCode);
        Assert.Equal("audio/wav", audio.Content.Headers.ContentType?.MediaType);
        var disposition = audio.Content.Headers.ContentDisposition;
        Assert.StartsWith("Chatterbox Multilingual v3-", disposition?.FileNameStar ?? disposition?.FileName?.Trim('"'));
    }

    [Fact]
    public async Task Without_a_recording_each_model_speaks_with_its_own_voice()
    {
        var client = _app.CreateClient();

        await client.PostAsJsonAsync("/api/speech/takes", new { text = "Hallo", models = new[] { "qwen3-tts" } });

        await WaitForTakes(client, all => all.Single().Finished);
        var job = Assert.Single(_app.Speech.Jobs);
        Assert.Equal((null, null, "german"), (job.RefAudio, job.RefText, job.Model.LangCode));
    }

    [Fact]
    public async Task A_take_says_when_its_work_began_while_waiting_says_nothing()
    {
        var client = _app.CreateClient();

        var takes = await (await client.PostAsJsonAsync("/api/speech/takes", new { text = "Hallo", models = new[] { "qwen3-tts" } }))
            .Content.ReadFromJsonAsync<SpeechTake[]>(TestApp.Json);
        Assert.Null(Assert.Single(takes!).StartedAt);

        await WaitForTakes(client, all => all.Single().Finished);
        // The queue page reads the snapshot, which keeps the last finished take.
        var status = await client.GetFromJsonAsync<StatusSnapshot>("/api/status", TestApp.Json);
        var take = Assert.Single(status!.Speech!);
        Assert.NotNull(take.StartedAt);
        Assert.InRange(take.StartedAt!.Value, take.CreatedAt, take.UpdatedAt);
    }

    [Fact]
    public async Task A_request_the_lab_cannot_speak_is_refused()
    {
        var client = _app.CreateClient();

        var empty = await client.PostAsJsonAsync("/api/speech/takes", new { text = " ", models = new[] { "chatterbox" } });
        var unknownModel = await client.PostAsJsonAsync("/api/speech/takes", new { text = "Hallo", models = new[] { "bark" } });
        var unknownVoice = await client.PostAsJsonAsync("/api/speech/takes", new { text = "Hallo", voiceId = "nope", models = new[] { "chatterbox" } });

        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Contains("\"text\"", await empty.Content.ReadAsStringAsync());
        Assert.Contains("\"models\"", await unknownModel.Content.ReadAsStringAsync());
        Assert.Contains("\"voiceId\"", await unknownVoice.Content.ReadAsStringAsync());
        Assert.Empty(_app.Speech.Jobs);
    }

    [Fact]
    public async Task Without_mlx_audio_the_lab_says_how_to_install_it()
    {
        _app.Speech.Installed = false;
        var client = _app.CreateClient();

        var response = await client.PostAsJsonAsync("/api/speech/takes", new { text = "Hallo", models = new[] { "chatterbox" } });

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        Assert.Contains("install-speech.sh", await response.Content.ReadAsStringAsync());
        Assert.False((await client.GetFromJsonAsync<SpeechInfo>("/api/speech", TestApp.Json))!.Installed);
    }

    [Fact]
    public async Task A_take_waits_while_songs_are_generated_and_then_shuts_yue2_down()
    {
        var client = _app.CreateClient();
        await client.PostAsJsonAsync("/api/generate", new { style = "pop", lyrics = "[verse]\nLa", title = "X" });
        await _app.Worker.NextCommand();
        _app.Worker.Emit(new { @event = "started", job = "20260926-120000-X", title = "X", songs = new[] { new { path = _app.AudioPath("20260926-120000-X", "song1"), index = 1 } } });
        await _app.WaitForStatus(client, s => s.Worker.Busy);

        await client.PostAsJsonAsync("/api/speech/takes", new { text = "Hallo", models = new[] { "chatterbox" } });
        await Task.Delay(100);
        Assert.Empty(_app.Speech.Jobs);

        _app.Worker.Emit(new { @event = "stage", path = _app.AudioPath("20260926-120000-X", "song1"), stage = "ready" });
        Assert.Equal("done", (await WaitForTakes(client, all => all.Single().Finished)).Single().Stage);
        // YuE2 leaves the memory before the speech model takes it.
        Assert.True(_app.Worker.Disposed);
    }

    [Fact]
    public async Task A_song_asked_for_while_a_take_speaks_waits_in_the_queue()
    {
        _app.Speech.Gate = new TaskCompletionSource();
        var client = _app.CreateClient();
        await client.PostAsJsonAsync("/api/speech/takes", new { text = "Hallo", models = new[] { "chatterbox" } });
        await TestApp.WaitUntil(() => _app.Speech.Jobs.Count == 1);

        var response = await client.PostAsJsonAsync("/api/generate", new { style = "pop", lyrics = "[verse]\nLa", title = "X" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Null(_app.Launcher.Current);
        Assert.Single((await _app.WaitForStatus(client, s => s.Queue is { Count: 1 })).Queue!);

        _app.Speech.Gate.SetResult();
        Assert.Equal("generate", (string?)(await (await _app.StartedWorker()).NextCommand())["cmd"]);
    }

    [Fact]
    public async Task The_status_carries_the_takes_in_the_works_so_the_queue_can_show_them()
    {
        _app.Speech.Gate = new TaskCompletionSource();
        var client = _app.CreateClient();
        await client.PostAsJsonAsync(
            "/api/speech/takes",
            new { text = "Hallo", models = new[] { "chatterbox", "qwen3-tts", "moss-tts" } });
        await TestApp.WaitUntil(() => _app.Speech.Jobs.Count == 1);

        var status = await _app.WaitForStatus(client, s => s.Speech is { Count: 3 } && s.Speech[0].Stage == "speaking");
        Assert.Equal(["chatterbox", "qwen3-tts", "moss-tts"], status.Speech!.Select(t => t.ModelId));
        Assert.Equal(["speaking", "queued", "queued"], status.Speech!.Select(t => t.Stage));

        _app.Speech.Gate.SetResult();
        await WaitForTakes(client, all => all.Count == 3 && all.All(t => t.Finished));

        // A finished take leaves with the next change; the last one stays until then, like a version or stems.
        Assert.Equal(["moss-tts"], _app.Snapshot().Speech!.Select(t => t.ModelId));
    }

    [Fact]
    public async Task Deleting_a_take_that_is_spoken_stops_it()
    {
        _app.Speech.Gate = new TaskCompletionSource();
        var client = _app.CreateClient();
        var take = (await (await client.PostAsJsonAsync("/api/speech/takes", new { text = "Hallo", models = new[] { "chatterbox" } }))
            .Content.ReadFromJsonAsync<SpeechTake[]>(TestApp.Json))!.Single();
        await TestApp.WaitUntil(() => _app.Speech.Jobs.Count == 1);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/speech/takes/{take.Id}")).StatusCode);

        await WaitForTakes(client, all => all.Count == 0);
        // The memory is free again: a song goes straight to the worker.
        await TestApp.WaitUntil(() => !_app.Services.GetRequiredService<ModelMemory>().Holds(LargeModel.Speech));
        await client.PostAsJsonAsync("/api/generate", new { style = "pop", lyrics = "[verse]\nLa", title = "X" });
        Assert.NotNull(_app.Launcher.Current);
    }

    [Fact]
    public async Task A_failed_take_says_why()
    {
        _app.Speech.Failure = "Error loading model: 401 Client Error";
        var client = _app.CreateClient();

        await client.PostAsJsonAsync("/api/speech/takes", new { text = "Hallo", models = new[] { "higgs-v3" } });

        var take = (await WaitForTakes(client, all => all.Single().Finished)).Single();
        Assert.Equal(("failed", "Error loading model: 401 Client Error"), (take.Stage, take.Message));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/speech/takes/{take.Id}/audio")).StatusCode);
    }

    [Fact]
    public void The_message_of_a_failed_take_is_mlx_audios_own_error_line()
    {
        Assert.Equal(
            "Error loading model: Repository Not Found",
            MlxAudioEngine.FailureMessage(["Text: Hallo", "Error loading model: Repository Not Found", "Traceback (most recent call last):", "  File \"x.py\""]));
        Assert.Equal("ModuleNotFoundError: No module named 'mlx_audio'", MlxAudioEngine.FailureMessage(["", "ModuleNotFoundError: No module named 'mlx_audio'"]));
        const string missing = "Error: this model needs the Python package 'torch', which the speech lab's environment lacks; run deploy/install-speech.sh again.";
        Assert.Equal(missing, MlxAudioEngine.FailureMessage(["YUEUI {\"event\": \"stage\", \"stage\": \"loading\"}", "Fetching 12 files", missing]));
        Assert.Equal("The model wrote no audio.", MlxAudioEngine.FailureMessage([]));
    }

    [Fact]
    public async Task An_uploaded_speech_recording_is_cleaned_and_kept_beside_the_original()
    {
        _app.Speech.Gate = new TaskCompletionSource();
        var client = _app.CreateClient();

        var response = await client.PostAsync("/api/speech/cleanups", Cleanup("deepfilternet3", "Vortrag Mai.m4a"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var take = await response.Content.ReadFromJsonAsync<SpeechTake>(TestApp.Json);
        Assert.Equal(("deepfilternet3", "Vortrag Mai.m4a", "queued", ""), (take!.ModelId, take.Source, take.Stage, take.Text));
        Assert.Equal("00000020", Assert.Single(_app.Speech.Recordings));
        await TestApp.WaitUntil(() => _app.Speech.Cleanups.Count == 1);
        var job = _app.Speech.Cleanups[0];
        Assert.Equal(("mlx-community/DeepFilterNet-mlx", "v3", 90.0), (job.Model.Repo, job.Model.Subfolder, job.Seconds));
        Assert.EndsWith(Path.Combine("speech", "sources", $"{take.Id}.wav"), job.Input);
        // Progress reaches the browsers while it cleans, through the same events as a take.
        var working = (await _app.WaitForStatus(client, s => s.Speech is [{ Stage: "cleaning", Progress: 0.5 }])).Speech![0];
        Assert.Equal(take.Id, working.Id);

        _app.Speech.Gate.SetResult();
        var done = (await WaitForTakes(client, all => all.Single().Finished)).Single();
        Assert.Equal(("done", 90.0, 9.75), (done.Stage, done.Seconds, done.SpeakSeconds));
        var source = await client.GetAsync($"/api/speech/takes/{take.Id}/source");
        Assert.Equal("audio/wav", source.Content.Headers.ContentType?.MediaType);
        var audio = await client.GetAsync($"/api/speech/takes/{take.Id}/audio?download=true");
        var disposition = audio.Content.Headers.ContentDisposition;
        Assert.Equal("Vortrag Mai-clean.wav", disposition?.FileNameStar ?? disposition?.FileName?.Trim('"'));

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/speech/takes/{take.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/speech/takes/{take.Id}/source")).StatusCode);
        Assert.False(File.Exists(job.Input));
    }

    [Fact]
    public async Task A_cleanup_needs_a_known_model_and_a_recording()
    {
        var client = _app.CreateClient();

        var unknown = await client.PostAsync("/api/speech/cleanups", Cleanup("chatterbox", "a.wav"));
        var empty = await client.PostAsync("/api/speech/cleanups", new MultipartFormDataContent { { new StringContent("mossformer2-se"), "model" } });

        Assert.Contains("\"model\"", await unknown.Content.ReadAsStringAsync());
        Assert.Contains("\"file\"", await empty.Content.ReadAsStringAsync());
        Assert.Empty(_app.Speech.Recordings);
        _app.Speech.Installed = false;
        Assert.Equal(HttpStatusCode.NotImplemented, (await client.PostAsync("/api/speech/cleanups", Cleanup("mossformer2-se", "a.wav"))).StatusCode);
    }

    [Fact]
    public async Task A_cleanup_waits_while_songs_are_generated_like_a_take()
    {
        var client = _app.CreateClient();
        await client.PostAsJsonAsync("/api/generate", new { style = "pop", lyrics = "[verse]\nLa", title = "X" });
        await _app.Worker.NextCommand();
        _app.Worker.Emit(new { @event = "started", job = "20260926-120000-X", title = "X", songs = new[] { new { path = _app.AudioPath("20260926-120000-X", "song1"), index = 1 } } });
        await _app.WaitForStatus(client, s => s.Worker.Busy);

        await client.PostAsync("/api/speech/cleanups", Cleanup("mossformer2-se", "rede.wav"));
        await Task.Delay(100);
        Assert.Empty(_app.Speech.Cleanups);

        _app.Worker.Emit(new { @event = "stage", path = _app.AudioPath("20260926-120000-X", "song1"), stage = "ready" });
        Assert.Equal("done", (await WaitForTakes(client, all => all.Single().Finished)).Single().Stage);
        Assert.True(_app.Worker.Disposed);
    }

    [Fact]
    public void The_length_of_a_take_is_read_from_its_header()
    {
        var path = Path.Combine(_app.Root, "take.wav");
        File.WriteAllBytes(path, FakeSpeech.Wav(3.5));

        Assert.Equal(3.5, WavInfo.Seconds(path));
        File.WriteAllText(path, "not a wav");
        Assert.Null(WavInfo.Seconds(path));
    }

    [Fact]
    public async Task Ffmpeg_turns_a_recording_into_24_kHz_mono_without_the_silence_around_it()
    {
        if (AacEncoder.FindFfmpeg() is not { } ffmpeg)
        {
            return;
        }
        // One second of silence, two of a tone, one of silence, in stereo at 48 kHz as a browser might record.
        var input = Path.Combine(_app.Root, "recording.wav");
        var generate = Process.Start(new ProcessStartInfo(ffmpeg,
        [
            "-nostdin", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
            "anullsrc=r=48000:cl=stereo:d=1[a];sine=f=220:r=48000:d=2,aformat=channel_layouts=stereo[b];anullsrc=r=48000:cl=stereo:d=1[c];[a][b][c]concat=n=3:v=0:a=1",
            input,
        ]) { RedirectStandardError = true })!;
        await generate.WaitForExitAsync();
        Assert.Equal(0, generate.ExitCode);
        var engine = new MlxAudioEngine(Options.Create(new SpeechOptions { Root = _app.Root }), NullLogger<MlxAudioEngine>.Instance);
        var output = Path.Combine(_app.Root, "voice.wav");

        await engine.PrepareVoiceAsync(input, output, CancellationToken.None);

        var bytes = File.ReadAllBytes(output);
        Assert.Equal((1, 24000), (BitConverter.ToInt16(bytes, 22), BitConverter.ToInt32(bytes, 24)));
        Assert.InRange(WavInfo.Seconds(output)!.Value, 1.9, 2.6);
    }

    [Fact]
    public async Task Ffmpeg_turns_a_recording_to_clean_into_48_kHz_mono_without_cutting_anything()
    {
        if (AacEncoder.FindFfmpeg() is not { } ffmpeg)
        {
            return;
        }
        // A phone's recording: AAC in stereo at 44.1 kHz, beginning and ending in silence that must stay.
        var input = Path.Combine(_app.Root, "talk.m4a");
        var generate = Process.Start(new ProcessStartInfo(ffmpeg,
        [
            "-nostdin", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
            "anullsrc=r=44100:cl=stereo:d=1[a];sine=f=220:r=44100:d=2,aformat=channel_layouts=stereo[b];anullsrc=r=44100:cl=stereo:d=1[c];[a][b][c]concat=n=3:v=0:a=1",
            "-c:a", "aac", input,
        ]) { RedirectStandardError = true })!;
        await generate.WaitForExitAsync();
        Assert.Equal(0, generate.ExitCode);
        var engine = new MlxAudioEngine(Options.Create(new SpeechOptions { Root = _app.Root }), NullLogger<MlxAudioEngine>.Instance);
        var output = Path.Combine(_app.Root, "source.wav");

        await engine.PrepareRecordingAsync(input, output, CancellationToken.None);

        var bytes = File.ReadAllBytes(output);
        Assert.Equal((1, 48000, 16), (BitConverter.ToInt16(bytes, 22), BitConverter.ToInt32(bytes, 24), BitConverter.ToInt16(bytes, 34)));
        Assert.InRange(WavInfo.Seconds(output)!.Value, 3.9, 4.1);
    }

    private static MultipartFormDataContent Cleanup(string model, string fileName) => new()
    {
        { new StringContent(model), "model" },
        // An M4A begins with the size of its ftyp box.
        { new ByteArrayContent([0, 0, 0, 0x20, 0x66, 0x74, 0x79, 0x70]), "file", fileName },
    };

    private static MultipartFormDataContent Recording(string label, string transcript) => new()
    {
        { new StringContent(label), "label" },
        { new StringContent(transcript), "transcript" },
        // What Chrome's MediaRecorder writes: a WebM, which starts with EBML's magic number.
        { new ByteArrayContent([0x1A, 0x45, 0xDF, 0xA3, 1, 2, 3]), "file", "recording.webm" },
    };

    private async Task<IReadOnlyList<SpeechTake>> WaitForTakes(HttpClient client, Func<IReadOnlyList<SpeechTake>, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            var takes = (await client.GetFromJsonAsync<SpeechInfo>("/api/speech", TestApp.Json))!.Takes;
            if (condition(takes))
            {
                return takes;
            }
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"The takes never matched: {string.Join(", ", takes.Select(t => $"{t.ModelId} {t.Stage} {t.Message}"))}");
            }
            await Task.Delay(20);
        }
    }
}
