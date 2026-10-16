import Foundation
import TonwerkKit
import XCTest
@testable import TonwerkUI

final class AceTests: XCTestCase {
    private func job(_ id: String, stage: String, updated: String = "2026-10-16T08:00:00Z",
                     created: String = "2026-10-16T07:00:00Z", fraction: Double = 0.42) throws
        -> Components.Schemas.AceJob
    {
        let json: [String: Any] = [
            "id": id, "title": "Neon", "request": ["caption": "dark synthwave", "task": "text2music"], "seed": 7,
            "stage": stage, "fraction": fraction, "createdAt": created, "updatedAt": updated,
            "finished": ["done", "failed", "cancelled"].contains(stage),
        ]
        let data = try JSONSerialization.data(withJSONObject: json)
        return try JSONDecoder.tonwerk().decode(Components.Schemas.AceJob.self, from: data)
    }

    func testANewSongSendsOnlyWhatWasSetAndNothingOfACover() throws {
        var form = AceForm()
        form.caption = "  dark synthwave  "
        form.lyrics = "[verse]\nHallo"
        form.bpm = 96
        form.seed = "42"
        form.coverStrength = 0.8
        form.source = "run/song1"
        let request = form.request
        XCTAssertEqual(request["caption"] as? String, "dark synthwave")
        XCTAssertEqual(request["lyrics"] as? String, "[verse]\nHallo")
        XCTAssertEqual(request["bpm"] as? Int, 96)
        XCTAssertEqual(request["seed"] as? Int64, 42)
        XCTAssertEqual(request["enrichCaption"] as? Bool, true)
        XCTAssertTrue(request["title"] is NSNull, "empty leaves it to the model")
        XCTAssertTrue(request["source"] is NSNull, "a new song has no source")
        XCTAssertTrue(request["coverStrength"] is NSNull)
        XCTAssertNoThrow(try JSONSerialization.data(withJSONObject: request))
    }

    func testACoverTakesLengthTempoAndKeyFromItsSource() {
        var form = AceForm()
        form.task = .cover
        form.caption = "jazz"
        form.instrumental = true
        form.lyrics = "ignored"
        form.bpm = 120
        form.key = "C major"
        form.lmTemperature = 0.9
        form.source = "run/song1"
        form.coverStrength = 0.6
        let request = form.request
        XCTAssertEqual(request["task"] as? String, "cover")
        XCTAssertEqual(request["source"] as? String, "run/song1")
        XCTAssertEqual(request["coverStrength"] as? Double, 0.6)
        XCTAssertTrue(request["bpm"] is NSNull)
        XCTAssertTrue(request["key"] is NSNull)
        XCTAssertTrue(request["lmTemperature"] is NSNull, "the LM does not run for a cover")
        XCTAssertTrue(request["lyrics"] is NSNull, "an instrumental has no lyrics")
        XCTAssertTrue(request["repaintStart"] is NSNull)
    }

    func testASongsRequestFillsTheFormAndTheRestGoesBackToDefaults() throws {
        let data = Data(#"{"caption":"lofi","bpm":80,"seed":99,"batch":2,"task":"repaint","source":"r/song2","repaintStart":10}"#.utf8)
        let request = try JSONDecoder.tonwerk().decode(Components.Schemas.AceSongRequest.self, from: data)
        var stale = AceForm()
        stale.lyrics = "old"
        let form = AceForm(request: request)
        XCTAssertEqual(form.caption, "lofi")
        XCTAssertEqual(form.bpm, 80)
        XCTAssertEqual(form.seed, "99")
        XCTAssertEqual(form.batch, 2)
        XCTAssertEqual(form.task, .repaint)
        XCTAssertEqual(form.repaintStart, 10)
        XCTAssertEqual(form.lyrics, "")
        XCTAssertTrue(form.enrichCaption)
    }

    func testAStoredFormOfAnOlderVersionKeepsWhatItCanRead() throws {
        let data = Data(#"{"caption":"rock","batch":"three","task":"unknown"}"#.utf8)
        let form = try JSONDecoder().decode(AceForm.self, from: data)
        XCTAssertEqual(form.caption, "rock")
        XCTAssertEqual(form.batch, 1)
        XCTAssertEqual(form.task, .text2music)
    }

    func testModelsRemovedFromTheServerAndTheAdvancedReset() {
        var form = AceForm()
        form.lmModel = "gone.gguf"
        form.ditModel = "turbo.gguf"
        form.steps = 50
        XCTAssertTrue(form.advancedChanged)
        let kept = form.keeping(lms: ["big.gguf"], dits: ["turbo.gguf"], adapters: [])
        XCTAssertEqual(kept.lmModel, "")
        XCTAssertEqual(kept.ditModel, "turbo.gguf")
        XCTAssertFalse(form.resettingAdvanced().advancedChanged)
    }

    func testLiveJobsLayOverTheLoadedOnesAndStagesReadAsThePagesWords() throws {
        let loaded = [try job("a", stage: "queued"), try job("b", stage: "composing")]
        let live = [
            try job("a", stage: "synthesizing", updated: "2026-10-16T08:01:00Z", created: "2026-10-16T07:30:00Z"),
            try job("b", stage: "cancelled", updated: "2026-10-16T08:02:00Z"),
        ]
        let merged = AceSession.merge(loaded, live: live)
        XCTAssertEqual(merged.map(\.id), ["a"])
        XCTAssertEqual(AceSession.stageName(merged[0]), "Erzeugt Klang 42 %")
        XCTAssertEqual(AceSession.stageName(try job("c", stage: "queued")), "Wartet")
        XCTAssertEqual(AceSession.stageName(try job("c", stage: "done")), "Fertig")
    }

    @MainActor
    func testJobsInTheWorksGetATileAndCount() throws {
        let model = AppModel(defaults: UserDefaults(suiteName: "AceTests-\(UUID().uuidString)")!)
        XCTAssertFalse(model.shownModels.contains(.ace))
        model.liveAce["a"] = try job("a", stage: "composing")
        model.liveAce["b"] = try job("b", stage: "queued", created: "2026-10-16T07:10:00Z")
        model.liveAce["c"] = try job("c", stage: "done")
        XCTAssertEqual(model.aceJobs.map(\.id), ["a", "b"])
        XCTAssertEqual(model.running(.ace), 1)
        XCTAssertEqual(model.waiting(.ace), 1)
        XCTAssertTrue(model.shownModels.contains(.ace))
        XCTAssertEqual(model.queueCount, 2)
    }
}
