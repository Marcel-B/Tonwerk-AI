import Foundation
import Observation
import TonwerkKit

/// The backing vocals page's settings, as `HarmonyPage.vue` keeps them (`yue-ui.harmony`): which parts, the key
/// (nil finds it), only in the chorus, and whether the chord track goes into the MIDI file. Kept per device.
struct HarmonySettings: Codable, Equatable {
    static let parts = ["ThirdAbove", "ThirdBelow", "SixthBelow", "Alto", "Tenor", "Bass", "Drone", "DroneHeld"]
    static let keys = ["C", "Db", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B"]
        + ["Cm", "C#m", "Dm", "Ebm", "Em", "Fm", "F#m", "Gm", "G#m", "Am", "Bbm", "Bm"]

    var parts = ["ThirdAbove", "SixthBelow"]
    var key: String?
    var chorusOnly = false
    var withChords = false

    init() {}

    /// A stored form of another version keeps what it can read; parts no longer offered are dropped.
    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        if let parts = try? container.decode([String].self, forKey: .parts) {
            self.parts = parts.filter(Self.parts.contains)
        }
        key = (try? container.decode(String.self, forKey: .key)).flatMap { Self.keys.contains($0) ? $0 : nil }
        chorusOnly = (try? container.decode(Bool.self, forKey: .chorusOnly)) ?? false
        withChords = (try? container.decode(Bool.self, forKey: .withChords)) ?? false
    }

    private static let storageKey = "harmonySettings"

    static func load(from defaults: UserDefaults) -> HarmonySettings {
        defaults.data(forKey: storageKey).flatMap { try? JSONDecoder().decode(HarmonySettings.self, from: $0) }
            ?? HarmonySettings()
    }

    func save(to defaults: UserDefaults) {
        if let data = try? JSONEncoder().encode(self) {
            defaults.set(data, forKey: Self.storageKey)
        }
    }

    /// The Logic page's conversion with only the backing vocals switched on, as the web page sends it: no bass,
    /// drums or chord pattern, so nothing here depends on the Logic template. Every assigned track goes on its
    /// instrument's channel, as there.
    func options(assignments: [String: Int64], instruments: [Components.Schemas.Instrument]) -> [String: Any] {
        var form = LogicForm()
        form.includeChords = withChords
        var options = form.options(assignments: assignments, instruments: instruments)
        var arrangement = options["arrangement"] as? [String: Any] ?? [:]
        if !parts.isEmpty {
            var harmony: [String: Any] = [
                "parts": Self.parts.filter(parts.contains),
                "voiceId": "Vocal",
                // YuE2 names its sections in English; a MIDI file edited in a German Logic may say Refrain.
                "sections": chorusOnly ? ["chorus", "refrain"] : [String](),
            ]
            if let key {
                harmony["key"] = key
            } else {
                harmony["key"] = NSNull()
            }
            arrangement["harmony"] = harmony
        }
        options["arrangement"] = arrangement
        return options
    }

    static func name(_ part: String) -> String {
        switch part {
        case "ThirdAbove": String(localized: "Terz darüber")
        case "ThirdBelow": String(localized: "Terz darunter")
        case "SixthBelow": String(localized: "Sexte darunter")
        case "Alto": String(localized: "Alt (Chorsatz)")
        case "Tenor": String(localized: "Tenor (Chorsatz)")
        case "Bass": String(localized: "Bass (Chorsatz)")
        case "Drone": String(localized: "Bordun (Grundton im Rhythmus)")
        case "DroneHeld": String(localized: "Bordun liegend")
        default: part
        }
    }
}

/// The backing vocals page (`HarmonyPage.vue`): a melody (a MIDI file or score.abc, or a library song) in, a MIDI
/// file with one track per part out, heard first in the app's own sounds.
@MainActor
@Observable
final class HarmonySession {
    enum Source: Equatable {
        case file(PickedFile)
        case song(Song)

        static func == (lhs: Source, rhs: Source) -> Bool {
            switch (lhs, rhs) {
            case (.file(let a), .file(let b)): a == b
            case (.song(let a), .song(let b)): a.id == b.id
            default: false
            }
        }
    }

    var settings: HarmonySettings {
        didSet {
            guard settings != oldValue else { return }
            settings.save(to: defaults)
            markStale()
        }
    }
    var source: Source? {
        didSet {
            guard source != oldValue else { return }
            markStale()
        }
    }
    private(set) var conversion: LogicConversion?
    private(set) var busy = false
    private(set) var problem: String?
    /// The result belongs to the settings it was made with; a change marks it, as on the web page.
    private(set) var stale = false

    let player = LogicPlayer()

    private unowned let model: AppModel
    private let defaults: UserDefaults

    init(model: AppModel, defaults: UserDefaults = .standard) {
        self.model = model
        self.defaults = defaults
        settings = HarmonySettings.load(from: defaults)
    }

    var canGenerate: Bool { source != nil && !settings.parts.isEmpty && !busy }

