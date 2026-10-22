import Foundation
import SwiftUI

/// Building blocks for a painted cover's description, as the web page's `coverBlocks.ts` (same list, same functions,
/// held to the same tests; change both). Each block is one English sentence the image models read well; a block is
/// in the description when its sentence is, so typing it by hand counts and deleting it unchooses it.
public enum CoverBlocks {
    public enum Group: String, CaseIterable, Identifiable, Sendable {
        case medium, era, light, framing

        public var id: String { rawValue }

        var name: String {
            switch self {
            case .medium: "Coverart"
            case .era: "Ära"
            case .light: "Licht und Farbe"
            case .framing: "Bildaufbau"
            }
        }

        var hint: String {
            switch self {
            case .medium: "Wie das Bild gemacht ist. Eine Art zur Zeit, eine neue ersetzt die alte."
            case .era: "Der Look einer Zeit, wie auf Platten von damals."
            case .light: "Stimmung über Licht und Farben; zwei passen meist zusammen."
            case .framing: "Was im Bild wo steht."
            }
        }
    }

    public struct Block: Sendable, Equatable {
        public let key: String
        public let group: Group
        public let name: String
        public let text: String
    }

    /// In the order the description lists them. A medium is one of a kind: choosing one replaces the other.
    public static let all: [Block] = [
        Block(key: "photo", group: .medium, name: "Fotografie",
              text: "Shot as a photograph, natural film grain, shallow depth of field."),
        Block(key: "oil", group: .medium, name: "Ölgemälde",
              text: "Painted in oil on canvas, visible brush strokes."),
        Block(key: "watercolour", group: .medium, name: "Aquarell",
              text: "A soft watercolour painting, bleeding washes on paper."),
        Block(key: "pencil", group: .medium, name: "Bleistiftskizze",
              text: "A graphite pencil sketch on textured paper, visible strokes and cross-hatching, monochrome."),
        Block(key: "charcoal", group: .medium, name: "Kohlezeichnung",
              text: "A charcoal drawing, smudged deep blacks and soft grey tones."),
        Block(key: "ink", group: .medium, name: "Tuschezeichnung",
              text: "An ink drawing with bold brush lines and hatching, black on white paper."),
        Block(key: "lineArt", group: .medium, name: "Linienzeichnung",
              text: "Clean line art, thin even outlines on a flat background."),
        Block(key: "screenPrint", group: .medium, name: "Siebdruck",
              text: "A screen print in two or three flat inks, slightly misregistered, on grainy paper."),
        Block(key: "collage", group: .medium, name: "Collage",
              text: "A collage of cut paper and old photographs with torn edges."),
        Block(key: "render", group: .medium, name: "3D-Render",
              text: "A 3D render with soft studio lighting and smooth materials."),
        Block(key: "anime", group: .medium, name: "Anime",
              text: "A still from a hand-drawn anime film."),
        Block(key: "bauhaus", group: .medium, name: "Bauhaus",
              text: "A minimal Bauhaus composition of simple geometric shapes in primary colours."),
        Block(key: "instant", group: .medium, name: "Sofortbild",
              text: "A faded instant photo with soft colours and a slight vignette."),
        Block(key: "jazz60s", group: .era, name: "60er Jazz-Platte",
              text: "Like a 1960s jazz record sleeve, bold flat colour fields."),
        Block(key: "vinyl70s", group: .era, name: "70er Rock-Platte",
              text: "With the look of a 1970s rock record sleeve, warm faded colours."),
        Block(key: "synthwave80s", group: .era, name: "80er Synthwave",
              text: "1980s synthwave look, a neon grid, chrome and a setting sun."),
        Block(key: "rave90s", group: .era, name: "90er Rave-Flyer",
              text: "Like a 1990s rave flyer, acid colours and blurred motion."),
        Block(key: "y2k", group: .era, name: "Y2K",
              text: "Y2K aesthetic, glossy chrome and iridescent plastic."),
        Block(key: "cassette", group: .era, name: "Lo-Fi-Kassette",
              text: "Lo-fi cassette aesthetic, washed-out colours, dust and tape grain."),
        Block(key: "neon", group: .light, name: "Neon bei Nacht",
              text: "Lit by neon signs at night, wet reflections."),
        Block(key: "goldenHour", group: .light, name: "Goldene Stunde",
              text: "Warm golden-hour light, long shadows."),
        Block(key: "blackWhite", group: .light, name: "Schwarzweiß",
              text: "Black and white, strong contrast."),
        Block(key: "pastel", group: .light, name: "Pastell",
              text: "Soft pastel colours."),
        Block(key: "dramatic", group: .light, name: "Kontrastreich",
              text: "High contrast, deep shadows, dramatic light."),
        Block(key: "duotone", group: .light, name: "Duoton",
              text: "A duotone of two strong colours."),
        Block(key: "mist", group: .light, name: "Nebel",
              text: "A misty, hazy atmosphere."),
        Block(key: "portrait", group: .framing, name: "Porträt",
              text: "A close portrait of a single figure in the centre."),
        Block(key: "landscape", group: .framing, name: "Weite Landschaft",
              text: "A wide landscape with a small figure in it."),
        Block(key: "closeUp", group: .framing, name: "Nahaufnahme",
              text: "An extreme close-up of one object."),
        Block(key: "symmetric", group: .framing, name: "Symmetrisch",
              text: "A strictly symmetrical composition."),
        Block(key: "space", group: .framing, name: "Viel Leerraum",
              text: "Lots of empty space around a small central motif."),
        Block(key: "above", group: .framing, name: "Von oben",
              text: "Seen from directly above."),
    ]

