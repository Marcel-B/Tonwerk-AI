import Foundation
import TonwerkKit

/// The score the Logic page's endpoints answer, in YueToLogic.Core's own JSON (camelCase, enums by their names),
/// which the OpenAPI description leaves as `JsonElement`; the web page mirrors it in `logic/types.ts`.
struct LogicConversion: Decodable {
    let success: Bool
    let score: LogicScore?
    let diagnostics: [LogicDiagnostic]
    /// The Standard MIDI File as base64, which the backing vocals page saves.
    var midi: String?
}

struct LogicDiagnostic: Decodable, Hashable {
    let severity: String
    let code: String
    let message: String
    let line: Int?
}

struct LogicScore: Decodable {
    let ticksPerQuarterNote: Int
    let title: String?
    let tempoBpm: Double
    let timeSignatures: [TimeSignature]
    let sections: [Section]
    let voices: [Voice]
    let chords: [Chord]
    let lengthTicks: Int
    /// Silent lead-in before the music; the recording starts here.
    let countInTicks: Int
    let durationSeconds: Double

    struct TimeSignature: Decodable {
        let startTicks: Int
        let numerator: Int
        let denominator: Int
    }

    struct Section: Decodable {
        let startTicks: Int
        let name: String
    }

    struct Voice: Decodable, Identifiable {
        let id: String
        let displayName: String
        let notes: [Note]
        let kind: String

        var isDrums: Bool { kind == "Drums" }
    }

    struct Note: Decodable {
        let startTicks: Int
        let durationTicks: Int
        let noteNumber: Int
        let velocity: Int?
    }

    struct Chord: Decodable {
        let startTicks: Int
        let durationTicks: Int
        let text: String
        let symbol: Symbol?

        struct Symbol: Decodable {
            let rootPitchClass: Int
            let quality: String
            let bassPitchClass: Int?
        }
    }

    func seconds(_ ticks: Int) -> Double {
        Double(ticks) / Double(ticksPerQuarterNote) * 60 / tempoBpm
    }

    var countInSeconds: Double { seconds(countInTicks) }

    /// Bar lines in ticks up to the end, honouring meter changes (`ScoreDocument.GetBarPosition`).
    var barLines: [Int] {
        var lines: [Int] = []
        let signatures = timeSignatures.isEmpty ? [TimeSignature(startTicks: 0, numerator: 4, denominator: 4)] : timeSignatures
        for (index, signature) in signatures.enumerated() {
            let measure = 4 * ticksPerQuarterNote / max(1, signature.denominator) * signature.numerator
            let end = index + 1 < signatures.count ? signatures[index + 1].startTicks : lengthTicks
            guard measure > 0 else { continue }
            var tick = signature.startTicks
            while tick < end {
                lines.append(tick)
                tick += measure
            }
        }
        return lines
    }

    /// The voices as they will sound, as `score.ts` `playableVoices`: without a chord pattern the renderer plays one
    /// block chord per symbol, which the score does not list as a voice.
    func playableVoices(includeChords: Bool) -> [Voice] {
        let shown = voices.filter { includeChords || $0.kind != "Chords" }
        guard includeChords, !chords.isEmpty, !shown.contains(where: { $0.kind == "Chords" }) else { return shown }
        let notes = chords.flatMap { chord in
            Self.blockChord(chord).map {
                Note(startTicks: chord.startTicks, durationTicks: chord.durationTicks, noteNumber: $0, velocity: 72)
            }
        }
        let melodies = shown.filter { ["Melody", "Doubling", "Harmony"].contains($0.kind) }.count
        var result = shown
        result.insert(Voice(id: "Chords", displayName: "Chords", notes: notes, kind: "Chords"), at: melodies)
        return result
    }

    /// YuE2's chord vocabulary as semitones above the root (`ChordVoicing.GetIntervals`), root in the octave of C3.
    static let chordIntervals: [String: [Int]] = [
        "Major": [0, 4, 7], "Minor": [0, 3, 7], "Diminished": [0, 3, 6], "Augmented": [0, 4, 8],
        "Dominant7": [0, 4, 7, 10], "Major7": [0, 4, 7, 11], "Minor7": [0, 3, 7, 10], "Diminished7": [0, 3, 6, 9],
        "HalfDiminished7": [0, 3, 6, 10], "Suspended4": [0, 5, 7], "Suspended2": [0, 2, 7], "Major6": [0, 4, 7, 9],
        "Minor6": [0, 3, 7, 9], "Dominant7Suspended4": [0, 5, 7, 10], "MinorMajor7": [0, 3, 7, 11],
    ]

    static func blockChord(_ chord: Chord) -> [Int] {
        guard let symbol = chord.symbol else { return [] }
        let notes = (chordIntervals[symbol.quality] ?? []).map { 48 + symbol.rootPitchClass + $0 }
        guard let bass = symbol.bassPitchClass else { return notes }
        return [36 + bass] + notes
    }
}

// MARK: The form

