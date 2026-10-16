import Foundation
import Observation
import TonwerkKit

/// The ACE-Step page's form, as the web page's `ace.ts`: nil and empty strings leave a value to the model, as the
/// server's `AceSongRequest` does, and the page shows them as "automatisch". Kept across starts like the create form.
struct AceForm: Codable, Equatable {
    enum Task: String, Codable, CaseIterable, Identifiable {
        case text2music
        case cover
        case repaint

        var id: String { rawValue }

        var name: String {
            switch self {
            case .text2music: String(localized: "Neuer Song")
            case .cover: String(localized: "Cover")
            case .repaint: String(localized: "Neu malen")
            }
        }

        var hint: String {
            switch self {
            case .text2music: String(localized: "Aus Beschreibung und Text entsteht ein neuer Song.")
            case .cover: String(localized: "Ein Song aus der Bibliothek im neuen Stil: Melodie und Aufbau bleiben, der Klang folgt der Beschreibung.")
            case .repaint: String(localized: "Ein Abschnitt eines Songs aus der Bibliothek wird neu erzeugt, der Rest bleibt.")
            }
        }
    }

    var title = ""
    var caption = ""
    var lyrics = ""
    var instrumental = false
    /// ACE-Step's language code; empty lets the LM tell.
    var language = ""
    var duration: Double?
    var bpm: Int?
    var key = ""
    var timeSignature: Int?
    /// Typed as text like the create form's seed; empty for a new one.
    var seed = ""
    var batch = 1
    /// GGUF file names; empty for the server's default (the largest LM, a turbo DiT).
    var lmModel = ""
    var ditModel = ""
    var steps: Int?
    var guidance: Double?
    var shift: Double?
    /// Empty for euler, acestep.cpp's default.
    var solver = ""
    var lmTemperature: Double?
    var lmCfgScale: Double?
    var lmTopP: Double?
    var lmTopK: Int?
    var negativePrompt = ""
    var enrichCaption = true
    var adapter = ""
    var adapterScale: Double?
    var task = Task.text2music
    /// The library song (`run/songN`) a cover or repaint starts from.
    var source = ""
    var coverStrength: Double?
    var repaintStart: Double?
    var repaintEnd: Double?

    init() {}

    /// A stored form of an older version: what it lacks or holds in another type takes the default.
    init(from decoder: Decoder) throws {
        self.init()
        let container = try decoder.container(keyedBy: CodingKeys.self)
        func read<T: Decodable>(_ key: CodingKeys, _ value: inout T) {
            if let decoded = try? container.decodeIfPresent(T.self, forKey: key) {
                value = decoded
            }
        }
        read(.title, &title)
        read(.caption, &caption)
        read(.lyrics, &lyrics)
        read(.instrumental, &instrumental)
        read(.language, &language)
        read(.duration, &duration)
        read(.bpm, &bpm)
        read(.key, &key)
        read(.timeSignature, &timeSignature)
        read(.seed, &seed)
        read(.batch, &batch)
        read(.lmModel, &lmModel)
        read(.ditModel, &ditModel)
        read(.steps, &steps)
        read(.guidance, &guidance)
        read(.shift, &shift)
        read(.solver, &solver)
        read(.lmTemperature, &lmTemperature)
        read(.lmCfgScale, &lmCfgScale)
        read(.lmTopP, &lmTopP)
        read(.lmTopK, &lmTopK)
        read(.negativePrompt, &negativePrompt)
        read(.enrichCaption, &enrichCaption)
        read(.adapter, &adapter)
        read(.adapterScale, &adapterScale)
        read(.task, &task)
        read(.source, &source)
        read(.coverStrength, &coverStrength)
        read(.repaintStart, &repaintStart)
        read(.repaintEnd, &repaintEnd)
    }

    private static let key = "aceForm"

    static func load(from defaults: UserDefaults) -> AceForm {
        guard let data = defaults.data(forKey: key), let form = try? JSONDecoder().decode(AceForm.self, from: data)
        else { return AceForm() }
        return form
    }

