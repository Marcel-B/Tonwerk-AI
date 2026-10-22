import CoreGraphics
import ImageIO
import UniformTypeIdentifiers
import XCTest
@testable import TonwerkUI

final class CoverTests: XCTestCase {
    func testPromptKeepsTheMoodAndLeavesTempoAndVocalsOut() {
        let prompt = CoverPrompt.suggest(
            title: "Nachtzug", style: "dark synthwave,  dreamy , 120 bpm, female vocal, ,German language",
            withTitle: false)
        XCTAssertEqual(
            prompt,
            "A square painting, one striking scene with a strong central motif, rich colour and dramatic light, painterly, high detail. Its mood and colours evoke dark synthwave, dreamy music.")
    }

    func testPromptWritesTheTitleOnlyWhenAsked() {
        let prompt = CoverPrompt.suggest(title: "Nachtzug", style: "", withTitle: true)
        XCTAssertEqual(
            prompt,
            "A square painting, one striking scene with a strong central motif, rich colour and dramatic light, painterly, high detail. The words \"Nachtzug\" are written once in elegant, legible lettering.")
        XCTAssertFalse(CoverPrompt.suggest(title: "", style: "", withTitle: true).contains("words"))
    }

    func testCoverBlocksAreUniqueAndNoneHoldsAnother() {
        XCTAssertEqual(Set(CoverBlocks.all.map(\.key)).count, CoverBlocks.all.count)
        for a in CoverBlocks.all {
            for b in CoverBlocks.all where a != b {
                XCTAssertFalse(a.text.contains(b.text), "\(a.key) holds \(b.key)")
            }
        }
    }

    func testCoverBlockIsAddedAtTheEndAndTakenOutAgain() throws {
        let neon = try XCTUnwrap(CoverBlocks.block("neon")).text
        let added = CoverBlocks.toggle("neon", in: "A fox in snow.")
        XCTAssertEqual(added, "A fox in snow. \(neon)")
        XCTAssertEqual(CoverBlocks.blocks(in: added), ["neon"])
        XCTAssertEqual(CoverBlocks.toggle("neon", in: added), "A fox in snow.")
    }

    func testOneMediumAtATime() throws {
        let pencil = try XCTUnwrap(CoverBlocks.block("pencil")).text
        let photo = try XCTUnwrap(CoverBlocks.block("photo")).text
        let both = CoverBlocks.toggle("pencil", in: CoverBlocks.toggle("photo", in: "A fox."))
        XCTAssertEqual(both, "A fox. \(pencil)")
        XCTAssertFalse(both.contains(photo))
    }

    func testCoverBlocksForTheSongFollowWholeWords() {
        XCTAssertEqual(CoverBlocks.forSong(style: "German, dark synthwave, 110 BPM"), ["synthwave80s", "neon", "charcoal", "dramatic"])
        XCTAssertEqual(CoverBlocks.forSong(style: "trap"), ["dramatic", "photo"])
        XCTAssertEqual(CoverBlocks.forSong(style: "post-rock"), ["landscape", "mist"])
        XCTAssertEqual(CoverBlocks.forSong(style: ""), [])
    }

    func testPromptWithAMediumDropsPaintingAndListsBlocksBeforeTheTitle() throws {
        let pencil = try XCTUnwrap(CoverBlocks.block("pencil")).text
        let neon = try XCTUnwrap(CoverBlocks.block("neon")).text
        XCTAssertEqual(
            CoverPrompt.suggest(title: "Nachtzug", style: "dreamy", withTitle: true, blocks: ["neon", "pencil"]),
            "A square picture, one striking scene with a strong central motif, high detail. Its mood and colours evoke dreamy music. \(pencil) \(neon) The words \"Nachtzug\" are written once in elegant, legible lettering.")
    }

    func testPhotoIsCutSquareAtTheGivenSide() throws {
        let jpeg = try XCTUnwrap(CoverPhoto.squareJPEG(from: png(width: 300, height: 200), side: 100))
        let source = try XCTUnwrap(CGImageSourceCreateWithData(jpeg as CFData, nil))
        XCTAssertEqual(CGImageSourceGetType(source) as String?, UTType.jpeg.identifier)
        let image = try XCTUnwrap(CGImageSourceCreateImageAtIndex(source, 0, nil))
        XCTAssertEqual(image.width, 100)
        XCTAssertEqual(image.height, 100)
    }

    func testSmallPhotoIsNotBlownUp() throws {
        let jpeg = try XCTUnwrap(CoverPhoto.squareJPEG(from: png(width: 60, height: 80), side: 1000))
        let source = try XCTUnwrap(CGImageSourceCreateWithData(jpeg as CFData, nil))
        let image = try XCTUnwrap(CGImageSourceCreateImageAtIndex(source, 0, nil))
        XCTAssertEqual(image.width, image.height)
        XCTAssertLessThanOrEqual(image.width, 80)
    }

    func testNoPictureNoCover() {
        XCTAssertNil(CoverPhoto.squareJPEG(from: Data("not a picture".utf8)))
    }

    private func png(width: Int, height: Int) throws -> Data {
        let context = try XCTUnwrap(CGContext(
            data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
            space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        context.setFillColor(red: 0.1, green: 0.7, blue: 0.5, alpha: 1)
        context.fill(CGRect(x: 0, y: 0, width: width, height: height))
        let image = try XCTUnwrap(context.makeImage())
        let data = NSMutableData()
        let destination = try XCTUnwrap(CGImageDestinationCreateWithData(data, UTType.png.identifier as CFString, 1, nil))
        CGImageDestinationAddImage(destination, image, nil)
        XCTAssertTrue(CGImageDestinationFinalize(destination))
        return data as Data
    }
}
