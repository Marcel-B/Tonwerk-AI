import Foundation
import TonwerkKit
import XCTest
@testable import TonwerkUI

final class VoicesTests: XCTestCase {
    private func decode<T: Decodable>(_: T.Type, _ json: String) throws -> T {
        try JSONDecoder.tonwerk().decode(T.self, from: Data(json.utf8))
    }

    func testAStemSetAsTheServerWritesItDecodes() throws {
        let set = try decode(Components.Schemas.StemSetState.self, """
            {"id":"a1","songId":"2026-10-15_run/song1","title":"Morgen","model":"htdemucs","dereverb":true,
             "stage":"done","message":null,"finished":true,"upload":false,
             "createdAt":"2026-10-15T12:23:12.1234567+00:00","updatedAt":"2026-10-15T12:25:00+00:00",
             "stems":[{"name":"vocals_dry","file":"vocals_dry.flac","seconds":182.5,"peaks":[0,0.5,1]},
                      {"name":"drums","file":"drums.flac","seconds":182.5,"peaks":null}]}
            """)
        XCTAssertEqual(set.stems?.map(\.name), ["vocals_dry", "drums"])
        XCTAssertEqual(set.stems?.first?.peaks, [0, 0.5, 1])
        XCTAssertNil(set.stems?.last?.peaks)
        XCTAssertNotNil(set.createdAt)
    }

    func testStemsAreNamedAsPeopleCallThem() {
        XCTAssertEqual(VoicesSession.stemName("vocals_dry"), "Gesang (trocken)")
        XCTAssertEqual(VoicesSession.stemName("No_Vocals"), "Instrumental")
        XCTAssertEqual(VoicesSession.stemName("synth"), "synth", "an unknown stem keeps its name")
    }

    func testModelMenuSaysWhatAModelSplits() {
        XCTAssertEqual(VoicesSession.splits(["Instrumental", "Vocals"]), "Gesang / Instrumental")
        XCTAssertEqual(VoicesSession.splits(["vocals", "karaoke"]), "Leadgesang / Rest")
        XCTAssertEqual(VoicesSession.splits(["vocals", "drums", "bass", "guitar", "piano", "other"]), "Gesang + 5 Instrumente")
        XCTAssertEqual(VoicesSession.splits(["kick", "snare", "toms", "hh", "ride", "crash"]), "Schlagzeug in 6 Teile")
        XCTAssertEqual(VoicesSession.splits(nil), "")
    }

    func testStagesHaveGermanNames() {
        XCTAssertEqual(VoiceStages.name(nil), "Wartet")
        XCTAssertEqual(VoiceStages.name("converting"), "Singt")
        XCTAssertEqual(VoiceStages.name("whatever"), "whatever")
    }

    func testOnlyWholeOctavesAreOffered() {
        XCTAssertEqual(VoiceSettings.octaves.map(\.value), [-12, 0, 12])
        XCTAssertEqual(VoiceSettings().strength, 0.7)
        XCTAssertEqual(VoiceSettings().steps, 50)
    }

    func testAPickedFileIsCopiedAndNamed() async throws {
        let folder = FileManager.default.temporaryDirectory.appending(path: UUID().uuidString)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let original = folder.appending(path: "Meine Stimme.wav")
        try Data("not audio".utf8).write(to: original)
        let file = try await PickedFile.copy(from: original)
        XCTAssertNotEqual(file.url, original)
        XCTAssertEqual(try Data(contentsOf: file.url), Data("not audio".utf8))
        XCTAssertEqual(file.baseName, "Meine Stimme")
        XCTAssertNil(file.seconds, "AVFoundation cannot read it, so there is nothing to trim")
    }

    @MainActor
    func testTheQueueListsOnlyUnfinishedJobsOldestFirst() throws {
        let defaults = UserDefaults(suiteName: "VoicesTests-\(UUID().uuidString)")!
        let model = AppModel(defaults: defaults)
        func version(_ id: String, _ created: String, finished: Bool) throws -> Components.Schemas.VersionState {
            try decode(Components.Schemas.VersionState.self, """
                {"id":"\(id)","songId":"run/song1","voiceId":"v","voiceLabel":"Anna","stage":"\(finished ? "done" : "converting")",
                 "finished":\(finished),"createdAt":"\(created)"}
                """)
        }
        model.liveVersions = [
            "new": try version("new", "2026-10-15T12:00:00Z", finished: false),
            "old": try version("old", "2026-10-15T10:00:00Z", finished: false),
            "done": try version("done", "2026-10-15T09:00:00Z", finished: true),
        ]
        model.liveSwaps = [
            "s": try decode(Components.Schemas.SwapState.self, """
                {"id":"s","fileName":"take.wav","voiceId":"v","voiceLabel":"Anna","stage":"queued","finished":false,
                 "createdAt":"2026-10-15T11:00:00Z"}
                """),
        ]
        XCTAssertEqual(model.voiceJobs.map(\.id), ["version:old", "swap:s", "version:new"])
        XCTAssertEqual(model.title(of: model.voiceJobs[1]).title, "take.wav")
    }
}
