import Foundation
import TonwerkKit
@testable import TonwerkUI
import XCTest

final class SongTests: XCTestCase {
    private let server = URL(string: "https://tonwerk.test:8443")!

    func testTheLibrarysSongsCarryTheirRun() throws {
        let json = """
            [{"id":"20260921-165850-Neon-Night","title":"Neon Night","originalTitle":"Neon Night",
              "createdAt":"2026-09-21T16:58:50+02:00","style":"synthwave, female vocal","lyrics":"[verse]",
              "bytes":10,"songs":[
                {"id":"20260921-165850-Neon-Night/song1","index":1,"seed":null,"quality":null,"seconds":187.4,
                 "hasAudio":true,"hasScore":true,"canRender":true,"bytes":5,"rating":4,"versions":[],
                 "coverUpdatedAt":"2026-10-01T10:00:00.1234567+00:00"},
                {"id":"20260921-165850-Neon-Night/song2","index":2,"seed":null,"quality":null,"seconds":null,
                 "hasAudio":false,"hasScore":false,"canRender":false,"bytes":5,"rating":null,"versions":[]}]}]
            """
        let runs = try JSONDecoder.tonwerk().decode([Components.Schemas.RunInfo].self, from: Data(json.utf8))

        let songs = Song.all(in: runs)

        XCTAssertEqual(songs.map(\.id), ["20260921-165850-Neon-Night/song1", "20260921-165850-Neon-Night/song2"])
        XCTAssertEqual(songs[0].displayTitle, "Neon Night · 1")
        XCTAssertEqual(songs[0].rating, 4)
        XCTAssertEqual(songs[0].duration, "3:07")
        XCTAssertNil(songs[1].rating)
        XCTAssertFalse(songs[1].hasAudio)
        XCTAssertEqual(
            songs[0].streamURL(on: server).absoluteString,
            "https://tonwerk.test:8443/api/songs/20260921-165850-Neon-Night/song1/stream")
        XCTAssertEqual(
            songs[0].coverURL(on: server)?.absoluteString,
            "https://tonwerk.test:8443/api/songs/20260921-165850-Neon-Night/song1/cover?v=1790848800")
        XCTAssertNil(songs[1].coverURL(on: server))
    }

    func testARunOfOneSongHasNoNumber() {
        XCTAssertEqual(Song(run: "r", name: "song1", title: "Herbst", number: nil).displayTitle, "Herbst")
    }

    func testTheSearchWantsEveryWordInTitleOrStyle() {
        let song = Song(run: "r", name: "song1", title: "Herbst am Meer", number: nil, style: "Acoustic, German")
        XCTAssertTrue(song.matches(""))
        XCTAssertTrue(song.matches("meer acoustic"))
        XCTAssertFalse(song.matches("meer rock"))
    }

    func testTheClock() {
        XCTAssertEqual(Song.clock(0), "0:00")
        XCTAssertEqual(Song.clock(61.9), "1:01")
        XCTAssertEqual(Song.clock(.nan), "0:00")
    }
}
