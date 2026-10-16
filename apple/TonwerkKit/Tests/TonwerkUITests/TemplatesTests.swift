import Foundation
import XCTest
@testable import TonwerkUI

final class TemplatesTests: XCTestCase {
    /// A template as the web page's `toTemplateSettings` saves it, with every group.
    private let webTemplate = """
        {"id":3,"name":"Warm","createdAt":"2026-10-14T08:00:00Z","updatedAt":"2026-10-15T08:00:00Z",
         "settings":{"style":"German, pop, warm piano","instrumental":false,"seed":"42","quality":"full","batch":3,
          "cot":"melody","draftSteps":8,"fullSteps":40,"engines":"gpu","maxSeconds":240,
          "abcSampling":{"temperature":0.7,"topP":0.9,"topK":30,"repetitionPenalty":1.005,"penaltyWindow":100},
          "semanticSampling":{"temperature":0.9},
          "voiceId":"v1","voiceShift":-12,"voiceStrength":0.7,"voiceSteps":50}}
        """

    private func template(_ json: String) throws -> PromptTemplate {
        try JSONDecoder().decode(PromptTemplate.self, from: Data(json.utf8))
    }

    func testAWebTemplateFillsTheFormAndLeavesTitleAndLyrics() throws {
        let saved = try template(webTemplate)
        XCTAssertEqual(saved.settings.parts, [.style, .seed, .parameters, .voice])
        var form = CreateForm()
        form.title = "Mein Titel"
        form.lyrics = "[verse]\nHallo"
        form.useAdvanced = false
        let filled = saved.settings.apply(to: form)
        XCTAssertEqual(filled.title, "Mein Titel")
        XCTAssertEqual(filled.lyrics, "[verse]\nHallo")
        XCTAssertEqual(filled.style, "German, pop, warm piano")
        XCTAssertEqual(filled.seed, "42")
        XCTAssertTrue(filled.full)
        XCTAssertEqual(filled.batch, 3)
        XCTAssertEqual(filled.planning, .melody)
        XCTAssertEqual(filled.fullSteps, 40)
        XCTAssertEqual(filled.maxSeconds, 240)
        XCTAssertEqual(filled.semanticSampling.temperature, 0.9)
        XCTAssertEqual(filled.semanticSampling.topK, CreateForm.Sampling.semanticDefaults.topK, "missing values are the model's")
        XCTAssertTrue(filled.useAdvanced, "a template's parameters are meant to be heard")
    }

    func testOnlyTheGroupsItHoldsAreTouchedAndWrongValuesAreSkipped() throws {
        let saved = try template("""
            {"id":1,"name":"Nur Stil","createdAt":"","updatedAt":"","settings":{"style":"rock","seed":42,"quality":"best"}}
            """)
        XCTAssertEqual(saved.settings.parts, [.style, .parameters], "a quality, even an unknown one, is a parameter")
        var form = CreateForm()
        form.seed = "7"
        form.useAdvanced = false
        let filled = saved.settings.apply(to: form)
        XCTAssertEqual(filled.style, "rock")
        XCTAssertEqual(filled.seed, "7", "a number is not the web form's seed text")
        XCTAssertFalse(filled.full, "an unknown quality is skipped")
        XCTAssertEqual(filled.batch, form.batch, "what the template does not hold stays")
    }

    func testAScoreInTheFormKeepsItsPlanning() throws {
        var form = CreateForm()
        form.useScore("X:1\nK:C\nV:Vocal\nC D E F|")
        let planning = form.planning
        var settings = TemplateSettings()
        settings.cot = planning == .off ? "full" : "off"
        XCTAssertEqual(settings.apply(to: form).planning, planning)
    }

    func testTheAppSavesTheWebFormsNamesAndKeepsAVoiceItCannotSet() throws {
        var form = CreateForm()
        form.style = "jazz"
        form.full = true
        form.planning = .off
        let settings = TemplateSettings(form: form, parts: [.style, .parameters])
        let json = try XCTUnwrap(JSONSerialization.jsonObject(with: JSONEncoder().encode(settings)) as? [String: Any])
        XCTAssertEqual(json["style"] as? String, "jazz")
        XCTAssertEqual(json["quality"] as? String, "full")
        XCTAssertEqual(json["cot"] as? String, "off")
        XCTAssertNil(json["seed"], "a group not chosen stays out")
        XCTAssertNil(json["voiceId"])

        let overwritten = settings.keepingVoice(of: try template(webTemplate).settings)
        XCTAssertEqual(overwritten.voiceId, "v1")
        XCTAssertEqual(overwritten.parts, [.style, .parameters, .voice])
    }

    func testTemplatesAreSortedByNameInAnyCase() throws {
        let list = try ["b", "A", "c"].enumerated().map { index, name in
            try template(#"{"id":\#(index),"name":"\#(name)","createdAt":"","updatedAt":"","settings":{}}"#)
        }
        XCTAssertEqual(TemplateStore.byName(list).map(\.name), ["A", "b", "c"])
    }
}