    func save(to defaults: UserDefaults) {
        if let data = try? JSONEncoder().encode(self) {
            defaults.set(data, forKey: Self.key)
        }
    }

    /// Only a new song is composed by the LM; a cover or repaint goes to the DiT directly.
    var usesLm: Bool { task == .text2music }

    /// The parameters under "Erweitert", which the reset puts back.
    var advancedChanged: Bool { resettingAdvanced() != self }

    func resettingAdvanced() -> AceForm {
        let defaults = AceForm()
        var form = self
        form.lmModel = defaults.lmModel
        form.ditModel = defaults.ditModel
        form.steps = defaults.steps
        form.guidance = defaults.guidance
        form.shift = defaults.shift
        form.solver = defaults.solver
        form.lmTemperature = defaults.lmTemperature
        form.lmCfgScale = defaults.lmCfgScale
        form.lmTopP = defaults.lmTopP
        form.lmTopK = defaults.lmTopK
        form.negativePrompt = defaults.negativePrompt
        form.enrichCaption = defaults.enrichCaption
        form.adapter = defaults.adapter
        form.adapterScale = defaults.adapterScale
        return form
    }

    /// The form as `POST api/ace` takes it, as `toAceRequest` builds it: only what was set, the rest left to the
    /// model; what a task does not read stays out.
    var request: [String: Any] {
        func text(_ value: String) -> String? {
            let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
            return trimmed.isEmpty ? nil : trimmed
        }
        let lm = usesLm
        let adapterSet = text(adapter) != nil
        var body: [String: Any] = [:]
        // Every field goes out, the unset ones as null, as the web page sends them.
        func put(_ key: String, _ value: Any?) {
            body[key] = value ?? NSNull()
        }
        put("title", text(title))
        put("caption", caption.trimmingCharacters(in: .whitespacesAndNewlines))
        put("lyrics", instrumental ? nil : text(lyrics))
        put("instrumental", instrumental)
        put("language", instrumental ? nil : text(language))
        // The source decides a cover's or repaint's length, tempo and key.
        put("duration", lm ? duration : nil)
        put("bpm", lm ? bpm : nil)
        put("key", lm ? text(key) : nil)
        put("timeSignature", lm ? timeSignature : nil)
        put("seed", Int64(seed.trimmingCharacters(in: .whitespaces)))
        put("batch", batch)
        put("lmModel", lm ? text(lmModel) : nil)
        put("ditModel", text(ditModel))
        put("steps", steps)
        put("guidance", guidance)
        put("shift", shift)
        put("solver", text(solver))
        put("lmTemperature", lm ? lmTemperature : nil)
        put("lmCfgScale", lm ? lmCfgScale : nil)
        put("lmTopP", lm ? lmTopP : nil)
        put("lmTopK", lm ? lmTopK : nil)
        put("negativePrompt", lm ? text(negativePrompt) : nil)
        put("enrichCaption", lm ? enrichCaption : nil)
        put("adapter", text(adapter))
        put("adapterScale", adapterSet ? adapterScale : nil)
        put("task", task.rawValue)
        put("source", lm ? nil : text(source))
        put("coverStrength", task == .cover ? coverStrength : nil)
        put("repaintStart", task == .repaint ? repaintStart : nil)
        put("repaintEnd", task == .repaint ? repaintEnd : nil)
        return body
    }

