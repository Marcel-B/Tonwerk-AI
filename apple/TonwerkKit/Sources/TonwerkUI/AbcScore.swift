import Foundation

/// A score in YuE2's ABC dialect, transposed by semitones or into another key: notes of every voice, chord symbols and
/// key signatures. Ported from the web page's `abcTranspose.ts`; keep both in step.
///
/// The model sings the "Vocal" voice of a supplied score at the written pitch, so this is how a song gets lower or
/// higher before it is generated. Notes are respelled in the new key, so a transposed score reads like one YuE2 wrote.
/// A change of mode (major to natural minor on the same root and back) moves the notes too, since YuE2 reads a minor
/// `K:` as well, but only the notes make the song sound minor.
public enum AbcScore {
    public struct Transposition: Equatable, Sendable {
        public let abc: String
        /// The first key of the result, e.g. "A" or "F#m".
        public let key: String
    }

    // MARK: Keys

    static let letters: [Character] = Array("CDEFGAB")
    static let naturals = [0, 2, 4, 5, 7, 9, 11]
    static let sharpOrder: [Character] = Array("FCGDAEB")
    static let flatOrder: [Character] = Array("BEADGCF")

    // Index = pitch class of the root. One spelling per class, at most six accidentals (F# over Gb, Ebm over D#m).
    static let majorKeys = ["C", "Db", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B"]
    static let minorKeys = ["Cm", "C#m", "Dm", "Ebm", "Em", "Fm", "F#m", "Gm", "G#m", "Am", "Bbm", "Bm"]
    static let signatures: [String: Int] = [
        "Cb": -7, "Gb": -6, "Db": -5, "Ab": -4, "Eb": -3, "Bb": -2, "F": -1, "C": 0,
        "G": 1, "D": 2, "A": 3, "E": 4, "B": 5, "F#": 6, "C#": 7,
        "Abm": -7, "Ebm": -6, "Bbm": -5, "Fm": -4, "Cm": -3, "Gm": -2, "Dm": -1, "Am": 0,
        "Em": 1, "Bm": 2, "F#m": 3, "C#m": 4, "G#m": 5, "D#m": 6, "A#m": 7,
    ]

    struct Key: Equatable {
        var name: String
        var minor: Bool
        var sharps: Int
    }

    static let cMajor = Key(name: "C", minor: false, sharps: 0)

    static func mod12(_ value: Int) -> Int {
        ((value % 12) + 12) % 12
    }

    /// Reads the key of a `K:` field; clef and other `name=value` attributes are ignored.
    static func parseKey(_ text: String) -> Key? {
        let compact = text.split(whereSeparator: \.isWhitespace).filter { !$0.contains("=") }.joined()
        guard let first = compact.first, "ABCDEFGabcdefg".contains(first) else { return nil }
        var rest = compact.dropFirst()
        var accidental = ""
        if let next = rest.first, next == "#" || next == "b" {
            accidental = String(next)
            rest = rest.dropFirst()
        }
        let mode = rest.lowercased()
        let minor = ["m", "min", "minor", "aeo", "aeolian"].contains(mode)
        if !minor && !["", "maj", "major", "ion", "ionian"].contains(mode) {
            return nil
        }
        let name = first.uppercased() + accidental + (minor ? "m" : "")
        guard let sharps = signatures[name] else { return nil }
        return Key(name: name, minor: minor, sharps: sharps)
    }

    static func keyAlteration(_ key: Key, _ letter: Character) -> Int {
        if key.sharps > 0 {
            return (sharpOrder.firstIndex(of: letter) ?? 7) < key.sharps ? 1 : 0
        }
        if key.sharps < 0 {
            return (flatOrder.firstIndex(of: letter) ?? 7) < -key.sharps ? -1 : 0
        }
        return 0
    }

    static func keyRoot(_ key: Key) -> Int {
        let name = Array(key.name)
        let shift = name.count > 1 ? (name[1] == "#" ? 1 : name[1] == "b" ? -1 : 0) : 0
        return mod12(naturals[letters.firstIndex(of: name[0]) ?? 0] + shift)
    }

    static func transposeKey(_ key: Key, _ semitones: Int) -> Key {
        let root = mod12(keyRoot(key) + semitones)
        let name = key.minor ? minorKeys[root] : majorKeys[root]
        return Key(name: name, minor: key.minor, sharps: signatures[name] ?? 0)
    }

    static func sameRoot(_ key: Key, minor: Bool) -> Key {
        let name = (minor ? minorKeys : majorKeys)[keyRoot(key)]
        return Key(name: name, minor: minor, sharps: signatures[name] ?? 0)
    }

    /// Letter and alteration for a pitch class: the key's own note if there is one, else sharps or flats as the key has.
    static func spell(_ pitchClass: Int, _ key: Key) -> (letter: Character, alteration: Int) {
        for i in 0..<7 {
            let alteration = keyAlteration(key, letters[i])
            if mod12(naturals[i] + alteration) == pitchClass {
                return (letters[i], alteration)
            }
        }
        // A note outside the key: natural if it is one, else with the accidentals the key uses (without a signature
        // C#, Eb, F#, G#, Bb, as they are usually written).
        let direction = key.sharps < 0 || (key.sharps == 0 && (pitchClass == 3 || pitchClass == 10)) ? -1 : 1
        for alteration in [0, direction, -direction] {
            if let i = naturals.firstIndex(where: { mod12($0 + alteration) == pitchClass }) {
                return (letters[i], alteration)
            }
        }
        return (letters[0], 0)
    }

    static func spellName(_ pitchClass: Int, _ key: Key) -> String {
        let (letter, alteration) = spell(pitchClass, key)
        return String(letter) + String(repeating: alteration > 0 ? "#" : "b", count: abs(alteration))
    }

    static func pitchClassOf(_ letter: Character, _ accidental: String) -> Int {
        mod12(naturals[letters.firstIndex(of: letter) ?? 0] + (accidental == "#" ? 1 : accidental == "b" ? -1 : 0))
    }

    // MARK: Chords

    /// YuE2's chord vocabulary as intervals above the root, to find a chord's new quality after a change of mode.
    static let chordIntervals: [(quality: String, intervals: [Int])] = [
        ("", [0, 4, 7]),
        ("m", [0, 3, 7]),
        ("dim", [0, 3, 6]),
        ("aug", [0, 4, 8]),
        ("7", [0, 4, 7, 10]),
        ("maj7", [0, 4, 7, 11]),
        ("m7", [0, 3, 7, 10]),
        ("dim7", [0, 3, 6, 9]),
        ("m7b5", [0, 3, 6, 10]),
        ("sus4", [0, 5, 7]),
        ("sus2", [0, 2, 7]),
        ("6", [0, 4, 7, 9]),
        ("m6", [0, 3, 7, 9]),
        ("7sus4", [0, 5, 7, 10]),
        ("m(maj7)", [0, 3, 7, 11]),
    ]

    /// "Am7", "F#", "C/E", "Bbmaj7/D": root and bass move by `move`. With `tones`, the chord's notes move too and the
    /// quality becomes the one they form (C in C major is Cm in C minor); a quality YuE2 does not know stays.
    static func rewriteChord(_ symbol: String, move: (Int) -> Int, key: Key, tones: Bool) -> String {
        guard let groups = match("^([A-G])([#b]?)([^/]*)(?:/([A-G])([#b]?))?$", symbol),
              let rootLetter = groups[1]?.first
        else { return symbol }
        let root = pitchClassOf(rootLetter, groups[2] ?? "")
        let newRoot = mod12(move(root))
        var quality = groups[3] ?? ""
        if tones, let intervals = chordIntervals.first(where: { $0.quality == quality })?.intervals {
            let moved = intervals.map { mod12(move(root + $0) - newRoot) }.sorted()
            if let found = chordIntervals.first(where: { $0.intervals == moved }) {
                quality = found.quality
            }
        }
        var bass = ""
        if let bassLetter = groups[4]?.first {
            bass = "/" + spellName(mod12(move(pitchClassOf(bassLetter, groups[5] ?? ""))), key)
        }
        return spellName(newRoot, key) + quality + bass
    }

    // MARK: Notes

    static func accidentalText(_ alteration: Int) -> String {
        ["__", "_", "=", "^", "^^"][alteration + 2]
    }

    static func noteText(_ pitch: Int, _ letter: Character, _ alteration: Int) -> String {
        // The octave follows from the natural letter: B# and Cb sit on the other side of the octave boundary.
        let natural = pitch - alteration
        let octave = Int((Double(natural - 60) / 12).rounded(.down))
        if octave >= 1 {
            return String(letter).lowercased() + String(repeating: "'", count: octave - 1)
        }
        return String(letter) + String(repeating: ",", count: -octave)
    }

    /// The upper-case letter of a note, or nil.
    static func noteLetter(_ c: Character) -> Character? {
        "ABCDEFGabcdefg".contains(c) ? Character(c.uppercased()) : nil
    }

    /// Accidentals as YuE2 reads them: one applies to its note letter in every octave until the next bar line or key
    /// change (after `^F`, `f` is sharp too). Both the old and the new spelling follow that rule.
    final class Bar {
        private var byLetter: [Character: Int] = [:]
        var key: Key

        init(_ key: Key) {
            self.key = key
        }

        func resolve(_ letter: Character, _ explicit: Int?) -> Int {
            if let explicit {
                byLetter[letter] = explicit
                return explicit
            }
            return byLetter[letter] ?? AbcScore.keyAlteration(key, letter)
        }

        /// The accidental to write for a note, or "" when the key and the bar already give it.
        func write(_ letter: Character, _ alteration: Int) -> String {
            if resolve(letter, nil) == alteration {
                return ""
            }
            byLetter[letter] = alteration
            return AbcScore.accidentalText(alteration)
        }

        func reset(_ key: Key? = nil) {
            byLetter.removeAll()
            if let key {
                self.key = key
            }
        }
    }

    /// An accidental at `i` and the index after it, or nil and `i`.
    static func accidental(_ chars: [Character], _ i: Int) -> (Int?, Int) {
        guard i < chars.count else { return (nil, i) }
        let next: Character? = i + 1 < chars.count ? chars[i + 1] : nil
        switch chars[i] {
        case "^": return next == "^" ? (2, i + 2) : (1, i + 1)
        case "_": return next == "_" ? (-2, i + 2) : (-1, i + 1)
        case "=": return (0, i + 1)
        default: return (nil, i)
        }
    }

    // MARK: Rewriting

    /// How a rewrite changes keys and notes; `pitch` gets the key the note was written in.
    struct Rewrite {
        let key: (Key) -> Key
        let pitch: (Int, Key) -> Int
        /// Whether chords change their quality with their notes (a change of mode) or keep it (a transposition).
        let chordTones: Bool
    }

    static func transposition(_ semitones: Int) -> Rewrite {
        Rewrite(key: { transposeKey($0, semitones) }, pitch: { pitch, _ in pitch + semitones }, chordTones: false)
    }

    /// Major to natural minor on the same root and back: third, sixth and seventh go down a semitone (or up). Natural
    /// rather than harmonic minor, since it keeps the chords within YuE2's vocabulary.
    static let modeChange = Rewrite(
        key: { sameRoot($0, minor: !$0.minor) },
        pitch: { pitch, from in
            let degree = mod12(pitch - keyRoot(from))
            if from.minor {
                return degree == 3 || degree == 8 || degree == 10 ? pitch + 1 : pitch
            }
            return degree == 4 || degree == 9 || degree == 11 ? pitch - 1 : pitch
        },
        chordTones: true)

    /// Transposes a whole score. Header fields, comments and anything that is not a note or chord stay as they are.
    public static func transpose(_ abc: String, by semitones: Int) -> Transposition {
        rewrite(abc, transposition(semitones))
    }

    /// Turns a major score into minor on the same root (D into Dm) or a minor one into major, at every key change.
    public static func changeMode(_ abc: String) -> Transposition {
        rewrite(abc, modeChange)
    }

    static func rewrite(_ abc: String, _ rewrite: Rewrite) -> Transposition {
        var oldKey = cMajor
        var newKey = rewrite.key(cMajor)
        var firstKey: String?
        var lines: [String] = []
        for line in abc.components(separatedBy: "\n") {
            if let field = match(#"^(\s*K:\s*)(.*)$"#, line) {
                let text = field[2] ?? ""
                guard let key = parseKey(text) else {
                    lines.append(line)
                    continue
                }
                oldKey = key
                newKey = rewrite.key(key)
                if firstKey == nil {
                    firstKey = newKey.name
                }
                let afterName = text.firstIndex(where: \.isWhitespace).map { String(text[$0...]) } ?? ""
                lines.append((field[1] ?? "") + newKey.name + afterName)
                continue
            }
            // Other fields (X:, M:, V:, w:, …) and comments hold no notes.
            if match(#"^\s*([A-Za-z]:|%)"#, line) != nil {
                lines.append(line)
                continue
            }
            let rewritten = rewriteMusic(line, rewrite, oldKey, newKey)
            oldKey = rewritten.oldKey
            newKey = rewritten.newKey
            lines.append(rewritten.text)
        }
        return Transposition(abc: lines.joined(separator: "\n"), key: firstKey ?? newKey.name)
    }

    static func rewriteMusic(_ line: String, _ rewrite: Rewrite, _ oldKey: Key, _ newKey: Key)
        -> (text: String, oldKey: Key, newKey: Key)
    {
        let before = Bar(oldKey)
        let after = Bar(newKey)
        let chars = Array(line)
        var out = ""
        var i = 0
        while i < chars.count {
            let c = chars[i]
            if c == "%" {
                out += String(chars[i...])
                break
            }
            if c == "\"" {
                guard let end = chars[(i + 1)...].firstIndex(of: "\"") else {
                    out += String(chars[i...])
                    break
                }
                let from = before.key
                let symbol = String(chars[(i + 1)..<end])
                out += "\"" + rewriteChord(
                    symbol, move: { rewrite.pitch($0, from) }, key: after.key, tones: rewrite.chordTones) + "\""
                i = end + 1
                continue
            }
            if c == "[", i + 2 < chars.count, chars[i + 1].isASCII, chars[i + 1].isLetter, chars[i + 2] == ":" {
                let end = chars[i...].firstIndex(of: "]")
                let stop = end.map { $0 + 1 } ?? chars.count
                let key = chars[i + 1] == "K" ? parseKey(String(chars[(i + 3)..<(end ?? chars.count)])) : nil
                if let key {
                    let moved = rewrite.key(key)
                    before.reset(key)
                    after.reset(moved)
                    out += "[K:\(moved.name)]"
                } else {
                    out += String(chars[i..<stop])
                }
                i = stop
                continue
            }
            if c == "!" || c == "+" {
                let end = chars[(i + 1)...].firstIndex(of: c)
                let stop = end.map { $0 + 1 } ?? chars.count
                out += String(chars[i..<stop])
                i = stop
                continue
            }
            if c == "|" {
                before.reset()
                after.reset()
                out.append(c)
                i += 1
                continue
            }
            let (explicit, at) = accidental(chars, i)
            if at < chars.count, let letter = noteLetter(chars[at]) {
                var pitch = 60 + naturals[letters.firstIndex(of: letter) ?? 0] + (chars[at] == letter ? 0 : 12)
                var j = at + 1
                while j < chars.count, chars[j] == "'" || chars[j] == "," {
                    pitch += chars[j] == "'" ? 12 : -12
                    j += 1
                }
                pitch = rewrite.pitch(pitch + before.resolve(letter, explicit), before.key)
                let spelled = spell(mod12(pitch), after.key)
                out += after.write(spelled.letter, spelled.alteration)
                    + noteText(pitch, spelled.letter, spelled.alteration)
                i = j
                continue
            }
            out.append(c)
            i += 1
        }
        return (out, before.key, after.key)
    }

    // MARK: For the form

    /// The key the score starts in, spelled as in ``keyChoices`` (Gb as F#), so the picker finds it.
    public static func key(of abc: String) -> String? {
        guard let field = match(#"^\s*K:\s*(.*)$"#, abc, options: .anchorsMatchLines),
              let key = parseKey(field[1] ?? "")
        else { return nil }
        return (key.minor ? minorKeys : majorKeys)[keyRoot(key)]
    }

    /// Every major and minor key to pick a target from; the other mode changes the notes too (``changeMode(_:)``).
    public static var keyChoices: [String] {
        majorKeys + minorKeys
    }

    /// "F#m" as "F♯-Moll", "Bb" as "B♭-Dur".
    public static func keyLabel(_ name: String) -> String {
        let minor = name.hasSuffix("m")
        var root = minor ? String(name.dropLast()) : name
        root = root.replacingOccurrences(of: "#", with: "♯")
        if root.count == 2, root.hasSuffix("b") {
            root = String(root.prefix(1)) + "♭"
        }
        return minor ? String(localized: "\(root)-Moll") : String(localized: "\(root)-Dur")
    }

    /// Rewrites the score into the named key (one of ``keyChoices``): into the other mode on the same root first if the
    /// target has it, then by the shorter way, at most six semitones down or five up, so the voice stays near where it
    /// was.
    public static func transpose(_ abc: String, to target: String) -> Transposition {
        let field = match(#"^\s*K:\s*(.*)$"#, abc, options: .anchorsMatchLines)
        var from = field.flatMap { parseKey($0[1] ?? "") } ?? cMajor
        guard let to = parseKey(target) else { return Transposition(abc: abc, key: from.name) }
        var abc = abc
        if to.minor != from.minor {
            abc = changeMode(abc).abc
            from = sameRoot(from, minor: to.minor)
        }
        let up = mod12(keyRoot(to) - keyRoot(from))
        return transpose(abc, by: up > 5 ? up - 12 : up)
    }

    static let noteNames = ["C", "C♯", "D", "E♭", "E", "F", "F♯", "G", "A♭", "A", "B♭", "B"]

    /// Lowest and highest note of the "Vocal" voice in scientific pitch (C4 = middle C), or nil without one.
    public static func vocalRange(_ abc: String) -> (low: String, high: String)? {
        let pitches = vocalPitches(abc)
        guard let low = pitches.min(), let high = pitches.max() else { return nil }
        func name(_ pitch: Int) -> String {
            noteNames[mod12(pitch)] + String(Int((Double(pitch) / 12).rounded(.down)) - 1)
        }
        return (name(low), name(high))
    }

    /// MIDI pitches of the "Vocal" voice in order.
    static func vocalPitches(_ abc: String) -> [Int] {
        var voice = ""
        var key = cMajor
        var pitches: [Int] = []
        for line in abc.components(separatedBy: "\n") {
            if let field = match(#"^\s*V:\s*(\S+)"#, line) {
                voice = field[1] ?? ""
                continue
            }
            if let field = match(#"^\s*K:\s*(.*)$"#, line) {
                key = parseKey(field[1] ?? "") ?? key
                continue
            }
            if match(#"^\s*([A-Za-z]:|%)"#, line) != nil || voice != "Vocal" {
                continue
            }
            let bar = Bar(key)
            let chars = Array(line)
            var i = 0
            while i < chars.count {
                let c = chars[i]
                if c == "%" {
                    break
                }
                if c == "\"" || c == "!" || c == "+" {
                    // Chords and decorations hold no sung notes; an unclosed one runs to the end of the line.
                    guard let end = chars[(i + 1)...].firstIndex(of: c) else { break }
                    i = end + 1
                    continue
                }
                if c == "[", i + 2 < chars.count, chars[i + 1] == "K", chars[i + 2] == ":",
                   let end = chars[i...].firstIndex(of: "]")
                {
                    key = parseKey(String(chars[(i + 3)..<end])) ?? key
                    bar.reset(key)
                    i = end + 1
                    continue
                }
                if c == "|" {
                    bar.reset()
                    i += 1
                    continue
                }
                let (explicit, at) = accidental(chars, i)
                if at < chars.count, let letter = noteLetter(chars[at]) {
                    var pitch = 60 + naturals[letters.firstIndex(of: letter) ?? 0] + (chars[at] == letter ? 0 : 12)
                    var j = at + 1
                    while j < chars.count, chars[j] == "'" || chars[j] == "," {
                        pitch += chars[j] == "'" ? 12 : -12
                        j += 1
                    }
                    pitches.append(pitch + bar.resolve(letter, explicit))
                    i = j
                    continue
                }
                i += 1
            }
        }
        return pitches
    }

    /// The planning a score needs: "full" keeps its chords when it has them, "melody" plans them anew. Only body lines
    /// count, the voice declarations quote their names as well.
    public static func planning(for abc: String) -> CreateForm.Planning {
        match(#"^(?![A-Za-z]:|%).*"[^"]+""#, abc, options: .anchorsMatchLines) != nil ? .full : .melody
    }

    /// YuE2's examples/score.abc: the melody and chords for its "City Lights" lyrics.
    public static let example = """
        X:1
        T:
        M:4/4
        L:1/16
        Q:1/4=88
        V: Vocal clef=treble name="Vocal Melody" snm="Vocal"
        V: Ins clef=treble name="Ins Melody" snm="Inst."
        K:C
        % verse
        V: Vocal
        "C"E2G2A2G2E2D2C4|"G"D2E2G2E2D2C2D4|"Am"E2G2A2c2B2A2G4|"F"F2E2D2E2G2E2C4|
        V: Ins
        Z4|
        % chorus
        V: Vocal
        "C"G2A2c2B2A2G2E4|"F"F2A2G2E2D2E2G4|"G"A2c2B2A2G2E2D4|"C"E2G2A2G2E2D2C4|
        V: Ins
        Z4|
        """

    /// The first match's groups (0 the whole match), nil where a group took no part.
    static func match(_ pattern: String, _ text: String, options: NSRegularExpression.Options = []) -> [String?]? {
        guard let regex = try? NSRegularExpression(pattern: pattern, options: options),
              let found = regex.firstMatch(in: text, range: NSRange(text.startIndex..., in: text))
        else { return nil }
        return (0..<found.numberOfRanges).map { index in
            Range(found.range(at: index), in: text).map { String(text[$0]) }
        }
    }
}
