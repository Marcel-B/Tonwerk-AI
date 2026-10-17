using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using YueToLogic.Core.Abc;
using YueUI.Api.Sketches;

namespace YueUI.Api.Tests;

public sealed class SketchScoreTests
{
    [Fact]
    public void The_plan_becomes_a_score_yue2_reads_back()
    {
        var job = new SketchJob { Id = "a", Plan = SketchSamples.Plan() };

        var result = SketchScore.Result(job);

        Assert.Contains("Q:1/4=96", result.Abc);
        Assert.Contains("K:Dm", result.Abc);
        Assert.Contains("% intro", result.Abc);
        Assert.Contains("% chorus", result.Abc);
        Assert.Contains("% outro", result.Abc);
        var parsed = new AbcScoreParser().Parse(result.Abc);
        Assert.True(parsed.Success);
        var score = parsed.Score!;
        Assert.Equal(96, score.TempoBpm);
        var vocal = score.Voices.Single(v => v.Id == "Vocal").Notes;
        // The hook exactly as the sketch writes it, then the next line with its tie held through.
        Assert.Equal(
            [69, 72, 69, 67, 65, 67, 64, 67, 69, 69, 72, 74, 74, 72, 69, 67],
            vocal.Select(n => n.NoteNumber));
        Assert.Equal(6 * 120, vocal[1].DurationTicks);
        Assert.Equal(4 * 120, vocal[11].DurationTicks);
        var ins = score.Voices.Single(v => v.Id == "Ins").Notes;
        Assert.Equal([62, 69, 67, 62, 69, 67, 64], ins.Select(n => n.NoteNumber));
        Assert.Equal(["Dm", "Bbmaj7", "F", "C/E", "Dm7", "Bbmaj7", "Dm"], score.Chords.Select(c => c.Text));
        Assert.Equal(7 * 16 * 120, score.LengthTicks);
    }

    [Fact]
    public void The_form_gets_lyrics_with_tags_and_a_score_that_keeps_its_chords()
    {
        var result = SketchScore.Result(new SketchJob { Id = "a", Plan = SketchSamples.Plan() });

        Assert.Equal(
            "[Intro]\n\n[Chorus]\nBefore the silence, there was this:\nyour crooked smile against my cheek.\n\n[Outro]\n",
            result.Lyrics);
        Assert.Equal("full", result.Request.Cot);
        Assert.Equal(result.Abc, result.Request.Abc);
        Assert.Equal("Before the Silence", result.Request.Title);
        Assert.False(result.Request.Instrumental);
        // 7 bars at 96 BPM are 17.5 seconds, 25 tokens each, and a margin.
        Assert.Equal(688, result.Request.MaxTokens);
        Assert.Equal(7, result.Bars);
        Assert.Equal(17.5, result.Seconds, 3);
        var simplified = Assert.Single(result.Warnings);
        Assert.Equal(("chordSimplified", "Dm(add9) → Dm"), (simplified.Code, simplified.Detail));
    }

    [Fact]
    public void Ace_step_gets_lyrics_style_tempo_key_metre_and_length()
    {
        var result = SketchScore.Result(new SketchJob { Id = "a", Plan = SketchSamples.Plan() });

        var ace = result.Ace;
        Assert.Equal("Before the Silence", ace.Title);
        Assert.Equal(SketchSamples.Plan().Style, ace.Caption);
        Assert.Equal(result.Lyrics, ace.Lyrics);
        Assert.Equal("en", ace.Language);
        Assert.Equal(96, ace.Bpm);
        Assert.Equal("D minor", ace.Key);
        Assert.Equal(4, ace.TimeSignature);
        // 17.5 seconds, rounded up and raised to ACE-Step's shortest.
        Assert.Equal(18, ace.Duration);
        Assert.Equal("text2music", ace.Task);
        Assert.Null(ace.Seed);
    }

