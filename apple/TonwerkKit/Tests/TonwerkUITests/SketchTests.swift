import Foundation
import TonwerkKit
import XCTest
@testable import TonwerkUI

final class SketchTests: XCTestCase {
    func testASketchBeingWrittenIsOnlyItsJob() throws {
        let data = Data(#"""
            {"job":{"id":"a","title":"Regen","stage":"writing","source":"# Regen","createdAt":"2026-10-16T08:00:00Z",
             "updatedAt":"2026-10-16T08:00:00Z","finished":false}}
            """#.utf8)
        let detail = try JSONDecoder.tonwerk().decode(SketchDetail.self, from: data)
        XCTAssertEqual(detail.job.stage, "writing")
        XCTAssertNil(detail.job.plan)
        XCTAssertNil(detail.request)
        XCTAssertNil(detail.bars)
    }

    func testAPlannedSketchReadsItsSectionsAndWarnings() throws {
        let data = Data(#"""
            {"job":{"id":"a","title":"Regen","stage":"done","finished":true,
              "plan":{"title":"Regen","style":"German, pop","tempo":92,"beatsPerBar":4,"key":"Dm","notes":["Refrain höher"],
                "sections":[{"name":"Strophe 1","tag":"verse","parts":["piano","bass"],"energy":2,"lyrics":["Es regnet"],
                  "bars":[{"chords":"Dm","vocal":null,"lyric":null,"ins":null,"parts":null},
                          {"chords":"F C/E","vocal":null,"lyric":null,"ins":null,"parts":null}]}]}},
             "abc":"X:1","lyrics":"[verse]\nEs regnet","request":{"title":"Regen","style":"German, pop","lyrics":"[verse]\nEs regnet","instrumental":false,
               "quality":"full","cot":"full","seed":null,"engines":null,"draftSteps":null,"maxTokens":null,"abc":"X:1",
               "fullSteps":null},
             "warnings":[{"code":"barLength","bar":3,"detail":"18"},{"code":"chordSimplified","detail":"Bbmaj7"}],
             "bars":2,"seconds":5.2}
            """#.utf8)
        let detail = try JSONDecoder.tonwerk().decode(SketchDetail.self, from: data)
        let section = try XCTUnwrap(detail.job.plan?.sections.first)
        XCTAssertEqual(SketchSession.chordLine(section), "| Dm | F C/E |")
        XCTAssertEqual(SketchSession.warning(detail.warnings![0]), "Takt 3: 18 Sechzehntel, angepasst")
        XCTAssertEqual(SketchSession.warning(detail.warnings![1]), "Für YuE2 vereinfacht (im MIDI bleiben sie): Bbmaj7")
        XCTAssertEqual(SketchSession.summary(bars: detail.bars, seconds: detail.seconds), "2 Takte · 0:05")
        XCTAssertNil(SketchSession.summary(bars: nil, seconds: 3))
        XCTAssertEqual(SketchSession.stageName("writing"), "wird geschrieben …")
        XCTAssertEqual(SketchSession.stageName("waiting"), "wartet auf den Speicher")
    }

    func testThePlanGoesToTheAcePageWithWhatAceStepReads() throws {
        let data = Data(#"""
            {"job":{"id":"a","title":"Regen","stage":"done","finished":true},
             "ace":{"title":"Regen","caption":"German, pop","lyrics":"[Verse]\nEs regnet\n","instrumental":false,
               "language":"de","duration":30,"bpm":92,"key":"D minor","timeSignature":4,"task":"text2music"}}
            """#.utf8)
        let detail = try JSONDecoder.tonwerk().decode(SketchDetail.self, from: data)
        let form = AceForm(request: try XCTUnwrap(detail.ace))
        XCTAssertEqual(form.caption, "German, pop")
        XCTAssertEqual(form.lyrics, "[Verse]\nEs regnet\n")
        XCTAssertEqual(form.language, "de")
        XCTAssertEqual(form.bpm, 92)
        XCTAssertEqual(form.key, "D minor")
        XCTAssertEqual(form.timeSignature, 4)
        XCTAssertEqual(form.duration, 30)
        XCTAssertEqual(form.task, .text2music)
        XCTAssertTrue(form.enrichCaption)
        XCTAssertEqual(form.batch, 1)
    }
}
