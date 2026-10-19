using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using YueToLogic.Core.Abc;
using YueUI.Api.Singing;

namespace YueUI.Api.Tests;

public sealed class SingingTests : IDisposable
{
    private const string Run = "20260921-165850-Neon-Night";

    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task The_page_lists_the_voices_and_whether_soulx_singer_is_installed()
    {
        var info = await _app.CreateClient().GetFromJsonAsync<SingingInfo>("/api/singing", TestApp.Json);

        Assert.True(info!.Installed);
        Assert.True(info.ModelDownloaded);
        Assert.EndsWith(Path.Combine("singing", "env", "bin", "python"), info.Python);
        Assert.Equal(["en", "zh"], info.Voices.Select(v => v.Id));
        Assert.Empty(info.Takes);
    }

    [Fact]
    public async Task A_midi_file_with_lyric_events_is_sung_without_a_text()
    {
        var client = _app.CreateClient();

        var response = await client.PostAsync("/api/singing", Form(Midi(("Piano", [(60, null), (64, null)]), ("Lead Vocal", [(67, "Hel"), (69, "lo")])), "melody.mid"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var take = await response.Content.ReadFromJsonAsync<SingTake>(TestApp.Json);
        Assert.Equal(("melody", "en", 2, "melody.mid"), (take!.Title, take.VoiceId, take.NoteCount, take.FileName));

        var done = await WaitFor(client, take.Id, t => t.Finished);
        Assert.Equal(("done", 3.0, 2), (done.Stage, done.Seconds, done.Sung));
        var job = Assert.Single(_app.Singing.Jobs);
        Assert.Equal([(67, "Hel", 0.0, 0.5), (69, "lo", 0.5, 1.0)], job.Notes.Select(n => (n.Pitch, n.Lyric, n.Start, n.End)));

        var audio = await client.GetAsync($"/api/singing/{take.Id}/audio?download=true");
        Assert.Equal("audio/wav", audio.Content.Headers.ContentType?.MediaType);
        Assert.Equal("melody.wav", audio.Content.Headers.ContentDisposition?.FileNameStar ?? audio.Content.Headers.ContentDisposition?.FileName);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/singing/{take.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/singing/{take.Id}/audio")).StatusCode);
    }

    [Fact]
    public async Task A_melody_without_lyric_events_needs_words()
    {
        var client = _app.CreateClient();

        var refused = await client.PostAsync("/api/singing", Form(Midi(("Melody", [(60, null), (62, null)])), "melody.mid"));
        var sung = await client.PostAsync("/api/singing", Form(Midi(("Melody", [(60, null), (62, null)])), "melody.mid", ("lyrics", "Hello world"), ("voice", "zh"), ("pitchShift", "-12")));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("lyrics", await refused.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Accepted, sung.StatusCode);
        var take = await sung.Content.ReadFromJsonAsync<SingTake>(TestApp.Json);
        await WaitFor(client, take!.Id, t => t.Finished);
        var job = Assert.Single(_app.Singing.Jobs);
        Assert.Equal(("Hello world", "zh", -12), (job.Lyrics, job.Voice.Id, job.PitchShift));
    }

    [Fact]
    public async Task A_library_song_is_sung_from_its_score_with_its_own_lyrics()
    {
        var directory = _app.AddSong(Run, "song1");
        File.WriteAllText(Path.Combine(directory, "score.abc"), LogicEndpointTests.SampleScore);
        var client = _app.CreateClient();

        var response = await client.PostAsync("/api/singing", Form(null, null, ("song", $"{Run}/song1")));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var take = await response.Content.ReadFromJsonAsync<SingTake>(TestApp.Json);
        Assert.Equal(($"{Run}/song1", "[verse]\nLa la"), (take!.SongId, take.Lyrics));
        Assert.True(take.NoteCount > 10);
        await WaitFor(client, take.Id, t => t.Finished);
    }

    [Fact]
    public async Task Requests_that_cannot_be_sung_are_refused()
    {
        var client = _app.CreateClient();
        var midi = Midi(("Vocal", [(60, "la")]));

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/singing", Form(null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/singing", Form(midi, "a.mid", ("voice", "nobody")))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/singing", Form(midi, "a.mid", ("pitchShift", "30")))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/singing", Form(null, null, ("song", $"{Run}/song9")))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsync("/api/singing", Form("X:1\nK:C\n"u8.ToArray(), "a.abc", ("lyrics", "la")))).StatusCode);

