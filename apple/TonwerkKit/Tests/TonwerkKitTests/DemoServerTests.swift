import Foundation
import TonwerkKit
import XCTest

/// The demo server answers through URLSession.shared as the real one would, so the generated client reads it.
final class DemoServerTests: XCTestCase {
    override class func setUp() {
        DemoServer.register()
    }

    private let tonwerk = Tonwerk(server: DemoServer.url)

    func testTheDemoLibraryIsReadThroughTheGeneratedClient() async throws {
        let runs = try await tonwerk.api.listLibrary().ok.body.json

        XCTAssertEqual(runs.map(\.title), ["Neon Night", "Sonnenfeld", "Glasregen"])
        for song in runs.flatMap(\.songs) {
            let address = DemoServer.url.appending(path: "api/songs/\(song.id)/stream")
            XCTAssertTrue(DemoServer.playable(address).isFileURL, song.id)
        }
    }

    func testRatingsAndPlaylistsAreKept() async throws {
        let song = "20261016-221000-Glasregen/song1"
        _ = try await tonwerk.api.rateSong(
            path: .init(run: "20261016-221000-Glasregen", song: "song1"), body: .json(.init(rating: 2)))
        let rated = try await tonwerk.api.listLibrary().ok.body.json.flatMap(\.songs).first { $0.id == song }
        XCTAssertEqual(rated?.rating, 2)

        let list = try await tonwerk.api.createPlaylist(body: .json(.init(name: "Neu"))).created.body.json
        let changed = try await tonwerk.api.setPlaylistSongs(path: .init(id: list.id), body: .json(.init(songIds: [song])))
            .ok.body.json
        XCTAssertEqual(changed.songIds, [song])
    }

    func testTheStreamStartsWithASnapshot() async throws {
        var events = try await tonwerk.events().makeAsyncIterator()

        guard case .snapshot(let snapshot) = try await events.next() else { return XCTFail("no snapshot first") }
        XCTAssertEqual(snapshot.worker.status, .stopped)
    }

    func testWhatNeedsTheModelsIsRefused() async throws {
        var request = URLRequest(url: DemoServer.url.appending(path: "api/generate"))
        request.httpMethod = "POST"
        let (_, response) = try await URLSession.shared.data(for: request)
        XCTAssertEqual((response as? HTTPURLResponse)?.statusCode, 503)
    }
}
