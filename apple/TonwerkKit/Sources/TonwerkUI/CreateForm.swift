import Foundation
import TonwerkKit

/// The create form, as the web page's `form.ts` keeps it. `seed` stays text so an empty field means "random".
public struct CreateForm: Codable, Equatable, Sendable {
    public enum Planning: String, Codable, CaseIterable, Sendable {
        /// A score with melody and chords first, then the song (YuE2's default).
        case full
        case melody
        case off
    }

    /// One of the model's sampling settings, every value filled in.
    public struct Sampling: Codable, Equatable, Sendable {
        public var temperature: Double
        public var topP: Double
        public var topK: Int
        public var repetitionPenalty: Double
        public var penaltyWindow: Int

        /// YuE2's GenerationConfig: the checkpoint's own values for the score.
        public static let abcDefaults = Sampling(
            temperature: 0.7, topP: 0.9, topK: 30, repetitionPenalty: 1.005, penaltyWindow: 100)
        /// And for the song tokens.
        public static let semanticDefaults = Sampling(
            temperature: 1.0, topP: 0.95, topK: 100, repetitionPenalty: 1.2, penaltyWindow: 50)

        /// Only what differs from the model's values, so unchanged songs still share token batches with others.
        func overrides(from defaults: Sampling) -> Components.Schemas.SamplingOverrides? {
            guard self != defaults else { return nil }
            return .init(
                temperature: temperature == defaults.temperature ? nil : temperature,
                topP: topP == defaults.topP ? nil : topP,
                topK: topK == defaults.topK ? nil : Int32(topK),
                repetitionPenalty: repetitionPenalty == defaults.repetitionPenalty ? nil : repetitionPenalty,
                penaltyWindow: penaltyWindow == defaults.penaltyWindow ? nil : Int32(penaltyWindow))
        }

        func with(_ overrides: Components.Schemas.SamplingOverrides?) -> Sampling {
            var result = self
            result.temperature = overrides?.temperature ?? temperature
            result.topP = overrides?.topP ?? topP
            result.topK = overrides?.topK.map(Int.init) ?? topK
            result.repetitionPenalty = overrides?.repetitionPenalty ?? repetitionPenalty
            result.penaltyWindow = overrides?.penaltyWindow.map(Int.init) ?? penaltyWindow
            return result
        }
    }

    public static let tokensPerSecond = 25
    /// The model's default of 9000 tokens; longer songs need the worker extension.
    public static let maxSongSeconds = 360
    public static let lengthChoices = [30, 60, 90, 120, 150, 180, 240, 300, maxSongSeconds, 420, 480, 540, 600]
    public static let defaultDraftSteps = 8
    public static let defaultFullSteps = 32

    public var title = ""
    public var style = ""
    public var lyrics = ""
    /// Keywords for a lyrics draft; not sent with the song.
    public var idea = ""
    public var german = true
    /// The lyrics model; empty for the server's default, so a changed default reaches the form.
    public var lyricsModel = ""
    /// The Mac whose lyrics model drafts; empty lets the server choose.
    public var lyricsMachine = ""
    /// How much a thinking model thinks before a draft (`LyricsReasoning`'s raw value); empty leaves it to LM Studio.
    public var lyricsReasoning = ""
    /// The draft this form asked for and still waits for, kept so it lands after a restart too.
    public var draftId = ""
    public var instrumental = false
    public var full = false
    public var batch = 1
    public var seed = ""

    /// Whether the score goes out with the song. Off keeps it in the form unsent, so "use everything" can bring a
    /// song's own score along and still make that very song again.
    public var useAbc = true
    public var abc = ""

    /// Whether the advanced parameters go out; off sends the worker's defaults and keeps the values in the form.
    public var useAdvanced = true
    public var planning = Planning.full
    public var draftSteps = CreateForm.defaultDraftSteps
    public var fullSteps = CreateForm.defaultFullSteps
    /// "", "gpu" or "gpu+ane"; empty lets the worker choose.
    public var engines = ""
    /// Longest song in seconds; the API wants tokens.
    public var maxSeconds = CreateForm.maxSongSeconds
    public var abcSampling = Sampling.abcDefaults
    public var semanticSampling = Sampling.semanticDefaults
    /// A LoRA from the server's folder; empty for none.
    public var lora = ""
    public var loraStrength = 1.0

