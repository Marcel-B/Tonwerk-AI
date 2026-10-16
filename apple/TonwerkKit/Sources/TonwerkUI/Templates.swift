import Foundation
import Observation
import TonwerkKit

/// A prompt template's settings as the web page's `form.ts` saves them (`templatePartKeys`): the form's values of the
/// chosen groups, under the web form's names, so a template saved in the browser loads here and the other way round.
/// Every value is optional, since a template holds only its groups; one of the wrong type is skipped like a stored
/// form's.
struct TemplateSettings: Codable, Equatable, Sendable {
    enum Part: String, CaseIterable, Identifiable, Sendable {
        case style
        case seed
        case parameters
        /// The reference voice the web form sings each song with afterwards; the app's form has none yet, so it is
        /// shown and kept, never set here.
        case voice

        var id: String { rawValue }

        var name: String {
            switch self {
            case .style: String(localized: "Stil")
            case .seed: String(localized: "Seed")
            case .parameters: String(localized: "Parameter")
            case .voice: String(localized: "Stimme")
            }
        }
    }

    /// One sampling setting, only the values the template holds; the rest come from the model's.
    struct Sampling: Codable, Equatable, Sendable {
        var temperature: Double?
        var topP: Double?
        var topK: Int?
        var repetitionPenalty: Double?
        var penaltyWindow: Int?

        init(_ sampling: CreateForm.Sampling) {
            temperature = sampling.temperature
            topP = sampling.topP
            topK = sampling.topK
            repetitionPenalty = sampling.repetitionPenalty
            penaltyWindow = sampling.penaltyWindow
        }

        init(from decoder: any Decoder) throws {
            let container = try decoder.container(keyedBy: CodingKeys.self)
            temperature = try? container.decodeIfPresent(Double.self, forKey: .temperature)
            topP = try? container.decodeIfPresent(Double.self, forKey: .topP)
            topK = try? container.decodeIfPresent(Int.self, forKey: .topK)
            repetitionPenalty = try? container.decodeIfPresent(Double.self, forKey: .repetitionPenalty)
            penaltyWindow = try? container.decodeIfPresent(Int.self, forKey: .penaltyWindow)
        }

        /// Laid over the model's values, as `keepValid` does, so a template saved before a value existed still
        /// gets one.
        func over(_ defaults: CreateForm.Sampling) -> CreateForm.Sampling {
            var result = defaults
            result.temperature = temperature ?? defaults.temperature
            result.topP = topP ?? defaults.topP
            result.topK = topK ?? defaults.topK
            result.repetitionPenalty = repetitionPenalty ?? defaults.repetitionPenalty
            result.penaltyWindow = penaltyWindow ?? defaults.penaltyWindow
            return result
        }
    }

    var style: String?
    var instrumental: Bool?
    var seed: String?
    /// "draft" or "full".
    var quality: String?
    var batch: Int?
    /// The planning, by the web form's name.
    var cot: String?
    var draftSteps: Int?
    var fullSteps: Int?
    var engines: String?
    var maxSeconds: Int?
    var abcSampling: Sampling?
    var semanticSampling: Sampling?
    var voiceId: String?
    var voiceShift: Int?
    var voiceStrength: Double?
    var voiceSteps: Int?

    init() {}

    enum CodingKeys: String, CodingKey {
        case style, instrumental, seed, quality, batch, cot, draftSteps, fullSteps, engines, maxSeconds
        case abcSampling, semanticSampling, voiceId, voiceShift, voiceStrength, voiceSteps
    }

    init(from decoder: any Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        func value<T: Decodable>(_ key: CodingKeys) -> T? {
            (try? container.decodeIfPresent(T.self, forKey: key)) ?? nil
        }
        style = value(.style)
        instrumental = value(.instrumental)
        seed = value(.seed)
        quality = value(.quality)
        batch = value(.batch)
        cot = value(.cot)
        draftSteps = value(.draftSteps)
        fullSteps = value(.fullSteps)
        engines = value(.engines)
        maxSeconds = value(.maxSeconds)
        abcSampling = value(.abcSampling)
        semanticSampling = value(.semanticSampling)
        voiceId = value(.voiceId)
        voiceShift = value(.voiceShift)
        voiceStrength = value(.voiceStrength)
        voiceSteps = value(.voiceSteps)
    }

