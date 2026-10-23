import Foundation
import XCTest
@testable import TonwerkUI

/// The same cases as the web page's `singing.test.ts` (handing a Logic track to the singing page).
final class SingHandOffTests: XCTestCase {
    private func note(_ start: Double, _ end: Double, _ pitch: Int = 60) -> SingMelodyAnswer.Note {
        SingMelodyAnswer.Note(start: start, end: end, pitch: pitch, lyric: nil)
    }

    func testOnlyTheSungWordsOfLyricsGo() {
        let lyrics = "[Verse]\nRain on the roof (whispered)\nNobody knows\n\n[Chorus]\n(Screamy vocals)\nHold on\n[Outro]"
        XCTAssertEqual(ShownLine.sung(lyrics), "Rain on the roof\nNobody knows\n\nHold on")
    }

    func testATrackIsReadInSecondsAfterTheCountIn() throws {
        let json = """
            {"ticksPerQuarterNote": 480, "title": null, "tempoBpm": 120, "timeSignatures": [], "sections": [],
             "chords": [], "lengthTicks": 3840, "countInTicks": 1920, "durationSeconds": 4,
             "voices": [
               {"id": "Vocal", "displayName": "Vocal Melody", "kind": "Melody", "notes": [
                 {"startTicks": 2400, "durationTicks": 480, "noteNumber": 64, "velocity": null},
                 {"startTicks": 1920, "durationTicks": 480, "noteNumber": 62, "velocity": null}]},
               {"id": "Harmony 3rd down", "displayName": "Harmony 3rd down", "kind": "Harmony", "notes": []},
               {"id": "Ins", "displayName": "Ins Melody", "kind": "Melody", "notes": []}]}
            """
        let score = try JSONDecoder().decode(LogicScore.self, from: Data(json.utf8))
        let notes = score.sungNotes(of: "Vocal")
        XCTAssertEqual(notes.map(\.start), [0, 0.5])
        XCTAssertEqual(notes.map(\.end), [0.5, 1])
        XCTAssertEqual(notes.map(\.pitch), [62, 64])
        XCTAssertEqual(score.voices.map(\.isVocal), [true, true, false])
    }

    func testABackingPartSingsTheMelodysWords() {
        // "Hold" on the first note, held over the second, "on" on the third, "me" on the fourth.
        let guide = [note(0, 1), note(1, 2), note(2, 3), note(3, 4)]
        let answer = SingAlignAnswer(
            notes: [
                .init(word: "hold", type: 2, phonemes: ""), .init(word: nil, type: 3, phonemes: ""),
                .init(word: "on", type: 2, phonemes: ""), .init(word: "me", type: 2, phonemes: ""),
            ],
            leftover: 0)
        // A part joining on the held syllable, one in the middle of "on", one outside the melody.
        let part = [note(1, 2, 57), note(2, 2.5, 57), note(2.5, 3, 55), note(3, 4, 57), note(5, 6, 57)]
        XCTAssertEqual(
            SingHandOff.words(for: part, guide: guide, answer: answer).map { $0.lyric ?? "" },
            ["hold", "on", "-", "me", ""])
        XCTAssertEqual(
            SingHandOff.words(for: guide, guide: guide, answer: answer).map { $0.lyric ?? "" },
            ["hold", "-", "on", "me"])
    }
}