    /// Words looked for in the song's style (lower-cased, whole words) and the blocks they suggest.
    static let forStyle: [(String, [String])] = [
        ("synthwave", ["synthwave80s", "neon"]),
        ("synthpop", ["synthwave80s", "pastel"]),
        ("8-bit", ["synthwave80s", "duotone"]),
        ("techno", ["neon", "duotone", "symmetric"]),
        ("house", ["neon", "duotone"]),
        ("industrial", ["blackWhite", "dramatic"]),
        ("electro", ["neon", "duotone"]),
        ("electronic", ["neon", "render"]),
        ("dubstep", ["neon", "dramatic"]),
        ("trap", ["dramatic", "photo"]),
        ("eurodance", ["rave90s", "y2k"]),
        ("dance pop", ["y2k", "pastel"]),
        ("disco", ["rave90s", "neon"]),
        ("funk", ["vinyl70s", "screenPrint"]),
        ("lo-fi", ["cassette", "pastel", "anime"]),
        ("lofi", ["cassette", "pastel", "anime"]),
        ("chillout", ["cassette", "watercolour"]),
        ("downtempo", ["cassette", "mist"]),
        ("lounge", ["jazz60s", "goldenHour"]),
        ("ambient", ["watercolour", "mist", "space"]),
        ("jazz", ["jazz60s", "screenPrint"]),
        ("big band", ["jazz60s"]),
        ("bossa nova", ["jazz60s", "goldenHour"]),
        ("soul", ["jazz60s", "instant"]),
        ("blues", ["vinyl70s", "blackWhite"]),
        ("r&b", ["neon", "portrait"]),
        ("hip-hop", ["photo", "dramatic"]),
        ("rap", ["photo", "dramatic"]),
        ("grime", ["photo", "blackWhite"]),
        ("punk", ["screenPrint", "collage"]),
        ("grunge", ["collage", "blackWhite"]),
        ("metal", ["charcoal", "dramatic"]),
        ("rock", ["vinyl70s", "photo"]),
        ("progressive", ["oil", "landscape"]),
        ("post-rock", ["landscape", "mist"]),
        ("folk", ["pencil", "goldenHour"]),
        ("country", ["goldenHour", "instant"]),
        ("bluegrass", ["pencil", "goldenHour"]),
        ("celtic", ["ink", "mist"]),
        ("k-pop", ["pastel", "y2k"]),
        ("j-rock", ["anime", "dramatic"]),
        ("reggae", ["screenPrint", "goldenHour"]),
        ("epic", ["oil", "landscape", "dramatic"]),
        ("futuristic", ["render", "neon"]),
        ("dark", ["charcoal", "dramatic"]),
        ("melancholic", ["pencil", "mist"]),
        ("dreamy", ["watercolour", "pastel"]),
        ("acoustic", ["pencil", "goldenHour"]),
        ("piano", ["ink", "space"]),
        ("pop", ["pastel", "portrait"]),
    ]

