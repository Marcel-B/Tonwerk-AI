import Foundation

/// Building blocks for the style prompt, ported from the web page's `styleTags.ts` (keep both in step). YuE2 publishes
/// no tag list: its guide asks for genre, instruments, vocal character, language and tempo, and its examples are short
/// descriptive phrases. The single words come from YuE v1's top_200_tags.json, the phrases from YuE2's own examples.
public enum StyleBlocks {
    public enum Category: String, CaseIterable, Identifiable, Sendable {
        case language, genre, voice, timbre, instruments, mood, arrangement, tempo

        public var id: String { rawValue }

        public var name: String {
            switch self {
            case .language: String(localized: "Sprache")
            case .genre: String(localized: "Genre")
            case .voice: String(localized: "Stimme")
            case .timbre: String(localized: "Klangfarbe")
            case .instruments: String(localized: "Instrumente")
            case .mood: String(localized: "Stimmung")
            case .arrangement: String(localized: "Gestaltung")
            case .tempo: String(localized: "Tempo")
            }
        }

        public var hint: String {
            switch self {
            case .language:
                String(localized: "Die Sprache des Gesangs, eine pro Song; sie steht vorn wie in den YuE2-Beispielen.")
            case .genre: String(localized: "Mehrere lassen sich mischen, zwei bis drei bleiben meist stimmig.")
            case .voice: String(localized: "Wer singt, eine Wahl pro Song.")
            case .timbre: String(localized: "Wie die Stimme klingt, auch die Stimmlage. Ein bis zwei genügen.")
            case .instruments:
                String(localized: "Was die Begleitung spielt; drei bis vier prägen das Arrangement deutlich.")
            case .mood: String(localized: "Die Stimmung färbt Melodie, Harmonie und Gesang.")
            case .arrangement:
                String(localized: "Wendungen aus YuE2s eigenen Beispielen; „half-time beat“ hat sich hier bewährt.")
            case .tempo: String(localized: "Ein Tempo pro Song; es steht am Ende des Stils.")
            }
        }

        /// One of a kind: choosing a tag replaces the category's other one.
        public var exclusive: Bool {
            self == .language || self == .voice || self == .tempo
        }

        public var tags: [String] {
            switch self {
            case .language:
                ["English", "German", "Mandarin", "Cantonese", "Japanese", "Korean", "Spanish", "French"]
            case .genre:
                [
                    "pop", "dance pop", "synthpop", "K-pop", "rock", "pop rock", "indie rock", "alternative rock",
                    "hard rock", "progressive rock", "post-rock", "grunge", "J-rock", "punk", "pop punk", "emo",
                    "metal", "heavy metal", "metalcore", "industrial", "electronic", "electro", "house", "techno",
                    "dubstep", "eurodance", "downtempo", "chillout", "lounge", "ambient", "8-bit", "hip-hop", "rap",
                    "trap", "grime", "R&B", "soul", "funk", "disco", "jazz", "big band", "blues", "bossa nova",
                    "latin pop", "reggaeton", "reggae", "dancehall", "ska", "folk", "indie folk", "country",
                    "bluegrass", "celtic", "singer-songwriter", "gospel", "classical", "opera", "orchestral",
                    "soundtrack", "80s", "new wave", "shoegaze",
                ]
            case .voice:
                ["male vocal", "female vocal", "male and female duet", "choir", "child vocal"]
            case .timbre:
                [
                    "bright vocal", "airy vocal", "warm vocal", "mellow vocal", "rich vocal", "clear vocal",
                    "soft vocal", "sweet vocal", "smooth vocal", "breathy vocal", "whispery vocal", "powerful vocal",
                    "soulful vocal", "husky vocal", "raspy vocal", "gritty vocal", "raw vocal", "gravelly vocal",
                    "gruff vocal", "harsh vocal", "screamy vocal", "deep vocal", "high-pitched vocal",
                    "soprano vocal", "alto vocal", "tenor vocal", "baritone vocal",
                ]
            case .instruments:
                [
                    "piano", "acoustic piano", "electric piano", "Rhodes", "organ", "acoustic guitar",
                    "electric guitar", "bass", "rounded electric bass", "upright bass", "synth bass", "808 bass",
                    "drums", "light drums", "restrained drums", "brushed drums", "drum machine", "percussion",
                    "turntables", "synthesizer", "strings", "violin", "cello", "harp", "flute", "saxophone",
                    "tenor saxophone", "trumpet", "trombone", "brass", "harmonica", "accordion", "ukulele", "banjo",
                    "pedal steel guitar", "glockenspiel", "marimba", "sitar", "tabla", "erhu",
                ]
            case .mood:
                [
                    "uplifting", "inspiring", "happy", "energetic", "powerful", "intense", "hopeful", "romantic",
                    "heartfelt", "emotional", "sensual", "intimate", "nostalgic", "dreamy", "calm", "relaxing",
                    "chill", "peaceful", "atmospheric", "melancholic", "sad", "dark", "dramatic", "epic",
                    "futuristic", "aggressive", "rebellious", "playful", "groovy",
                ]
            case .arrangement:
                [
                    "lyrical memorable melody", "unhurried phrasing", "clear diction", "spacious modern harmony",
                    "half-time beat",
                ]
            case .tempo:
                ["60 BPM", "70 BPM", "80 BPM", "90 BPM", "100 BPM", "110 BPM", "120 BPM", "128 BPM", "140 BPM", "170 BPM"]
            }
        }
    }

