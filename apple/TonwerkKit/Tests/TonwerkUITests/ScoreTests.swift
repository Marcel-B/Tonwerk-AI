import Foundation
import TonwerkKit
@testable import TonwerkUI
import XCTest

/// The web page's `abcTranspose.test.ts`, held to the same results.
final class AbcScoreTests: XCTestCase {
    // One bar that leans on YuE2's rule that an accidental holds for its letter in every octave until the bar line.
    private let score = "X:1\nK:C\nV:Vocal\n\"C\" C D E F | ^F G ^f A |\n\"G7\" B c d2 |]"

    func testATranspositionMovesNotesChordsAndKeyAndSpellsThemInTheNewKey() {
        XCTAssertEqual(
            AbcScore.transpose(score, by: 2),
            .init(abc: "X:1\nK:D\nV:Vocal\n\"D\" D E F G | ^G A g B |\n\"A7\" c d e2 |]", key: "D"))
    }

    func testANaturalIsWrittenWhereTheKeyOrAnEarlierAccidentalWouldSharpen() {
        XCTAssertEqual(
            AbcScore.transpose(score, by: -1).abc,
            "X:1\nK:B\nV:Vocal\n\"B\" B, C D E | =F ^F =f G |\n\"F#7\" A B c2 |]")
        XCTAssertEqual(
            AbcScore.transpose(score, by: 1).abc,
            "X:1\nK:Db\nV:Vocal\n\"Db\" D E F G | =G A g B |\n\"Ab7\" c d e2 |]")
    }

    func testAnOctaveKeepsTheSpellingAndMovesTheOctaveMarks() {
        XCTAssertEqual(
            AbcScore.transpose(score, by: 12).abc,
            "X:1\nK:C\nV:Vocal\n\"C\" c d e f | ^f g f' a |\n\"G7\" b c' d'2 |]")
    }

    func testThereAndBackSingsTheSameNotes() {
        for semitones in [1, 3, 5, 7, -2, -6] {
            let back = AbcScore.transpose(AbcScore.transpose(score, by: semitones).abc, by: -semitones).abc
            XCTAssertEqual(AbcScore.vocalPitches(back), AbcScore.vocalPitches(score), "\(semitones)")
        }
        XCTAssertEqual(
            AbcScore.transpose(AbcScore.transpose(score, by: 2).abc, by: -2).abc,
            score.replacingOccurrences(of: "^f", with: "f"))
    }

    func testMajorToMinorLowersThirdSixthAndSeventhAndTheChordsFollow() {
        XCTAssertEqual(
            AbcScore.changeMode("X:1\nK:D\nV:Vocal\n\"D\" D E F G | A B c d |"),
            .init(abc: "X:1\nK:Dm\nV:Vocal\n\"Dm\" D E F G | A B c d |", key: "Dm"))
    }

    func testIntoTheOtherModeChangesTheModeThenTakesTheShorterWay() {
        XCTAssertEqual(
            AbcScore.transpose(score, to: "Am"),
            .init(abc: "X:1\nK:Am\nV:Vocal\n\"Am\" A, B, C D | _E =E _e F |\n\"Em7\" G A B2 |]", key: "Am"))
        XCTAssertEqual(AbcScore.vocalPitches(AbcScore.transpose(score, to: "G").abc).first, 55)
        XCTAssertEqual(AbcScore.vocalPitches(AbcScore.transpose(score, to: "F").abc).first, 65)
    }

    func testAll24KeysAreOfferedAndLabelled() {
        XCTAssertEqual(Set(AbcScore.keyChoices).count, 24)
        XCTAssertEqual(AbcScore.keyLabel("F#m"), "F♯-Moll")
        XCTAssertEqual(AbcScore.keyLabel("Bb"), "B♭-Dur")
        XCTAssertEqual(AbcScore.keyLabel("B"), "B-Dur")
    }

    func testTheVocalRange() {
        XCTAssertEqual(AbcScore.vocalPitches(score), [60, 62, 64, 65, 66, 67, 78, 69, 71, 72, 74])
        let range = AbcScore.vocalRange(score)
        XCTAssertEqual(range?.low, "C4")
        XCTAssertEqual(range?.high, "F♯5")
        XCTAssertNil(AbcScore.vocalRange("X:1\nK:C\n"))
        XCTAssertEqual(AbcScore.key(of: score), "C")
        XCTAssertNil(AbcScore.key(of: "X:1\nT:none\n"))
    }

