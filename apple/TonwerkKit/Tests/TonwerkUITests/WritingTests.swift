import Foundation
import TonwerkKit
import XCTest
@testable import TonwerkUI

final class WritingTests: XCTestCase {
    private func reply(stage: String, thinking: Bool = false, message: String? = nil) throws -> Components.Schemas.WritingReply {
        let json: [String: Any?] = [
            "id": "r1", "chatId": "c1", "stage": stage, "model": "google/gemma-4-31b", "text": "Zeile",
            "thinking": thinking, "message": message, "updatedAt": "2026-10-22T08:00:00Z",
            "finished": ["done", "stopped", "failed"].contains(stage),
        ]
        let data = try JSONSerialization.data(withJSONObject: json.mapValues { $0 ?? NSNull() })
        return try JSONDecoder.tonwerk().decode(Components.Schemas.WritingReply.self, from: data)
    }

    func testStagesSayWhatTheModelDoes() throws {
        XCTAssertEqual(WritingSession.stageName(try reply(stage: "waiting")), "wartet auf den Speicher")
        XCTAssertEqual(WritingSession.stageName(try reply(stage: "loading")), "lädt google/gemma-4-31b …")
        XCTAssertEqual(WritingSession.stageName(try reply(stage: "writing", thinking: true)), "denkt nach …")
        XCTAssertEqual(WritingSession.stageName(try reply(stage: "writing")), "schreibt …")
        XCTAssertEqual(WritingSession.stageName(try reply(stage: "failed", message: "LM Studio refused")), "LM Studio refused")
    }

    func testTheWritingEventIsDecoded() throws {
        let event = try ServerEvent(
            name: "writing",
            data: #"{"id":"r1","chatId":"c1","stage":"writing","model":"qwen/qwen3.6-35b-a3b","text":"Herz","thinking":false,"message":null,"updatedAt":"2026-10-22T08:00:00Z","finished":false}"#)
        guard case .writing(let reply) = event else { return XCTFail("\(event)") }
        XCTAssertEqual(reply.text, "Herz")
    }
}