    var score: LogicScore? { conversion?.score }

    var voices: [LogicScore.Voice] {
        score?.playableVoices(includeChords: settings.withChords) ?? []
    }

    var notes: [LogicDiagnostic] { Self.notes(conversion?.diagnostics ?? []) }

    /// What the parts were derived from (key, guessed chords) and what went wrong, without the conversion's routine
    /// notes; a melody alone has no chord track, which the MIDI reading would otherwise warn about (`YTL074`).
    nonisolated static func notes(_ diagnostics: [LogicDiagnostic]) -> [LogicDiagnostic] {
        diagnostics.filter { $0.severity == "Info" ? ["YTL031", "YTL032"].contains($0.code) : $0.code != "YTL074" }
    }

    private func markStale() {
        stale = conversion != nil
    }

    func pick(_ url: URL) async {
        do {
            source = .file(try await PickedFile.copy(from: url))
        } catch {
            problem = error.localizedDescription
        }
    }

    func generate() async {
        guard let source, canGenerate else { return }
        busy = true
        problem = nil
        defer { busy = false }
        player.stop()
        do {
            async let instruments = model.logicInstruments()
            async let assignments = model.logicAssignments()
            if !model.soundLibrary.loaded {
                await model.soundLibrary.load()
            }
            let asked = settings
            let options = asked.options(assignments: await assignments, instruments: await instruments)
            let result = try await model.convertHarmony(source, options: options)
            conversion = result
            stale = settings != asked || self.source != source
            if let score = result.score {
                player.load(score, tracks: tracks, mixer: LogicMixer())
            } else if let error = result.diagnostics.first(where: { $0.severity == "Error" }) {
                problem = error.message
            }
        } catch {
            conversion = nil
            problem = error.localizedDescription
        }
    }

    /// Every part in the app's sound for its track, as the Logic page plays an unassigned track.
    private var tracks: [LogicPlayer.Track] {
        voices.map { voice in
            LogicPlayer.Track(
                voice: voice, route: .app,
                sound: voice.isDrums ? nil : model.soundLibrary.sound(forTrack: voice.id, kind: voice.kind))
        }
    }

    /// The MIDI file in the temporary folder, named after the melody as the web page names it.
    func midiFile() throws -> URL {
        guard let midi = conversion?.midi, let data = Data(base64Encoded: midi), let source else {
            throw ServerError(String(localized: "Keine MIDI-Datei."))
        }
        let folder = FileManager.default.temporaryDirectory
            .appending(path: "Tonwerk-Harmony", directoryHint: .isDirectory)
            .appending(path: UUID().uuidString, directoryHint: .isDirectory)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let file = folder.appending(path: Self.fileName(source))
        try data.write(to: file)
        return file
    }

    nonisolated static func fileName(_ source: Source) -> String {
        let base = switch source {
        case .file(let file): file.baseName
        case .song(let song): song.displayTitle
        }
        return "\(AppModel.projectName(base))-begleitstimmen.mid"
    }

    func stop() {
        player.stop()
    }
}

extension AppModel {
    func openHarmony() {
        if harmonyPage == nil {
            harmonyPage = HarmonySession(model: self)
        }
        page = .harmony
    }

    /// The melody through the Logic page's converter; a MIDI file or score.abc goes up as `file`, a library song by
    /// its id. A score that cannot be converted answers its errors (422).
    func convertHarmony(_ source: HarmonySession.Source, options: [String: Any]) async throws -> LogicConversion {
        let json = (try? JSONSerialization.data(withJSONObject: options)).flatMap { String(data: $0, encoding: .utf8) } ?? "{}"
        let answer: (Data, HTTPURLResponse)
        switch source {
        case .file(let file):
            answer = try await uploadFile(
                address("api/logic/convert"), fields: [("options", json)], fileField: "file", file: file,
                accept: [200, 422])
        case .song(let song):
            answer = try await uploadFields(address("api/logic/convert"), fields: [("song", song.id), ("options", json)])
        }
        return try JSONDecoder().decode(LogicConversion.self, from: answer.0)
    }

    private func uploadFields(_ url: URL, fields: [(String, String)]) async throws -> (Data, HTTPURLResponse) {
        let boundary = "tonwerk-\(UUID().uuidString)"
        var body = Data()
        for (name, value) in fields {
            body.append(Data("--\(boundary)\r\nContent-Disposition: form-data; name=\"\(name)\"\r\n\r\n\(value)\r\n".utf8))
        }
        body.append(Data("--\(boundary)--\r\n".utf8))
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.timeoutInterval = 120
        request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")
        let (data, response) = try await URLSession.shared.upload(for: request, from: body)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        guard let http = response as? HTTPURLResponse, status == 200 || status == 422 else {
            throw ServerError(problem: ServerError.text(of: data) ?? String(localized: "Der Server antwortete \(status)."))
        }
        return (data, http)
    }
}