    static func block(_ key: String) -> Block? { all.first { $0.key == key } }

    /// The blocks whose sentence the description holds, in the list's order.
    public static func blocks(in prompt: String) -> [String] {
        all.filter { prompt.contains($0.text) }.map(\.key)
    }

    /// Adds the block's sentence at the end or, if the description has it, takes it out; another medium goes.
    public static func toggle(_ key: String, in prompt: String) -> String {
        guard let block = block(key) else { return prompt }
        if prompt.contains(block.text) {
            return tidy(prompt.replacingOccurrences(of: block.text, with: ""))
        }
        var kept = prompt
        if block.group == .medium {
            for other in all where other.group == .medium {
                kept = kept.replacingOccurrences(of: other.text, with: "")
            }
        }
        return tidy("\(tidy(kept)) \(block.text)")
    }

    static func tidy(_ text: String) -> String {
        text.replacingOccurrences(of: "[ \\t]{2,}", with: " ", options: .regularExpression)
            .replacingOccurrences(of: " +\n", with: "\n", options: .regularExpression)
            .replacingOccurrences(of: "\n +", with: "\n", options: .regularExpression)
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }

    /// Blocks for the song's style, at most `limit`, those the style hints at first.
    public static func forSong(style: String, limit: Int = 8) -> [String] {
        let lower = " " + style.lowercased().replacingOccurrences(of: "[,;/]", with: " ", options: .regularExpression) + " "
        var found: [String] = []
        for (word, keys) in forStyle {
            // A whole word: "rap" must not come from "trap", "rock" not from "post-rock".
            let pattern = "(^|\\s)" + NSRegularExpression.escapedPattern(for: word) + "(?=\\s)"
            guard lower.range(of: pattern, options: .regularExpression) != nil else { continue }
            for key in keys where !found.contains(key) {
                found.append(key)
            }
        }
        return Array(found.prefix(limit))
    }

    /// The sentences of the chosen blocks, in the list's order.
    static func sentences(_ keys: [String]) -> [String] {
        all.filter { keys.contains($0.key) }.map(\.text)
    }

    /// Whether one of the blocks names a medium, so that the suggestion need not say painting.
    static func hasMedium(_ keys: [String]) -> Bool {
        keys.contains { block($0)?.group == .medium }
    }
}

/// The cover blocks under the description, as the web page's `CoverBlocks.vue`. A tap asks the sheet to put the
/// block's sentence in or take it out, since only the sheet knows whether the description is still its suggestion.
struct CoverBlocksView: View {
    let chosen: [String]
    let songStyle: String
    let toggle: (String) -> Void
    @State private var choice: Choice?

    private enum Choice: Hashable {
        case song
        case group(CoverBlocks.Group)
    }

    var body: some View {
        let fitting = CoverBlocks.forSong(style: songStyle)
        let current = choice ?? (fitting.isEmpty ? .group(.medium) : .song)
        VStack(alignment: .leading, spacing: 10) {
            Picker("Gruppe", selection: Binding(get: { current }, set: { choice = $0 })) {
                if !fitting.isEmpty {
                    Text("Zum Song").tag(Choice.song)
                }
                ForEach(CoverBlocks.Group.allCases) { group in
                    let count = CoverBlocks.all.filter { $0.group == group && chosen.contains($0.key) }.count
                    Text(count == 0 ? group.name : "\(group.name) (\(count))").tag(Choice.group(group))
                }
            }
            switch current {
            case .song:
                Text("Passend zum Stil des Songs vorgeschlagen.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                chips(fitting)
            case .group(let group):
                Text(group.hint)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                chips(CoverBlocks.all.filter { $0.group == group }.map(\.key))
            }
        }
    }

    private func chips(_ keys: [String]) -> some View {
        FlowLayout {
            ForEach(keys, id: \.self) { key in
                Chip(title: CoverBlocks.block(key)?.name ?? key, chosen: chosen.contains(key)) {
                    toggle(key)
                }
            }
        }
    }
}
