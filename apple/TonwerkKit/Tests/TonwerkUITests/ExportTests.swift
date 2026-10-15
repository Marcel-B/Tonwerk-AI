@testable import TonwerkUI
import XCTest

final class ExportTests: XCTestCase {
    func testGenreIsAWholePartOfTheStyleFirst() {
        XCTAssertEqual(Genre.of("English, indie rock, female vocal"), "Indie rock")
        XCTAssertEqual(Genre.of("German, Synthpop, 90 bpm"), "Synthpop")
    }

    func testGenreNamedInsideFreeTextLongestFirst() {
        XCTAssertEqual(Genre.of("dark dance pop with a warm pad"), "Dance pop")
        XCTAssertEqual(Genre.of("whispered vocals over piano"), "")
    }

    @MainActor
    func testFileNamePrefersTheUtf8Name() {
        XCTAssertEqual(
            AppModel.fileName("attachment; filename=\"Gruen.mp3\"; filename*=UTF-8''Gr%C3%BCn.mp3"), "Grün.mp3")
        XCTAssertEqual(AppModel.fileName("attachment; filename=\"Song.flac\""), "Song.flac")
        XCTAssertNil(AppModel.fileName(nil))
    }
}
