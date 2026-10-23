import Foundation

/// A vocal track of the Logic page handed to the singing page with the song's words (the web page's `singHandOff`
/// in `singing.ts`; keep both alike): the track's notes, the vocal melody the server lays the words on, and the words
/// that are sung.
struct SingHandOff {
    var melody: SingMelodyAnswer
    /// The vocal melody: the server lays the words on it, and the track's notes take them where they start with it.
    var guide: [SingMelodyAnswer.Note]

    init(score: LogicScore, voice: String, lyrics: String, title: String) {
        let notes = score.sungNotes(of: voice)
        melody = SingMelodyAnswer(
            notes: notes, tempoBpm: score.tempoBpm, lyrics: ShownLine.sung(lyrics), title: title)
        guide = score.voices.contains { $0.id == "Vocal" } ? score.sungNotes(of: "Vocal") : notes
    }

    /// The guide as the server's `SungNote`s, without words, for `POST api/singing/align`.
    var guideForAlign: [[String: Any]] {
        guide.map { ["start": $0.start, "end": $0.end, "pitch": $0.pitch] }
    }

    /// The words the server laid on the guide (in its order) carried over to the track's notes: a note starting with
    /// a guide note sings what that one sings, so a backing part sings the melody's syllables even where it only
    /// joins in the chorus. A note starting inside a guide note goes on with the word before; one outside the melody
    /// stays silent. A part that joins in on a held syllable sings that word anew, since a hold needs a word before.
    static func words(
        for notes: [SingMelodyAnswer.Note], guide: [SingMelodyAnswer.Note], answer: SingAlignAnswer
    ) -> [SingMelodyAnswer.Note] {
        let bare = notes.map { SingMelodyAnswer.Note(start: $0.start, end: $0.end, pitch: $0.pitch, lyric: nil) }
        guard answer.notes.count == guide.count else { return bare }
        // The word each guide note belongs to, for a part joining on a held syllable.
        var words: [String?] = []
        for (index, sung) in answer.notes.enumerated() {
            words.append(sung.type == 2 ? sung.word : sung.type == 3 && index > 0 ? words[index - 1] : nil)
        }
        func sungBy(_ index: Int) -> String {
            let sung = answer.notes[index]
            return sung.type == 2 ? sung.word ?? "" : sung.type == 3 ? "-" : ""
        }
        // Timing humanized per note may move a part's note a little off the melody's.
        func near(_ note: SingMelodyAnswer.Note, _ other: SingMelodyAnswer.Note) -> Bool {
            abs(note.start - other.start) < min(0.08, (other.end - other.start) / 2)
        }
        var previous: (lyric: String, end: Double)?
        return bare.map { note in
            var index = guide.firstIndex { near(note, $0) }
            var lyric = index.map(sungBy) ?? ""
            if index == nil {
                index = guide.firstIndex { $0.start < note.start && note.start < $0.end }
                lyric = index.flatMap { words[$0] } != nil ? "-" : ""
            }
            let follows = previous.map { !$0.lyric.isEmpty && note.start - $0.end < 0.05 } ?? false
            if lyric == "-" && !follows {
                lyric = index.flatMap { words[$0] } ?? ""
            }
            previous = (lyric, note.end)
            return SingMelodyAnswer.Note(
                start: note.start, end: note.end, pitch: note.pitch, lyric: lyric.isEmpty ? nil : lyric)
        }
    }
}

extension LogicScore.Voice {
    /// The tracks a voice sings: the melody, its doubling and the backing vocals.
    var isVocal: Bool { id == "Vocal" || id.hasPrefix("Vocal ") || kind == "Harmony" }
}

extension LogicScore {
    /// A track as notes in seconds from where the song starts (after a count-in).
    func sungNotes(of voice: String) -> [SingMelodyAnswer.Note] {
        (voices.first { $0.id == voice }?.notes ?? [])
            .map { note in
                SingMelodyAnswer.Note(
                    start: max(0, seconds(note.startTicks - countInTicks)),
                    end: seconds(note.startTicks + note.durationTicks - countInTicks),
                    pitch: note.noteNumber, lyric: nil)
            }
            .filter { $0.end > $0.start }
            .sorted { ($0.start, $0.pitch) < ($1.start, $1.pitch) }
    }
}

extension ShownLine {
    /// The words of lyrics that are sung: section tags and directions in round brackets left out, as the full-screen
    /// player shows them, paragraphs kept apart by one empty line.
    static func sung(_ lyrics: String) -> String {
        var kept: [String] = []
        for line in lines(of: lyrics) where !line.section {
            if !line.text.isEmpty || kept.last.map({ !$0.isEmpty }) == true {
                kept.append(line.text)
            }
        }
        while kept.last?.isEmpty == true { kept.removeLast() }
        return kept.joined(separator: "\n")
    }
}

extension SingSession {
    /// A track of the Logic page with the song's words: the server lays them on the vocal melody, and the track's
    /// notes take what the melody note they start with sings (``SingHandOff/words(for:guide:answer:)``).
    func take(_ handed: SingHandOff) async {
        editor.load(handed.melody)
        title = handed.melody.title
        lyrics = handed.melody.lyrics
        guard !handed.melody.lyrics.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              !handed.melody.notes.isEmpty
        else { return }
        do {
            let answer = try await model.alignSinging(handed.guideForAlign, lyrics: handed.melody.lyrics)
            var melody = handed.melody
            melody.notes = SingHandOff.words(for: handed.melody.notes, guide: handed.guide, answer: answer)
            editor.load(melody)
            editor.leftover = answer.leftover
            problem = nil
        } catch {
            // Without SoulX-Singer installed the page says so itself; the words stay in their field.
            if installed { problem = error.localizedDescription }
        }
    }
}

extension AppModel {
    /// Opens the singing page with a vocal track of the Logic page and the library song's sung words.
    func singTrack(_ voice: LogicScore.Voice, of score: LogicScore, song: Song?) {
        let name = song?.title ?? score.title ?? String(localized: "Ohne Titel")
        let handed = SingHandOff(
            score: score, voice: voice.id, lyrics: song?.lyrics ?? "", title: "\(name) · \(voice.displayName)")
        openSing()
        guard let page = singPage else { return }
        Task { await page.take(handed) }
    }
}
