import Foundation
import Observation
import TonwerkKit

/// A sketch as `GET api/sketches/{id}` answers it: before Claude's plan only the job is there, so everything else is
/// optional here, unlike the generated `SketchResult`.
struct SketchDetail: Decodable {
    let job: Components.Schemas.SketchJob
    let abc: String?
    let request: Components.Schemas.SongRequest?
    let warnings: [Components.Schemas.SketchWarning]?
    let bars: Int?
    let seconds: Double?
}

/// The sketch page's state (`SketchPage.vue`): a written sketch in, after a few minutes Claude's plan, and from it a
/// score with lyrics for the create form, a MIDI draft and the score as files. The server keeps the sketches; the
/// page asks when shown and every few seconds while one is being written.
@MainActor
@Observable
final class SketchSession {
    private(set) var info: Components.Schemas.SketchInfo?
    private(set) var sketches: [Components.Schemas.SketchSummary] = []
    private(set) var selected: SketchDetail?
    private(set) var sending = false
    var problem: String?
    var text = ""
    var wishes = ""
    /// The chosen Claude model, empty for the server's default, so a new default reaches it.
    private(set) var chosenModel: String

    private static let modelKey = "sketchModel"
    private unowned let model: AppModel
    @ObservationIgnored private var poll: Task<Void, Never>?

    init(model: AppModel) {
        self.model = model
        chosenModel = UserDefaults.standard.string(forKey: Self.modelKey) ?? ""
    }

    /// What the picker shows: the chosen model while the server offers it, else the default.
    var modelId: String {
        get {
            guard let info else { return chosenModel }
            return info.models.contains { $0.id == chosenModel } ? chosenModel : info.model
        }
        set {
            chosenModel = newValue == info?.model ? "" : newValue
            UserDefaults.standard.set(chosenModel, forKey: Self.modelKey)
        }
    }

    var working: Bool { sketches.contains { $0.stage == "queued" || $0.stage == "writing" } }

    func load() async {
        if let info = try? await model.sketchInfo() {
            self.info = info
        }
        await refresh()
    }

    func refresh() async {
        do {
            sketches = try await model.sketches()
            let summary = selected.flatMap { shown in sketches.first { $0.id == shown.job.id } }
            if selected != nil && summary == nil {
                selected = nil
            } else if let summary, summary.stage != selected?.job.stage {
                selected = try await model.sketch(summary.id)
            } else if selected == nil, let first = sketches.first {
                selected = try await model.sketch(first.id)
            }
            problem = nil
        } catch {
            problem = error.localizedDescription
        }
        schedule()
    }

    private func schedule() {
        poll?.cancel()
        guard working, model.page == .sketch else { return }
        poll = Task { [weak self] in
            try? await Task.sleep(for: .seconds(4))
            guard !Task.isCancelled else { return }
            await self?.refresh()
        }
    }

    func stop() {
        poll?.cancel()
    }

    /// A model the server no longer offers goes to the default rather than to a refusal.
    private func offered(_ id: String?) -> String? {
        guard let id, info?.models.contains(where: { $0.id == id }) == true else { return nil }
        return id
    }

    func send(_ source: String? = nil, instruction: String? = nil, with modelId: String? = nil) async {
        let source = source ?? text
        guard !source.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, !sending else { return }
        sending = true
        defer { sending = false }
        do {
            let queued = try await model.planSketch(
                source, instruction: (instruction ?? wishes).trimmingCharacters(in: .whitespacesAndNewlines),
                model: offered(modelId ?? self.modelId))
            sketches = [queued] + sketches.filter { $0.id != queued.id }
            selected = try await model.sketch(queued.id)
            problem = nil
            schedule()
        } catch {
            problem = error.localizedDescription
        }
    }

    func show(_ summary: Components.Schemas.SketchSummary) async {
        do {
            selected = try await model.sketch(summary.id)
        } catch {
            problem = error.localizedDescription
        }
    }

    func delete(_ detail: SketchDetail) async {
        do {
            try await model.deleteSketch(detail.job.id)
            selected = nil
            await refresh()
        } catch {
            problem = error.localizedDescription
        }
    }

    /// The planned song in the create form: lyrics, style and the score with planning "full".
    func useSong(_ detail: SketchDetail) {
        guard let request = detail.request else { return }
        model.form.take(request)
        model.saveForm()
        model.page = .create
    }