    /// A song's request laid over the form, as `fromAceRequest` does: what it does not say goes back to the default.
    init(request: Components.Schemas.AceSongRequest) {
        self.init()
        title = request.title ?? ""
        caption = request.caption ?? ""
        lyrics = request.lyrics ?? ""
        instrumental = request.instrumental ?? false
        language = request.language ?? ""
        duration = request.duration
        bpm = request.bpm.map(Int.init)
        key = request.key ?? ""
        timeSignature = request.timeSignature.map(Int.init)
        seed = request.seed.map(String.init) ?? ""
        batch = request.batch.map(Int.init) ?? 1
        lmModel = request.lmModel ?? ""
        ditModel = request.ditModel ?? ""
        steps = request.steps.map(Int.init)
        guidance = request.guidance
        shift = request.shift
        solver = request.solver ?? ""
        lmTemperature = request.lmTemperature
        lmCfgScale = request.lmCfgScale
        lmTopP = request.lmTopP
        lmTopK = request.lmTopK.map(Int.init)
        negativePrompt = request.negativePrompt ?? ""
        enrichCaption = request.enrichCaption ?? true
        adapter = request.adapter ?? ""
        adapterScale = request.adapterScale
        task = request.task.flatMap(Task.init(rawValue:)) ?? .text2music
        source = request.source ?? ""
        coverStrength = request.coverStrength
        repaintStart = request.repaintStart
        repaintEnd = request.repaintEnd
    }

    /// Models or an adapter removed from the server's folder since would be refused.
    func keeping(lms: [String], dits: [String], adapters: [String]) -> AceForm {
        var form = self
        if !lmModel.isEmpty && !lms.contains(lmModel) { form.lmModel = "" }
        if !ditModel.isEmpty && !dits.contains(ditModel) { form.ditModel = "" }
        if !adapter.isEmpty && !adapters.contains(adapter) { form.adapter = "" }
        return form
    }

    /// The languages ACE-Step's LM knows (the server's `AceSongRequest.Languages`), German and English first.
    static let languages = [
        "de", "en", "ar", "az", "bg", "bn", "ca", "cs", "da", "el", "es", "fa", "fi", "fr", "he", "hi", "hr", "ht",
        "hu", "id", "is", "it", "ja", "ko", "la", "lt", "ms", "ne", "nl", "no", "pa", "pl", "pt", "ro", "ru", "sa",
        "sk", "sr", "sv", "sw", "ta", "te", "th", "tl", "tr", "uk", "ur", "vi", "yue", "zh",
    ]

    /// ACE-Step reads keys as "C major", "F# minor".
    static let keys = ["C", "C#", "Db", "D", "Eb", "E", "F", "F#", "Gb", "G", "Ab", "A", "Bb", "B"]
        .flatMap { root in ["major", "minor"].map { "\(root) \($0)" } }

    static let solvers = ["euler", "sde", "dpm3m", "stork4"]
}

/// The ACE-Step page's state, the native counterpart of `AcePage.vue`. Kept on the model, so a typed caption survives
/// a page change; the form itself is ``AppModel/aceForm``, since "use everything" fills it before the page opens.
@MainActor
@Observable
final class AceSession {
    private(set) var info: Components.Schemas.AceInfo?
    private(set) var loading = false
    private(set) var sending = false
    var problem: String?
    var notice: String?

    private unowned let model: AppModel

    init(model: AppModel) {
        self.model = model
    }

    func load() async {
        guard !loading else { return }
        loading = true
        defer { loading = false }
        do {
            let info = try await model.aceInfo()
            self.info = info
            problem = nil
            let kept = model.aceForm.keeping(lms: info.lmModels, dits: info.ditModels, adapters: info.adapters)
            if kept != model.aceForm {
                model.aceForm = kept
            }
        } catch {
            problem = String(localized: "ACE-Step ließ sich nicht laden: \(error.localizedDescription)")
        }
    }

    /// What was loaded, with the live changes laid over it; a deleted job arrives as cancelled and leaves.
    var jobs: [Components.Schemas.AceJob] {
        Self.merge(info?.jobs ?? [], live: Array(model.liveAce.values))
    }

    nonisolated static func merge(
        _ loaded: [Components.Schemas.AceJob], live: [Components.Schemas.AceJob]
    ) -> [Components.Schemas.AceJob] {
        var byId = Dictionary(loaded.map { ($0.id, $0) }, uniquingKeysWith: { _, last in last })
        for job in live {
            if let known = byId[job.id], (known.updatedAt ?? .distantPast) > (job.updatedAt ?? .distantPast) {
                continue
            }
            byId[job.id] = job
        }
        return byId.values
            .filter { $0.stage != "cancelled" }
            .sorted { ($0.createdAt ?? .distantPast) > ($1.createdAt ?? .distantPast) }
    }

