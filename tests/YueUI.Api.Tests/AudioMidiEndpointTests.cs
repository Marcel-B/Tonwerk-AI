using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using YueUI.Api.Voices;

namespace YueUI.Api.Tests;

public sealed class AudioMidiEndpointTests : IDisposable
{
    private const string Run = "20260921-165850-Neon-Night";

    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task An_uploaded_recording_is_decoded_and_comes_back_as_a_vocal_MIDI_file()
    {
        var client = _app.CreateClient();

        var response = await client.PostAsync("/api/audio-midi", Upload("Mein Gesang.m4a", ("tempo", "92"), ("quantize", "true")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("audio/midi", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("Mein Gesang.mid", response.Content.Headers.ContentDisposition!.FileNameStar);
        Assert.Equal(FakeAudioMidi.Midi, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("42", response.Headers.GetValues("X-Audio-Midi-Notes").Single());
        // Through ffmpeg first, whatever the phone recorded.
        Assert.Equal(["audio.wav"], _app.Mixer.Decoded);
        var job = Assert.Single(_app.AudioMidi.Jobs);
        Assert.Equal(("Vocal", AudioMidi.AudioMidiKind.Voice, 92.0), (job.TrackName, job.Kind, job.Tempo));
        Assert.Equal(new AudioMidi.AudioMidiSettings(Mono: true, Quantize: true, Bends: false, Tempo: 92), job.Settings);
        Assert.Equal("recording"u8.ToArray(), _app.AudioMidi.Audio.Single());
        // The upload's folder goes with the request.
        Assert.False(Directory.Exists(Path.GetDirectoryName(job.Audio)));
    }

    [Fact]
    public async Task Without_a_tempo_an_upload_is_written_at_120_and_cannot_be_put_on_the_grid()
    {
        var client = _app.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/audio-midi", Upload("take.wav", ("mono", "false")))).StatusCode);
        var job = Assert.Single(_app.AudioMidi.Jobs);
        Assert.Equal((120.0, false), (job.Tempo, job.Settings.Mono));

        var grid = await client.PostAsync("/api/audio-midi", Upload("take.wav", ("quantize", "true")));
        Assert.Equal(HttpStatusCode.BadRequest, grid.StatusCode);
        Assert.Contains("quantize", await grid.Content.ReadAsStringAsync());
        var tempo = await client.PostAsync("/api/audio-midi", Upload("take.wav", ("tempo", "500")));
        Assert.Equal(HttpStatusCode.BadRequest, tempo.StatusCode);
        var empty = new MultipartFormDataContent { { new StringContent("true"), "mono" } };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/audio-midi", empty)).StatusCode);
        Assert.Single(_app.AudioMidi.Jobs);
    }

    [Fact]
    public async Task Without_Basic_Pitch_the_page_learns_how_to_install_it()
    {
        _app.AudioMidi.Installed = false;
        var client = _app.CreateClient();

        var info = await client.GetFromJsonAsync<JsonObject>("/api/audio-midi");
        Assert.False((bool)info!["installed"]!);
        Assert.EndsWith(Path.Combine("midi", "env", "bin", "python"), (string)info["python"]!);

        var response = await client.PostAsync("/api/audio-midi", Upload("take.wav"));
        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        Assert.Contains("install-midi.sh", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_vocal_stem_becomes_the_melody_at_the_tempo_of_its_song()
    {
        var set = await SeparatedSong("Q:1/4=88");
        var client = _app.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/songs/{Run}/song2/stems/{set.Id}/vocals/midi", new { mono = true, quantize = true, bends = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Neon Night-song2-vocals.mid", response.Content.Headers.ContentDisposition!.FileNameStar);
        var job = Assert.Single(_app.AudioMidi.Jobs);
        Assert.Equal(("Vocal", AudioMidi.AudioMidiKind.Voice, 88.0), (job.TrackName, job.Kind, job.Tempo));
        Assert.True(job.Settings.Quantize && job.Settings.Bends);
        // The stored stem itself, nothing decoded on the way.
        Assert.Equal(FakeMixer.Flac, _app.AudioMidi.Audio.Single());
        Assert.Empty(_app.Mixer.Decoded);
    }

    [Fact]
    public async Task An_instrument_stem_keeps_its_name_and_its_whole_range()
    {
        var set = await SeparatedSong("Q:1/8=180");
        var client = _app.CreateClient();

        var response = await client.PostAsync($"/api/songs/{Run}/song2/stems/{set.Id}/drums/midi", JsonContent.Create(new { }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var job = Assert.Single(_app.AudioMidi.Jobs);
        // Eighths at 180 are quarters at 90.
        Assert.Equal(("Drums", AudioMidi.AudioMidiKind.Drums, 90.0), (job.TrackName, job.Kind, job.Tempo));
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/songs/{Run}/song2/stems/{set.Id}/bass/midi", JsonContent.Create(new { }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/songs/{Run}/song1/stems/{set.Id}/drums/midi", JsonContent.Create(new { }))).StatusCode);
    }

    [Fact]
    public async Task A_song_whose_score_names_no_tempo_offers_no_grid()
    {
        var set = await SeparatedSong("X:1");
        var client = _app.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/songs/{Run}/song2/stems/{set.Id}/vocals/midi", new { quantize = true });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/songs/{Run}/song2/stems/{set.Id}/vocals/midi", new { quantize = true, tempo = 100 })).StatusCode);
        Assert.Equal(100, _app.AudioMidi.Jobs.Single().Tempo);
    }

    [Fact]
    public async Task A_stem_of_an_uploaded_file_takes_the_tempo_given_and_its_name()
    {
        _app.Stems.Files = ["vocals.wav", "bass.wav"];
        var client = _app.CreateClient();
        await client.PostAsync("/api/stems", StemEndpointTests.Upload("Demo.mp3"));
        var set = await DoneSet(client);

        var noGrid = await client.PostAsJsonAsync($"/api/stems/{set.Id}/bass/midi", new { quantize = true });
        var response = await client.PostAsJsonAsync($"/api/stems/{set.Id}/bass/midi", new { quantize = true, tempo = 96 });

        Assert.Equal(HttpStatusCode.BadRequest, noGrid.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Demo-bass.mid", response.Content.Headers.ContentDisposition!.FileNameStar);
        var job = Assert.Single(_app.AudioMidi.Jobs);
        Assert.Equal(("Bass", AudioMidi.AudioMidiKind.Bass, 96.0), (job.TrackName, job.Kind, job.Tempo));
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/stems/{set.Id}/drums/midi", new { })).StatusCode);
    }

    [Fact]
    public async Task A_track_without_notes_or_a_failing_model_is_answered_with_why()
    {
        var client = _app.CreateClient();

        _app.AudioMidi.Notes = 0;
        var silent = await client.PostAsync("/api/audio-midi", Upload("take.wav"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, silent.StatusCode);
        Assert.Contains("No notes", await silent.Content.ReadAsStringAsync());

        _app.AudioMidi.Failure = "Error: librosa could not read the file.";
        var failed = await client.PostAsync("/api/audio-midi", Upload("take.wav"));
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Contains("librosa could not read", await failed.Content.ReadAsStringAsync());
    }

    private async Task<StemSetState> SeparatedSong(string score)
    {
        var directory = _app.AddSong(Run, "song2");
        File.WriteAllText(Path.Combine(directory, "score.abc"), $"X:1\nT:\nM:4/4\nL:1/16\n{score}\nK:C\n");
        _app.Stems.Files = ["vocals.wav", "drums.wav"];
        var client = _app.CreateClient();
        await client.PostAsJsonAsync($"/api/songs/{Run}/song2/stems", new { model = "htdemucs" });
        return await DoneSet(client);
    }

    private static async Task<StemSetState> DoneSet(HttpClient client)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            var sets = await client.GetFromJsonAsync<StemSetState[]>("/api/stems", TestApp.Json);
            if (sets?.FirstOrDefault() is { Stage: "done" } set)
            {
                return set;
            }
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The stems never got there.");
            }
            await Task.Delay(20);
        }
    }

    private static MultipartFormDataContent Upload(string name, params (string Name, string Value)[] fields)
    {
        var form = new MultipartFormDataContent { { new ByteArrayContent("recording"u8.ToArray()), "file", name } };
        foreach (var (field, value) in fields)
        {
            form.Add(new StringContent(value), field);
        }
        return form;
    }
}
