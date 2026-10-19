import Foundation
import TonwerkKit
import XCTest
@testable import TonwerkUI

final class SingingTests: XCTestCase {
    private func take(_ id: String, stage: String, updated: String = "2026-10-19T08:00:00Z",
                      created: String = "2026-10-19T07:00:00Z", progress: Double? = nil) throws
        -> Components.Schemas.SingTake
    {
        var json: [String: Any] = [
            "id": id, "title": "Melodie", "stage": stage, "voiceId": "en", "voiceLabel": "Beispielstimme Englisch",
            "lyrics": "Hello world", "pitchShift": 0, "autoShift": false, "noteCount": 4,
            "createdAt": created, "updatedAt": updated,
        ]
        if let progress {
            json["progress"] = progress
        }
        let data = try JSONSerialization.data(withJSONObject: json)
        return try JSONDecoder.tonwerk().decode(Components.Schemas.SingTake.self, from: data)
    }

    func testLiveTakesLayOverTheLoadedOnesAndStagesReadAsThePagesWords() throws {
        let loaded = [try take("a", stage: "queued"), try take("b", stage: "singing")]
        let live = [
            try take("a", stage: "singing", updated: "2026-10-19T08:01:00Z", created: "2026-10-19T07:30:00Z",
                     progress: 0.5),
            try take("b", stage: "cancelled", updated: "2026-10-19T08:02:00Z"),
        ]
        let merged = SingSession.merge(loaded, live: live)
        XCTAssertEqual(merged.map(\.id), ["a"])
        XCTAssertEqual(SingSession.stageName(merged[0]), "Singt 50 %")
        XCTAssertEqual(SingSession.stageName(try take("c", stage: "queued")), "Wartet")
        XCTAssertTrue(SingSession.finished(try take("c", stage: "done")))
        XCTAssertFalse(SingSession.finished(try take("c", stage: "loading")))
    }

    @MainActor
    func testTakesInTheWorksGetATileAndCount() throws {
        let model = AppModel(defaults: UserDefaults(suiteName: "SingingTests-\(UUID().uuidString)")!)
        XCTAssertFalse(model.shownModels.contains(.singing))
        model.liveSinging["a"] = try take("a", stage: "singing")
        model.liveSinging["b"] = try take("b", stage: "queued", created: "2026-10-19T07:10:00Z")
        model.liveSinging["c"] = try take("c", stage: "done")
        XCTAssertEqual(model.singJobs.map(\.id), ["a", "b"])
        XCTAssertEqual(model.running(.singing), 1)
        XCTAssertEqual(model.waiting(.singing), 1)
        XCTAssertTrue(model.shownModels.contains(.singing))
        XCTAssertEqual(model.queueCount, 2)
        XCTAssertEqual(QueueModel.holder(memory: .singing, workerBusy: true), .singing)
    }

    func testTheSingingEventDecodes() throws {
        let event = try ServerEvent(
            name: "singing", data: #"{"id":"a","title":"Melodie","stage":"loading","createdAt":"2026-10-19T07:00:00Z"}"#)
        guard case .singing(let take) = event else { return XCTFail("\(event)") }
        XCTAssertEqual(take.stage, "loading")
    }
}
