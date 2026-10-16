import CoreGraphics
import Foundation
import TonwerkKit
import XCTest
@testable import TonwerkUI

final class VideoTests: XCTestCase {
    private func solid(_ red: Double, _ green: Double, _ blue: Double, size: Int = 64) -> CGImage {
        let context = CGContext(
            data: nil, width: size, height: size, bitsPerComponent: 8, bytesPerRow: 0,
            space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        context.setFillColor(CGColor(srgbRed: red, green: green, blue: blue, alpha: 1))
        context.fill(CGRect(x: 0, y: 0, width: size, height: size))
        return context.makeImage()!
    }

    func testTheRandomNumbersAreTheBrowsers() {
        // Values from video.ts' seeded and hashOf in Node, so a song gets the same sky in both.
        let random = VideoArt.seeded(42)
        XCTAssertEqual(random(), 0.6011037519201636, accuracy: 1e-15)
        XCTAssertEqual(random(), 0.44829055899754167, accuracy: 1e-15)
        XCTAssertEqual(random(), 0.8524657934904099, accuracy: 1e-15)
        XCTAssertEqual(VideoArt.hashOf("2026-10-15_run/song1"), 816_798_105)
        XCTAssertEqual(VideoArt.hashOf("ü"), 2_030_796_987)
    }

    func testTheAnalyzerTakesTheCoversHue() {
        XCTAssertEqual(VideoArt.coverColour(solid(1, 0, 0)), "#e96363")
        XCTAssertEqual(VideoArt.coverColour(solid(0, 0, 1)), "#6363e9")
        XCTAssertEqual(VideoArt.coverColour(solid(0.5, 0.5, 0.5)), VideoArt.accent, "a grey cover keeps the emerald")
    }

    func testWordsWrapAtTheWidth() {
        let lines = VideoArt.wrap("eins zwei drei vier", width: 9) { Double($0.count) }
        XCTAssertEqual(lines, ["eins zwei", "drei vier"])
        XCTAssertEqual(VideoArt.wrap("  ", width: 9) { Double($0.count) }, [])
    }

    func testEveryLayerHasTheSizeTheServerChecks() throws {
        var settings = VideoSettings()
        settings.motion = .plasma
        let lines = (1...30).map { "Zeile \($0)" }
        let layers = try XCTUnwrap(VideoArt.layers(
            cover: solid(0.2, 0.4, 0.9), songId: "run/song1", title: "Neon Nacht", subtitle: "Marcel", lyrics: lines,
            settings: settings))
        XCTAssertEqual([layers.background.width, layers.background.height], [1920, 1080])
        XCTAssertEqual(layers.cover?.width, 1920)
        XCTAssertEqual(layers.title?.height, 1080)
        XCTAssertNil(layers.particles)
        XCTAssertEqual(layers.plasma?.frames.count, VideoArt.plasmaFrames)
        XCTAssertEqual(layers.plasma.map { [$0.frames[0].width, $0.frames[0].height] }, [1000, 1000])
        XCTAssertEqual(layers.plasma?.x, 460)
        // 30 lines: a strip of 24 and one of 6, each with an empty slot of the band's height above.
        XCTAssertEqual(layers.lyrics?.map(\.height), [180 * 25, 180 * 7])
        let names = VideoArt.pngs(of: layers).map { $0.name }
        XCTAssertEqual(names.prefix(3), ["background", "cover", "title"])
        XCTAssertTrue(names.contains("plasma7"))
        XCTAssertTrue(names.contains("lyrics1"))
        XCTAssertNotNil(VideoArt.preview(of: layers, settings: settings))
    }

    func testAPhotoVideoSendsOnlyAShade() throws {
        var settings = VideoSettings()
        settings.source = .photos
        settings.format = .portrait
        settings.showTitle = false
        let layers = try XCTUnwrap(VideoArt.layers(
            cover: solid(1, 1, 0), songId: "run/song1", title: "", subtitle: "", lyrics: nil, settings: settings))
        XCTAssertEqual([layers.background.width, layers.background.height], [1080, 1920])
        XCTAssertNil(layers.particles, "the photos move themselves")
        XCTAssertNil(layers.lyrics)
        XCTAssertEqual(VideoArt.pngs(of: layers).map { $0.name }, ["shade", "cover"])
    }

    func testThePlasmaWithoutACoverFillsTheFrame() throws {
        var settings = VideoSettings()
        settings.motion = .plasma
        settings.showCover = false
        settings.format = .portrait
        let layers = try XCTUnwrap(VideoArt.layers(
            cover: solid(1, 0, 1), songId: "run/song2", title: "X", subtitle: "", lyrics: nil, settings: settings))
        XCTAssertNil(layers.cover)
        XCTAssertEqual(layers.plasma.map { [$0.frames[0].width, $0.frames[0].height] }, [1080, 1920])
        XCTAssertEqual(layers.plasma?.y, 0)
    }

    func testSettingsAreKept() {
        let defaults = UserDefaults(suiteName: "VideoTests-\(UUID().uuidString)")!
        XCTAssertEqual(VideoSettings.load(from: defaults), VideoSettings())
        var settings = VideoSettings()
        settings.format = .portrait
        settings.effect = .wave
        settings.motif = "Meer"
        settings.save(to: defaults)
        XCTAssertEqual(VideoSettings.load(from: defaults), settings)
        XCTAssertEqual(settings.drawn.motif, "", "typing a motif draws nothing again")
    }

    func testAVideoAsTheServerWritesItDecodesAndIsDescribed() throws {
        let video = try JSONDecoder.tonwerk().decode(Components.Schemas.VideoState.self, from: Data("""
            {"id":"v1","songId":"run/song1","title":"Neon","format":"portrait","effect":"bars","motion":"plasma",
             "showCover":false,"color":"#e96363","showTitle":true,"media":null,"motif":null,"stage":"rendering",
             "fraction":0.426,"message":null,"bytes":null,"createdAt":"2026-10-16T08:00:00.1234567+00:00",
             "updatedAt":"2026-10-16T08:01:00+00:00","finished":false}
            """.utf8))
        XCTAssertEqual(AppModel.describe(video), "Hochkant (Reels) · Balken · Plasma · ohne Cover")
        XCTAssertEqual(AppModel.videoStage(video), "43 %")
    }
}
