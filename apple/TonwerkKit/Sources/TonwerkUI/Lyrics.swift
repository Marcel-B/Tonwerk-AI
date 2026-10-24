import Foundation
import TonwerkKit

/// The lyrics as the full-screen player shows them, the web page's `timing.ts`. The server times only the sung lines
/// (a letter or digit, outside the section tags), in order (`LyricsAligner.SungLines`), so each shown line knows which
/// timed line it is. The shown lines are cut finer than that (a tag on its own line, directions apart, long paragraphs
/// broken), so several may share one timed line. Whisper's timing only knows the whole line, so its pieces divide its
/// time by their share of its words (`from`, `to`): a paragraph then lights line by line. Keep both sides alike.
struct ShownLine: Equatable {
    let text: String
    let section: Bool
    /// A performance direction in round brackets, "(Screamy vocals, 808 bass)": the model reads it, nobody sings it.
    var direction = false
    /// Index into the timing's lines, or nil for a tag, a direction or an empty line.
    let timed: Int?
    /// Where in its timed line's time this piece starts and ends, as shares of it (0 and 1 for a whole line).
    var from = 0.0
    var to = 1.0

    /// Longer than this, a line is a paragraph whose line breaks were lost (as a chatbot's lyrics often come).
    static let paragraph = 60

    static func lines(of lyrics: String, directions: Bool = false) -> [ShownLine] {
        let english = !looksGerman(lyrics)
        var shown: [ShownLine] = []
        func add(_ line: ShownLine) {
            // A line left empty by hidden directions must not leave a gap of two, nor one at the start.
            if line.text.isEmpty, !line.section, shown.last.map({ $0.text.isEmpty && !$0.section }) ?? true { return }
            shown.append(line)
        }
        var next = 0
        for raw in lyrics.split(separator: "\n", omittingEmptySubsequences: false) {
            let line = raw.trimmingCharacters(in: .whitespaces)
            let section = line.hasPrefix("[") && line.hasSuffix("]") && line.count > 2
                && !line.dropFirst().dropLast().contains("]")
            var timed: Int?
            if !section && hasLetter(line) {
                timed = next
                next += 1
            }
            if line.isEmpty {
                add(ShownLine(text: "", section: false, timed: nil))
                continue
            }
            let parts = pieces(of: line)
            let hadDirection = parts.contains { $0.kind == .direction }
            let firstPiece = shown.count
            for piece in parts {
                switch piece.kind {
                case .tag:
                    add(ShownLine(text: piece.text, section: true, timed: nil))
                case .direction:
                    if directions { add(ShownLine(text: piece.text, section: false, direction: true, timed: nil)) }
                case .text:
                    // Commas and dots left over between removed directions are not worth a line.
                    guard !hadDirection || hasLetter(piece.text) else { continue }
                    for text in broken(piece.text, english: english) {
                        add(ShownLine(text: text, section: false, timed: timed))
                    }
                }
            }
            // The sung pieces of this line get their share of its time, by words.
            let sung = shown.indices.dropFirst(firstPiece).filter { shown[$0].timed != nil }
            let words = sung.map { shown[$0].text.split(separator: " ").count }
            let total = Double(words.reduce(0, +))
            var before = 0
            for (index, count) in zip(sung, words) {
                shown[index].from = Double(before) / total
                before += count
                shown[index].to = Double(before) / total
            }
        }
        while shown.last?.text.isEmpty == true { shown.removeLast() }
        return shown
    }

    /// The shown lines the server timed one by one, in its order, or nil for a timing made before it cut lines as the
    /// player does (`LyricsAligner.SungLines`); the server hears such a song again, meanwhile `from`/`to` divide each
    /// old line's time.
    static func timedOneByOne(_ lines: [ShownLine], times: [LineTimes]) -> [Int]? {
        let sung = lines.indices.filter { lines[$0].timed != nil && hasLetter(lines[$0].text) }
        guard sung.count == times.count, zip(sung, times).allSatisfy({ lines[$0].text == $1.text }) else { return nil }
        return sung
    }

    /// The shown line sung at `seconds`, an index into `lines`: the timed line from `LineTimes.line`, then, for an
    /// older timing, the piece of it whose share of its time has begun.
    static func shown(at seconds: Double, in lines: [ShownLine], times: [LineTimes]) -> Int? {
        guard let timed = LineTimes.line(at: seconds, in: times) else { return nil }
        if let exact = timedOneByOne(lines, times: times) { return exact[timed] }
        let line = times[timed]
        let share = line.end > line.start ? (seconds - line.start) / (line.end - line.start) : 0
        var found: Int?
        for (index, piece) in lines.enumerated() where piece.timed == timed && (found == nil || piece.from <= share) {
            found = index
        }
        return found
    }

