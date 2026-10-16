import Foundation
import TonwerkKit

/// One song of the library as the screens and the player need it: the server's `SongInfo` with its run's title and
/// date, and the addresses of its stream and cover.
public struct Song: Identifiable, Hashable, Sendable {
    /// `run/songN`, the song's id everywhere (playlists, links, the web page's `#/songs/…`).
    public let id: String
    public let run: String
    /// The folder's `songN`.
    public let name: String
    public let title: String
    /// The title the worker gave the run, when it was renamed since.
    public let originalTitle: String
    /// Which of the run's songs, shown only when the run has several.
    public let number: Int?
    public let style: String
    public let lyrics: String
    public let createdAt: Date?
    public let seconds: Double?
    public let rating: Int?
    public let note: String?
    public let coverUpdatedAt: Date?
    public let hasAudio: Bool
    /// Whether YuE2 wrote a score.abc for it (not with planning off).
    public let hasScore: Bool
    public let seed: Int64?
    public let quality: String?
    /// `ace` for ACE-Step's songs, nil for YuE2's.
    public let engine: String?
    /// Heard tempo and key (`SongTraits`), once the analyzer had the song.
    public let tempo: Double?
    public let key: String?
    /// What the song's folder takes on disk.
    public let bytes: Int64
    /// The song sung again with other voices.
    public let versions: [Version]
    /// Set on the copy the player plays when it plays a version: the id stays the song's, as on the web page, so
    /// rating and playlists keep meaning the song.
    public let version: Version?

    /// A version of the song with another voice (`VersionState`).
    public struct Version: Identifiable, Hashable, Sendable {
        public let id: String
        public let voice: String
        public let semitones: Int
        /// `done`, `failed`, `cancelled`, or a stage while it is made.
        public let stage: String

        public init(id: String, voice: String, semitones: Int = 0, stage: String = "done") {
            self.id = id
            self.voice = voice
            self.semitones = semitones
            self.stage = stage
        }

        public var playable: Bool { stage == "done" }
    }

    public init(
        run: String, name: String, title: String, number: Int?, style: String = "", lyrics: String = "",
        createdAt: Date? = nil, seconds: Double? = nil, rating: Int? = nil, note: String? = nil,
        coverUpdatedAt: Date? = nil, hasAudio: Bool = true, hasScore: Bool = false, originalTitle: String? = nil,
        seed: Int64? = nil, quality: String? = nil, engine: String? = nil, tempo: Double? = nil, key: String? = nil,
        bytes: Int64 = 0, versions: [Version] = [], version: Version? = nil
    ) {
        id = "\(run)/\(name)"
        self.run = run
        self.name = name
        self.title = title
        self.number = number
        self.style = style
        self.lyrics = lyrics
        self.createdAt = createdAt
        self.seconds = seconds
        self.rating = rating
        self.note = note
        self.coverUpdatedAt = coverUpdatedAt
        self.hasAudio = hasAudio
        self.hasScore = hasScore
        self.originalTitle = originalTitle ?? title
        self.seed = seed
        self.quality = quality
        self.engine = engine
        self.tempo = tempo
        self.key = key
        self.bytes = bytes
        self.versions = versions
        self.version = version
    }

    /// A copy with some fields changed; the rest stays as it was.
    public func with(
        title: String? = nil, rating: Int?? = nil, note: String?? = nil, version: Version?? = nil
    ) -> Song {
        Song(
            run: run, name: name, title: title ?? self.title, number: number, style: style, lyrics: lyrics,
            createdAt: createdAt, seconds: seconds, rating: rating ?? self.rating, note: note ?? self.note,
            coverUpdatedAt: coverUpdatedAt, hasAudio: hasAudio, hasScore: hasScore, originalTitle: originalTitle,
            seed: seed, quality: quality, engine: engine, tempo: tempo, key: key, bytes: bytes, versions: versions,
            version: version ?? self.version)
    }

    /// The run's title, with the song's number when the run made several, and the voice when a version plays.
    public var displayTitle: String {
        let base = number.map { "\(title) · \($0)" } ?? title
        return version.map { "\(base) (\($0.voice))" } ?? base
    }

    /// The 192 kbit/s AAC copy the web player streams too (the FLAC is too large for the home upload); a version's
    /// own copy when the player plays one.
    public func streamURL(on server: URL) -> URL {
        if let version {
            return server.appending(path: "api/songs/\(run)/\(name)/versions/\(version.id)/stream")
        }
        return server.appending(path: "api/songs/\(run)/\(name)/stream")
    }