    nonisolated static func stageName(_ stage: String) -> String {
        switch stage {
        case "queued": String(localized: "wartet")
        case "writing": String(localized: "Claude schreibt …")
        case "done": String(localized: "fertig")
        case "failed": String(localized: "fehlgeschlagen")
        default: stage
        }
    }

    nonisolated static func summary(bars: Int?, seconds: Double?) -> String? {
        guard let bars, bars > 0, let seconds, seconds > 0 else { return nil }
        let duration = Duration.seconds(seconds).formatted(.time(pattern: .minuteSecond))
        return String(localized: "\(bars) Takte · \(duration)")
    }

    nonisolated static func warning(_ warning: Components.Schemas.SketchWarning) -> String {
        let bar = warning.bar.map(String.init) ?? ""
        let detail = warning.detail ?? ""
        return switch warning.code {
        case "barLength": String(localized: "Takt \(bar): \(detail) Sechzehntel, angepasst")
        case "note": String(localized: "Takt \(bar): Note „\(detail)“ unlesbar, weggelassen")
        case "chord": String(localized: "Takt \(bar): Akkord „\(detail)“ unbekannt")
        case "chordSimplified": String(localized: "Für YuE2 vereinfacht (im MIDI bleiben sie): \(detail)")
        case "syllables": String(localized: "Takt \(bar): \(detail) Noten/Silben, passt nicht genau")
        case "key": String(localized: "Tonart „\(detail)“ unbekannt, C-Dur angenommen")
        case "tooLong": String(localized: "Mit \(detail) s länger als zehn Minuten, YuE2 bricht ab")
        default: [warning.code, bar, detail].filter { !$0.isEmpty }.joined(separator: " ")
        }
    }

    /// A section's chords bar by bar, the way the sketch writes them: `| Dm | Bbmaj7 | F C/E |`.
    nonisolated static func chordLine(_ section: Components.Schemas.SketchSection) -> String {
        "| \(section.bars.map(\.chords).joined(separator: " | ")) |"
    }
}

extension AppModel {
    func openSketch() {
        if sketchPage == nil {
            sketchPage = SketchSession(model: self)
        }
        page = .sketch
    }

    func sketchInfo() async throws -> Components.Schemas.SketchInfo {
        let (data, _) = try await call("GET", address("api/sketches/info"))
        return try decode(Components.Schemas.SketchInfo.self, data)
    }

    func sketches() async throws -> [Components.Schemas.SketchSummary] {
        let (data, _) = try await call("GET", address("api/sketches"))
        return try decode([Components.Schemas.SketchSummary].self, data)
    }

    func sketch(_ id: String) async throws -> SketchDetail {
        let (data, _) = try await call("GET", address("api/sketches", id))
        return try decode(SketchDetail.self, data)
    }

    func planSketch(_ text: String, instruction: String, model: String?) async throws
        -> Components.Schemas.SketchSummary
    {
        let body: [String: Any] = [
            "text": text,
            "instruction": instruction.isEmpty ? NSNull() : instruction,
            "model": model.map { $0 as Any } ?? NSNull(),
        ]
        let (data, _) = try await call("POST", address("api/sketches"), json: body, accept: [202])
        return try decode(Components.Schemas.SketchSummary.self, data)
    }

    func deleteSketch(_ id: String) async throws {
        _ = try await call("DELETE", address("api/sketches", id), accept: [204, 404])
    }

    /// The MIDI draft or the score in the temporary folder, to save or share, named after the sketch.
    func downloadSketch(_ job: Components.Schemas.SketchJob, midi: Bool) async throws -> URL {
        let (data, _) = try await call("GET", address("api/sketches", job.id, midi ? "midi" : "score"))
        let folder = FileManager.default.temporaryDirectory
            .appending(path: "Tonwerk-Ausarbeitung", directoryHint: .isDirectory)
            .appending(path: UUID().uuidString, directoryHint: .isDirectory)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let title = job.title.flatMap { $0.isEmpty ? nil : $0 } ?? "sketch"
        let file = folder.appending(path: "\(Self.projectName(title)).\(midi ? "mid" : "abc")")
        try data.write(to: file)
        return file
    }
}
