import Foundation
import TonwerkKit
import XCTest
@testable import TonwerkUI

final class LogicTests: XCTestCase {
    private func instrument(
        _ id: Int64, channel: Int32, drums: Components.Schemas.DrumNotes? = nil
    ) -> Components.Schemas.Instrument {
        Components.Schemas.Instrument(
            id: id, name: "Gerät \(id)", port: "Port \(id)", channel: channel,
            kind: drums == nil ? .synth : .drumMachine, drums: drums)
    }

    func testOptionsPutAssignedTracksOnTheirChannels() throws {
        var form = LogicForm()
        form.bass = "Walking"
        form.countIn = 2
        let options = form.options(assignments: ["Bass": 7, "Vocal": 99], instruments: [instrument(7, channel: 12)])
        XCTAssertEqual(options["midiChannels"] as? [String: Int], ["Bass": 12])
        let arrangement = try XCTUnwrap(options["arrangement"] as? [String: Any])
        XCTAssertEqual((arrangement["bass"] as? [String: Any])?["pattern"] as? String, "Walking")
        XCTAssertEqual((arrangement["countIn"] as? [String: Any])?["bars"] as? Int, 2)
        XCTAssertNil(arrangement["drums"])
        XCTAssertNil(arrangement["chords"], "chords as written leave the chord track to the renderer")
        XCTAssertTrue(JSONSerialization.isValidJSONObject(options))
    }

    func testDrumsTakeTheDrumMachinesNotes() {
        let machine = instrument(3, channel: 10, drums: .init(kick: 60, snare: 62, closedHiHat: 64, openHiHat: 65, crash: 67, clap: 63))
        XCTAssertEqual(
            LogicForm.drumNotes(splitDrums: false, assignments: ["Drums": 3], instruments: [machine]),
            ["kick": 60, "snare": 62, "closedHiHat": 64, "openHiHat": 65, "crash": 67, "clap": 63])
        XCTAssertNil(LogicForm.drumNotes(splitDrums: false, assignments: [:], instruments: [machine]))
        // A split kit: only the snare on the machine, the rest General MIDI, the clap from the kick's (none).
        let split = LogicForm.drumNotes(splitDrums: true, assignments: ["Snare": 3], instruments: [machine])
        XCTAssertEqual(split?["snare"], 62)
        XCTAssertEqual(split?["kick"], 36)
        XCTAssertEqual(split?["clap"], 39)
    }

    func testMixerIsClampedAndKeyedInUpperCase() {
        let mixer = LogicMixer(json: [
            "master": 5,
            "tracks": ["Vocal": ["volume": 0.5, "pan": -3], "Bass": "broken"],
        ])
        XCTAssertEqual(mixer.master, LogicMixer.maxVolume)
        XCTAssertEqual(mixer["vocal"], LogicMixer.Mix(volume: 0.5, pan: -1))
        XCTAssertEqual(mixer["Bass"], LogicMixer.Mix())
        XCTAssertEqual(LogicMixer(json: nil), LogicMixer())
    }

    private let conversion = Data("""
    {"success":true,"midi":"TVRoZA==","diagnostics":[{"severity":"Warning","code":"YTL010","message":"Odd bar","line":4,"column":null}],
     "score":{"ticksPerQuarterNote":480,"title":"Test","tempoBpm":120,
      "timeSignatures":[{"startTicks":0,"numerator":4,"denominator":4},{"startTicks":3840,"numerator":3,"denominator":4}],
      "keySignatures":[],"sections":[{"startTicks":0,"name":"verse"}],
      "voices":[{"id":"Vocal","displayName":"Vocal","kind":"Melody","notes":[{"startTicks":0,"durationTicks":480,"noteNumber":64,"velocity":null}]},
                {"id":"Bass","displayName":"Bass","kind":"Bass","notes":[]}],
      "chords":[{"startTicks":0,"durationTicks":1920,"text":"Am/C","symbol":{"rootPitchClass":9,"quality":"Minor","bassPitchClass":0}}],
      "lengthTicks":5280,"countInTicks":960,"durationSeconds":5.5}}
    """.utf8)

    func testConversionDecodesAndAddsTheWrittenChords() throws {
        let result = try JSONDecoder().decode(LogicConversion.self, from: conversion)
        let score = try XCTUnwrap(result.score)
        XCTAssertEqual(score.countInSeconds, 1, accuracy: 0.0001)
        XCTAssertEqual(score.barLines, [0, 1920, 3840])
        let voices = score.playableVoices(includeChords: true)
        XCTAssertEqual(voices.map(\.id), ["Vocal", "Chords", "Bass"])
        XCTAssertEqual(voices[1].notes.map(\.noteNumber), [36, 57, 60, 64])
        XCTAssertEqual(score.playableVoices(includeChords: false).map(\.id), ["Vocal", "Bass"])
    }

    func testProjectNameAvoidsPathCharacters() {
        XCTAssertEqual(AppModel.projectName("AC/DC: Live"), "AC-DC- Live")
        XCTAssertEqual(AppModel.projectName("  "), "Tonwerk")
    }
}
