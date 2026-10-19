import Foundation
import XCTest
@testable import TonwerkUI

final class LogTests: XCTestCase {
    func testEmptyFiltersStayOutOfTheQuery() {
        XCTAssertEqual(AppModel.logQuery(source: nil, level: nil, text: "  ", before: nil), [])
        let items = AppModel.logQuery(
            source: "worker", level: "warning", text: " timeout ", before: "2026-10-16T06:00:00.1234567+00:00")
        XCTAssertEqual(items.map(\.name), ["source", "level", "q", "before"])
        XCTAssertEqual(items[2].value, "timeout")
        XCTAssertEqual(items[3].value, "2026-10-16T06:00:00.1234567+00:00", "the next page asks with the server's own time")
    }

    func testAPageAsTheServerWritesItDecodes() throws {
        let page = try JSONDecoder().decode(LogPage.self, from: Data("""
            {"entries":[
              {"time":"2026-10-16T06:00:00.1234567+00:00","source":"worker","level":"error",
               "message":"Worker exited\\nexit code 1","exception":"System.Exception: boom"},
              {"time":"2026-10-16T05:59:00+00:00","source":"update","level":"info","message":"deployed abc123",
               "exception":null}],
             "more":true}
            """.utf8))
        XCTAssertTrue(page.more)
        let first = try XCTUnwrap(page.entries.first)
        XCTAssertEqual(first.time, "2026-10-16T06:00:00.1234567+00:00")
        XCTAssertNotNil(first.date)
        XCTAssertTrue(first.hasDetails)
        XCTAssertEqual(first.firstLine, "Worker exited")
        XCTAssertEqual(first.fullText, "Worker exited\nexit code 1\nSystem.Exception: boom")
        XCTAssertFalse(page.entries[1].hasDetails)
    }

    func testSourcesAreNamedAsOnThePage() {
        XCTAssertEqual(LogSession.sourceName("worker"), "YuE2-Worker")
        XCTAssertEqual(LogSession.sourceName("export"), "Export und Streaming")
        XCTAssertEqual(LogSession.sourceName("unknown"), "unknown")
        XCTAssertEqual(LogSession.sources.count, 15, "every source the server's LogSources.All knows")
    }
}
