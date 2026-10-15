import Foundation
import TonwerkDSP
import XCTest
@testable import TonwerkUI

final class SynthSoundTests: XCTestCase {
    func testAStoredSoundIsFilledFromTheKindsDefaultAndClamped() {
        let sound = SynthSound(json: [
            "osc1": ["wave": "square", "octave": 7, "width": 0.01, "detune": "broken"],
            "filter": ["cutoff": 99999, "type": "comb"],
            "ampEnv": ["release": true],
            "fx": ["delay": ["mix": 0.5, "feedback": 3]],
        ], kind: "Bass")
        XCTAssertEqual(sound.engine, .analog)
        XCTAssertEqual(sound.analog.osc1.wave, .square)
        XCTAssertEqual(sound.analog.osc1.octave, 2)
        XCTAssertEqual(sound.analog.osc1.width, 0.05)
        XCTAssertEqual(sound.analog.osc1.detune, 0, "the bass's own detune, not a broken value")
        XCTAssertEqual(sound.analog.osc2, AnalogSound.defaults(for: "Bass").osc2)
        XCTAssertEqual(sound.analog.filter.cutoff, 18000)
        XCTAssertEqual(sound.analog.filter.type, .lowpass)
        XCTAssertEqual(sound.analog.ampEnv.release, 0.08, "a Boolean is no number")
        XCTAssertEqual(sound.fx.delayMix, 0.5)
        XCTAssertEqual(sound.fx.delayFeedback, 0.9)
        XCTAssertEqual(sound.fx.reverbMix, 0)
    }

    func testAnFmSoundRoundsRatiosToHalvesAndKeepsFourOperators() {
        let sound = SynthSound(json: ["engine": "fm", "algorithm": 9.4, "ops": [["ratio": 2.3], ["level": -1]]], kind: "Chords")
        XCTAssertEqual(sound.engine, .fm)
        XCTAssertEqual(sound.fm.algorithm, 8)
        XCTAssertEqual(sound.fm.ops.count, 4)
        XCTAssertEqual(sound.fm.ops[0].ratio, 2.5)
        XCTAssertEqual(sound.fm.ops[1].level, 0)
        XCTAssertEqual(sound.fm.ops[3].ratio, 14, "the electric piano's bell")
    }

    func testTheJsonCarriesTheChosenEngineAndReadsBackTheSame() throws {
        var sound = SynthSound.defaults(for: "Chords")
        sound.analog.filter.cutoff = 900
        sound.fx.reverbMix = 0.3
        let json = sound.json
        XCTAssertEqual(json["engine"] as? String, "analog")
        XCTAssertNil(json["ops"])
        XCTAssertTrue(JSONSerialization.isValidJSONObject(json))
        let data = try JSONSerialization.data(withJSONObject: json)
        XCTAssertEqual(SynthSound(json: try JSONSerialization.jsonObject(with: data), kind: "Chords"), sound)

        sound.engine = .fm
        let fm = SynthSound(json: sound.json, kind: "Chords")
        XCTAssertEqual(fm.fm, sound.fm)
        XCTAssertEqual(fm.fx, sound.fx)
    }

    func testDoublingAndHarmonyAreTheLeadQuieter() {
        XCTAssertEqual(AnalogSound.defaults(for: "Doubling").volume, 0.5)
        XCTAssertEqual(FmSound.defaults(for: "Harmony").volume, 0.45)
        XCTAssertEqual(AnalogSound.defaults(for: "Harmony").osc1, AnalogSound.lead.osc1)
    }

    func testThePatchHandsTheSynthesizerTheValues() {
        var sound = SynthSound.defaults(for: "Bass", engine: .fm)
        sound.analog.filter.type = .bandpass
        sound.analog.lfo.target = .amp
        sound.fm.lfo.target = .index
        let patch = sound.patch
        XCTAssertEqual(patch.engine, 1)
        XCTAssertEqual(patch.filterType, 2)
        XCTAssertEqual(patch.lfo.target, 2)
        XCTAssertEqual(patch.fmLfo.target, 1)
        XCTAssertEqual(patch.osc2.octave, -1)
        XCTAssertEqual(patch.osc2.wave, 3)
        XCTAssertEqual(patch.algorithm, 3)
        XCTAssertEqual(patch.op3.ratio, 3)
        XCTAssertEqual(patch.fmVolume, 0.85)
    }

    func testFormatsReadAsTheEditorShowsThem() {
        XCTAssertEqual(ParamFormat.seconds.text(0.25), "250 ms")
        XCTAssertEqual(ParamFormat.hertz.text(1800), "1.8 kHz")
        XCTAssertEqual(ParamFormat.percent.text(0.354), "35 %")
    }
}
