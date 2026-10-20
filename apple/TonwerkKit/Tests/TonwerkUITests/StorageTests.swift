import Foundation
import TonwerkKit
import XCTest
@testable import TonwerkUI

final class StorageTests: XCTestCase {
    private let overview = Data("""
        {"volumes":[{"mount":"/","free":100000000000,"total":500000000000},
                    {"mount":"/Volumes/Studio SSD","free":900000000000,"total":1000000000000}],
         "categories":[
           {"key":"songs","group":"work","bytes":4000000000,"count":120,"volume":"/","orphaned":0,
            "largest":[{"kind":"song","id":"2026-10-15_run/song1","songId":"2026-10-15_run/song1","title":"Neon",
                        "detail":null,"bytes":60000000,"createdAt":"2026-10-15T20:00:00.1234567+00:00"}]},
           {"key":"stream","group":"app","bytes":800000000,"count":0,"volume":"/","orphaned":0,"largest":[]},
           {"key":"videos","group":"work","bytes":0,"count":0,"volume":"/","orphaned":0,"largest":[]},
           {"key":"stems","group":"work","bytes":2000000000,"count":4,"volume":"/","orphaned":300,"largest":[]},
           {"key":"yue2","group":"models","bytes":30000000000,"count":0,"volume":"/Volumes/Studio SSD",
            "orphaned":0,"largest":[]},
           {"key":"database","group":"app","bytes":5000000,"count":0,"volume":"/","orphaned":0,"largest":[]}],
         "failed":1200}
        """.utf8)

    func testOnlyWhatFreesSomethingIsOffered() throws {
        let decoded = try JSONDecoder.tonwerk().decode(Components.Schemas.StorageOverview.self, from: overview)
        let suggestions = StorageSession.suggestions(of: decoded)
        XCTAssertEqual(suggestions.map(\.target), ["stream", "failed", "orphans"], "no videos to delete, so no offer")
        XCTAssertEqual(suggestions.last?.bytes, 300)
        XCTAssertEqual(decoded.categories.first?.largest.first?.songId, "2026-10-15_run/song1")
    }

    func testEveryKindDeletesThroughItsOwnEndpoint() {
        XCTAssertEqual(AppModel.deletePath(kind: "song", id: "r/song1", songId: "r/song1"), "api/songs/r/song1")
        XCTAssertEqual(AppModel.deletePath(kind: "cover", id: "r/song1", songId: "r/song1"), "api/songs/r/song1/cover")
        XCTAssertEqual(
            AppModel.deletePath(kind: "version", id: "v1", songId: "r/song1"), "api/songs/r/song1/versions/v1")
        XCTAssertNil(AppModel.deletePath(kind: "version", id: "v1", songId: nil))
        XCTAssertEqual(AppModel.deletePath(kind: "take", id: "t1", songId: nil), "api/speech/takes/t1")
        XCTAssertEqual(AppModel.deletePath(kind: "singtake", id: "s1", songId: nil), "api/singing/s1")
        XCTAssertEqual(AppModel.deletePath(kind: "singvoice", id: "v1", songId: nil), "api/singing/voices/v1")
        XCTAssertNil(AppModel.deletePath(kind: "lora", id: "x", songId: nil), "a kind this app does not know")
    }

    func testPartsAndVolumesAreNamedAsOnThePage() {
        XCTAssertEqual(StorageSession.categoryName("seedvcModels"), "Seed-VC")
        XCTAssertEqual(StorageSession.categoryName("database"), "Datenbank und Einstellungen")
        XCTAssertEqual(StorageSession.categoryName("newPart"), "newPart")
        XCTAssertEqual(StorageSession.volumeName("/"), "Mac")
        XCTAssertEqual(StorageSession.volumeName("/Volumes/Studio SSD"), "Studio SSD")
    }
}
