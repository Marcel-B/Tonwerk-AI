import Foundation
import HTTPTypes
import OpenAPIRuntime
import TonwerkKit
import XCTest

/// The fixtures are real answers of the server, written by its OpenApiTests next to the description.
final class TonwerkKitTests: XCTestCase {
    func testAnAddressBecomesTheServersRoot() {
        XCTAssertEqual(ServerAddress.parse(" mac-mini.tail6105c0.ts.net:8443 ")?.absoluteString, "https://mac-mini.tail6105c0.ts.net:8443")
        XCTAssertEqual(ServerAddress.parse("https://mac-mini.tail6105c0.ts.net:8443/ui/#/songs/run/song1")?.absoluteString, "https://mac-mini.tail6105c0.ts.net:8443")
        XCTAssertEqual(ServerAddress.parse("http://127.0.0.1:5090")?.absoluteString, "http://127.0.0.1:5090")
        XCTAssertNil(ServerAddress.parse(""))
        XCTAssertNil(ServerAddress.parse("ftp://mac-mini"))
    }

    func testDotNetDatesAreRead() throws {
        let dates = TonwerkDates()
        let plain = try dates.decode("2026-10-15T12:23:12+00:00")
        XCTAssertEqual(plain.timeIntervalSince1970, 1_792_066_992, accuracy: 0.001)
        XCTAssertEqual(try dates.decode("2026-10-15T12:23:12.1234567+00:00").timeIntervalSince(plain), 0.123, accuracy: 0.001)
        XCTAssertEqual(try dates.decode("2026-10-15T14:23:12.5+02:00").timeIntervalSince(plain), 0.5, accuracy: 0.001)
        XCTAssertEqual(try dates.decode("2026-10-15T12:23:12Z"), plain)
    }

    func testTheLibraryIsReadThroughTheGeneratedClient() async throws {
        let tonwerk = Tonwerk(server: URL(string: "https://tonwerk.test")!, transport: Fixtures())

        let runs = try await tonwerk.api.listLibrary().ok.body.json

        let run = try XCTUnwrap(runs.first)
        XCTAssertEqual(run.title, "Neon Night")
        XCTAssertEqual(run.songs.map(\.id), ["20260921-165850-Neon-Night/song1", "20260921-165850-Neon-Night/song2"])
        XCTAssertEqual(run.songs.first?.rating, 4)
    }

    func testPlaylistsAreRead() async throws {
        let tonwerk = Tonwerk(server: URL(string: "https://tonwerk.test")!, transport: Fixtures())

        let playlists = try await tonwerk.api.listPlaylists().ok.body.json

        XCTAssertEqual(playlists.first?.songIds, ["20260921-165850-Neon-Night/song1"])
    }

    func testTheEventStreamIsDecodedByName() async throws {
        let status = try String(contentsOf: Fixtures.url("status"), encoding: .utf8)
            .replacingOccurrences(of: "\n", with: "")
        let stream = """
            event: snapshot
            data: \(status)

            event: ping
            data: {}

            event: library
            data: {}

            event: memory
            data: {"holder":"lyrics"}

            event: somethingNew
            data: {"later":true}


            """

        var events: [ServerEvent] = []
        for try await event in ServerEvent.events(in: HTTPBody(stream)) {
            events.append(event)
        }

        XCTAssertEqual(events.count, 5)
        guard case .snapshot(let snapshot) = events[0] else { return XCTFail("\(events[0])") }
        XCTAssertEqual(snapshot.bundleWindowSeconds, 1200)
        guard case .ping = events[1], case .library = events[2] else { return XCTFail("\(events)") }
        guard case .memory = events[3] else { return XCTFail("\(events[3])") }
        guard case .unknown("somethingNew") = events[4] else { return XCTFail("\(events[4])") }
    }
}

/// Answers each operation with its fixture.
private struct Fixtures: ClientTransport {
    static func url(_ name: String) throws -> URL {
        try XCTUnwrap(Bundle.module.url(forResource: name, withExtension: "json", subdirectory: "Fixtures"))
    }

    func send(_ request: HTTPRequest, body: HTTPBody?, baseURL: URL, operationID: String) async throws -> (HTTPResponse, HTTPBody?) {
        let name = switch operationID.lowercased() {
        case "listlibrary": "library"
        case "listplaylists": "playlists"
        case "getstatus": "status"
        default: ""
        }
        guard !name.isEmpty else { return (HTTPResponse(status: .notFound), nil) }
        let data = try Data(contentsOf: Self.url(name))
        return (HTTPResponse(status: .ok, headerFields: [.contentType: "application/json"]), HTTPBody(data))
    }
}