/// The Logic page's parameters that matter most, as `logic/options.ts` maps them to `ConversionOptions`; the rest
/// keep the converter's defaults. Kept per device.
struct LogicForm: Codable, Equatable {
    static let bassPatterns = ["Eighths", "Quarters", "RootFifth", "Octaves", "Offbeat", "Sustained", "Walking"]
    static let drumPatterns = ["FourOnTheFloor", "Backbeat", "HalfTime", "Disco", "SixteenthHats", "Shuffle"]
    static let chordPatterns = ["Block", "Eighths", "Sixteenths", "Offbeat", "ArpeggioUp", "ArpeggioUpDown"]

    var includeChords = true
    /// Empty plays one sustained block chord per symbol, as the score notates it.
    var chordPattern = ""
    /// Empty for no bass.
    var bass = ""
    var drums = ""
    var splitDrums = false
    var crash = true
    var countIn = 0
    /// One region per song section in the Logic project; the export only.
    var splitSections = false
    /// Backing vocals from the melody (`ArrangementOptions.Harmony`), as `HarmonySettings` names them.
    var harmonyParts: [String] = []
    var harmonyKey: String?
    var harmonyChorusOnly = false
    /// The melody doubled on a track of its own, whole octaves away.
    var doubleVocal = false
    var doubleOctave = -1
    /// Bass and drums heard in the song's stems instead of generated (`bassStems`, `drumStems`); a library song only.
    var bassFromStems = false
    var drumsFromStems = false

    static let doubleOctaves = [-2, -1, 1, 2]

    init() {}

    /// A form stored by an older version lacks the newer fields; those take their defaults instead of the whole form.
    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        includeChords = (try? c.decode(Bool.self, forKey: .includeChords)) ?? true
        chordPattern = (try? c.decode(String.self, forKey: .chordPattern)) ?? ""
        bass = (try? c.decode(String.self, forKey: .bass)) ?? ""
        drums = (try? c.decode(String.self, forKey: .drums)) ?? ""
        splitDrums = (try? c.decode(Bool.self, forKey: .splitDrums)) ?? false
        crash = (try? c.decode(Bool.self, forKey: .crash)) ?? true
        countIn = (try? c.decode(Int.self, forKey: .countIn)) ?? 0
        splitSections = (try? c.decode(Bool.self, forKey: .splitSections)) ?? false
        harmonyParts = ((try? c.decode([String].self, forKey: .harmonyParts)) ?? []).filter(HarmonySettings.parts.contains)
        harmonyKey = (try? c.decode(String.self, forKey: .harmonyKey)).flatMap { HarmonySettings.keys.contains($0) ? $0 : nil }
        harmonyChorusOnly = (try? c.decode(Bool.self, forKey: .harmonyChorusOnly)) ?? false
        doubleVocal = (try? c.decode(Bool.self, forKey: .doubleVocal)) ?? false
        doubleOctave = (try? c.decode(Int.self, forKey: .doubleOctave)).flatMap { Self.doubleOctaves.contains($0) ? $0 : nil } ?? -1
        bassFromStems = (try? c.decode(Bool.self, forKey: .bassFromStems)) ?? false
        drumsFromStems = (try? c.decode(Bool.self, forKey: .drumsFromStems)) ?? false
    }

    private static let key = "logicForm"

    static func load(from defaults: UserDefaults) -> LogicForm {
        defaults.data(forKey: key).flatMap { try? JSONDecoder().decode(LogicForm.self, from: $0) } ?? LogicForm()
    }

    func save(to defaults: UserDefaults) {
        if let data = try? JSONEncoder().encode(self) {
            defaults.set(data, forKey: Self.key)
        }
    }

    /// `ConversionOptions` as JSON, every assigned track on its instrument's channel and the drums on its drum
    /// machine's notes (`withInstrumentChannels`, `withDrumNotes`), so preview, MIDI and project agree.
    func options(assignments: [String: Int64], instruments: [Components.Schemas.Instrument]) -> [String: Any] {
        var arrangement: [String: Any] = [
            "defaultOctaveShift": 0,
            "octaveShifts": [String: Int](),
        ]
        if !bass.isEmpty {
            arrangement["bass"] = ["pattern": bass, "octaveShift": 0]
        }
        if !drums.isEmpty {
            var drumOptions: [String: Any] = ["pattern": drums, "crashOnSections": crash, "separateTracks": splitDrums]
            if let notes = Self.drumNotes(splitDrums: splitDrums, assignments: assignments, instruments: instruments) {
                drumOptions["notes"] = notes
            }
            arrangement["drums"] = drumOptions
        }
        if includeChords, !chordPattern.isEmpty {
            arrangement["chords"] = ["pattern": chordPattern, "inversion": "RootPosition", "octaveShift": 0]
        }
        if countIn > 0 {
            arrangement["countIn"] = ["bars": countIn, "click": true]
        }
        if doubleVocal {
            arrangement["doubling"] = ["voiceId": "Vocal", "semitones": 12 * doubleOctave]
        }
        if !harmonyParts.isEmpty {
            arrangement["harmony"] = [
                "parts": HarmonySettings.parts.filter(harmonyParts.contains),
                "voiceId": "Vocal",
                "key": harmonyKey.map { $0 as Any } ?? NSNull(),
                // YuE2 names its sections in English; a MIDI file edited in a German Logic may say Refrain.
                "sections": harmonyChorusOnly ? ["chorus", "refrain"] : [String](),
            ] as [String: Any]
        }
        var channels: [String: Int] = [:]
        for (track, instrument) in Self.assigned(assignments, instruments) {
            channels[track] = Int(instrument.channel)
        }
        return [
            "ticksPerQuarterNote": 480,
            "includeChordTrack": includeChords,
            "arrangement": arrangement,
            "midiChannels": channels,
            "midiPrograms": [String: Int](),
        ]
    }

    static func assigned(
        _ assignments: [String: Int64], _ instruments: [Components.Schemas.Instrument]
    ) -> [(String, Components.Schemas.Instrument)] {
        assignments.compactMap { track, id in
            instruments.first { $0.id == id }.map { (track, $0) }
        }
    }

    static let generalMidiDrums = ["kick": 36, "snare": 38, "closedHiHat": 42, "openHiHat": 46, "crash": 49, "clap": 39]
    private static let drumTracks: [(track: String, drums: [String])] = [
        ("Kick", ["kick"]), ("Snare", ["snare"]), ("HiHat", ["closedHiHat", "openHiHat"]), ("Crash", ["crash"]),
    ]

    private static func machine(
        _ track: String, _ assignments: [String: Int64], _ instruments: [Components.Schemas.Instrument]
    ) -> [String: Int]? {
        guard let id = assignments[track], let instrument = instruments.first(where: { $0.id == id }),
              instrument.kind == .drumMachine, let drums = instrument.drums
        else { return nil }
        let notes: [String: Int?] = [
            "kick": drums.kick.map(Int.init), "snare": drums.snare.map(Int.init),
            "closedHiHat": drums.closedHiHat.map(Int.init), "openHiHat": drums.openHiHat.map(Int.init),
            "crash": drums.crash.map(Int.init), "clap": drums.clap.map(Int.init),
        ]
        return generalMidiDrums.merging(notes.compactMapValues { $0 }) { _, own in own }
    }

    /// The notes the drums are generated on: one drum track from the machine on `Drums`; a split kit each drum from
    /// the machine its track plays, the count-in's clap from the kick's. Nil when no drum machine is involved.
    static func drumNotes(
        splitDrums: Bool, assignments: [String: Int64], instruments: [Components.Schemas.Instrument]
    ) -> [String: Int]? {
        guard splitDrums else { return machine("Drums", assignments, instruments) }
        let machines = drumTracks.map { ($0.drums, machine($0.track, assignments, instruments)) }
        guard machines.contains(where: { $0.1 != nil }) else { return nil }
        var merged = generalMidiDrums
        for (drums, machine) in machines {
            guard let machine else { continue }
            for drum in drums {
                merged[drum] = machine[drum]
            }
        }
        merged["clap"] = machine("Kick", assignments, instruments)?["clap"] ?? generalMidiDrums["clap"]
        return merged
    }

    /// What the export is told per track: the instrument's name, port and channel.
    static func exportInstruments(
        _ assignments: [String: Int64], _ instruments: [Components.Schemas.Instrument]
    ) -> [String: Any] {
        Dictionary(uniqueKeysWithValues: assigned(assignments, instruments).map { track, instrument in
            (track, ["name": instrument.name, "port": instrument.port, "channel": Int(instrument.channel)] as [String: Any])
        })
    }
}

