import Foundation
import TonwerkKit
import XCTest
@testable import TonwerkUI

final class MachinesTests: XCTestCase {
    private func machine(_ id: String, name: String, local: Bool) throws -> Components.Schemas.MachineInfo {
        let json: [String: Any] = [
            "id": id, "name": name, "local": local, "reachable": true, "busy": false, "engines": ["speech"],
        ]
        let data = try JSONSerialization.data(withJSONObject: json)
        return try JSONDecoder.tonwerk().decode(Components.Schemas.MachineInfo.self, from: data)
    }

    @MainActor
    func testWhereAJobRanIsShownOnlyWhenItSaysSomething() throws {
        let model = AppModel(defaults: UserDefaults(suiteName: "MachinesTests-\(UUID().uuidString)")!)
        XCTAssertFalse(model.helpersOffered)
        XCTAssertNil(model.ranOnNote("Mac mini"), "without a list nothing is known about the Macs")

        model.machines = [try machine("local", name: "Mac mini", local: true)]
        XCTAssertFalse(model.helpersOffered)
        XCTAssertNil(model.ranOnNote("Mac mini"))
        XCTAssertEqual(model.ranOnNote("MacBook"), "auf MacBook")

        model.machines.append(try machine("helper", name: "MacBook", local: false))
        XCTAssertTrue(model.helpersOffered)
        XCTAssertEqual(model.ranOnNote("Mac mini"), "auf Mac mini")
        XCTAssertNil(model.ranOnNote(nil))
        XCTAssertNil(model.ranOnNote(""))
    }
}