    var canSend: Bool {
        let form = model.aceForm
        return info?.ready == true && !form.caption.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            && (form.usesLm || !form.source.isEmpty) && !sending
    }

    func send() async {
        sending = true
        defer { sending = false }
        do {
            let job = try await model.composeAce(model.aceForm)
            if var info {
                info.jobs = [job] + info.jobs.filter { $0.id != job.id }
                self.info = info
            }
            problem = nil
            notice = String(localized: "ACE-Step ist dran; der Song erscheint in der Bibliothek, sobald er fertig ist.")
        } catch {
            notice = nil
            problem = error.localizedDescription
        }
    }

    func delete(_ job: Components.Schemas.AceJob) async {
        do {
            try await model.deleteAceJob(job.id)
            if var info {
                info.jobs.removeAll { $0.id == job.id }
                self.info = info
            }
        } catch {
            problem = error.localizedDescription
        }
    }

    /// The job's request back in the form, with the seed it was made with, to vary one thing.
    func reuse(_ job: Components.Schemas.AceJob) {
        var request = job.request
        request.seed = job.seed
        model.aceForm = AceForm(request: request)
    }

    /// The songs the job made, as the library lists them; one deleted since is left out.
    func songs(of job: Components.Schemas.AceJob) -> [Song] {
        guard let run = job.run else { return [] }
        return model.songs.filter { $0.run == run }
    }

    nonisolated static func stageName(_ job: Components.Schemas.AceJob) -> String {
        let stage = job.stage ?? "queued"
        let name = switch stage {
        case "queued": String(localized: "Wartet")
        case "loading": String(localized: "Lädt")
        case "writing": String(localized: "Schreibt")
        case "composing": String(localized: "Komponiert")
        case "synthesizing": String(localized: "Erzeugt Klang")
        case "decoding": String(localized: "Dekodiert")
        case "saving": String(localized: "Speichert")
        case "done": String(localized: "Fertig")
        case "failed": String(localized: "Fehlgeschlagen")
        case "cancelled": String(localized: "Abgebrochen")
        default: stage
        }
        if job.finished == true || stage == "queued" || stage == "saving" {
            return name
        }
        return "\(name) \(Int(((job.fraction ?? 0) * 100).rounded())) %"
    }
}

extension AppModel {
    func openAce() {
        if acePage == nil {
            acePage = AceSession(model: self)
        }
        page = .ace
    }

    /// ACE-Step's jobs not finished, oldest first, as the queue lists them.
    var aceJobs: [Components.Schemas.AceJob] {
        liveAce.values
            .filter { $0.finished != true && $0.stage != "cancelled" }
            .sorted { ($0.createdAt ?? .distantPast) < ($1.createdAt ?? .distantPast) }
    }

    func aceInfo() async throws -> Components.Schemas.AceInfo {
        let (data, _) = try await call("GET", address("api/ace"))
        return try decode(Components.Schemas.AceInfo.self, data)
    }

    func composeAce(_ form: AceForm) async throws -> Components.Schemas.AceJob {
        let (data, _) = try await call("POST", address("api/ace"), json: form.request, accept: [202])
        return try decode(Components.Schemas.AceJob.self, data)
    }

    func deleteAceJob(_ id: String) async throws {
        _ = try await call("DELETE", address("api/ace", id), accept: [204, 404])
    }

    /// An ACE-Step song made again: its own settings go back into its page, since YuE2's form knows none of them.
    func useAceSong(_ song: Song) async {
        guard let answer = try? await call("GET", address("api/songs", song.run, song.name, "ace")),
              let request = try? decode(Components.Schemas.AceSongRequest.self, answer.0)
        else { return }
        aceForm = AceForm(request: request)
        openAce()
    }
}