    public init() {}

    enum CodingKeys: String, CodingKey {
        case title, style, lyrics, idea, german, lyricsModel, lyricsMachine, lyricsReasoning, draftId, instrumental, full, batch, seed
        case useAbc, abc, useAdvanced, planning, draftSteps, fullSteps, engines, maxSeconds
        case abcSampling, semanticSampling, lora, loraStrength
    }

    /// Field by field, so a form kept before a value was added (or one that no longer reads) keeps the rest.
    public init(from decoder: any Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        func value<T: Decodable>(_ key: CodingKeys, _ fallback: T) -> T {
            ((try? container.decodeIfPresent(T.self, forKey: key)) ?? nil) ?? fallback
        }
        let empty = CreateForm()
        title = value(.title, empty.title)
        style = value(.style, empty.style)
        lyrics = value(.lyrics, empty.lyrics)
        idea = value(.idea, empty.idea)
        german = value(.german, empty.german)
        lyricsModel = value(.lyricsModel, empty.lyricsModel)
        lyricsMachine = value(.lyricsMachine, empty.lyricsMachine)
        lyricsReasoning = value(.lyricsReasoning, empty.lyricsReasoning)
        draftId = value(.draftId, empty.draftId)
        instrumental = value(.instrumental, empty.instrumental)
        full = value(.full, empty.full)
        batch = value(.batch, empty.batch)
        seed = value(.seed, empty.seed)
        useAbc = value(.useAbc, empty.useAbc)
        abc = value(.abc, empty.abc)
        useAdvanced = value(.useAdvanced, empty.useAdvanced)
        planning = value(.planning, empty.planning)
        draftSteps = value(.draftSteps, empty.draftSteps)
        fullSteps = value(.fullSteps, empty.fullSteps)
        engines = value(.engines, empty.engines)
        maxSeconds = value(.maxSeconds, empty.maxSeconds)
        abcSampling = value(.abcSampling, empty.abcSampling)
        semanticSampling = value(.semanticSampling, empty.semanticSampling)
        lora = value(.lora, empty.lora)
        loraStrength = value(.loraStrength, empty.loraStrength)
    }

    private static let key = "createForm"

    static func load(from defaults: UserDefaults) -> CreateForm {
        guard let data = defaults.data(forKey: key),
              let form = try? JSONDecoder().decode(CreateForm.self, from: data)
        else { return CreateForm() }
        return form
    }

    func save(to defaults: UserDefaults) {
        if let data = try? JSONEncoder().encode(self) {
            defaults.set(data, forKey: Self.key)
        }
    }