    /// One German sentence per voice block, shown as a tooltip on the Mac: the timbre words are singers' and
    /// producers' jargon, and what YuE2 makes of them follows how they are used in the songs it learned from.
    public static func explanation(of tag: String) -> String? {
        explanations[tag.lowercased()]
    }

    private static let explanations: [String: String] = [
        "male vocal": "Ein Mann singt die Hauptstimme.",
        "female vocal": "Eine Frau singt die Hauptstimme.",
        "male and female duet": "Ein Mann und eine Frau singen im Wechsel oder zusammen.",
        "choir": "Viele Stimmen singen gemeinsam, wie ein Chor.",
        "child vocal": "Eine Kinderstimme: hoch, hell und unverbraucht.",
        "bright vocal": "Hell und strahlend, mit viel Glanz in den Höhen.",
        "airy vocal": "Luftig und leicht, mit hörbarem Atem um den Ton.",
        "warm vocal": "Warm und rund, mit Fülle in der Tiefe und weichen Höhen.",
        "mellow vocal": "Sanft und gedämpft, ohne Schärfe, entspannt gesungen.",
        "rich vocal": "Voll und obertonreich, eine Stimme mit viel Substanz.",
        "clear vocal": "Klar und rein, ohne Rauheit oder Hauch, gut verständlich.",
        "soft vocal": "Leise und zart gesungen, mit wenig Druck.",
        "sweet vocal": "Lieblich und süß, freundlich und eher hell.",
        "smooth vocal": "Geschmeidig und glatt, mit fließenden Übergängen zwischen den Tönen.",
        "breathy vocal": "Hauchig: viel Atemluft im Ton, intim und nah.",
        "whispery vocal": "Fast geflüstert, noch mehr Luft als Ton.",
        "powerful vocal": "Kraftvoll und laut, mit viel Stütze und Volumen.",
        "soulful vocal": "Gefühlvoll wie im Soul, mit Verzierungen und viel Ausdruck.",
        "husky vocal": "Belegt und etwas heiser, dunkel und rauchig.",
        "raspy vocal": "Kratzig: ein reibendes Geräusch liegt auf dem Ton, wie bei Rockstimmen.",
        "gritty vocal": "Körnig und angeraut, mit Biss und etwas Verzerrung.",
        "raw vocal": "Roh und ungeschliffen, direkt statt poliert.",
        "gravelly vocal": "Wie Schotter: tief, stark angeraut und knarzend.",
        "gruff vocal": "Barsch und brummig, rau und tief.",
        "harsh vocal": "Hart und schneidend, bewusst unschön, wie im Metal.",
        "screamy vocal": "Geschrien statt gesungen, wie im Metalcore oder Punk.",
        "deep vocal": "Tief und dunkel, aus der unteren Stimmlage.",
        "high-pitched vocal": "Hoch gesungen, aus der oberen Stimmlage.",
        "soprano vocal": "Sopran: die höchste Frauenstimmlage.",
        "alto vocal": "Alt: die tiefe Frauenstimmlage.",
        "tenor vocal": "Tenor: die hohe Männerstimmlage.",
        "baritone vocal": "Bariton: die mittlere Männerstimmlage, zwischen Tenor und Bass.",
    ]

