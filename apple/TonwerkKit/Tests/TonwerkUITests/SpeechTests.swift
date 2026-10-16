import Foundation
import TonwerkKit
import XCTest
@testable import TonwerkUI

final class SpeechTests: XCTestCase {
    private func take(_ id: String, stage: String, updated: String = "2026-10-16T08:00:00Z", source: String? = nil,
                      created: String = "2026-10-16T07:00:00Z", progress: Double? = nil) throws
        -> Components.Schemas.SpeechTake
    {
        let json: [String: Any?] = [
            "id": id, "modelId": "higgs-v3", "modelLabel": "Higgs Audio v3", "text": "Hallo", "source": source,
            "progress": progress, "voiceId": nil, "voiceLabel": nil, "stage": stage, "message": nil, "seconds": 4.2,
            "loadSeconds": 12.34, "speakSeconds": 8, "peakMemoryGb": 6.5, "createdAt": created, "updatedAt": updated,
            "finished": ["done", "failed", "cancelled"].contains(stage),
        ]
        let data = try JSONSerialization.data(withJSONObject: json.mapValues { $0 ?? NSNull() })
        return try JSONDecoder.tonwerk().decode(Components.Schemas.SpeechTake.self, from: data)
    }

    private func model(_ json: String) throws -> Components.Schemas.SpeechModelInfo {
        try JSONDecoder.tonwerk().decode(Components.Schemas.SpeechModelInfo.self, from: Data(json.utf8))
    }

    func testLiveTakesLayOverTheLoadedOnesAndCancelledOnesLeave() throws {
        let loaded = [
            try take("a", stage: "queued", updated: "2026-10-16T08:00:00Z"),
            try take("b", stage: "done", updated: "2026-10-16T08:05:00Z", created: "2026-10-16T07:30:00Z"),
            try take("c", stage: "speaking"),
        ]
        let live = [
            try take("a", stage: "speaking", updated: "2026-10-16T08:01:00Z"),
            // Older than what was loaded: an event that arrived before the page asked.
            try take("b", stage: "speaking", updated: "2026-10-16T08:04:00Z", created: "2026-10-16T07:30:00Z"),
            try take("c", stage: "cancelled", updated: "2026-10-16T08:02:00Z"),
        ]
        let merged = SpeechSession.merge(loaded, live: live)
        XCTAssertEqual(merged.map(\.id), ["b", "a"], "newest first, the cancelled one gone")
        XCTAssertEqual(merged.map(\.stage), ["done", "speaking"])
    }

    func testStagesNotesAndSizesAreTheLabsWords() throws {
        XCTAssertEqual(SpeechSession.stageName(try take("a", stage: "loading")), "Lädt Modell")
        XCTAssertEqual(SpeechSession.stageName(try take("a", stage: "cleaning", source: "Talk.m4a", progress: 0.426)), "Säubert 43 %")
        XCTAssertEqual(SpeechSession.stats(try take("a", stage: "done")), "Laden 12.3 s · Sprechen 8 s · 6.5 GB Speicher")

        let known = try model("""
            {"id":"moss-tts","label":"MOSS-TTS","repo":"r","downloadGb":2.4,"clones":true,"license":"Apache 2.0",
             "note":"48 kHz, pauses in the text.","downloaded":false}
            """)
        XCTAssertEqual(SpeechSession.note(known), "48 kHz, Pausen als [pause 1.5s] im Text.")
        XCTAssertEqual(SpeechSession.download(known), "2.4 GB Download")
        let added = try model("""
            {"id":"other","label":"Other","repo":"r","downloadGb":0.08,"clones":false,"license":"MIT",
             "note":"Configured on the server.","downloaded":false}
            """)
        XCTAssertEqual(SpeechSession.note(added), "Configured on the server.", "a model of the server's own keeps its note")
        XCTAssertEqual(SpeechSession.download(added), "80 MB Download")
    }

    @MainActor
    func testTakesInTheWorksGetATileAndCount() throws {
        let model = AppModel(defaults: UserDefaults(suiteName: "SpeechTests-\(UUID().uuidString)")!)
        XCTAssertFalse(model.shownModels.contains(.speech))
        model.liveSpeech["a"] = try take("a", stage: "speaking")
        model.liveSpeech["b"] = try take("b", stage: "queued", created: "2026-10-16T07:10:00Z")
        model.liveSpeech["c"] = try take("c", stage: "done")
        XCTAssertEqual(model.speechJobs.map(\.id), ["a", "b"])
        XCTAssertEqual(model.running(.speech), 1)
        XCTAssertEqual(model.waiting(.speech), 1)
        XCTAssertTrue(model.shownModels.contains(.speech))
        XCTAssertEqual(model.queueCount, 2)
    }
}
