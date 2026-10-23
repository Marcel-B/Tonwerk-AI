import Foundation
import Observation

/// A note of the singing page's editor, in beats on its grid, with the syllable it sings.
struct SingNote: Identifiable, Equatable, Hashable {
    var id = UUID()
    var start: Double
    var length: Double
    var pitch: Int
    /// A word or syllable, a hold mark (``holds``: the word before goes on over this note at its pitch, a melisma),
    /// or empty for a note left silent.
    var lyric = ""

    var end: Double { start + length }

    /// The marks SoulX-Singer's own MIDI editor and other singing editors use; `yueui_sing.py` reads the same.
    static let holds: Set<String> = ["-", "+", "~", "_"]

    var trimmed: String { lyric.trimmingCharacters(in: .whitespaces) }
    var isHold: Bool { Self.holds.contains(trimmed) }
    var isSilent: Bool { trimmed.isEmpty }
}

/// The melody the server answers for a file, a song or a take (`SingingMelody`), decoded by hand like the voices' calls.
struct SingMelodyAnswer: Decodable {
    struct Note: Decodable {
        let start: Double
        let end: Double
        let pitch: Int
        let lyric: String?
    }

    var notes: [Note]
    let tempoBpm: Double
    let lyrics: String
    let title: String
}

/// What each note sings, in the order sent (`SingingAligned`).
struct SingAlignAnswer: Decodable {
    struct Note: Decodable {
        let word: String?
        /// SoulX-Singer's note type: 2 a word's first note, 3 held over, 1 none.
        let type: Int
        let phonemes: String
    }

    let notes: [Note]
    let leftover: Int
}

/// The singing page's note editor: notes on a grid of beats, a syllable on each, kept in order of time. Every change
/// goes through ``change(_:)``, which keeps an undo step and counts ``revision``, so the page knows when to ask the
/// server for the pronunciation again.
@MainActor
@Observable
final class SingEditor {
    private(set) var notes: [SingNote] = []
    var tempo: Double = 120 {
        didSet { if tempo != oldValue { revision += 1 } }
    }
    var selection: SingNote.ID?
    /// Beats a new note snaps to: a sixteenth by default.
    var grid: Double = 0.25
    /// Points per beat at zoom 1.
    var zoom: Double = 1
    /// The pronunciation of the word starting on a note, as the server reads it (``SingAlignAnswer``).
    private(set) var phonemes: [SingNote.ID: String] = [:]
    /// Syllables of the last spread text that found no note.
    var leftover = 0
    /// Counts changes of notes and tempo.
    private(set) var revision = 0
    /// The length the next drawn note gets: the last one drawn or stretched.
    private(set) var drawLength: Double = 1
    private var history: [[SingNote]] = []

    let player = LogicPlayer()

    var canUndo: Bool { !history.isEmpty }

    var selected: SingNote? { notes.first { $0.id == selection } }

    /// Whether any note carries words; otherwise the page's text is spread over them on the server.
    var hasLyrics: Bool { notes.contains { !$0.isSilent } }

    var lengthInBeats: Double { notes.map(\.end).max() ?? 0 }

    func seconds(_ beats: Double) -> Double { beats * 60 / max(1, tempo) }

    func beats(_ seconds: Double) -> Double { seconds * max(1, tempo) / 60 }

    // MARK: Changes

    func change(_ body: (inout [SingNote]) -> Void) {
        history.append(notes)
        if history.count > 100 {
            history.removeFirst()
        }
        body(&notes)
        notes.sort { ($0.start, $0.pitch) < ($1.start, $1.pitch) }
        revision += 1
    }

    func undo() {
        guard let last = history.popLast() else { return }
        notes = last
        if let selection, !notes.contains(where: { $0.id == selection }) {
            self.selection = nil
        }
        revision += 1
    }

    func snap(_ beat: Double) -> Double { max(0, (beat / grid).rounded(.down) * grid) }

    func round(_ beats: Double) -> Double { (beats / grid).rounded() * grid }

    /// A note drawn at a point: on the grid, as long as the last one drawn but never into the next note, singing
    /// "la" until it is given a word.
    @discardableResult
    func add(at beat: Double, pitch: Int) -> SingNote {
        let start = snap(beat)
        let next = notes.first { $0.start > start }?.start
        let length = max(grid, min(drawLength, (next ?? .infinity) - start))
        let note = SingNote(start: start, length: length, pitch: min(127, max(0, pitch)), lyric: "la")
        change { $0.append(note) }
        selection = note.id
        return note
    }