    /// The form's values of the chosen groups, as the web page's `toTemplateSettings` writes them.
    init(form: CreateForm, parts: Set<Part>) {
        if parts.contains(.style) {
            // Instrumental decides whether the style is sung, so it belongs to it.
            style = form.style
            instrumental = form.instrumental
        }
        if parts.contains(.seed) {
            seed = form.seed
        }
        if parts.contains(.parameters) {
            quality = form.full ? "full" : "draft"
            batch = form.batch
            cot = form.planning.rawValue
            draftSteps = form.draftSteps
            fullSteps = form.fullSteps
            engines = form.engines
            maxSeconds = form.maxSeconds
            abcSampling = Sampling(form.abcSampling)
            semanticSampling = Sampling(form.semanticSampling)
        }
    }

    /// The groups the template holds; a group counts when any of its values is there.
    var parts: [Part] {
        Part.allCases.filter { part in
            switch part {
            case .style: style != nil || instrumental != nil
            case .seed: seed != nil
            case .parameters:
                quality != nil || batch != nil || cot != nil || draftSteps != nil || fullSteps != nil
                    || engines != nil || maxSeconds != nil || abcSampling != nil || semanticSampling != nil
            case .voice: voiceId != nil || voiceShift != nil || voiceStrength != nil || voiceSteps != nil
            }
        }
    }

    /// The voice this template holds, kept when the app overwrites it, since the app cannot set one.
    func keepingVoice(of other: TemplateSettings) -> TemplateSettings {
        var result = self
        result.voiceId = other.voiceId
        result.voiceShift = other.voiceShift
        result.voiceStrength = other.voiceStrength
        result.voiceSteps = other.voiceSteps
        return result
    }

    /// The form with the template's values laid over it, as `applyTemplate` does: what the template does not hold
    /// stays, a value the form cannot take is skipped, and with a score in the form its planning stays.
    func apply(to form: CreateForm) -> CreateForm {
        var result = form
        if let style { result.style = style }
        if let instrumental { result.instrumental = instrumental }
        if let seed { result.seed = seed }
        if let quality, quality == "draft" || quality == "full" { result.full = quality == "full" }
        if let batch, (1...10).contains(batch) { result.batch = batch }
        if let cot, let planning = CreateForm.Planning(rawValue: cot), !form.usesScore { result.planning = planning }
        if let draftSteps, draftSteps > 0 { result.draftSteps = draftSteps }
        if let fullSteps, fullSteps > 0 { result.fullSteps = fullSteps }
        if let engines { result.engines = engines }
        if let maxSeconds, maxSeconds > 0 { result.maxSeconds = maxSeconds }
        if let abcSampling { result.abcSampling = abcSampling.over(CreateForm.Sampling.abcDefaults) }
        if let semanticSampling { result.semanticSampling = semanticSampling.over(CreateForm.Sampling.semanticDefaults) }
        // A template's parameters are meant to be heard.
        if parts.contains(.parameters) {
            result.useAdvanced = true
        }
        return result
    }

    /// The values worth reading at a glance, as the templates page lists them: the seed, and parameters that differ
    /// from the model's.
    var details: [String] {
        var lines: [String] = []
        if let seed {
            let trimmed = seed.trimmingCharacters(in: .whitespaces)
            lines.append(trimmed.isEmpty ? String(localized: "Seed zufällig") : String(localized: "Seed \(trimmed)"))
        }
        if instrumental == true {
            lines.append(String(localized: "Instrumental"))
        }
        if let quality {
            lines.append(quality == "full" ? String(localized: "Qualität Voll") : String(localized: "Qualität Entwurf"))
        }
        if let batch, batch > 1 {
            lines.append(String(localized: "\(batch) Songs"))
        }
        if let cot, cot != "full" {
            lines.append(cot == "melody" ? String(localized: "Planung Melodie") : String(localized: "Planung aus"))
        }
        if let maxSeconds, maxSeconds != CreateForm.maxSongSeconds {
            lines.append(String(localized: "Länge bis \(maxSeconds / 60):\(String(format: "%02d", maxSeconds % 60))"))
        }
        let song = semanticSampling
        let score = abcSampling
        if let value = song?.temperature, value != CreateForm.Sampling.semanticDefaults.temperature {
            lines.append(String(localized: "Temperatur Song \(value.formatted())"))
        }
        if let value = score?.temperature, value != CreateForm.Sampling.abcDefaults.temperature {
            lines.append(String(localized: "Temperatur Partitur \(value.formatted())"))
        }
        let other = { (sampling: Sampling?, defaults: CreateForm.Sampling) -> Bool in
            guard let sampling else { return false }
            return (sampling.topP.map { $0 != defaults.topP } ?? false)
                || (sampling.topK.map { $0 != defaults.topK } ?? false)
                || (sampling.repetitionPenalty.map { $0 != defaults.repetitionPenalty } ?? false)
                || (sampling.penaltyWindow.map { $0 != defaults.penaltyWindow } ?? false)
        }
        if other(song, .semanticDefaults) || other(score, .abcDefaults) {
            lines.append(String(localized: "Sampling angepasst"))
        }
        return lines
    }
}

