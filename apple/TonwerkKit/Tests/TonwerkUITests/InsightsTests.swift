import Foundation
import TonwerkKit
import XCTest
@testable import TonwerkUI

final class InsightsTests: XCTestCase {
    private func report(styles: Int, average: Double = 3) throws -> Components.Schemas.InsightReport {
        // Scores falling from 4.5, so the first blocks are above the average and the last below.
        let entries = (0..<styles).map { index -> [String: Any] in
            ["value": "block\(index)", "count": 3, "average": 4.5 - Double(index) * 0.2, "score": 4.5 - Double(index) * 0.2]
        }
        let engine: [[String: Any]] = [["value": "ace", "count": 4, "average": 3.5, "score": 3.2]]
        let groups: [[String: Any]] = [["key": "style", "entries": entries], ["key": "engine", "entries": engine]]
        let json: [String: Any] = ["rated": 20, "average": average, "groups": groups]
        let data = try JSONSerialization.data(withJSONObject: json)
        return try JSONDecoder.tonwerk().decode(Components.Schemas.InsightReport.self, from: data)
    }

    func testProvenBlocksAreTheStyleBlocksAboveTheAverageAtMostTwelve() throws {
        XCTAssertEqual(Insights.proven(try report(styles: 5)), ["block0", "block1", "block2", "block3", "block4"])
        XCTAssertEqual(Insights.proven(try report(styles: 5, average: 4)), ["block0", "block1", "block2"])
        XCTAssertEqual(Insights.proven(try report(styles: 20, average: 0)).count, 12)
        XCTAssertEqual(Insights.proven(nil), [])
    }

    func testOnlyALongStyleListIsSplitIntoBestAndWeakest() throws {
        let long = try report(styles: 20)
        let style = long.groups[0]
        let parts = Insights.split(style)
        XCTAssertEqual(parts.best.count, 10)
        XCTAssertEqual(parts.weak.map(\.value), (15..<20).map { "block\($0)" })
        XCTAssertTrue(Insights.split(long.groups[1]).weak.isEmpty)
        XCTAssertTrue(Insights.split(try report(styles: 15).groups[0]).weak.isEmpty)
    }

    func testValuesReadAsThePagesWords() throws {
        XCTAssertEqual(Insights.valueName(group: "engine", "ace"), "ACE-Step")
        XCTAssertEqual(Insights.valueName(group: "length", "<2"), "unter 2 Minuten")
        XCTAssertEqual(Insights.valueName(group: "length", "3-4"), "3 bis 4 Minuten")
        XCTAssertEqual(Insights.valueName(group: "lora", ""), "ohne LoRA")
        XCTAssertEqual(Insights.valueName(group: "style", "half-time beat"), "half-time beat")
        let style = try report(styles: 20).groups[0]
        XCTAssertEqual(Insights.tone(style.entries[0], average: 3), .good)
        XCTAssertEqual(Insights.tone(style.entries[19], average: 3), .weak)
    }
}