    /// Where a shown line starts, for a tap on it: its own timed line's start, or for an older timing its share of one.
    static func start(of index: Int, in lines: [ShownLine], times: [LineTimes]) -> Double? {
        guard lines.indices.contains(index), let timed = lines[index].timed else { return nil }
        if let exact = timedOneByOne(lines, times: times) {
            return exact.firstIndex(of: index).map { times[$0].start }
        }
        guard times.indices.contains(timed) else { return nil }
        return times[timed].start + lines[index].from * (times[timed].end - times[timed].start)
    }

    /// Whether the lyrics hold directions in round brackets, so the player offers to show them.
    static func hasDirections(_ lyrics: String) -> Bool {
        lyrics.split(separator: "\n").contains { pieces(of: String($0)).contains { $0.kind == .direction } }
    }

    private enum Kind { case tag, direction, text }
    private struct Piece {
        let kind: Kind
        let text: String
    }

    /// A line cut into `[Tags]`, `(directions)` and the text between; a bracket that never closes is text.
    private static func pieces(of line: String) -> [Piece] {
        let chars = Array(line)
        var found: [Piece] = []
        var text = ""
        var i = 0
        while i < chars.count {
            let open = chars[i]
            let close: Character? = open == "[" ? "]" : open == "(" ? ")" : nil
            if let close, let end = chars[(i + 1)...].firstIndex(of: close), end > i + 1 {
                found.append(Piece(kind: .text, text: text))
                found.append(Piece(kind: open == "[" ? .tag : .direction, text: String(chars[i...end])))
                text = ""
                i = end + 1
            } else {
                text.append(open)
                i += 1
            }
        }
        found.append(Piece(kind: .text, text: text))
        return found.compactMap { piece in
            let words = piece.text.split(whereSeparator: \.isWhitespace).joined(separator: " ")
            return words.isEmpty ? nil : Piece(kind: piece.kind, text: words)
        }
    }

    /// A paragraph broken where a line plausibly ended: after a sentence's end, and in English also before a capital
    /// that follows a lower-case word ("carbon glass The ghosts"), but never before "I". German capitalises every noun,
    /// so there only the sentence ends count. A guess: the stored lyrics stay as they are.
    private static func broken(_ text: String, english: Bool) -> [String] {
        guard text.count > paragraph else { return [text] }
        var lines: [[Substring]] = []
        for word in text.split(separator: " ") {
            var ends = false
            if let previous = lines.last?.last {
                var end = previous
                while let last = end.last, "\"'”’".contains(last) { end = end.dropLast() }
                let sentence = end.last.map { ".!?…".contains($0) } ?? false
                let capital = english && (previous.last?.isLowercase ?? false) && (word.first?.isUppercase ?? false)
                    && word != "I" && !word.hasPrefix("I'") && !word.hasPrefix("I’")
                ends = sentence || capital
            }
            if lines.isEmpty || ends {
                lines.append([word])
            } else {
                lines[lines.count - 1].append(word)
            }
        }
        return lines.map { $0.joined(separator: " ") }
    }

    private static func hasLetter(_ text: String) -> Bool {
        text.unicodeScalars.contains { CharacterSet.alphanumerics.contains($0) }
    }

    /// German lyrics (umlauts or a few common words) keep their capitals; see `broken`.
    private static func looksGerman(_ lyrics: String) -> Bool {
        if lyrics.contains(where: { "äöüÄÖÜß".contains($0) }) { return true }
        let common: Set<String> = ["und", "ich", "nicht", "ist", "wir", "mein", "dein", "auf", "kein"]
        return lyrics.lowercased().split(whereSeparator: { !$0.isLetter }).contains { common.contains(String($0)) }
    }
}

/// When each sung line starts and ends, in seconds.
struct LineTimes: Equatable {
    let start: Double
    let end: Double
    /// The line as the server timed it, to tell whether it timed the shown lines one by one.
    var text: String?

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
        let waiting = answer.info.state == "queued" || answer.info.state == "listening"
        // Also with a timing: an old one is heard again with the lines cut as the player shows them.
        let lines = answer.timing.map { timing in
            timing.lines.map { LineTimes(start: $0.start, end: $0.end, text: $0.text) }
        }
        return (lines, waiting)
    }
}
