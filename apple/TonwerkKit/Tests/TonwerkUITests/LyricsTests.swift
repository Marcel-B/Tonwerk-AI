@testable import TonwerkUI
import XCTest

final class LyricsTests: XCTestCase {
    func testOnlySungLinesAreCountedLikeTheServer() {
        let lines = ShownLine.lines(of: "[Verse]\nErste Zeile\n\n  Zweite Zeile  \n[Chorus]\n...\nDritte")
        XCTAssertEqual(lines.map(\.timed), [nil, 0, nil, 1, nil, nil, 2])
        XCTAssertEqual(lines.map(\.section), [true, false, false, false, true, false, false])
        XCTAssertEqual(lines[3].text, "Zweite Zeile")
    }

    func testATagBeforeTextStandsOnItsOwnLine() {
        let lines = ShownLine.lines(of: "[Verse] la la\n[a] [b]")
        XCTAssertEqual(lines.map(\.text), ["[Verse]", "la la", "[a]", "[b]"])
        XCTAssertEqual(lines.map(\.section), [true, false, true, true])
        XCTAssertEqual(lines.map(\.timed), [nil, 0, nil, nil])
    }

    private let paragraph =
        "[Intro] (Heavy distorted synth bass intro, 140 BPM) (Sound of electronic glitches)\n\n"
        + "[Verse 1] (Cold, robotic female vocals) Neon rain on carbon glass The ghosts of who we were, they pass "
        + "Where does the circuit end, and I begin? (Slightly more aggressive) I traded breath for silver veins\n\n"
        + "[Chorus]\nHold on"

    func testDirectionsAreHiddenAndLostLineEndsBroken() {
        XCTAssertEqual(ShownLine.lines(of: paragraph).map(\.text), [
            "[Intro]", "", "[Verse 1]", "Neon rain on carbon glass", "The ghosts of who we were, they pass",
            "Where does the circuit end, and I begin?", "I traded breath for silver veins", "", "[Chorus]", "Hold on",
        ])
    }

    func testDirectionsShowApartOnRequestAndAreNeverLit() {
        let directions = ShownLine.lines(of: paragraph, directions: true).filter(\.direction)
        XCTAssertEqual(directions.map(\.text), [
            "(Heavy distorted synth bass intro, 140 BPM)", "(Sound of electronic glitches)",
            "(Cold, robotic female vocals)", "(Slightly more aggressive)",
        ])
        XCTAssertTrue(directions.allSatisfy { $0.timed == nil })
        XCTAssertTrue(ShownLine.hasDirections(paragraph))
        XCTAssertFalse(ShownLine.hasDirections("[Verse]\nRain on the roof"))
    }

    func testPiecesOfOneLineShareTheServersNumber() {
        let lines = ShownLine.lines(of: paragraph)
        XCTAssertEqual(lines.filter { $0.timed == 1 }.count, 4)
        XCTAssertEqual(lines.last?.timed, 2)
    }

    func testAParagraphLightsLineByLineByItsShareOfTheWords() {
        let lines = ShownLine.lines(of: paragraph)
        // The verse is timed line 1 from 10 s to 37 s: 27 words, a second each.
        let times = [LineTimes(start: 0, end: 5), LineTimes(start: 10, end: 37), LineTimes(start: 40, end: 42)]
        func at(_ seconds: Double) -> String? {
            ShownLine.shown(at: seconds, in: lines, times: times).map { lines[$0].text }
        }
        XCTAssertEqual(at(12), "Neon rain on carbon glass")
        XCTAssertEqual(at(16), "The ghosts of who we were, they pass")
        XCTAssertEqual(at(33), "I traded breath for silver veins")
        XCTAssertEqual(at(39), "I traded breath for silver veins")
        XCTAssertEqual(at(41), "Hold on")
        XCTAssertNil(at(8))
        let third = lines.firstIndex { $0.text.hasPrefix("Where does") }!
        XCTAssertEqual(ShownLine.start(of: third, in: lines, times: times), 23)
    }

    func testATimingOfTheShownLinesIsTakenOneByOne() {
        let lines = ShownLine.lines(of: paragraph)
        let texts = [
            "Neon rain on carbon glass", "The ghosts of who we were, they pass", "Where does the circuit end, and I begin?",
            "I traded breath for silver veins", "Hold on",
        ]
        let times = texts.enumerated().map {
            LineTimes(start: 10 + Double($0.offset) * 10, end: 18 + Double($0.offset) * 10, text: $0.element)
        }
        XCTAssertEqual(ShownLine.shown(at: 21, in: lines, times: times).map { lines[$0].text }, texts[1])
        XCTAssertEqual(ShownLine.shown(at: 51, in: lines, times: times).map { lines[$0].text }, "Hold on")
        let third = lines.firstIndex { $0.text.hasPrefix("Where does") }!
        XCTAssertEqual(ShownLine.start(of: third, in: lines, times: times), 30)
    }

    func testGermanCapitalsAndShortLinesStay() {
        let german = "Ich laufe durch die Stadt im Regen und der Wind singt mir ein Lied von Morgen"
        XCTAssertEqual(ShownLine.lines(of: german).map(\.text), [german])
        XCTAssertEqual(ShownLine.lines(of: "Rain on the roof Nobody knows").map(\.text), ["Rain on the roof Nobody knows"])
    }

    func testTheSungLineIsTheLastStartedUntilABreak() {
        let times = [LineTimes(start: 2, end: 5), LineTimes(start: 6, end: 9), LineTimes(start: 30, end: 33)]
        XCTAssertNil(LineTimes.line(at: 1, in: times))
        XCTAssertEqual(LineTimes.line(at: 2, in: times), 0)
        XCTAssertEqual(LineTimes.line(at: 5.5, in: times), 0)
        XCTAssertEqual(LineTimes.line(at: 12.9, in: times), 1)
        XCTAssertNil(LineTimes.line(at: 13.5, in: times))
        XCTAssertEqual(LineTimes.line(at: 31, in: times), 2)
        XCTAssertNil(LineTimes.line(at: 3, in: []))
    }
}