    /// A drag ended: by beats and semitones, the start on the grid.
    func move(_ id: SingNote.ID, by beats: Double, semitones: Int) {
        let shift = round(beats)
        guard shift != 0 || semitones != 0 else { return }
        change { notes in
            guard let index = notes.firstIndex(where: { $0.id == id }) else { return }
            notes[index].start = max(0, notes[index].start + shift)
            notes[index].pitch = min(127, max(0, notes[index].pitch + semitones))
        }
    }

    func resize(_ id: SingNote.ID, by beats: Double) {
        guard let note = notes.first(where: { $0.id == id }) else { return }
        let length = max(grid, round(note.length + beats))
        guard length != note.length else { return }
        drawLength = length
        change { notes in
            if let index = notes.firstIndex(where: { $0.id == id }) { notes[index].length = length }
        }
    }

    func delete(_ id: SingNote.ID) {
        let next = neighbour(of: id, step: 1) ?? neighbour(of: id, step: -1)
        change { $0.removeAll { $0.id == id } }
        selection = next
    }

    func clear() {
        change { $0.removeAll() }
        selection = nil
        phonemes = [:]
        leftover = 0
    }

    /// Typed into a note: one word stays on it; several go one per note from here on, as singing editors do, so a
    /// line can be typed at once. Returns the note after the last one written, to go on typing there.
    @discardableResult
    func type(_ text: String, on id: SingNote.ID) -> SingNote.ID? {
        guard let first = notes.firstIndex(where: { $0.id == id }) else { return nil }
        let words = text.split(whereSeparator: \.isWhitespace).map(String.init)
        change { notes in
            if words.isEmpty {
                notes[first].lyric = ""
                return
            }
            for (offset, word) in words.enumerated() where first + offset < notes.count {
                notes[first + offset].lyric = word
            }
        }
        let after = first + max(1, words.count)
        return after < notes.count ? notes[after].id : nil
    }

    /// Makes a note go on with the word before (or gives it back a syllable of its own).
    func toggleHold(_ id: SingNote.ID) {
        change { notes in
            guard let index = notes.firstIndex(where: { $0.id == id }) else { return }
            notes[index].lyric = notes[index].isHold ? "la" : "-"
        }
    }

    func neighbour(of id: SingNote.ID?, step: Int) -> SingNote.ID? {
        guard let id, let index = notes.firstIndex(where: { $0.id == id }) else { return notes.first?.id }
        let next = index + step
        return notes.indices.contains(next) ? notes[next].id : nil
    }

    // MARK: Words from the server

    /// The words of a text laid on the notes by the server (``SingAlignAnswer`` in the order of ``notes``): a word's
    /// first note gets it, the notes it is held over the hold mark, the rest stay silent.
    func spread(_ answer: SingAlignAnswer) {
        guard answer.notes.count == notes.count else { return }
        change { notes in
            for (index, sung) in answer.notes.enumerated() {
                switch sung.type {
                case 2: notes[index].lyric = sung.word ?? ""
                case 3: notes[index].lyric = "-"
                default: notes[index].lyric = ""
                }
            }
        }
        leftover = answer.leftover
        read(answer)
    }

    /// The pronunciation of each word, by the notes as they were sent.
    func read(_ answer: SingAlignAnswer, for sent: [SingNote.ID]? = nil) {
        let ids = sent ?? notes.map(\.id)
        guard answer.notes.count == ids.count else { return }
        var found: [SingNote.ID: String] = [:]
        for (id, sung) in zip(ids, answer.notes) where sung.type == 2 && !sung.phonemes.isEmpty {
            found[id] = sung.phonemes
        }
        phonemes = found
    }

    func forgetPhonemes() {
        phonemes = [:]
    }

    // MARK: To and from the server

    /// A melody from a file, a song or a take, on the grid of its tempo (a 96th of a beat, so nothing is moved that
    /// a sixteenth grid would not show).
    func load(_ melody: SingMelodyAnswer) {
        player.stop()
        tempo = melody.tempoBpm > 0 ? min(400, melody.tempoBpm) : 120
        let fine = { (beats: Double) in (beats * 96).rounded() / 96 }
        change { notes in
            notes = melody.notes.map { note in
                let start = fine(beats(note.start))
                return SingNote(
                    start: max(0, start), length: max(1.0 / 96, fine(beats(note.end)) - start), pitch: note.pitch,
                    lyric: note.lyric ?? "")
            }
        }
        history.removeAll()
        selection = nil
        phonemes = [:]
        leftover = 0
    }

