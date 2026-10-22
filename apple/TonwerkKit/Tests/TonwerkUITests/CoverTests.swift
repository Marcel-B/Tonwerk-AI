import CoreGraphics
import ImageIO
import TonwerkKit
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

    private func lora(_ name: String, strength: Double = 1) -> Components.Schemas.ImageLoraInfo {
        .init(
            name: name, label: name, model: "klein-4b", trigger: nil, strength: strength, sizeBytes: 1,
            modifiedAt: Date(timeIntervalSince1970: 0))
    }

    func testLorasAreChosenAtTheirOwnStrengthAndNoMoreThanThree() {
        var chosen: [ImageLoras.Choice] = []
        for name in ["a", "b", "c", "d"] {
            chosen = ImageLoras.toggle(lora(name, strength: 0.8), in: chosen)
        }
        XCTAssertEqual(chosen.map(\.name), ["a", "b", "c"])
        XCTAssertEqual(chosen.first?.strength, 0.8)
        XCTAssertEqual(ImageLoras.toggle(lora("b"), in: chosen).map(\.name), ["a", "c"])
    }

    func testLorasThatNoLongerFitLeaveTheChoice() {
        let chosen = [ImageLoras.Choice(name: "a", strength: 1), ImageLoras.Choice(name: "gone", strength: 1)]
        XCTAssertEqual(ImageLoras.kept(chosen, fitting: [lora("a")]).map(\.name), ["a"])
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

    private func imageModel(references: Int, startImage: Bool) throws -> Components.Schemas.ImageModelInfo {
        let json = """
            {"id":"m","label":"M","downloadGb":1,"memoryGb":1,"license":"x","commercial":true,"gated":false,
             "downloaded":true,"tokenFound":true,"references":\(references),"startImage":\(startImage)}
            """
        return try JSONDecoder().decode(Components.Schemas.ImageModelInfo.self, from: Data(json.utf8))
    }

    func testReferencesFitWhatTheModelTakes() throws {
        let items = ["a", "b", "c"].map {
            CoverReferences.Item(source: "upload", id: $0, role: $0 == "a" ? "figure" : "style", preview: nil, label: $0)
        }
        let klein = CoverReferences.fit(items, model: try imageModel(references: 2, startImage: false))
        XCTAssertEqual(klein.map(\.id), ["a", "b"])
        XCTAssertEqual(klein.map(\.role), ["figure", "style"])
        XCTAssertEqual(klein.map(\.strength), [nil, nil])
        let zImage = CoverReferences.fit(items, model: try imageModel(references: 0, startImage: true), strength: 0.7)
        XCTAssertEqual(zImage.map(\.id), ["a"])
        XCTAssertEqual(zImage.first?.role, "start")
        XCTAssertEqual(zImage.first?.strength, 0.7)
        XCTAssertTrue(CoverReferences.fit(items, model: try imageModel(references: 0, startImage: false)).isEmpty)
    }

    func testStyleTextIsSwappedNeverDoubled() {
        let first = CoverReferences.withStyleText("A city at night.", previous: nil, next: "Neon rain.")
        XCTAssertEqual(first, "A city at night. Neon rain.")
        XCTAssertEqual(CoverReferences.withStyleText(first, previous: "Neon rain.", next: "Pencil sketch."), "A city at night. Pencil sketch.")
        XCTAssertEqual(CoverReferences.withStyleText(first, previous: nil, next: "Neon rain."), first)
        XCTAssertEqual(CoverReferences.withStyleText(first, previous: "Neon rain.", next: nil), "A city at night.")
    }

    func testTheViewerShowsEachTextOnceAndTheDetailsInOneLine() throws {
        let json = Data("""
            {"imageId": "i9", "modelId": "flux2-klein-4b", "modelLabel": "FLUX.2 Klein 4B",
             "prompt": "Ein Zug bei Nacht", "paintedPrompt": "A train at night", "sentPrompt": "A train at night. metrorose.",
             "seed": 42, "loras": [{"name": "MetroRose", "label": "MetroRose", "strength": 0.8, "trigger": "metrorose"}],
             "references": [{"role": "figure", "label": "Kandidat"}], "createdAt": "2026-10-22T12:00:00Z"}
            """.utf8)
        let painting = ViewedImage.Painting(try JSONDecoder.tonwerk().decode(Components.Schemas.CoverPainting.self, from: json))

        XCTAssertEqual(painting.texts.map(\.id), ["prompt", "english", "sent"])
        XCTAssertTrue(painting.details.hasPrefix("FLUX.2 Klein 4B · MetroRose 0"))
        XCTAssertTrue(painting.details.hasSuffix("Seed 42"))

        var plain = painting
        plain.paintedPrompt = "Ein Zug bei Nacht"
        plain.sentPrompt = "Ein Zug bei Nacht"
        XCTAssertEqual(plain.texts.map(\.id), ["prompt"])
    }
}
