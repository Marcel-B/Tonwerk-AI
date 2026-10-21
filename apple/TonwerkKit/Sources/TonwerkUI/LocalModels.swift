import TonwerkKit

extension Components.Schemas.LyricsModel {
    /// What the server suggests a local model for (`Lyrics:Roles`), in words; nil for the others.
    var roleNote: String? {
        switch role {
        case "quality": String(localized: "Qualität, finale Texte")
        case "ideas": String(localized: "schnell: Ideen, Reime, Varianten")
        default: nil
        }
    }

    /// The memory it needs and the listing Mac lacks, in words.
    var memoryNote: String? {
        needsGb.map { String(localized: "braucht \(Int($0)) GB") }
    }
}
