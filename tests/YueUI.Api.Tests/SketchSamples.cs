using YueUI.Api.Sketches;

namespace YueUI.Api.Tests;

/// <summary>
/// A short plan in the shape Claude answers, from the sketch "Before the Silence" (96 BPM, D minor): two bars of intro
/// with the album's motif, the chorus' first four bars with the hook as the sketch writes it, and a bar of outro.
/// </summary>
public static class SketchSamples
{
    public static SketchPlan Plan() => new(
        "Before the Silence",
        "English, intimate dream pop, dark synth-pop pulse, close female voice, soft piano, warm analog pad, round synth bass, muted drums, 96 BPM",
        96,
        4,
        "Dm",
        [
            new SketchSection("Intro", "intro", ["piano"], 1, [],
            [
                new SketchBar("Dm(add9)", null, null, "D4:6 A4:2 G4:4 r:4", null),
                new SketchBar("Bbmaj7", null, null, null, null),
            ]),
            new SketchSection("Chorus 1", "chorus", ["piano", "pad", "bass", "drums"], 3,
            [
                "Before the silence, there was this:",
                "your crooked smile against my cheek.",
            ],
            [
                // The hook as the sketch writes it: A4 C5 A4 G4 F4 | G4 E4 G4, an eighth's breath at the end.
                new SketchBar("F", "A4:2 C5:6 A4:2 G4:4 F4:2", "Before the silence,", null, null),
                new SketchBar("C/E", "G4:4 E4:4 G4:6 r:2", "there was this:", null, null),
                new SketchBar("Dm7", "A4:2 A4:2 C5:4 D5:4~ r:4", "your crooked smile", null, null),
                new SketchBar("Bbmaj7", "D5:4 C5:2 A4:2 G4:4 r:4", "against my cheek.", null, ["piano", "pad", "bass"]),
            ]),
            new SketchSection("Outro", "outro", ["piano", "pad"], 1, [],
            [
                new SketchBar("Dm(add9)", null, null, "D4:6 A4:2 G4:4 E4:4", null),
            ]),
        ],
        ["The verse melody stays in D4 to A4."]);
}
