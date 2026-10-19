import Foundation
import XCTest
@testable import TonwerkUI

final class SingEditorTests: XCTestCase {
    private func melody(_ json: String) throws -> SingMelodyAnswer {
        try JSONDecoder().decode(SingMelodyAnswer.self, from: Data(json.utf8))
    }

    @MainActor
    func testDrawnNotesSnapToTheGridAndNeverRunIntoTheNext() {
        let editor = SingEditor()
        let later = editor.add(at: 2.1, pitch: 64)
        XCTAssertEqual(later.start, 2)
        XCTAssertEqual(later.lyric, "la")
        editor.resize(later.id, by: 0.9)
        XCTAssertEqual(editor.notes[0].length, 2)

        let earlier = editor.add(at: 1.3, pitch: 62)
        XCTAssertEqual(earlier.start, 1.25)
        XCTAssertEqual(earlier.length, 0.75, "cut where the next note starts")
        XCTAssertEqual(editor.notes.map(\.pitch), [62, 64])
        XCTAssertEqual(editor.selection, earlier.id)

        editor.move(earlier.id, by: -1.1, semitones: 3)
        XCTAssertEqual(editor.notes[0].start, 0.25)
        XCTAssertEqual(editor.notes[0].pitch, 65)
        editor.undo()
        XCTAssertEqual(editor.notes[0].start, 1.25)
    }

    @MainActor
    func testTypedWordsGoOnePerNoteAndHoldsJoinTheWordBefore() {
        let editor = SingEditor()
        for beat in 0..<4 {
            editor.add(at: Double(beat), pitch: 60 + beat)
        }
        let first = editor.notes[0].id
        let next = editor.type("we are  going", on: first)
        XCTAssertEqual(editor.notes.map(\.lyric), ["we", "are", "going", "la"])
        XCTAssertEqual(next, editor.notes[3].id)

        editor.toggleHold(editor.notes[3].id)
        XCTAssertTrue(editor.notes[3].isHold)
        editor.type("", on: editor.notes[1].id)
        XCTAssertTrue(editor.notes[1].isSilent)

        // Words on the notes: the silent one stays home, the held one goes along.
        let sung = editor.sung()
        XCTAssertEqual(sung.compactMap { $0["lyric"] as? String }, ["we", "going", "-"])
        XCTAssertEqual(sung[1]["start"] as? Double, 1, "two beats at 120 bpm")
        XCTAssertEqual(editor.sung(all: true).count, 4)
    }

    @MainActor
    func testASpreadTextLandsOnTheNotesWithItsPronunciation() throws {
        let editor = SingEditor()
        for beat in 0..<3 {
            editor.add(at: Double(beat), pitch: 60)
        }
        let answer = try JSONDecoder().decode(SingAlignAnswer.self, from: Data(#"""
            {"notes": [{"word": "Circles", "type": 2, "phonemes": "S ER1 K AH0 L Z"},
                       {"word": "Circles", "type": 3, "phonemes": "S ER1 K AH0 L Z"},
                       {"word": null, "type": 1, "phonemes": ""}], "leftover": 2}
            """#.utf8))
        editor.spread(answer)
        XCTAssertEqual(editor.notes.map(\.lyric), ["Circles", "-", ""])
        XCTAssertEqual(editor.leftover, 2)
        XCTAssertEqual(editor.phonemes[editor.notes[0].id], "S ER1 K AH0 L Z")
        XCTAssertNil(editor.phonemes[editor.notes[1].id])
    }

    @MainActor
    func testAMelodyInSecondsLandsOnBeatsOfItsTempo() throws {
        let editor = SingEditor()
        editor.load(try melody(#"""
            {"notes": [{"start": 0.0, "end": 0.625, "pitch": 67, "lyric": "Hel"}, {"start": 0.625, "end": 1.25, "pitch": 69}],
             "tempoBpm": 96, "lyrics": "", "title": "melody", "part": "Vocal"}
            """#))
        XCTAssertEqual(editor.tempo, 96)
        XCTAssertEqual(editor.notes.map(\.start), [0, 1])
        XCTAssertEqual(editor.notes.map(\.length), [1, 1])
        XCTAssertEqual(editor.notes.map(\.lyric), ["Hel", ""])
        XCTAssertFalse(editor.canUndo, "a loaded melody is the start, not a step")

        let score = editor.score(title: "melody")
        XCTAssertEqual(score.voices[0].notes.map(\.startTicks), [0, 480])
        XCTAssertEqual(score.durationSeconds, 1.875, accuracy: 0.001)
    }

    @MainActor
    func testTheMidiFileCarriesTheWordsAndTempo() throws {
        let editor = SingEditor()
        editor.add(at: 0, pitch: 60)
        editor.add(at: 1, pitch: 62)
        editor.type("sun -", on: editor.notes[0].id)
        let midi = editor.midi(title: "Sonne")
        XCTAssertEqual(midi.prefix(4), Data("MThd".utf8))
        XCTAssertEqual(Array(midi[8...13]), [0, 1, 0, 2, 0x01, 0xE0])
        XCTAssertNotNil(midi.range(of: Data([0xFF, 0x05, 3]) + Data("sun".utf8)))
        XCTAssertNotNil(midi.range(of: Data([0xFF, 0x05, 1]) + Data("-".utf8)))
        XCTAssertNotNil(midi.range(of: Data([0xFF, 0x51, 3, 0x07, 0xA1, 0x20])), "500000 µs a beat")
        XCTAssertEqual(SingEditor.variable(480), Data([0x83, 0x60]))
        XCTAssertEqual(SingEditorView.name(60), "C3")
        XCTAssertEqual(SingEditorView.name(37), "C#1")
    }
}
