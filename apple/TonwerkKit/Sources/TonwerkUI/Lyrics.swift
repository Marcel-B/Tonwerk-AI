import Foundation
import TonwerkKit

/// The lyrics as the full-screen player shows them, the web page's `timing.ts`. The server times only the sung lines
/// (a letter or digit, outside the section tags), in order (`LyricsAligner.SungLines`), so each shown line knows which
/// timed line it is.
struct ShownLine: Equatable {
    let text: String
    let section: Bool
    /// Index into the timing's lines, or nil for a tag or an empty line.
    let timed: Int?

    static func lines(of lyrics: String) -> [ShownLine] {
        var next = 0
        return lyrics.split(separator: "\n", omittingEmptySubsequences: false).map { raw in
            let text = raw.trimmingCharacters(in: .whitespaces)
            let section = text.hasPrefix("[") && text.hasSuffix("]") && text.count > 2
                && !text.dropFirst().dropLast().contains("]")
            let sung = !section && text.unicodeScalars.contains { CharacterSet.alphanumerics.contains($0) }
            defer { if sung { next += 1 } }
            return ShownLine(text: text, section: section, timed: sung ? next : nil)
        }
    }
}

/// When each sung line starts and ends, in seconds.
struct LineTimes: Equatable {
    let start: Double
    let end: Double

    /// The timed line sung at `seconds`: the last one started, until the next starts; nil before the first and in a
    /// break that lasts more than four seconds past a line's end, as the web player lights it.
    static func line(at seconds: Double, in lines: [LineTimes]) -> Int? {
        guard let found = lines.lastIndex(where: { $0.start <= seconds }) else { return nil }
        return seconds <= lines[found].end + 4 ? found : nil
    }
}

extension AppModel {
    /// The song's line times, or nil while there are none. With `ask`, a song never heard is put in line for it.
    /// `waiting` says whether the server is still listening, worth asking again later.
    func timing(of song: Song, ask: Bool) async -> (lines: [LineTimes]?, waiting: Bool) {
        guard let tonwerk else { return (nil, false) }
        guard var answer = try? await tonwerk.api.getSongTiming(path: .init(run: song.run, song: song.name)).ok.body.json
        else { return (nil, false) }
        if answer.timing == nil && ask && answer.info.installed && answer.info.state == "none",
           let started = try? await tonwerk.api.startSongTiming(path: .init(run: song.run, song: song.name)).accepted.body.json
        {
            answer = started
        }
        if let timing = answer.timing {
            return (timing.lines.map { LineTimes(start: $0.start, end: $0.end) }, false)
        }
        return (nil, answer.info.state == "queued" || answer.info.state == "listening")
    }
}