    func testPlanningFollowsTheChords() {
        XCTAssertEqual(AbcScore.planning(for: AbcScore.example), .full)
        XCTAssertEqual(AbcScore.planning(for: "X:1\nV: Vocal name=\"Vocal\"\nK:C\nC D E F|"), .melody)
    }
}

final class StyleBlocksTests: XCTestCase {
    func testABlockGoesInAndOutAndLanguageLeadsTempoTrails() {
        var style = "dreamy"
        style = StyleBlocks.toggle("90 BPM", in: style)
        style = StyleBlocks.toggle("German", in: style)
        style = StyleBlocks.toggle("pop", in: style)
        XCTAssertEqual(style, "German, pop, dreamy, 90 BPM")
        XCTAssertEqual(StyleBlocks.toggle("pop", in: style), "German, dreamy, 90 BPM")
    }

    func testAnExclusiveBlockReplacesItsSibling() {
        XCTAssertEqual(StyleBlocks.toggle("120 BPM", in: "pop, 88 bpm"), "pop, 120 BPM")
        XCTAssertEqual(StyleBlocks.toggle("male vocal", in: "female vocal, piano"), "male vocal, piano")
        XCTAssertEqual(StyleBlocks.count(.genre, in: "pop, rock, piano"), 2)
    }

    func testEveryVoiceBlockIsExplained() {
        for tag in StyleBlocks.Category.voice.tags + StyleBlocks.Category.timbre.tags {
            XCTAssertNotNil(StyleBlocks.explanation(of: tag), tag)
        }
        XCTAssertNil(StyleBlocks.explanation(of: "pop"))
    }

    func testASectionTagGetsALineOfItsOwn() {
        XCTAssertEqual(SectionTags.append("[Verse]", to: ""), "[Verse]\n")
        XCTAssertEqual(SectionTags.append("[Chorus]", to: "[Verse]\nHallo\n"), "[Verse]\nHallo\n\n[Chorus]\n")
        XCTAssertEqual(SectionTags.append("[Outro]", to: "[Intro]\n"), "[Intro]\n[Outro]\n")
    }

    func testASectionTagGoesWhereTheCaretWas() {
        let lyrics = "[Verse]\nHallo\nWelt\n"
        let caret = Array(lyrics).firstIndex(of: "W")!
        XCTAssertEqual(SectionTags.insert("[Chorus]", into: lyrics, at: caret).text, "[Verse]\nHallo\n\n[Chorus]\nWelt\n")
        // Mid-line, the tag waits for the line's end.
        XCTAssertEqual(SectionTags.insert("[Chorus]", into: lyrics, at: 10).text, "[Verse]\nHallo\n\n[Chorus]\nWelt\n")
    }

    func testDirectionsStayInTheirSectionAndJoin() {
        let first = SectionTags.insert("(screaming)", into: "[Verse]\n", at: nil)
        XCTAssertEqual(first.text, "[Verse]\n(screaming)\n")
        let second = SectionTags.insert("(full power)", into: first.text, at: first.caret)
        XCTAssertEqual(second.text, "[Verse]\n(screaming, full power)\n")
        let lines = "Line one\nLine two\n"
        XCTAssertEqual(SectionTags.insert("(belting)", into: lines, at: 9).text, "Line one\n(belting)\nLine two\n")
    }

    func testShoutingWritesCapitalsAndTurnsBack() {
        let lyrics = "[Chorus]\nCold chrome, hollow heart\nTearing every nerve apart!"
        let loud = SectionTags.shout(lyrics, from: 9, to: 42)
        XCTAssertEqual(loud.text, "[Chorus]\nCOLD CHROME, HOLLOW HEART\nTEARING every nerve apart!")
        let back = SectionTags.shout(loud.text, from: loud.start, to: loud.end)
        XCTAssertEqual(back.text, lyrics)
        let line = "[Chorus]\n(Screaming) I am a ghost\nnext"
        XCTAssertEqual(SectionTags.shout(line, from: 25, to: 25).text, "[Chorus]\n(Screaming) I AM A GHOST\nnext")
        XCTAssertEqual(SectionTags.shout("heiß", from: 0, to: 4).end, 5)
    }
}