// MARK: The mixer

/// Volume (linear, 1 unchanged, up to 2) and pan (-1 to 1) per track, as the web page's mixer keeps them on the
/// server (`logic_mixer`, opaque JSON there), keyed by the track's name in upper case.
struct LogicMixer: Equatable {
    struct Mix: Equatable {
        var volume: Float = 1
        var pan: Float = 0
    }

    static let maxVolume: Float = 2
    var master: Float = 1
    var tracks: [String: Mix] = [:]

    static func key(_ track: String) -> String { track.uppercased() }

    subscript(track: String) -> Mix {
        get { tracks[Self.key(track)] ?? Mix() }
        set { tracks[Self.key(track)] = newValue }
    }

    /// What the server kept, made safe: out-of-range values clamped, anything broken dropped (`normalizeMixer`).
    init(json: Any?) {
        let value = json as? [String: Any] ?? [:]
        master = Self.level(value["master"], 1)
        for (track, entry) in value["tracks"] as? [String: Any] ?? [:] {
            let mix = entry as? [String: Any] ?? [:]
            let pan = (mix["pan"] as? NSNumber)?.floatValue ?? 0
            tracks[Self.key(track)] = Mix(volume: Self.level(mix["volume"], 1), pan: min(1, max(-1, pan.isFinite ? pan : 0)))
        }
    }

    init() {}

    var json: [String: Any] {
        [
            "master": master,
            "tracks": tracks.mapValues { ["volume": $0.volume, "pan": $0.pan] },
        ]
    }

    private static func level(_ value: Any?, _ fallback: Float) -> Float {
        guard let number = (value as? NSNumber)?.floatValue, number.isFinite else { return fallback }
        return min(maxVolume, max(0, number))
    }
}