    [Theory]
    [InlineData("G#m", 3, "Ab minor", 3)]
    [InlineData("Cb", 5, "B major", null)]
    [InlineData("F#", 6, "F# major", 6)]
    [InlineData("nonsense", 4, "C major", 4)]
    public void Ace_step_keys_use_its_spelling_and_odd_metres_are_left_to_it(
        string key, int beats, string expected, int? metre)
    {
        var plan = SketchSamples.Plan() with { Key = key, BeatsPerBar = beats };

        var ace = SketchScore.AceRequest(plan, "", 5);

        Assert.Equal(expected, ace.Key);
        Assert.Equal(metre, ace.TimeSignature);
        Assert.Equal(10, ace.Duration);
    }

    [Fact]
    public void Bars_that_do_not_add_up_are_repaired_and_reported()
    {
        var plan = SketchSamples.Plan() with
        {
            Key = "nonsense",
            Sections =
            [
                new SketchSection("Verse", "verse", [], 2, ["one two three four five six seven eight nine"],
                [
                    new SketchBar("Am X7 G", "A4:8 C5:8 E5:4", "go to sleep", null, null),
                    new SketchBar("Am", "A4:4 Q9:4", "four five six seven eight nine", null, null),
                ]),
            ],
        };

        var laid = SketchScore.LayOut(plan);

        Assert.Contains(new SketchWarning("barLength", 1, "16/16"), laid.Warnings);
        Assert.Contains(new SketchWarning("note", 2, "Q9:4"), laid.Warnings);
        Assert.Contains(new SketchWarning("barLength", 2, "4/16"), laid.Warnings);
        Assert.Contains(new SketchWarning("chord", 1, "X7"), laid.Warnings);
        Assert.Contains(new SketchWarning("key", null, "nonsense"), laid.Warnings);
        // Three syllables over two notes is close enough; seven over one is not.
        Assert.Contains(laid.Warnings, w => w is { Code: "syllables", Bar: 2, Detail: "1/7" });
        Assert.DoesNotContain(laid.Warnings, w => w is { Code: "syllables", Bar: 1 });
        // The third note did not fit; the bar ends where it should.
        Assert.Equal([69, 72, 69], laid.Score.Voices[0].Notes.Select(n => n.NoteNumber));
        Assert.Equal(["Am", "G", "Am"], laid.Score.Chords.Select(c => c.Text));
    }

    [Theory]
    [InlineData("Dm(add9)", "Dm", true)]
    [InlineData("C(add9)", "C", true)]
    [InlineData("Bbmaj7", "Bbmaj7", false)]
    [InlineData("F/A", "F/A", false)]
    [InlineData("Csus4", "Csus4", false)]
    [InlineData("Gm9", "Gm7", true)]
    [InlineData("Ebmaj9/G", "Ebmaj7/G", true)]
    [InlineData("A7#9", "A7", true)]
    [InlineData("Bø7", "Bm7b5", false)]
    [InlineData("C-7", "Cm7", false)]
    [InlineData("Dm(maj7)", "Dm(maj7)", false)]
    [InlineData("E5", "E", false)]
    [InlineData("F#m11", "F#m7", false)]
    public void Chords_keep_the_core_yue2_knows(string symbol, string core, bool ninth)
    {
        Assert.Equal((core, ninth), SketchScore.Normalize(symbol));
    }

    [Theory]
    [InlineData("Before the silence, there was this:", 8)]
    [InlineData("your crooked smile against my cheek.", 7)]
    [InlineData("Die Straße schläft, ich bleibe wach", 8)]
    public void Syllables_are_counted_close_enough(string line, int expected)
    {
        Assert.InRange(SketchScore.Syllables(line), expected - 1, expected + 1);
    }

    [Theory]
    [InlineData("# Before the Silence — Musikalische Arbeitsfassung 01\n\nText", "Before the Silence")]
    [InlineData("## Lied über Nichts\n", "Lied über Nichts")]
    [InlineData("no heading here\nmore", "no heading here")]
    public void A_sketch_is_named_by_its_first_heading(string text, string title)
    {
        Assert.Equal(title, SketchMaker.TitleOf(text));
    }

