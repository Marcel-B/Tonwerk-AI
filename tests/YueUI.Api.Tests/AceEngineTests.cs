using YueUI.Api.Ace;

namespace YueUI.Api.Tests;

public sealed class AceEngineTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("yueui-ace-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Models_are_sorted_by_kind_with_the_largest_lm_and_a_turbo_dit_first()
    {
        var models = Directory.CreateDirectory(Path.Combine(_root, "models")).FullName;
        foreach (var file in (string[])[
            "acestep-5Hz-lm-0.6B-Q8_0.gguf", "acestep-5Hz-lm-4B-Q8_0.gguf", "acestep-5Hz-lm-1.7B-Q8_0.gguf",
            "acestep-v15-sft-Q8_0.gguf", "acestep-v15-xl-turbo-Q8_0.gguf", "acestep-v15-turbo-Q8_0.gguf",
            "Qwen3-Embedding-0.6B-Q8_0.gguf", "vae-BF16.gguf", "notes.txt"])
        {
            File.WriteAllText(Path.Combine(models, file), "");
        }
        var adapters = Directory.CreateDirectory(Path.Combine(_root, "adapters")).FullName;
        File.WriteAllText(Path.Combine(adapters, "comfy.safetensors"), "");
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(adapters, "peft")).FullName, "adapter_model.safetensors"), "");
        Directory.CreateDirectory(Path.Combine(adapters, "empty"));

        var files = AceModelFiles.Scan(models, adapters);

        Assert.Equal(["acestep-5Hz-lm-4B-Q8_0.gguf", "acestep-5Hz-lm-1.7B-Q8_0.gguf", "acestep-5Hz-lm-0.6B-Q8_0.gguf"], files.Lm);
        Assert.Equal(["acestep-v15-turbo-Q8_0.gguf", "acestep-v15-xl-turbo-Q8_0.gguf", "acestep-v15-sft-Q8_0.gguf"], files.Dit);
        Assert.Equal(["comfy.safetensors", "peft"], files.Adapters);
        Assert.True(files.Complete);
        Assert.False(AceModelFiles.Scan(Path.Combine(_root, "missing"), adapters).Complete);
    }

    [Fact]
    public void The_lms_progress_counts_its_codes_against_the_songs_tokens()
    {
        var lm = new AceCppEngine.LmProgress(2);

        Assert.Equal(("writing", 0.05), lm.Read("[LM-Phase1] Step 50, 2 active, 31.2 tok/s"));
        Assert.Equal(("composing", 0.1), lm.Read("[LM-Phase2] max_tokens: 500, CFG: 2.00, N=2, prompts=2"));
        var halfway = lm.Read("[LM-Phase2] Step 250, 2 active, 500 total codes, 40.0 tok/s");
        Assert.Equal("composing", halfway!.Value.Stage);
        Assert.Equal(0.55, halfway.Value.Fraction, 6);
        Assert.Null(lm.Read("[Ace-LM] Ready: path=…"));
    }

    [Fact]
    public void The_synthesis_counts_diffusion_steps_then_decoded_songs()
    {
        var synth = new AceCppEngine.SynthProgress(2);

        var step = synth.Read("[DiT] Step 4/8 t=0.500");
        Assert.Equal("synthesizing", step!.Value.Stage);
        Assert.Equal(0.35, step.Value.Fraction, 6);
        Assert.Equal(("decoding", 0.7), synth.Read("[DiT-Generate] Total: 5000.0 ms (2500.0 ms/sample)"));
        Assert.Equal(0.85, synth.Read("[VAE-Decode Batch0] Decode: 1000.0 ms")!.Value.Fraction, 6);
        Assert.Equal(1, synth.Read("[VAE-Decode Batch1] Decode: 1000.0 ms")!.Value.Fraction, 6);
    }

    [Fact]
    public void A_failure_is_what_acestep_marked_fatal()
    {
        Assert.Equal(
            "[Ace-LM] FATAL: cannot scan --models /x",
            AceCppEngine.FailureMessage("ace-lm", ["[Request] Parsed", "[Ace-LM] FATAL: cannot scan --models /x", "bye"]));
        Assert.Equal("bye", AceCppEngine.FailureMessage("ace-lm", ["hello", "bye", " "]));
        Assert.Equal("ace-synth stopped without a word.", AceCppEngine.FailureMessage("ace-synth", []));
    }

    [Fact]
    public async Task Without_the_binaries_it_says_how_to_build_them()
    {
        var engine = new AceCppEngine(
            Microsoft.Extensions.Options.Options.Create(new AceOptions { Root = _root }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AceCppEngine>.Instance);

        Assert.False(engine.Installed);
        var failure = await Assert.ThrowsAsync<AceException>(() =>
            engine.GenerateAsync(new AceRun(_root, [], true, null), (_, _) => { }, CancellationToken.None));
        Assert.Contains("deploy/install-ace.sh", failure.Message);
    }

    [Fact]
    public async Task The_lm_writes_a_request_per_song_and_synth_a_wave_per_request()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var binaries = Path.Combine(_root, "build");
        // ace-lm writes request0.json … beside its input; ace-synth names each wave after its request plus the variation.
        Script(Path.Combine(binaries, "ace-lm"), """
            while [ $# -gt 0 ]; do case "$1" in --request) request="$2"; shift ;; esac; shift; done
            base="${request%.json}"
            echo "[LM-Phase1] Step 50, 2 active, 30.0 tok/s" >&2
            echo "[LM-Phase2] max_tokens: 100, CFG: 2.00, N=2, prompts=2" >&2
            echo "[LM-Phase2] Step 50, 2 active, 100 total codes, 40.0 tok/s" >&2
            echo '{"caption":"pop","lyrics":"La la","seed":7}' > "${base}0.json"
            echo '{"caption":"pop","lyrics":"Lo lo","seed":8}' > "${base}1.json"
            """);
        Script(Path.Combine(binaries, "ace-synth"), """
            echo "$@" > "$(dirname "$0")/synth-arguments"
            next=0
            for argument in "$@"; do
              if [ "$next" = 1 ]; then printf 'RIFF' > "${argument%.json}0.wav"; fi
              if [ "$argument" = "--request" ]; then next=1; fi
            done
            echo "[DiT] Step 8/8 t=0.100" >&2
            echo "[VAE-Decode Batch0] Decode: 10.0 ms" >&2
            echo "[VAE-Decode Batch1] Decode: 10.0 ms" >&2
            """);
        var work = Directory.CreateDirectory(Path.Combine(_root, "work")).FullName;
        var engine = new AceCppEngine(
            Microsoft.Extensions.Options.Options.Create(new AceOptions { Root = _root, Binaries = binaries }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AceCppEngine>.Instance);
        List<(string Stage, double Fraction)> heard = [];

        var outputs = await engine.GenerateAsync(
            new AceRun(work, new System.Text.Json.Nodes.JsonObject { ["caption"] = "pop", ["seed"] = 7, ["lm_batch_size"] = 2 }, true, null),
            (stage, fraction) => heard.Add((stage, fraction)),
            CancellationToken.None);

        Assert.Equal([Path.Combine(work, "request00.wav"), Path.Combine(work, "request10.wav")], outputs.Select(o => o.Wave));
        Assert.Equal(["La la", "Lo lo"], outputs.Select(o => (string?)o.Request["lyrics"]));
        Assert.Equal([7L, 8L], outputs.Select(o => (long?)o.Request["seed"]));
        Assert.Equal(
            $"--models {Path.Combine(_root, "models")} --adapters {Path.Combine(_root, "adapters")} --request {Path.Combine(work, "request0.json")} {Path.Combine(work, "request1.json")}",
            (await File.ReadAllTextAsync(Path.Combine(binaries, "synth-arguments"))).Trim());
        Assert.Equal(["loading", "writing", "composing", "synthesizing", "decoding"], heard.Select(h => h.Stage).Distinct());
        Assert.Equal(1, heard[^1].Fraction, 6);
        Assert.True(heard.Zip(heard.Skip(1)).All(pair => pair.Second.Fraction >= pair.First.Fraction));
    }

    [Fact]
    public async Task A_tool_that_fails_names_its_fatal_line()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var binaries = Path.Combine(_root, "build");
        Script(Path.Combine(binaries, "ace-lm"), """echo "[Ace-LM] FATAL: no LM models found under /x" >&2; exit 1""");
        Script(Path.Combine(binaries, "ace-synth"), "exit 0");
        var engine = new AceCppEngine(
            Microsoft.Extensions.Options.Options.Create(new AceOptions { Root = _root, Binaries = binaries }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AceCppEngine>.Instance);

        var failure = await Assert.ThrowsAsync<AceException>(() =>
            engine.GenerateAsync(new AceRun(_root, new System.Text.Json.Nodes.JsonObject { ["caption"] = "pop" }, true, null), (_, _) => { }, CancellationToken.None));

        Assert.Equal("[Ace-LM] FATAL: no LM models found under /x", failure.Message);
    }

    [Fact]
    public async Task An_empty_request_from_the_lm_stops_before_synth()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var binaries = Path.Combine(_root, "build");
        // Unpatched acestep.cpp: yyjson refuses invalid UTF-8, request_write leaves an empty file and ace-lm exits 0.
        Script(Path.Combine(binaries, "ace-lm"), """
            while [ $# -gt 0 ]; do case "$1" in --request) request="$2"; shift ;; esac; shift; done
            : > "${request%.json}0.json"
            """);
        Script(Path.Combine(binaries, "ace-synth"), """touch "$(dirname "$0")/synth-ran"; exit 1""");
        var engine = new AceCppEngine(
            Microsoft.Extensions.Options.Options.Create(new AceOptions { Root = _root, Binaries = binaries }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AceCppEngine>.Instance);

        var failure = await Assert.ThrowsAsync<AceException>(() =>
            engine.GenerateAsync(new AceRun(_root, new System.Text.Json.Nodes.JsonObject { ["caption"] = "pop" }, true, null), (_, _) => { }, CancellationToken.None));

        Assert.StartsWith("ace-lm wrote an empty request0.json", failure.Message);
        Assert.False(File.Exists(Path.Combine(binaries, "synth-ran")));
    }

    [Fact]
    public async Task Ace_understand_hears_a_recording_with_the_models_it_is_told()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var binaries = Path.Combine(_root, "build");
        Script(Path.Combine(binaries, "ace-understand"), """
            echo "$@" > "$(dirname "$0")/understand-arguments"
            while [ $# -gt 0 ]; do case "$1" in --request) request="$2"; shift ;; -o) out="$2"; shift ;; esac; shift; done
            cp "$request" "$(dirname "$0")/understand-request"
            printf '%s' '{"caption":"lofi piano, soft vocals","lyrics":"[Verse]\nHum","bpm":84,"keyscale":"Eb major","timesignature":"3","vocal_language":"en","duration":31.6,"audio_codes":"1,2"}' > "$out"
            """);
        var work = Directory.CreateDirectory(Path.Combine(_root, "work")).FullName;
        var engine = new AceCppEngine(
            Microsoft.Extensions.Options.Options.Create(new AceOptions { Root = _root, Binaries = binaries }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AceCppEngine>.Instance);

        Assert.True(engine.Understands);
        var heard = await engine.UnderstandAsync("/tmp/idee.wav", work, "lm.gguf", "dit.gguf", CancellationToken.None);

        Assert.Equal(new AceHeard("lofi piano, soft vocals", "[Verse]\nHum", 84, "Eb major", 3, "en", 32, false), heard);
        Assert.Equal(
            $"--models {Path.Combine(_root, "models")} --src-audio /tmp/idee.wav --request {Path.Combine(work, "understand.json")} -o {Path.Combine(work, "heard.json")}",
            (await File.ReadAllTextAsync(Path.Combine(binaries, "understand-arguments"))).Trim());
        Assert.Equal("""{"lm_model":"lm.gguf","synth_model":"dit.gguf"}""", (await File.ReadAllTextAsync(Path.Combine(binaries, "understand-request"))).Trim());
    }

    [Fact]
    public void What_was_heard_keeps_only_what_the_form_can_take()
    {
        var heard = AceHeard.From(new System.Text.Json.Nodes.JsonObject
        {
            ["caption"] = " ",
            ["lyrics"] = "[Instrumental]",
            ["bpm"] = 0,
            ["keyscale"] = "A minor",
            ["timesignature"] = "5",
            ["vocal_language"] = "unknown",
            ["duration"] = 0.0,
        });

        Assert.Equal(new AceHeard(null, null, null, "A minor", null, null, null, true), heard);
        Assert.Equal(4, AceHeard.From(new System.Text.Json.Nodes.JsonObject { ["timesignature"] = "4/4" }).TimeSignature);
    }

    private static void Script(string path, string body)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Theory]
    [InlineData("Wüste Nacht!", "Wueste-Nacht")]
    [InlineData("  Straße / 2026 ", "Strasse-2026")]
    [InlineData("…", "")]
    public void Run_names_keep_to_the_workers_pattern(string title, string slug) => Assert.Equal(slug, AceComposer.Slug(title));

    [Fact]
    public void The_title_falls_back_to_the_captions_first_words()
    {
        Assert.Equal("dark synthwave female vocals 80s", AceComposer.TitleFor(new AceSongRequest(Caption: "dark synthwave, female vocals, 80s drums, reverb")));
        Assert.Equal("Mine", AceComposer.TitleFor(new AceSongRequest(Title: " Mine ", Caption: "pop")));
    }

    [Fact]
    public void A_waves_length_comes_from_its_header()
    {
        var path = Path.Combine(_root, "song.wav");
        File.WriteAllBytes(path, FakeSpeech.Wav(2.5));

        Assert.Equal(2.5, AceComposer.WaveSeconds(path));
        File.WriteAllText(path, "not a wave");
        Assert.Null(AceComposer.WaveSeconds(path));
    }
}
