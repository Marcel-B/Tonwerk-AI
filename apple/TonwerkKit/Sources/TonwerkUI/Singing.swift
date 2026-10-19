import Foundation
import Observation
import TonwerkKit

/// The singing page's state: a melody (a MIDI file or score.abc, or a library song's score) and words go to
/// SoulX-Singer on the server (`POST api/singing`), which sings them with one of its voices. Kept by ``AppModel`` once
/// opened, so a chosen melody and typed words survive a page change.
@MainActor
@Observable
final class SingSession {
    private(set) var info: Components.Schemas.SingingInfo?
    private(set) var loading = false
    var problem: String?

    var source: ScoreSource? {
        didSet {
            guard source != oldValue else { return }
            title = source?.baseName ?? ""
        }
    }
    /// The words; for a library song its own lyrics when left empty, for a MIDI file its lyric events.
    var lyrics = ""
    var title = ""
    var voiceId: String?
    /// Semitones the melody moves; ignored while ``autoShift`` moves it into the voice's range.
    var pitchShift = 0
    var autoShift = false
    private(set) var sending = false

    let preview = AudioPreview()

    private unowned let model: AppModel

    init(model: AppModel) {
        self.model = model
        preview.songPlayer = model.player
    }

    func load() async {
        guard !loading else { return }
        loading = true
        defer { loading = false }
        do {
            let info = try await model.singing()
            self.info = info
            problem = nil
            if voiceId.map({ id in !info.voices.contains { $0.id == id } }) ?? true {
                voiceId = info.voices.first?.id
            }
        } catch {
            problem = String(localized: "Die Seite ließ sich nicht laden: \(error.localizedDescription)")
        }
    }

    /// What was loaded, with the live changes laid over it; a deleted take arrives as cancelled and leaves.
    var takes: [Components.Schemas.SingTake] {
        Self.merge(info?.takes ?? [], live: Array(model.liveSinging.values))
    }

    nonisolated static func merge(
        _ loaded: [Components.Schemas.SingTake], live: [Components.Schemas.SingTake]
    ) -> [Components.Schemas.SingTake] {
        var byId = Dictionary(loaded.map { ($0.id, $0) }, uniquingKeysWith: { _, last in last })
        for take in live {
            if let known = byId[take.id], (known.updatedAt ?? .distantPast) > (take.updatedAt ?? .distantPast) {
                continue
            }
            byId[take.id] = take
        }
        return byId.values
            .filter { $0.stage != "cancelled" }
            .sorted { ($0.createdAt ?? .distantPast) > ($1.createdAt ?? .distantPast) }
    }

    /// A library song brings its own lyrics; a file needs words unless its MIDI carries them, which the server checks.
    var canSing: Bool {
        source != nil && voiceId != nil && !sending && info?.installed == true && info?.modelDownloaded == true
    }

    func pick(_ url: URL) async {
        do {
            source = .file(try await PickedFile.copy(from: url))
        } catch {
            problem = error.localizedDescription
        }
    }

    func sing() async {
        guard let source, let voiceId, canSing else { return }
        sending = true
        defer { sending = false }
        var fields = [("voice", voiceId), ("autoShift", autoShift ? "true" : "false")]
        if !autoShift && pitchShift != 0 {
            fields.append(("pitchShift", String(pitchShift)))
        }
        let words = lyrics.trimmingCharacters(in: .whitespacesAndNewlines)
        if !words.isEmpty {
            fields.append(("lyrics", words))
        }
        let name = title.trimmingCharacters(in: .whitespaces)
        if !name.isEmpty {
            fields.append(("title", name))
        }
        do {
            let take = try await model.sing(source, fields: fields)
            if var info {
                info.takes.insert(take, at: 0)
                self.info = info
            }
            problem = nil
        } catch {
            problem = error.localizedDescription
        }
    }

    /// Stops a take still in the line or deletes a finished one.
    func delete(_ take: Components.Schemas.SingTake) async {
        if preview.current == take.id {
            preview.stop()
        }
        do {
            try await model.deleteSingTake(take.id)
            if var info {
                info.takes.removeAll { $0.id == take.id }
                self.info = info
            }
        } catch {
            problem = error.localizedDescription
        }
    }

    // MARK: Names

    nonisolated static func finished(_ take: Components.Schemas.SingTake) -> Bool {
        ["done", "failed", "cancelled"].contains(take.stage ?? "queued")
    }

