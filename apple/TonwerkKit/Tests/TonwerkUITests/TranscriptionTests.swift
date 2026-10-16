import Foundation
import TonwerkKit
import XCTest
@testable import TonwerkUI

final class TranscriptionTests: XCTestCase {
    private func state(_ json: String) throws -> Components.Schemas.TranscriptionState {
        try JSONDecoder.tonwerk().decode(Components.Schemas.TranscriptionState.self, from: Data(json.utf8))
    }

    func testStagesAndMelodiesAreNamedAsThePageNamesThem() {
        XCTAssertEqual(AppModel.transcriptionStage("progress"), "Transkribiert")
        XCTAssertEqual(AppModel.transcriptionStage(nil), "Wartet")
        XCTAssertEqual(AppModel.transcriptionStage("cancelled"), "Abgebrochen")
        XCTAssertEqual(TranscriptionSession.Melody.name(of: "melody-vocal"), "Nur Gesang")
        XCTAssertEqual(TranscriptionSession.Melody.name(of: nil), "Gesang und Instrumente")
    }

    func testAFailureSaysWhy() throws {
        let failed = try state("""
            {"id":"a1","fileName":"Demo.m4a","task":"melody-full","stage":"failed","fraction":null,"detail":"",
             "message":"afconvert failed","updatedAt":"2026-10-16T08:00:00.1234567+00:00","finished":true}
            """)
        XCTAssertEqual(AppModel.failureText(failed), "Demo.m4a: Fehlgeschlagen – afconvert failed")
    }

    func testAListAsTheServerWritesItDecodes() throws {
        let list = try JSONDecoder.tonwerk().decode(Components.Schemas.TranscriptionList.self, from: Data("""
            {"installed":true,"items":[{"id":"Demo-1","sourceName":"Demo.m4a","task":"melody-vocal",
             "createdAt":"2026-10-15T20:10:00.5+02:00","warnings":["no downbeat"],"bytes":48213}]}
            """.utf8))
        XCTAssertTrue(list.installed)
        XCTAssertEqual(list.items.first?.task, "melody-vocal")
        XCTAssertEqual(list.items.first?.warnings, ["no downbeat"])
    }

    @MainActor
    func testARunningTranscriptionGetsATileAndCounts() throws {
        let model = AppModel(defaults: UserDefaults(suiteName: "TranscriptionTests-\(UUID().uuidString)")!)
        XCTAssertFalse(model.shownModels.contains(.transcription))
        model.liveTranscriptions["a1"] = try state("""
            {"id":"a1","fileName":"Demo.m4a","task":"melody-full","stage":"progress","fraction":0.4,
             "detail":"melody","updatedAt":"2026-10-16T08:00:00+00:00","finished":false}
            """)
        model.liveTranscriptions["a0"] = try state("""
            {"id":"a0","fileName":"Old.m4a","task":"melody-full","stage":"done",
             "updatedAt":"2026-10-16T07:00:00+00:00","finished":true}
            """)
        XCTAssertEqual(model.runningTranscriptions.map(\.id), ["a1"])
        XCTAssertNil(model.lastTranscriptionFailure)
        XCTAssertTrue(model.isWorking(.transcription), "SheetSage2 runs beside YuE2")
        XCTAssertTrue(model.shownModels.contains(.transcription))
        XCTAssertEqual(model.state(of: .transcription), "rechnet")
        XCTAssertEqual(model.queueCount, 1)
    }
}
