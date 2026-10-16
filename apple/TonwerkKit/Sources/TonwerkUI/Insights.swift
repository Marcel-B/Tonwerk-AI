import Foundation
import TonwerkKit

/// What the rated songs were made with against their stars (`GET api/insights`), as the web page's `insights.ts`.
enum Insights {
    /// How many proven blocks the create form offers.
    static let provenCount = 12
    /// The style blocks are many: the best and, apart, the weakest; the other groups are short enough to show whole.
    static let shown = 10
    static let weakest = 5

    /// The style blocks of songs rated above the average, best first: what the style blocks offer as "Bewährt".
    /// Blocks written only once say nothing yet and are left out by the server.
    static func proven(_ report: Components.Schemas.InsightReport?) -> [String] {
        guard let report, let style = report.groups.first(where: { $0.key == "style" }) else { return [] }
        return Array(style.entries.filter { $0.score > report.average }.prefix(provenCount).map(\.value))
    }

    /// A group's best entries and, for the long style list, its weakest apart.
    static func split(_ group: Components.Schemas.InsightGroup)
        -> (best: [Components.Schemas.InsightEntry], weak: [Components.Schemas.InsightEntry])
    {
        guard group.key == "style", group.entries.count > shown + weakest else { return (group.entries, []) }
        return (Array(group.entries.prefix(shown)), Array(group.entries.suffix(weakest)))
    }

    static func groupName(_ key: String) -> String {
        switch key {
        case "style": String(localized: "Stil-Bausteine")
        case "engine": String(localized: "Modell")
        case "quality": String(localized: "Qualität")
        case "lora": String(localized: "LoRA")
        case "steps": String(localized: "Schritte (volle Qualität)")
        case "length": String(localized: "Länge")
        case "instrumental": String(localized: "Instrumental")
        default: key
        }
    }

    static func valueName(group: String, _ value: String) -> String {
        switch group {
        case "engine": value == "ace" ? "ACE-Step" : "YuE2"
        case "quality": value == "full" ? String(localized: "Volle Qualität") : String(localized: "Entwurf")
        case "lora": value.isEmpty ? String(localized: "ohne LoRA") : value
        case "instrumental": value == "yes" ? String(localized: "ja") : String(localized: "nein")
        case "length":
            switch value {
            case "<2": String(localized: "unter 2 Minuten")
            case "2-3": String(localized: "2 bis 3 Minuten")
            case "3-4": String(localized: "3 bis 4 Minuten")
            case "4+": String(localized: "über 4 Minuten")
            default: value
            }
        default: value
        }
    }

    enum Tone {
        case good, plain, weak
    }

    /// Better or worse than the average song, by more than a little.
    static func tone(_ entry: Components.Schemas.InsightEntry, average: Double) -> Tone {
        entry.score > average + 0.15 ? .good : entry.score < average - 0.15 ? .weak : .plain
    }
}

extension AppModel {
    /// Asked again whenever the page or the style blocks are shown, since songs are rated elsewhere meanwhile; an
    /// older server without the endpoint leaves it nil, and the "Bewährt" group stays away.
    func loadInsights() async throws {
        let (data, _) = try await call("GET", address("api/insights"))
        insights = try decode(Components.Schemas.InsightReport.self, data)
    }

    var provenStyleBlocks: [String] { Insights.proven(insights) }
}
