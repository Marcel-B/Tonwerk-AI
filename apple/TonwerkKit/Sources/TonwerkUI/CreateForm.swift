import Foundation
import TonwerkKit

/// The create form, as the web page's `form.ts` keeps it, minus the score and the advanced parameters for now.
/// `seed` stays text so an empty field means "random".
public struct CreateForm: Codable, Equatable, Sendable {
    public enum Planning: String, Codable, CaseIterable, Sendable {
        /// A score with melody and chords first, then the song (YuE2's default).
        case full
        case melody
        case off
    }

    public var title = ""
    public var style = ""
    public var lyrics = ""
    /// Keywords for a lyrics draft; not sent with the song.
    public var idea = ""
    public var german = true
    /// The lyrics model; empty for the server's default, so a changed default reaches the form.
    public var lyricsModel = ""
    /// The draft this form asked for and still waits for, kept so it lands after a restart too.
    public var draftId = ""
    public var instrumental = false
    public var full = false
    public var batch = 1
    public var planning = Planning.full
    public var seed = ""

    public init() {}

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
        return nil
    }

    /// What `POST /api/generate` takes.
    public var request: Components.Schemas.GenerateRequest {
        let title = title.trimmingCharacters(in: .whitespacesAndNewlines)
        return .init(
            style: style.trimmingCharacters(in: .whitespacesAndNewlines),
            lyrics: lyrics,
            title: title.isEmpty ? nil : title,
            batch: Int32(min(max(batch, 1), 10)),
            quality: full ? "full" : "draft",
            cot: planning.rawValue,
            seed: Int64(seed.trimmingCharacters(in: .whitespaces)),
            instrumental: instrumental)
    }

    /// A song's `request.json` laid over the form, like the web page's "use everything" without the score.
    public mutating func take(_ song: Components.Schemas.SongRequest) {
        title = song.title
        style = song.style
        lyrics = song.lyrics
        instrumental = song.instrumental ?? false
        full = song.quality == "full"
        planning = song.cot.flatMap(Planning.init(rawValue:)) ?? .full
        seed = song.seed.map(String.init) ?? ""
    }
}