    /// A score that goes out with the song. It and the planning chosen for it belong together and outlast a reset.
    public var usesScore: Bool {
        useAbc && !abc.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    /// The advanced parameters back at their defaults; the score is not a parameter and stays, with its planning.
    public func resettingAdvanced() -> CreateForm {
        let defaults = CreateForm()
        var form = self
        form.planning = usesScore ? planning : .full
        form.draftSteps = defaults.draftSteps
        form.fullSteps = defaults.fullSteps
        form.engines = defaults.engines
        form.maxSeconds = defaults.maxSeconds
        form.abcSampling = defaults.abcSampling
        form.semanticSampling = defaults.semanticSampling
        form.lora = defaults.lora
        form.loraStrength = defaults.loraStrength
        return form
    }

    /// Whether an advanced parameter differs from its default; the section is kept across starts, so a forgotten
    /// value must stay visible. A score (with its planning) is shown on its own.
    public var advancedChanged: Bool {
        var compared = self
        compared.planning = usesScore ? .full : planning
        let reset = compared.resettingAdvanced()
        return compared != reset
    }

    /// Why the song cannot be asked for yet, or nil.
    public var problem: String? {
        if style.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            return String(localized: "Der Stil fehlt.")
        }
        if !instrumental && lyrics.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            return String(localized: "Der Songtext fehlt (oder „Instrumental“ einschalten).")
        }
        if !seed.isEmpty && Int64(seed.trimmingCharacters(in: .whitespaces)) == nil {
            return String(localized: "Der Seed ist keine ganze Zahl.")
        }
        if usesScore && planning == .off && !instrumental {
            return String(localized: "Eine eigene Partitur braucht eine Planung („Melodie und Akkorde“ oder „Nur Melodie“).")
        }
        return nil
    }

    /// What `POST /api/generate` takes.
    public var request: Components.Schemas.GenerateRequest {
        // Switched off, the advanced parameters go out as their defaults; the score's planning stays with a score.
        let form = useAdvanced ? self : resettingAdvanced()
        let title = form.title.trimmingCharacters(in: .whitespacesAndNewlines)
        return .init(
            style: form.style.trimmingCharacters(in: .whitespacesAndNewlines),
            lyrics: form.lyrics,
            title: title.isEmpty ? nil : title,
            batch: Int32(min(max(form.batch, 1), 10)),
            quality: form.full ? "full" : "draft",
            cot: form.planning.rawValue,
            seed: Int64(form.seed.trimmingCharacters(in: .whitespaces)),
            instrumental: form.instrumental,
            engines: form.engines.isEmpty ? nil : form.engines,
            draftSteps: form.full ? nil : Int32(form.draftSteps),
            maxTokens: form.maxSeconds < Self.maxSongSeconds ? Int32(form.maxSeconds * Self.tokensPerSecond) : nil,
            abc: form.usesScore ? form.abc.trimmingCharacters(in: .whitespacesAndNewlines) : nil,
            fullSteps: form.full && form.fullSteps != Self.defaultFullSteps ? Int32(form.fullSteps) : nil,
            abcSampling: form.abcSampling.overrides(from: .abcDefaults),
            semanticSampling: form.semanticSampling.overrides(from: .semanticDefaults),
            lora: form.lora.isEmpty ? nil : form.lora,
            loraStrength: form.lora.isEmpty || form.loraStrength == 1 ? nil : form.loraStrength)
    }

    /// The form set to what a song was made with, like the web page's "use everything": its seed included, one song
    /// (the seed is that song's). The score is the one the song was asked with; without one, the score YuE2 wrote for
    /// it (`ownScore`) comes along switched off, so "generate" still makes the same song.
    public mutating func take(_ song: Components.Schemas.SongRequest, ownScore: String? = nil) {
        let defaults = CreateForm()
        title = song.title
        style = song.style
        lyrics = song.lyrics
        instrumental = song.instrumental ?? false
        full = song.quality == "full"
        batch = 1
        planning = song.cot.flatMap(Planning.init(rawValue:)) ?? .full
        seed = song.seed.map(String.init) ?? ""
        draftSteps = song.draftSteps.map(Int.init) ?? defaults.draftSteps
        engines = song.engines ?? ""
        // The form offers fixed lengths; the next one up still fits the song.
        let seconds = song.maxTokens.map { Double($0) / Double(Self.tokensPerSecond) } ?? Double(Self.maxSongSeconds)
        maxSeconds = Self.lengthChoices.first { Double($0) >= seconds } ?? Self.lengthChoices.last ?? Self.maxSongSeconds
        abc = song.abc ?? ownScore ?? ""
        useAbc = song.abc != nil
        useAdvanced = true
        fullSteps = song.fullSteps.map(Int.init) ?? defaults.fullSteps
        abcSampling = Sampling.abcDefaults.with(song.abcSampling)
        semanticSampling = Sampling.semanticDefaults.with(song.semanticSampling)
        lora = song.lora ?? ""
        loraStrength = song.loraStrength ?? defaults.loraStrength
    }

    /// A score (a song's score.abc, or one from a MIDI file) as the form's score and nothing else. Planning follows
    /// the score: chords are kept when it has them.
    public mutating func useScore(_ score: String) {
        abc = score
        planning = AbcScore.planning(for: score)
        useAbc = true
    }
}