    /// The notes as the server's `SungNote`s in seconds. With words on the notes only those that sing go (a silent
    /// one would hold the word before on the server); without any, all of them, for the page's text to be spread.
    func sung(all: Bool = false) -> [[String: Any]] {
        let chosen = all || !hasLyrics ? notes : notes.filter { !$0.isSilent }
        return chosen.map { note in
            var entry: [String: Any] = ["start": seconds(note.start), "end": seconds(note.end), "pitch": note.pitch]
            if !note.isSilent {
                entry["lyric"] = note.trimmed
            }
            return entry
        }
    }

    // MARK: MIDI

    /// The melody as a Standard MIDI File with a lyric event on every note that sings, as singing editors and
    /// Logic read it, and as this page reads it back.
    func midi(title: String) -> Data {
        let ppq = 480
        let tick = { (beats: Double) in Int((beats * Double(ppq)).rounded()) }
        var conductor = Self.meta(0x03, Data(title.utf8))
        let micros = Int((60_000_000 / max(1, tempo)).rounded())
        conductor += Self.meta(0x51, Data([UInt8(micros >> 16 & 0xFF), UInt8(micros >> 8 & 0xFF), UInt8(micros & 0xFF)]))
        conductor += Self.meta(0x58, Data([4, 2, 24, 8]))

        // Offs before ons at the same tick, so touching notes do not swallow each other.
        var events: [(tick: Int, order: Int, bytes: Data)] = []
        for note in notes {
            let start = tick(note.start)
            if !note.isSilent {
                events.append((start, 1, Self.metaBody(0x05, Data(note.trimmed.utf8))))
            }
            events.append((start, 2, Data([0x90, UInt8(note.pitch), 100])))
            events.append((max(start + 1, tick(note.end)), 0, Data([0x80, UInt8(note.pitch), 0])))
        }
        events.sort { ($0.tick, $0.order) < ($1.tick, $1.order) }
        var vocal = Self.meta(0x03, Data("Vocal".utf8))
        var now = 0
        for event in events {
            vocal += Self.variable(event.tick - now) + event.bytes
            now = event.tick
        }

        var file = Data("MThd".utf8) + Self.int32(6) + Data([0, 1, 0, 2]) + Data([UInt8(ppq >> 8), UInt8(ppq & 0xFF)])
        for track in [conductor, vocal] {
            let body = track + Data([0, 0xFF, 0x2F, 0])
            file += Data("MTrk".utf8) + Self.int32(body.count) + body
        }
        return file
    }

    /// A meta event at delta 0.
    private static func meta(_ type: UInt8, _ data: Data) -> Data { Data([0]) + metaBody(type, data) }

    private static func metaBody(_ type: UInt8, _ data: Data) -> Data {
        Data([0xFF, type]) + variable(data.count) + data
    }

    static func variable(_ value: Int) -> Data {
        var value = max(0, value)
        var bytes = [UInt8(value & 0x7F)]
        value >>= 7
        while value > 0 {
            bytes.insert(UInt8(value & 0x7F) | 0x80, at: 0)
            value >>= 7
        }
        return Data(bytes)
    }

    private static func int32(_ value: Int) -> Data {
        Data([UInt8(value >> 24 & 0xFF), UInt8(value >> 16 & 0xFF), UInt8(value >> 8 & 0xFF), UInt8(value & 0xFF)])
    }

    // MARK: Hearing it

    /// The notes as a one-voice score for ``LogicPlayer``, the melody played by the app's synthesizer.
    func score(title: String) -> LogicScore {
        let ppq = 480
        let tick = { (beats: Double) in Int((beats * Double(ppq)).rounded()) }
        let played = notes.map {
            LogicScore.Note(
                startTicks: tick($0.start), durationTicks: max(1, tick($0.end) - tick($0.start)), noteNumber: $0.pitch,
                velocity: $0.isSilent ? 60 : 96)
        }
        let length = tick(lengthInBeats) + ppq
        return LogicScore(
            ticksPerQuarterNote: ppq, title: title, tempoBpm: tempo,
            timeSignatures: [LogicScore.TimeSignature(startTicks: 0, numerator: 4, denominator: 4)], sections: [],
            voices: [LogicScore.Voice(id: "Vocal", displayName: "Vocal", notes: played, kind: "Melody")], chords: [],
            lengthTicks: length, countInTicks: 0, durationSeconds: seconds(Double(length) / Double(ppq)))
    }

    func play(title: String, sound: SynthSound) async {
        if player.isPlaying {
            player.stop()
            return
        }
        guard !notes.isEmpty else { return }
        let score = score(title: title)
        await player.load(
            score, tracks: [LogicPlayer.Track(voice: score.voices[0], route: .app, sound: sound)], mixer: LogicMixer())
        player.play(from: 0)
    }
}
