import TonwerkDSP
import XCTest

/// The C interface over the plugin's DSP: notes sound and end, voices are reused and stolen, the effects add what
/// they should. The DSP itself is measured in the Synth repository's tests.
final class SynthTests: XCTestCase {
    private let rate = 48000.0

    private func lead(engine: Int32 = 0) -> TWPatch {
        var patch = TWPatch()
        patch.engine = engine
        patch.osc1 = TWOscillator(wave: 2, octave: 0, detune: 0, level: 0.7, width: 0.5, pwm: 0, pwmLfo: true, pwmAmpEnv: 0, pwmFilterEnv: 0)
        patch.osc2 = TWOscillator(wave: 3, octave: 0, detune: 7, level: 0.35, width: 0.5, pwm: 0, pwmLfo: true, pwmAmpEnv: 0, pwmFilterEnv: 0)
        patch.cutoff = 1800
        patch.resonance = 2
        patch.envAmount = 1.5
        patch.keyTrack = 0.5
        patch.filterEnv = TWEnvelope(attack: 0.01, decay: 0.4, sustain: 0.3, release: 0.2)
        patch.ampEnv = TWEnvelope(attack: 0.01, decay: 0.3, sustain: 0.75, release: 0.15)
        patch.lfo = TWLfo(wave: 0, rate: 5.5, target: 0, depth: 0.1)
        patch.volume = 0.8
        patch.algorithm = 4
        patch.feedback = 0.4
        let env = TWEnvelope(attack: 0.03, decay: 0.4, sustain: 0.8, release: 0.2)
        patch.op1 = TWOperator(ratio: 1, detune: 0, level: 0.8, velocity: 0.7, env: env)
        patch.op2 = TWOperator(ratio: 1, detune: 0, level: 0.55, velocity: 0.6, env: env)
        patch.op3 = TWOperator(ratio: 1, detune: 0, level: 0.3, velocity: 0.7, env: env)
        patch.op4 = TWOperator(ratio: 2, detune: 0, level: 0.15, velocity: 0.7, env: env)
        patch.fmLfo = TWLfo(wave: 0, rate: 5.5, target: 0, depth: 0.1)
        patch.fmVolume = 0.7
        patch.delayTime = 0.35
        patch.delayFeedback = 0.35
        patch.delayTone = 4000
        patch.reverbDecay = 2
        return patch
    }

    private func synth(_ patch: TWPatch) -> OpaquePointer {
        let synth = tw_synth_create()!
        tw_synth_prepare(synth, rate)
        var patch = patch
        tw_synth_set_patch(synth, &patch)
        return synth
    }

    private func render(_ synth: OpaquePointer, seconds: Double) -> [Float] {
        var left = [Float](repeating: 0, count: Int(seconds * rate))
        var right = left
        left.withUnsafeMutableBufferPointer { l in
            right.withUnsafeMutableBufferPointer { r in
                tw_synth_render(synth, l.baseAddress, r.baseAddress, Int32(l.count))
            }
        }
        return left
    }

    private func rms(_ samples: ArraySlice<Float>) -> Float {
        (samples.reduce(0) { $0 + $1 * $1 } / Float(max(1, samples.count))).squareRoot()
    }

    func testBothEnginesSoundWhileHeldAndFallSilentAfterTheRelease() {
        for engine: Int32 in [0, 1] {
            let synth = synth(lead(engine: engine))
            defer { tw_synth_destroy(synth) }
            tw_synth_midi(synth, 0x90, 60, 100)
            let held = render(synth, seconds: 0.5)
            XCTAssertGreaterThan(rms(held[2400...]), 0.05, "engine \(engine)")
            tw_synth_midi(synth, 0x80, 60, 0)
            let after = render(synth, seconds: 1)
            XCTAssertLessThan(rms(after[24000...]), 1e-5, "engine \(engine)")
            XCTAssertEqual(tw_synth_active_voices(synth), 0)
        }
    }

    func testTheSameKeyTakesItsOwnVoiceAndTheSeventeenthNoteStealsOne() {
        let synth = synth(lead())
        defer { tw_synth_destroy(synth) }
        tw_synth_midi(synth, 0x90, 60, 100)
        tw_synth_midi(synth, 0x90, 60, 100)
        _ = render(synth, seconds: 0.01)
        XCTAssertEqual(tw_synth_active_voices(synth), 1)
        for key: UInt8 in 61...76 {
            tw_synth_midi(synth, 0x90, key, 100)
        }
        _ = render(synth, seconds: 0.01)
        XCTAssertEqual(tw_synth_active_voices(synth), 16)
        tw_synth_midi(synth, 0xB0, 123, 0)
        _ = render(synth, seconds: 1)
        XCTAssertEqual(tw_synth_active_voices(synth), 0, "all notes off lets every voice go")
    }

    func testTheDelayRepeatsTheNoteAfterItsTime() {
        var patch = lead()
        patch.ampEnv = TWEnvelope(attack: 0.002, decay: 0.05, sustain: 0, release: 0.02)
        patch.delayMix = 0.8
        patch.delayTime = 0.25
        let synth = synth(patch)
        defer { tw_synth_destroy(synth) }
        tw_synth_midi(synth, 0x90, 72, 127)
        var samples = render(synth, seconds: 0.05)
        tw_synth_midi(synth, 0x80, 72, 0)
        samples += render(synth, seconds: 0.5)
        XCTAssertLessThan(rms(samples[8000..<9000]), 1e-4, "silence between the note and its echo")
        XCTAssertGreaterThan(rms(samples[12000..<13500]), 0.01, "the echo a quarter second later")
    }

    func testAFullReverbAddsAboutTheDrySoundsLevel() {
        func level(reverb: Float) -> Float {
            var patch = lead()
            patch.osc1.level = 0
            patch.osc2.level = 0
            patch.noise = 1
            patch.cutoff = 18000
            patch.envAmount = 0
            patch.ampEnv = TWEnvelope(attack: 0.01, decay: 1, sustain: 1, release: 0.1)
            patch.lfo.depth = 0
            patch.reverbMix = reverb
            let synth = synth(patch)
            defer { tw_synth_destroy(synth) }
            tw_synth_midi(synth, 0x90, 60, 127)
            return rms(render(synth, seconds: 5)[(4 * 48000)...])
        }
        let dry = level(reverb: 0)
        let wet = (pow(level(reverb: 1), 2) - pow(dry, 2)).squareRoot()
        XCTAssertEqual(wet / dry, 1, accuracy: 0.25)
    }

    func testAnUnpreparedSynthPassesSilenceRatherThanGarbage() {
        let synth = tw_synth_create()!
        defer { tw_synth_destroy(synth) }
        var left = [Float](repeating: 7, count: 64)
        left.withUnsafeMutableBufferPointer { tw_synth_render(synth, $0.baseAddress, nil, 64) }
        XCTAssertEqual(left, [Float](repeating: 0, count: 64))
    }
}
