@testable import TonwerkUI
import XCTest

final class LyricsTests: XCTestCase {
    func testOnlySungLinesAreCountedLikeTheServer() {
        let lines = ShownLine.lines(of: "[Verse]\nErste Zeile\n\n  Zweite Zeile  \n[Chorus]\n...\nDritte")
        XCTAssertEqual(lines.map(\.timed), [nil, 0, nil, 1, nil, nil, 2])
        XCTAssertEqual(lines.map(\.section), [true, false, false, false, true, false, false])
        XCTAssertEqual(lines[3].text, "Zweite Zeile")
    }

    func testABracketInsideALineIsNoSection() {
        let lines = ShownLine.lines(of: "[Verse] la la\n[a] [b]")
        XCTAssertEqual(lines.map(\.section), [false, false])
        XCTAssertEqual(lines.map(\.timed), [0, 1])
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
