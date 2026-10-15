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

    func testTheSearchFoldsAccentsKeepsQuotesAndCanLookInTheLyrics() {
        let song = Song(
            run: "r", name: "song1", title: "Träume", number: nil, style: "pop",
            lyrics: "[Verse]\nIch gehe durch die\nNacht allein", originalTitle: "Neon")
        XCTAssertTrue(song.matches("traume"))
        XCTAssertTrue(song.matches("neon"))
        XCTAssertFalse(song.matches("nacht"))
        XCTAssertTrue(song.matches("\"die nacht\"", in: .lyrics))
        XCTAssertFalse(song.matches("\"nacht die\"", in: .lyrics))
        XCTAssertEqual(Song.terms(of: " a  \"b  c\" d"), ["a", "b c", "d"])
        XCTAssertEqual(Song.terms(of: "\"offen"), ["offen"])
    }

    func testAVersionPlaysFromItsOwnCopyUnderTheSongsId() {
        let song = Song(
            run: "r", name: "song2", title: "Herbst", number: nil,
            versions: [.init(id: "v1", voice: "Anna", semitones: 12)])
        let track = song.with(version: .some(song.versions[0]))
        XCTAssertEqual(track.id, song.id)
        XCTAssertEqual(track.displayTitle, "Herbst (Anna)")
        XCTAssertEqual(
            track.streamURL(on: server).absoluteString, "https://tonwerk.test:8443/api/songs/r/song2/versions/v1/stream")
        XCTAssertEqual(track.with(rating: .some(3)).version?.id, "v1")
        XCTAssertNil(track.with(rating: .some(nil)).rating)
    }

    func testTheClock() {
        XCTAssertEqual(Song.clock(0), "0:00")
        XCTAssertEqual(Song.clock(61.9), "1:01")
        XCTAssertEqual(Song.clock(.nan), "0:00")
    }
}

final class LibraryOrderTests: XCTestCase {
    private func song(_ run: String, _ name: String, rating: Int? = nil, seconds: Double? = nil, tempo: Double? = nil) -> Song {
        Song(run: run, name: name, title: run, number: nil, seconds: seconds, rating: rating, tempo: tempo)
    }

    func testRunsStayTogetherAndMoveByTheirBestSong() {
        let songs = [
            song("c", "song1", rating: 3), song("c", "song2", rating: 3),
            song("b", "song1", rating: 2), song("b", "song2", rating: 5),
            song("a", "song1"),
        ]
        XCTAssertEqual(LibrarySort.newest.apply(to: songs).map(\.id), songs.map(\.id))
        XCTAssertEqual(
            LibrarySort.oldest.apply(to: songs).map(\.id), ["a/song1", "b/song1", "b/song2", "c/song1", "c/song2"])
        XCTAssertEqual(
            LibrarySort.rating.apply(to: songs).map(\.id), ["b/song2", "b/song1", "c/song1", "c/song2", "a/song1"])
    }

    func testUnknownValuesGoLastEitherWay() {
        let songs = [song("a", "song1"), song("b", "song1", seconds: 200, tempo: 90), song("c", "song1", seconds: 100, tempo: 120)]
        XCTAssertEqual(LibrarySort.longest.apply(to: songs).map(\.run), ["b", "c", "a"])
        XCTAssertEqual(LibrarySort.shortest.apply(to: songs).map(\.run), ["c", "b", "a"])
        XCTAssertEqual(LibrarySort.fastest.apply(to: songs).map(\.run), ["c", "b", "a"])
        XCTAssertEqual(LibrarySort.slowest.apply(to: songs).map(\.run), ["b", "c", "a"])
    }

    func testTheRatingFilter() {
        let songs = [song("a", "song1", rating: 5), song("b", "song1", rating: 3), song("c", "song1")]
        XCTAssertEqual(songs.filter(RatingFilter.atLeast(5).admits).map(\.run), ["a"])
        XCTAssertEqual(songs.filter(RatingFilter.atLeast(3).admits).map(\.run), ["a", "b"])
        XCTAssertEqual(songs.filter(RatingFilter.unrated.admits).map(\.run), ["c"])
        XCTAssertEqual(songs.filter(RatingFilter.all.admits).count, 3)
        for filter in RatingFilter.choices {
            XCTAssertEqual(RatingFilter(stored: filter.stored), filter)
        }
    }
}

final class StagesTests: XCTestCase {
    private let start = Date(timeIntervalSince1970: 1000)

    func testDoneStepsTookUntilTheNextAndTheRestOfThePipelineWaits() {
        let steps = Stages.steps(
            history: [("queued", start), ("planning", start.addingTimeInterval(5)), ("tokens", start.addingTimeInterval(65))],
            current: "tokens", render: false, finished: false, now: start.addingTimeInterval(95))
        XCTAssertEqual(steps.map(\.stage), ["queued", "planning", "tokens", "synth", "decode"])
        XCTAssertEqual(steps.map(\.state), [.done, .done, .current, .pending, .pending])
        XCTAssertEqual(steps.map(\.seconds), [5, 60, 30, nil, nil])
    }

    func testARenderOnlySynthesizesAndAFinishedSongDropsItsEnd() {
        let render = Stages.steps(history: [("queued", start)], current: "queued", render: true, finished: false, now: start)
        XCTAssertEqual(render.map(\.stage), ["queued", "synth", "decode"])
        let done = Stages.steps(
            history: [("synth", start), ("decode", start.addingTimeInterval(10)), ("ready", start.addingTimeInterval(12))],
            current: "ready", render: true, finished: true, now: start.addingTimeInterval(99))
        XCTAssertEqual(done.map(\.stage), ["synth", "decode"])
        XCTAssertEqual(done.map(\.seconds), [10, 2])
    }

    func testStagesAreGerman() {
        XCTAssertEqual(Stages.name("tokens"), "Komponiert")
        XCTAssertEqual(Stages.step("planning"), "Partitur")
        XCTAssertEqual(Stages.name("odd"), "odd")
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
