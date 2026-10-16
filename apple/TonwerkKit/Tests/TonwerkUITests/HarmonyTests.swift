import Foundation
import TonwerkKit
import XCTest
@testable import TonwerkUI

final class HarmonyTests: XCTestCase {
    func testOnlyTheBackingVocalsAreSwitchedOnAsTheWebPageSendsThem() throws {
        var settings = HarmonySettings()
        settings.parts = ["Tenor", "ThirdAbove"]
        settings.key = "F#m"
        settings.chorusOnly = true
        let options = settings.options(assignments: [:], instruments: [])
        XCTAssertEqual(options["includeChordTrack"] as? Bool, false)
        let arrangement = try XCTUnwrap(options["arrangement"] as? [String: Any])
        XCTAssertNil(arrangement["bass"])
        XCTAssertNil(arrangement["drums"])
        XCTAssertNil(arrangement["chords"])
        let harmony = try XCTUnwrap(arrangement["harmony"] as? [String: Any])
        XCTAssertEqual(harmony["parts"] as? [String], ["ThirdAbove", "Tenor"], "in the page's order")
        XCTAssertEqual(harmony["voiceId"] as? String, "Vocal")
        XCTAssertEqual(harmony["key"] as? String, "F#m")
        XCTAssertEqual(harmony["sections"] as? [String], ["chorus", "refrain"])
        XCTAssertNoThrow(try JSONSerialization.data(withJSONObject: options))

        settings.key = nil
        settings.chorusOnly = false
        settings.withChords = true
        let again = settings.options(assignments: [:], instruments: [])
        XCTAssertEqual(again["includeChordTrack"] as? Bool, true)
        let found = try XCTUnwrap((again["arrangement"] as? [String: Any])?["harmony"] as? [String: Any])
        XCTAssertTrue(found["key"] is NSNull, "the converter finds the key")
        XCTAssertEqual(found["sections"] as? [String], [])
    }

    func testAStoredFormOfAnotherVersionKeepsWhatItCanRead() throws {
        let data = Data(#"{"parts":["Alto","Soprano"],"key":"H","chorusOnly":"yes","withChords":true}"#.utf8)
        let settings = try JSONDecoder().decode(HarmonySettings.self, from: data)
        XCTAssertEqual(settings.parts, ["Alto"])
        XCTAssertNil(settings.key)
        XCTAssertFalse(settings.chorusOnly)
        XCTAssertTrue(settings.withChords)
        XCTAssertEqual(HarmonySettings().parts, ["ThirdAbove", "SixthBelow"])
    }

    func testTheNotesLeaveOutTheRoutineOnes() {
        let diagnostics = [
            LogicDiagnostic(severity: "Info", code: "YTL031", message: "key", line: nil),
            LogicDiagnostic(severity: "Info", code: "YTL010", message: "routine", line: nil),
            LogicDiagnostic(severity: "Warning", code: "YTL074", message: "no chord track", line: nil),
            LogicDiagnostic(severity: "Warning", code: "YTL053", message: "kept", line: nil),
        ]
        XCTAssertEqual(HarmonySession.notes(diagnostics).map(\.code), ["YTL031", "YTL053"])
    }

    func testTheMidiFileIsNamedAfterTheMelodyAndTheConversionCarriesIt() throws {
        let file = PickedFile(url: URL(fileURLWithPath: "/tmp/Meine: Melodie.mid"), name: "Meine: Melodie.mid")
        XCTAssertEqual(HarmonySession.fileName(.file(file)), "Meine- Melodie-begleitstimmen.mid")
        let data = Data(#"{"success":true,"score":null,"diagnostics":[],"midi":"TVRoZA=="}"#.utf8)
        let conversion = try JSONDecoder().decode(LogicConversion.self, from: data)
        XCTAssertEqual(conversion.midi.flatMap { Data(base64Encoded: $0) }, Data("MThd".utf8))
    }
}
