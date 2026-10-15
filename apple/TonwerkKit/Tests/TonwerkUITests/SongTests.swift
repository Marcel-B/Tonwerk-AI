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

final class CreateFormTests: XCTestCase {
    func testAnEmptyFormNamesWhatIsMissing() {
        var form = CreateForm()
        XCTAssertNotNil(form.problem)
        form.style = "German, pop"
        XCTAssertNotNil(form.problem, "lyrics missing")
        form.instrumental = true
        XCTAssertNil(form.problem)
        form.seed = "abc"
        XCTAssertNotNil(form.problem)
    }

    func testTheRequestCarriesTheForm() {
        var form = CreateForm()
        form.style = " German, pop "
        form.lyrics = "[verse]\nHallo"
        form.batch = 12
        form.full = true
        form.planning = .melody
        form.seed = "42"

        let request = form.request

        XCTAssertEqual(request.style, "German, pop")
        XCTAssertNil(request.title)
        XCTAssertEqual(request.batch, 10)
        XCTAssertEqual(request.quality, "full")
        XCTAssertEqual(request.cot, "melody")
        XCTAssertEqual(request.seed, 42)
    }

    func testTheFormSurvivesARestart() throws {
        let defaults = try XCTUnwrap(UserDefaults(suiteName: "CreateFormTests"))
        defaults.removePersistentDomain(forName: "CreateFormTests")
        var form = CreateForm()
        form.title = "Herbst"
        form.save(to: defaults)

        XCTAssertEqual(CreateForm.load(from: defaults), form)
    }

    func testTheAdvancedParametersGoOutOnlyWhereTheyDiffer() {
        var form = CreateForm()
        form.style = "pop"
        form.instrumental = true
        XCTAssertNil(form.request.abcSampling)
        XCTAssertNil(form.request.maxTokens)
        XCTAssertEqual(form.request.draftSteps, 8)
        XCTAssertFalse(form.advancedChanged)

        form.semanticSampling.temperature = 0.9
        form.maxSeconds = 60
        form.engines = "gpu"
        XCTAssertTrue(form.advancedChanged)
        XCTAssertEqual(form.request.semanticSampling?.temperature, 0.9)
        XCTAssertNil(form.request.semanticSampling?.topK)
        XCTAssertEqual(form.request.maxTokens, 1500)
        XCTAssertEqual(form.request.engines, "gpu")

        form.useAdvanced = false
        XCTAssertNil(form.request.semanticSampling)
        XCTAssertNil(form.request.engines)
    }

    func testAScoreKeepsItsPlanningAndGoesOutOnlyWhenUsed() {
        var form = CreateForm()
        form.style = "pop"
        form.lyrics = "[verse]"
        form.useScore(AbcScore.example)
        XCTAssertEqual(form.planning, .full)
        XCTAssertNotNil(form.request.abc)
        form.planning = .off
        XCTAssertNotNil(form.problem)
        form.useAbc = false
        XCTAssertNil(form.problem)
        XCTAssertNil(form.request.abc)
    }

    func testAFormKeptByAnOlderVersionStillReads() throws {
        let old = #"{"title":"Herbst","style":"pop","lyrics":"","idea":"","german":true,"lyricsModel":"","draftId":"","instrumental":false,"full":true,"batch":2,"planning":"melody","seed":"7"}"#
        let form = try JSONDecoder().decode(CreateForm.self, from: Data(old.utf8))
        XCTAssertEqual(form.title, "Herbst")
        XCTAssertEqual(form.planning, .melody)
        XCTAssertEqual(form.abcSampling, .abcDefaults)
        XCTAssertTrue(form.useAdvanced)
    }

    func testAProblemAnswerSaysWhatWentWrong() {
        let validation = #"{"title":"One or more validation errors occurred.","status":400,"errors":{"style":["Style is required."]}}"#
        XCTAssertEqual(ServerError.text(of: Data(validation.utf8)), "Style is required.")
        let missing = #"{"title":"YuE Studio not found","status":503,"detail":"No worker at /x."}"#
        XCTAssertEqual(ServerError.text(of: Data(missing.utf8)), "No worker at /x.")
        XCTAssertNil(ServerError.text(of: Data()))
    }
}