        _app.Singing.ModelDownloaded = false;
        var missing = await client.PostAsync("/api/singing", Form(midi, "a.mid"));
        Assert.Equal(HttpStatusCode.NotImplemented, missing.StatusCode);
        Assert.Contains("install-singing.sh", await missing.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_failed_take_keeps_the_reason()
    {
        _app.Singing.Failure = "Error: no note has a word to sing.";
        var client = _app.CreateClient();

        var response = await client.PostAsync("/api/singing", Form(Midi(("Vocal", [(60, "la")])), "a.mid"));
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        var take = await response.Content.ReadFromJsonAsync<SingTake>(TestApp.Json);

        var failed = await WaitFor(client, take!.Id, t => t.Finished);
        Assert.Equal(("failed", "Error: no note has a word to sing."), (failed.Stage, failed.Message));
    }

    [Fact]
    public void The_sung_track_of_a_midi_file_is_the_one_named_like_a_voice_else_the_one_with_lyrics()
    {
        var named = SingingScores.FromMidi(Midi(("Piano", [(60, null), (62, null), (64, null)]), ("Gesang", [(70, null)])));
        var lyrics = SingingScores.FromMidi(Midi(("Piano", [(60, null), (62, null), (64, null)]), ("Track", [(70, "la")])));
        var fullest = SingingScores.FromMidi(Midi(("Harmony 1", [(60, null), (62, null), (64, null)]), ("Synth", [(70, null)]), ("Pad", [(50, null), (52, null)])));

        Assert.Equal(("Gesang", 70), (named.Part, named.Notes.Single().Pitch));
        Assert.Equal(("Track", "la"), (lyrics.Part, lyrics.Notes.Single().Lyric));
        Assert.Equal("Pad", fullest.Part);
        // Written alone, a track's name and lyrics land in a note-less first track.
        Assert.Equal("la", SingingScores.FromMidi(Midi(("Vocal", [(60, "la")]))).Notes.Single().Lyric);
        Assert.NotNull(SingingScores.FromMidi("MThd garbage"u8.ToArray()).Problem);
    }

    [Fact]
    public void The_sung_voice_of_a_score_is_the_vocal_one_in_seconds()
    {
        var score = SingingScores.FromAbc(LogicEndpointTests.SampleScore, new AbcScoreParser());

        Assert.Null(score.Problem);
        Assert.Equal("Vocal", score.Part);
        Assert.All(score.Notes, n => Assert.True(n.End > n.Start));
        Assert.True(score.Notes.Zip(score.Notes.Skip(1)).All(p => p.First.Start <= p.Second.Start));
    }

    /// <summary>One track per part, quarter notes at 120 bpm, each with its lyric event where given.</summary>
    private static byte[] Midi(params (string Name, (int Pitch, string? Lyric)[] Notes)[] tracks)
    {
        var chunks = tracks.Select(track =>
        {
            var events = new List<MidiEvent> { new SequenceTrackNameEvent(track.Name) };
            foreach (var (pitch, lyric) in track.Notes)
            {
                if (lyric is not null)
                {
                    events.Add(new LyricEvent(lyric));
                }
                events.Add(new NoteOnEvent((SevenBitNumber)pitch, (SevenBitNumber)100));
                events.Add(new NoteOffEvent((SevenBitNumber)pitch, (SevenBitNumber)0) { DeltaTime = 480 });
            }
            return new TrackChunk(events);
        });
        var file = new MidiFile(chunks) { TimeDivision = new TicksPerQuarterNoteTimeDivision(480) };
        using var stream = new MemoryStream();
        file.Write(stream);
        return stream.ToArray();
    }

    private static MultipartFormDataContent Form(byte[]? file, string? name, params (string Key, string Value)[] fields)
    {
        var form = new MultipartFormDataContent();
        if (file is not null)
        {
            var content = new ByteArrayContent(file);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(content, "file", name!);
        }
        foreach (var (key, value) in fields)
        {
            form.Add(new StringContent(value), key);
        }
        return form;
    }

    private static async Task<SingTake> WaitFor(HttpClient client, string id, Func<SingTake, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            var take = (await client.GetFromJsonAsync<SingingInfo>("/api/singing", TestApp.Json))!.Takes.FirstOrDefault(t => t.Id == id);
            if (take is not null && condition(take))
            {
                return take;
            }
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"The take never matched: {take?.Stage} {take?.Message}");
            }
            await Task.Delay(20);
        }
    }
}