    [Fact]
    public void The_midi_draft_plays_each_section_with_its_parts()
    {
        var midi = MidiFile.Read(new MemoryStream(SketchArranger.Midi(SketchSamples.Plan())));
        var tracks = midi.GetTrackChunks()
            .Select(t => (Name: t.Events.OfType<SequenceTrackNameEvent>().FirstOrDefault()?.Text, Notes: t.GetNotes().ToList()))
            .ToList();
        var names = tracks.Select(t => t.Name).ToList();
        Assert.Equal(["Conductor", "Vocal", "Ins", "Piano", "Pad", "Bass", "Drums"], names);
        var tempoMap = midi.GetTempoMap();
        Assert.Equal(96, tempoMap.GetTempoAtTime(new MidiTimeSpan(0)).BeatsPerMinute, 3);
        var markers = midi.GetTrackChunks().First().Events.OfType<MarkerEvent>().Select(m => m.Text).ToList();
        Assert.Equal(["Intro", "Chorus 1", "Outro"], markers);

        const long bar = 4 * 480;
        var drums = tracks.Single(t => t.Name == "Drums").Notes;
        Assert.All(drums, n => Assert.Equal(9, (int)n.Channel));
        // Only the chorus has drums, and its last bar (Bbmaj7 without drums in the plan) none.
        Assert.All(drums, n => Assert.InRange(n.Time, 2 * bar - 10, 5 * bar));
        var bass = tracks.Single(t => t.Name == "Bass").Notes;
        Assert.All(bass, n => Assert.InRange(n.Time, 2 * bar, 6 * bar - 1));
        // The slash chord C/E has E in the bass.
        Assert.Contains(bass, n => n.Time == 3 * bar && n.NoteNumber % 12 == 4);
        var piano = tracks.Single(t => t.Name == "Piano").Notes;
        // Dm(add9) in the intro: D in the left hand, and the ninth E in the right.
        Assert.Contains(piano, n => n.Time == 0 && n.NoteNumber == 50);
        Assert.Contains(piano, n => n.Time < bar && n.NoteNumber % 12 == 4);
        Assert.DoesNotContain(tracks.Single(t => t.Name == "Pad").Notes, n => n.Time < 2 * bar);
    }

    [Fact]
    public void The_voicing_sits_in_the_middle_register()
    {
        Assert.True(YueToLogic.Core.Harmony.ChordSymbolParser.TryParse("Dm", out var dm));
        var (left, right) = SketchArranger.Voicing(dm, addNinth: true);
        Assert.Equal(50, left);
        Assert.Equal([53, 57, 64], right);
    }
}

public sealed class SketchEndpointTests : IDisposable
{
    private readonly TestApp _app = new();

    private HttpClient? _client;