/// One template as `GET api/templates` lists it. Decoded by hand: the settings are opaque JSON to the server, so the
/// generated type has no fields for them.
struct PromptTemplate: Decodable, Equatable, Identifiable, Sendable {
    let id: Int64
    let name: String
    let settings: TemplateSettings
    let updatedAt: String
}

/// The prompt templates (`templates.ts`): the server keeps them in the database, so the backup takes them along;
/// the create page's menu and the templates page share this copy.
@MainActor
@Observable
final class TemplateStore {
    private(set) var templates: [PromptTemplate] = []
    private(set) var loaded = false
    var problem: String?
    /// The template the form was last filled from, so saving offers to overwrite it. Only in memory.
    var loadedId: Int64?

    private unowned let model: AppModel

    init(model: AppModel) {
        self.model = model
    }

    /// Asked again each time a page shows them: another device may have saved one meanwhile.
    func load() async {
        do {
            templates = Self.byName(try await model.templates())
            problem = nil
        } catch {
            problem = error.localizedDescription
        }
        loaded = true
    }

    nonisolated static func byName(_ list: [PromptTemplate]) -> [PromptTemplate] {
        list.sorted {
            let order = $0.name.localizedCaseInsensitiveCompare($1.name)
            return order == .orderedSame ? $0.id < $1.id : order == .orderedAscending
        }
    }

    /// The template of that name, in any case, as the server compares them.
    func named(_ name: String) -> PromptTemplate? {
        let key = name.trimmingCharacters(in: .whitespaces).uppercased()
        return templates.first { $0.name.uppercased() == key }
    }

    var lastLoaded: PromptTemplate? {
        loadedId.flatMap { id in templates.first { $0.id == id } }
    }

    private func put(_ template: PromptTemplate) {
        templates = Self.byName(templates.filter { $0.id != template.id } + [template])
    }

    /// Saves the form's chosen groups under a new name, or overwrites the template that has it.
    @discardableResult
    func save(name: String, parts: Set<TemplateSettings.Part>) async throws -> PromptTemplate {
        let trimmed = name.trimmingCharacters(in: .whitespaces)
        var settings = TemplateSettings(form: model.form, parts: parts)
        let saved: PromptTemplate
        if let existing = named(trimmed) {
            settings = settings.keepingVoice(of: existing.settings)
            saved = try await model.updateTemplate(existing.id, name: trimmed, settings: settings)
        } else {
            saved = try await model.createTemplate(name: trimmed, settings: settings)
        }
        put(saved)
        loadedId = saved.id
        return saved
    }

    func rename(_ template: PromptTemplate, to name: String) async throws {
        put(try await model.updateTemplate(template.id, name: name.trimmingCharacters(in: .whitespaces), settings: nil))
    }

    func delete(_ template: PromptTemplate) async throws {
        try await model.deleteTemplate(template.id)
        templates.removeAll { $0.id == template.id }
        if loadedId == template.id {
            loadedId = nil
        }
    }

    /// Fills the form from the template; title, lyrics and score stay.
    func apply(_ template: PromptTemplate) {
        model.form = template.settings.apply(to: model.form)
        model.saveForm()
        loadedId = template.id
    }
}

extension AppModel {
    func templates() async throws -> [PromptTemplate] {
        let (data, _) = try await call("GET", address("api/templates"))
        return try JSONDecoder().decode([PromptTemplate].self, from: data)
    }

    private static func object(_ settings: TemplateSettings) throws -> Any {
        try JSONSerialization.jsonObject(with: JSONEncoder().encode(settings))
    }

    func createTemplate(name: String, settings: TemplateSettings) async throws -> PromptTemplate {
        let (data, _) = try await call(
            "POST", address("api/templates"), json: ["name": name, "settings": try Self.object(settings)],
            accept: [201])
        return try JSONDecoder().decode(PromptTemplate.self, from: data)
    }

    /// A rename sends no settings, so the template keeps them.
    func updateTemplate(_ id: Int64, name: String, settings: TemplateSettings?) async throws -> PromptTemplate {
        var json: [String: Any] = ["name": name]
        if let settings {
            json["settings"] = try Self.object(settings)
        }
        let (data, _) = try await call("PUT", address("api/templates", String(id)), json: json)
        return try JSONDecoder().decode(PromptTemplate.self, from: data)
    }

    func deleteTemplate(_ id: Int64) async throws {
        _ = try await call("DELETE", address("api/templates", String(id)), accept: [204, 404])
    }
}
