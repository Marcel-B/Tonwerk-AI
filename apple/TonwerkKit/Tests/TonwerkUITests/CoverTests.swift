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