    private HttpClient Client => _client ??= _app.CreateClient();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task A_sketch_becomes_a_plan_a_score_and_a_midi_draft()
    {
        var response = await Client.PostAsJsonAsync("/api/sketches", new
        {
            text = "# Before the Silence — Arbeitsfassung\n\n96 BPM, D minor.",
            instruction = " keep the hook ",
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var queued = await response.Content.ReadFromJsonAsync<JsonObject>(TestApp.Json);
        Assert.Equal("Before the Silence", (string?)queued!["title"]);
        var id = (string)queued["id"]!;
        await WaitForStage(id, "done");

        Assert.Equal(("# Before the Silence — Arbeitsfassung\n\n96 BPM, D minor.", "keep the hook"), Assert.Single(_app.Sketches.Calls));
        var result = await Client.GetFromJsonAsync<JsonObject>($"/api/sketches/{id}", TestApp.Json);
        Assert.Equal("done", (string?)result!["job"]!["stage"]);
        Assert.Equal("claude-test", (string?)result["job"]!["model"]);
        Assert.StartsWith("X:1", (string?)result["abc"]);
        Assert.Equal("full", (string?)result["request"]!["cot"]);
        Assert.Equal(7, (int?)result["bars"]);
        Assert.Equal("chordSimplified", (string?)result["warnings"]![0]!["code"]);

        var list = await Client.GetFromJsonAsync<JsonArray>("/api/sketches", TestApp.Json);
        Assert.Equal(7, (int?)Assert.Single(list!)!["bars"]);

        var midi = await Client.GetAsync($"/api/sketches/{id}/midi");
        Assert.Equal("audio/midi", midi.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Before the Silence.mid", midi.Content.Headers.ContentDisposition?.FileNameStar);
        Assert.Equal("MThd"u8.ToArray(), (await midi.Content.ReadAsByteArrayAsync())[..4]);
        var score = await Client.GetStringAsync($"/api/sketches/{id}/score");
        Assert.Contains("% chorus", score);

        Assert.Equal(HttpStatusCode.NoContent, (await Client.DeleteAsync($"/api/sketches/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/api/sketches/{id}")).StatusCode);
    }

    [Fact]
    public async Task A_sketch_claude_cannot_plan_says_why()
    {
        _app.Sketches.Failure = "Claude declined to plan this sketch.";
        var queued = await (await Client.PostAsJsonAsync("/api/sketches", new { text = "Something" }))
            .Content.ReadFromJsonAsync<JsonObject>(TestApp.Json);

        var job = await WaitForStage((string)queued!["id"]!, "failed");

        Assert.Equal("Claude declined to plan this sketch.", (string?)job["message"]);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/api/sketches/{queued["id"]}/midi")).StatusCode);
    }

    [Fact]
    public async Task Without_a_key_nothing_is_sent()
    {
        _app.SketchKey = null;

        var info = await Client.GetFromJsonAsync<SketchInfo>("/api/sketches/info", TestApp.Json);
        var response = await Client.PostAsJsonAsync("/api/sketches", new { text = "Something" });

        Assert.False(info!.Configured);
        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        Assert.Empty(_app.Sketches.Calls);
    }

    [Fact]
    public async Task The_model_can_be_chosen_and_stays_with_the_sketch()
    {
        var info = (await Client.GetFromJsonAsync<SketchInfo>("/api/sketches/info", TestApp.Json))!;
        Assert.Equal("claude-opus-5-5", info.Model);
        Assert.Equal(["claude-opus-5-5", "claude-fable-5-1", "claude-sonnet-5-5"], info.Models.Select(m => m.Id));
        Assert.Equal("Claude Fable 5.1", info.Models[1].Name);

        var queued = await (await Client.PostAsJsonAsync("/api/sketches", new { text = "Something", model = "claude-fable-5-1" }))
            .Content.ReadFromJsonAsync<JsonObject>();
        await WaitForStage((string)queued!["id"]!, "done");

        Assert.Equal(["claude-fable-5-1"], _app.Sketches.Models);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.PostAsJsonAsync("/api/sketches", new { text = "Something", model = "gpt-5" })).StatusCode);
    }

    [Fact]
    public async Task Without_a_choice_the_default_model_plans()
    {
        var queued = await (await Client.PostAsJsonAsync("/api/sketches", new { text = "Something" })).Content.ReadFromJsonAsync<JsonObject>();
        await WaitForStage((string)queued!["id"]!, "done");

        Assert.Equal(["claude-opus-5-5"], _app.Sketches.Models);
    }

    [Fact]
    public async Task An_empty_sketch_is_refused()
    {
        var response = await Client.PostAsJsonAsync("/api/sketches", new { text = "  " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<JsonObject> WaitForStage(string id, string stage)
    {
        JsonObject? job = null;
        await TestApp.WaitUntil(() =>
        {
            var answer = Client.GetFromJsonAsync<JsonObject>($"/api/sketches/{id}", TestApp.Json).GetAwaiter().GetResult();
            job = answer!["job"]!.AsObject();
            return (string?)job["stage"] == stage;
        });
        return job!;
    }
}
