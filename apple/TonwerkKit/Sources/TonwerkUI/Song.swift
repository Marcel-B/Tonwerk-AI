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

    public init(
        run: String, name: String, title: String, number: Int?, style: String = "", lyrics: String = "",
        createdAt: Date? = nil, seconds: Double? = nil, rating: Int? = nil, note: String? = nil,
        coverUpdatedAt: Date? = nil, hasAudio: Bool = true
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
    }

    /// The run's title, with the song's number when the run made several.
    public var displayTitle: String {
        number.map { "\(title) · \($0)" } ?? title
    }

    /// The 192 kbit/s AAC copy the web player streams too (the FLAC is too large for the home upload).
    public func streamURL(on server: URL) -> URL {
        server.appending(path: "api/songs/\(run)/\(name)/stream")
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
                    hasAudio: info.hasAudio)
            }
        }
    }

    /// The text search of the web page's library (`search.ts`): every word in title or style.
    public func matches(_ query: String) -> Bool {
        let words = query.lowercased().split(whereSeparator: \.isWhitespace)
        guard !words.isEmpty else { return true }
        let text = (title + " " + style).lowercased()
        return words.allSatisfy { text.contains($0) }
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
