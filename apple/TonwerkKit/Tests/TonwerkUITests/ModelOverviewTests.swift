import Foundation
import TonwerkKit
import XCTest
@testable import TonwerkUI

final class ModelOverviewTests: XCTestCase {
    func testTheServersHolderWinsOverTheWorker() {
        XCTAssertEqual(QueueModel.holder(memory: .voices, workerBusy: true), .voice)
        XCTAssertEqual(QueueModel.holder(memory: nil, workerBusy: true), .yue)
        XCTAssertNil(QueueModel.holder(memory: nil, workerBusy: false))
        XCTAssertEqual(QueueModel.holder(memory: .images, workerBusy: false), .image)
    }

    @MainActor
    func testTilesSayWhoComputesAndWhatWaits() throws {
        let model = AppModel(defaults: UserDefaults(suiteName: "ModelOverviewTests-\(UUID().uuidString)")!)
        XCTAssertEqual(model.shownModels, [.yue, .lyrics, .voice], "the occasional ones stay away without work")
        XCTAssertEqual(model.state(of: .yue), "frei")
        model.worker = try JSONDecoder.tonwerk().decode(
            Components.Schemas.WorkerInfo.self,
            from: Data(#"{"status":"ready","busy":false,"studioRunning":false,"lastError":null}"#.utf8))
        XCTAssertEqual(model.state(of: .yue), "geladen")
        model.memoryHolder = .images
        XCTAssertTrue(model.isWorking(.image))
        XCTAssertEqual(model.shownModels, [.yue, .lyrics, .voice, .image])
        XCTAssertEqual(model.state(of: .image), "rechnet")
    }
}