    nonisolated static func stageName(_ take: Components.Schemas.SingTake) -> String {
        switch take.stage ?? "queued" {
        case "queued": String(localized: "Wartet")
        case "loading": String(localized: "Lädt Modell")
        case "singing":
            take.progress.map { String(localized: "Singt \(Int(($0 * 100).rounded())) %") } ?? String(localized: "Singt")
        case "done": String(localized: "Fertig")
        case "failed": String(localized: "Fehlgeschlagen")
        case "cancelled": String(localized: "Abgebrochen")
        case let other: other
        }
    }

    /// How the words fell on the notes: notes that got a syllable, notes left without one, words left over.
    nonisolated static func alignment(_ take: Components.Schemas.SingTake) -> String? {
        guard let sung = take.sung else { return nil }
        var parts = [String(localized: "\(sung) Noten gesungen")]
        if let unsung = take.unsung, unsung > 0 {
            parts.append(String(localized: "\(unsung) ohne Silbe"))
        }
        if let leftover = take.leftover, leftover > 0 {
            parts.append(String(localized: "\(leftover) Wörter übrig"))
        }
        return parts.joined(separator: " · ")
    }

    nonisolated static func stats(_ take: Components.Schemas.SingTake) -> String {
        let number = { (value: Double?) in value.map { $0.formatted(.number.precision(.fractionLength(0...1))) } ?? "–" }
        return String(localized: "Laden \(number(take.loadSeconds)) s · Singen \(number(take.singSeconds)) s · \(number(take.peakMemoryGb)) GB Speicher")
    }
}

extension AppModel {
    func openSing() {
        if singPage == nil {
            singPage = SingSession(model: self)
        }
        page = .sing
    }

    /// Takes not finished, oldest first, as the queue lists them.
    var singJobs: [Components.Schemas.SingTake] {
        liveSinging.values
            .filter { !SingSession.finished($0) }
            .sorted { ($0.createdAt ?? .distantPast) < ($1.createdAt ?? .distantPast) }
    }

    func singing() async throws -> Components.Schemas.SingingInfo {
        let (data, _) = try await call("GET", address("api/singing"))
        return try decode(Components.Schemas.SingingInfo.self, data)
    }

    /// A file goes up as `file`; a library song is named by `song`, whose score the server reads itself.
    func sing(_ source: ScoreSource, fields: [(String, String)]) async throws -> Components.Schemas.SingTake {
        let url = try address("api/singing")
        let data: Data
        switch source {
        case .file(let file):
            (data, _) = try await uploadFile(url, fields: fields, fileField: "file", file: file, accept: [202])
        case .song(let song):
            let boundary = "tonwerk-\(UUID().uuidString)"
            var body = Data()
            for (name, value) in [("song", song.id)] + fields {
                body.append(Data("--\(boundary)\r\nContent-Disposition: form-data; name=\"\(name)\"\r\n\r\n\(value)\r\n".utf8))
            }
            body.append(Data("--\(boundary)--\r\n".utf8))
            var request = URLRequest(url: url)
            request.httpMethod = "POST"
            request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")
            request.timeoutInterval = 120
            let (answer, response) = try await URLSession.shared.upload(for: request, from: body)
            let status = (response as? HTTPURLResponse)?.statusCode ?? 0
            guard status == 202 else {
                throw ServerError(problem: ServerError.text(of: answer) ?? String(localized: "Der Server antwortete \(status)."))
            }
            data = answer
        }
        return try decode(Components.Schemas.SingTake.self, data)
    }

    func deleteSingTake(_ id: String) async throws {
        _ = try await call("DELETE", address("api/singing", id), accept: [204, 404])
    }

    func singTakeAudio(_ id: String) throws -> URL { try address("api/singing", id, "audio") }

    /// A take's WAV in the temporary folder, to save or share, named after its title.
    func downloadSingTake(_ take: Components.Schemas.SingTake) async throws -> URL {
        let (data, _) = try await call("GET", address("api/singing", take.id, "audio"))
        let folder = FileManager.default.temporaryDirectory
            .appending(path: "Tonwerk-Gesang", directoryHint: .isDirectory)
            .appending(path: UUID().uuidString, directoryHint: .isDirectory)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let file = folder.appending(path: "\(Self.projectName(take.title ?? "Tonwerk")).wav")
        try data.write(to: file)
        return file
    }
}