    private static let byTag: [String: Category] = {
        var map: [String: Category] = [:]
        for category in Category.allCases {
            for tag in category.tags {
                map[tag.lowercased()] = category
            }
        }
        return map
    }()

    /// The style's comma-separated items; a sentence without commas stays one item.
    public static func items(_ style: String) -> [String] {
        style.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
    }

    /// Where a style item belongs; any tempo typed by hand counts, so that a chosen tempo replaces it.
    public static func category(of item: String) -> Category? {
        if let category = byTag[item.lowercased()] {
            return category
        }
        return item.range(of: #"^\d{2,3}\s*bpm$"#, options: [.regularExpression, .caseInsensitive]) != nil
            ? .tempo : nil
    }

    public static func has(_ tag: String, in style: String) -> Bool {
        items(style).contains { $0.lowercased() == tag.lowercased() }
    }

    /// Adds the tag or, if the style has it, removes it. Items the list does not know stay where they are.
    public static func toggle(_ tag: String, in style: String) -> String {
        let wanted = tag.lowercased()
        let list = items(style)
        if list.contains(where: { $0.lowercased() == wanted }) {
            return list.filter { $0.lowercased() != wanted }.joined(separator: ", ")
        }
        guard let kind = category(of: tag) else {
            return (list + [tag]).joined(separator: ", ")
        }
        let kept = kind.exclusive ? list.filter { category(of: $0) != kind } : list
        // Before the first item of a later category: language first, tempo last, as in YuE2's examples.
        let order = Category.allCases
        let rank = order.firstIndex(of: kind) ?? 0
        let later = kept.firstIndex { item in
            guard let other = category(of: item) else { return false }
            return (order.firstIndex(of: other) ?? 0) > rank
        }
        var result = kept
        result.insert(tag, at: kind == .language ? 0 : later ?? kept.count)
        return result.joined(separator: ", ")
    }

    public static func count(_ category: Category, in style: String) -> Int {
        items(style).filter { self.category(of: $0) == category }.count
    }
}

/// Section tags for the lyrics, from the web page's `lyricsTags.ts`. YuE2 passes the lyrics to the model as plain
/// text, so a tag counts by what the model learned: the sections its own scores name, plus the four YuE v1 documented.
public enum SectionTags {
    public static let known = ["[Intro]", "[Verse]", "[Pre-Chorus]", "[Chorus]", "[Bridge]", "[Interlude]", "[Outro]"]
    /// Common in lyric sheets, but neither YuE documents them: they reach the model as words.
    public static let other = ["[Hook]", "[Solo]", "[Break]", "[Build-Up]", "[Drop]"]

    /// Puts the tag on a line of its own at the end: after a sung line with an empty line before it, as sections are
    /// written; tags in a row (an instrumental's structure) stay together.
    public static func append(_ tag: String, to lyrics: String) -> String {
        var before = lyrics
        if before.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            before = ""
        } else {
            while before.hasSuffix("\n") {
                before.removeLast()
            }
            let previous = before.components(separatedBy: "\n").last ?? ""
            let isTag = previous.range(of: #"^\s*\[[^\]]+\]\s*$"#, options: .regularExpression) != nil
            before += isTag ? "\n" : "\n\n"
        }
        return before + tag + "\n"
    }
}