    /// Only songs with a cover of their own have one; the others get a drawn one (``CoverView``). The date makes a new
    /// cover a new address, as on the web page.
    public func coverURL(on server: URL) -> URL? {
        guard let coverUpdatedAt else { return nil }
        var url = server.appending(path: "api/songs/\(run)/\(name)/cover")
        url.append(queryItems: [URLQueryItem(name: "v", value: String(Int(coverUpdatedAt.timeIntervalSince1970)))])
        return url
    }

    /// The songs of the server's library, newest run first as the server sends them.
    public static func all(in runs: [Components.Schemas.RunInfo]) -> [Song] {
        runs.flatMap { run in
            run.songs.compactMap { info -> Song? in
                // The id is `run/songN`; anything else would not address the song's files.
                guard let slash = info.id.lastIndex(of: "/") else { return nil }
                return Song(
                    run: String(info.id[..<slash]),
                    name: String(info.id[info.id.index(after: slash)...]),
                    title: run.title,
                    number: run.songs.count > 1 ? Int(info.index) : nil,
                    style: run.style,
                    lyrics: run.lyrics,
                    createdAt: run.createdAt,
                    seconds: info.seconds,
                    rating: info.rating.map { Int($0) },
                    note: info.note,
                    coverUpdatedAt: info.coverUpdatedAt,
                    hasAudio: info.hasAudio,
                    hasScore: info.hasScore,
                    originalTitle: run.originalTitle,
                    seed: info.seed,
                    quality: info.quality,
                    engine: info.engine,
                    tempo: info.traits?.tempo,
                    key: info.traits?.key,
                    bytes: info.bytes,
                    versions: info.versions.map {
                        Version(
                            id: $0.id, voice: $0.voiceLabel, semitones: Int($0.semiToneShift ?? 0),
                            stage: $0.stage ?? "done")
                    })
            }
        }
    }

    /// Where the search looks, as on the web page (`search.ts`): lyrics share most words with every other song, so
    /// searched together with the title they would bury the hits that matter.
    public enum SearchScope: String, CaseIterable, Sendable {
        case titleStyle
        case lyrics
    }

    /// Every word must occur, in any order; a quoted part as written. Case and accents do not count, so "traume" finds
    /// "Träume".
    public func matches(_ query: String, in scope: SearchScope = .titleStyle) -> Bool {
        let terms = Self.terms(of: query)
        guard !terms.isEmpty else { return true }
        let text = Self.fold(scope == .lyrics ? lyrics : [title, originalTitle, style].joined(separator: "\n"))
        return terms.allSatisfy { text.contains($0) }
    }

    /// The query's words, a quoted part kept whole (`parseQuery` in `search.ts`).
    static func terms(of query: String) -> [String] {
        var terms: [String] = []
        var rest = Substring(query)
        while let start = rest.firstIndex(where: { !$0.isWhitespace }) {
            rest = rest[start...]
            let term: Substring
            if rest.first == "\"" {
                let body = rest.dropFirst()
                let end = body.firstIndex(of: "\"") ?? body.endIndex
                term = body[..<end]
                rest = end < body.endIndex ? body[body.index(after: end)...] : body[end...]
            } else {
                let end = rest.firstIndex(where: \.isWhitespace) ?? rest.endIndex
                term = rest[..<end]
                rest = rest[end...]
            }
            let folded = fold(String(term)).trimmingCharacters(in: .whitespaces)
            if !folded.isEmpty {
                terms.append(folded)
            }
        }
        return terms
    }

    /// Case and accents folded, every run of whitespace one space, so a quoted phrase also finds a wrapped line.
    static func fold(_ text: String) -> String {
        text.folding(options: [.caseInsensitive, .diacriticInsensitive], locale: nil)
            .split(whereSeparator: \.isWhitespace)
            .joined(separator: " ")
    }
}

extension Song {
    /// `3:07`, or nothing while the length is unknown.
    public var duration: String? {
        seconds.map(Self.clock)
    }

    public static func clock(_ seconds: Double) -> String {
        guard seconds.isFinite, seconds >= 0 else { return "0:00" }
        let whole = Int(seconds.rounded(.down))
        return String(format: "%d:%02d", whole / 60, whole % 60)
    }
}
