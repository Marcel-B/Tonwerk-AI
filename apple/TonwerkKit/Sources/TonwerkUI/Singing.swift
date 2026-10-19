import Foundation
import Observation
import TonwerkKit

/// The singing page's state: a melody in the note editor (``SingEditor``: drawn, or loaded from a MIDI file,
/// a score.abc, a library song or an earlier take), a syllable on each note or a text spread over them, sung by
/// SoulX-Singer on the server (`POST api/singing` with the editor's notes). Kept by ``AppModel`` once opened, so the
/// notes survive a page change.
@MainActor
@Observable
final class SingSession {
    private(set) var info: Components.Schemas.SingingInfo?
    private(set) var loading = false
    var problem: String?

    let editor = SingEditor()
    /// Where the editor's melody came from, for the title and the MIDI file's name.
    var title = ""
    /// The words to spread over the notes; a library song brings its lyrics.
    var lyrics = ""
    var voiceId: String?
    /// Semitones the melody moves; ignored while ``autoShift`` moves it into the voice's range.
    var pitchShift = 0
    var autoShift = false
    /// The Mac that sings; empty lets the server choose.
    var machine = ""
    private(set) var sending = false
    /// A melody being read or words being spread.
    private(set) var busy = false

    let preview = AudioPreview()

    // A voice of one's own: a recording of singing and the words sung, from which the server keeps a phrase.
    let recorder = VoiceRecorder()
    var recorded: PickedFile?
    var voiceName = ""
    var voiceLyrics = SingSession.suggestedPhrase
    private(set) var savingVoice = false
    /// What the server heard in the last recording made into a voice.
    private(set) var heard: Components.Schemas.SingingVoiceMade?

    /// Public domain, one syllable per note on steady notes, which is what the server reads best from a recording.
    nonisolated static let suggestedPhrase = "Twinkle, twinkle, little star, how I wonder what you are"

    private unowned let model: AppModel

    init(model: AppModel) {
        self.model = model
        preview.songPlayer = model.player
    }

    var installed: Bool { info?.installed == true }

    func load() async {
        guard !loading else { return }
        loading = true
        defer { loading = false }
        await model.loadMachines()
        do {
            let info = try await model.singing()
            self.info = info
            problem = nil
            if voiceId.map({ id in !info.voices.contains { $0.id == id } }) ?? true {
                voiceId = info.voices.first?.id
            }
        } catch {
            problem = String(localized: "Die Seite ließ sich nicht laden: \(error.localizedDescription)")
        }
    }

    /// What was loaded, with the live changes laid over it; a deleted take arrives as cancelled and leaves.
    var takes: [Components.Schemas.SingTake] {
        Self.merge(info?.takes ?? [], live: Array(model.liveSinging.values))
    }

    nonisolated static func merge(
        _ loaded: [Components.Schemas.SingTake], live: [Components.Schemas.SingTake]
    ) -> [Components.Schemas.SingTake] {
        var byId = Dictionary(loaded.map { ($0.id, $0) }, uniquingKeysWith: { _, last in last })
        for take in live {
            if let known = byId[take.id], (known.updatedAt ?? .distantPast) > (take.updatedAt ?? .distantPast) {
                continue
            }
            byId[take.id] = take
        }
        return byId.values
            .filter { $0.stage != "cancelled" }
            .sorted { ($0.createdAt ?? .distantPast) > ($1.createdAt ?? .distantPast) }
    }

    /// Notes with words, or notes and a text to spread over them.
    var canSing: Bool {
        !editor.notes.isEmpty && (editor.hasLyrics || !lyrics.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            && voiceId != nil && !sending && installed && info?.modelDownloaded == true
    }

    /// A MIDI file or score.abc into the editor, with the lyric events it has.
    func pick(_ url: URL) async {
        do {
            let file = try await PickedFile.copy(from: url)
            await open(.file(file))
        } catch {
            problem = error.localizedDescription
        }
    }

    /// A file's or a library song's melody into the editor; a song's lyrics go on its notes when they carry none.
    func open(_ source: ScoreSource) async {
        busy = true
        defer { busy = false }
        do {
            let melody = try await model.singMelody(source)
            editor.load(melody)
            title = melody.title
            if !melody.lyrics.isEmpty {
                lyrics = melody.lyrics
            }
            problem = nil
            if !editor.hasLyrics && !lyrics.isEmpty && installed {
                await spread()
            }
        } catch {
            problem = error.localizedDescription
        }
    }

    /// An earlier take's notes and words back into the editor, to change and sing again.
    func open(_ take: Components.Schemas.SingTake) async {
        busy = true
        defer { busy = false }
        do {
            let melody = try await model.singTakeNotes(take.id)
            editor.load(melody)
            title = melody.title
            lyrics = melody.lyrics
            problem = nil
        } catch {
            problem = error.localizedDescription
        }
    }

    func clear() {
        editor.player.stop()
        editor.clear()
        title = ""
    }

    /// The text laid on the notes by the server's rules (a word over as many notes as it has syllables, "-" holding
    /// one), replacing the words on them.
    func spread() async {
        guard !editor.notes.isEmpty else { return }
        busy = true
        defer { busy = false }
        do {
            let notes = editor.sung(all: true).map { entry in entry.filter { $0.key != "lyric" } }
            editor.spread(try await model.alignSinging(notes, lyrics: lyrics))
            problem = nil
        } catch {
            problem = error.localizedDescription
        }
    }

    /// The pronunciation of the words on the notes, asked again a moment after they change (the page calls this
    /// from a task keyed by the editor's revision).
    func readPhonemes() async {
        guard installed, editor.hasLyrics else {
            editor.forgetPhonemes()
            return
        }
        let ids = editor.notes.map(\.id)
        do {
            let answer = try await model.alignSinging(editor.sung(all: true), lyrics: nil)
            guard !Task.isCancelled, ids == editor.notes.map(\.id) else { return }
            editor.read(answer, for: ids)
        } catch {
            // Only a help while typing; the take itself says what went wrong.
        }
    }

    func sing() async {
        guard let voiceId, canSing else { return }
        sending = true
        defer { sending = false }
        var fields = [("voice", voiceId), ("autoShift", autoShift ? "true" : "false"), ("tempoBpm", String(editor.tempo))]
        if !autoShift && pitchShift != 0 {
            fields.append(("pitchShift", String(pitchShift)))
        }
        if !editor.hasLyrics {
            fields.append(("lyrics", lyrics.trimmingCharacters(in: .whitespacesAndNewlines)))
        }
        let name = title.trimmingCharacters(in: .whitespaces)
        if !name.isEmpty {
            fields.append(("title", name))
        }
        if !machine.isEmpty {
            fields.append(("machine", machine))
        }
        do {
            let data = try JSONSerialization.data(withJSONObject: editor.sung())
            fields.append(("notes", String(decoding: data, as: UTF8.self)))
            let take = try await model.singNotes(fields: fields)
            if var info {
                info.takes.insert(take, at: 0)
                self.info = info
            }
            problem = nil
        } catch {
            problem = error.localizedDescription
        }
    }

    /// The editor's melody as a MIDI file with its words, in the temporary folder.
    func midiFile() throws -> URL {
        let folder = FileManager.default.temporaryDirectory
            .appending(path: "Tonwerk-Gesang", directoryHint: .isDirectory)
            .appending(path: UUID().uuidString, directoryHint: .isDirectory)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let name = title.trimmingCharacters(in: .whitespaces)
        let file = folder.appending(path: "\(AppModel.projectName(name.isEmpty ? "Tonwerk" : name)).mid")
        try editor.midi(title: name).write(to: file)
        return file
    }

    func play() async {
        if !model.soundLibrary.loaded {
            await model.soundLibrary.load()
        }
        await editor.play(title: title, sound: model.soundLibrary.sound(forTrack: "Vocal", kind: "Melody"))
    }

    /// Stops a take still in the line or deletes a finished one.
    func delete(_ take: Components.Schemas.SingTake) async {
        if preview.current == take.id {
            preview.stop()
        }
        do {
            try await model.deleteSingTake(take.id)
            if var info {
                info.takes.removeAll { $0.id == take.id }
                self.info = info
            }
        } catch {
            problem = error.localizedDescription
        }
    }

    // MARK: Voices

    var ownVoices: [Components.Schemas.SingingVoiceInfo] { (info?.voices ?? []).filter { $0.own == true } }

    func record() async {
        preview.stop()
        do {
            try await recorder.start { [weak self] file in
                self?.recorded = file
            }
        } catch {
            problem = error.localizedDescription
        }
    }

    func pickVoice(_ url: URL) async {
        do {
            recorded = try await PickedFile.copy(from: url)
        } catch {
            problem = error.localizedDescription
        }
    }

    var canSaveVoice: Bool {
        recorded != nil && !recorder.recording && !savingVoice && installed
            && !voiceName.trimmingCharacters(in: .whitespaces).isEmpty
            && !voiceLyrics.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    func saveVoice() async {
        guard let recorded, canSaveVoice else { return }
        savingVoice = true
        defer { savingVoice = false }
        do {
            let created = try await model.addSingingVoice(
                label: voiceName.trimmingCharacters(in: .whitespaces),
                lyrics: voiceLyrics.trimmingCharacters(in: .whitespacesAndNewlines), file: recorded)
            self.recorded = nil
            voiceName = ""
            heard = created.heard
            problem = nil
            await load()
            voiceId = created.voice.id
        } catch {
            problem = error.localizedDescription
        }
    }

    func deleteVoice(_ voice: Components.Schemas.SingingVoiceInfo) async {
        if preview.current == "voice:\(voice.id)" {
            preview.stop()
        }
        do {
            try await model.deleteSingingVoice(voice.id)
            if voiceId == voice.id {
                voiceId = nil
            }
            heard = nil
            await load()
        } catch {
            problem = error.localizedDescription
        }
    }

    /// What the server made of a recording, and what to do about it where it heard something off.
    nonisolated static func heardNote(_ heard: Components.Schemas.SingingVoiceMade) -> String {
        var parts = [String(localized: "Behalten: \(heard.seconds.formatted(.number.precision(.fractionLength(1)))) s mit „\(heard.text)“, \(heard.notes) Noten gehört.")]
        if heard.leftover > 0 {
            parts.append(String(localized: "\(heard.leftover) Silben ohne Note: Text und Gesang passen nicht ganz, am besten genau den Text singen und Silben deutlich absetzen."))
        }
        if heard.peak > 0.98 {
            parts.append(String(localized: "Die Aufnahme ist übersteuert; etwas weiter weg vom Mikrofon singen."))
        } else if heard.peak < 0.05 {
            parts.append(String(localized: "Die Aufnahme ist sehr leise; näher ans Mikrofon."))
        }
        if heard.seconds < 4 {
            parts.append(String(localized: "Die Phrase ist kurz; ohne lange Pause 6 bis 12 Sekunden am Stück singen."))
        }
        return parts.joined(separator: " ")
    }

    // MARK: Names

    nonisolated static func finished(_ take: Components.Schemas.SingTake) -> Bool {
        ["done", "failed", "cancelled"].contains(take.stage ?? "queued")
    }

    nonisolated static func stageName(_ take: Components.Schemas.SingTake) -> String {
        switch take.stage ?? "queued" {
        case "queued": String(localized: "Wartet")
        case "loading": String(localized: "Lädt Modell")
        case "singing":
            take.progress.map { String(localized: "Singt \(Int(($0 * 100).rounded())) %") } ?? String(localized: "Singt")
        case "done": String(localized: "Fertig")
        case "failed": String(localized: "Fehlgeschlagen")
        case "cancelled": String(localized: "Abgebrochen")
        case let other: other
        }
    }

    /// How the words fell on the notes: notes that got a syllable, notes left without one, words left over.
    nonisolated static func alignment(_ take: Components.Schemas.SingTake) -> String? {
        guard let sung = take.sung else { return nil }
        var parts = [String(localized: "\(sung) Noten gesungen")]
        if let unsung = take.unsung, unsung > 0 {
            parts.append(String(localized: "\(unsung) ohne Silbe"))
        }
        if let leftover = take.leftover, leftover > 0 {
            parts.append(String(localized: "\(leftover) Wörter übrig"))
        }
        return parts.joined(separator: " · ")
    }

    nonisolated static func stats(_ take: Components.Schemas.SingTake) -> String {
        let number = { (value: Double?) in value.map { $0.formatted(.number.precision(.fractionLength(0...1))) } ?? "–" }
        return String(localized: "Laden \(number(take.loadSeconds)) s · Singen \(number(take.singSeconds)) s · \(number(take.peakMemoryGb)) GB Speicher")
    }
}

extension AppModel {
    func openSing() {
        if singPage == nil {
            singPage = SingSession(model: self)
        }
        page = .sing
    }

    /// Takes not finished, oldest first, as the queue lists them.
    var singJobs: [Components.Schemas.SingTake] {
        liveSinging.values
            .filter { !SingSession.finished($0) }
            .sorted { ($0.createdAt ?? .distantPast) < ($1.createdAt ?? .distantPast) }
    }

    func singing() async throws -> Components.Schemas.SingingInfo {
        let (data, _) = try await call("GET", address("api/singing"))
        return try decode(Components.Schemas.SingingInfo.self, data)
    }

    /// A file's or library song's melody for the editor (`POST api/singing/melody`).
    func singMelody(_ source: ScoreSource) async throws -> SingMelodyAnswer {
        let url = try address("api/singing/melody")
        switch source {
        case .file(let file):
            let (data, _) = try await uploadFile(url, fields: [], fileField: "file", file: file, accept: [200])
            return try decode(SingMelodyAnswer.self, data)
        case .song(let song):
            return try decode(SingMelodyAnswer.self, try await postForm(url, fields: [("song", song.id)], accept: [200]))
        }
    }

    func singTakeNotes(_ id: String) async throws -> SingMelodyAnswer {
        let (data, _) = try await call("GET", address("api/singing", id, "notes"))
        return try decode(SingMelodyAnswer.self, data)
    }

    /// What each note sings and how its word is spoken (`POST api/singing/align`).
    func alignSinging(_ notes: [[String: Any]], lyrics: String?) async throws -> SingAlignAnswer {
        var body: [String: Any] = ["notes": notes]
        if let lyrics {
            body["lyrics"] = lyrics
        }
        let (data, _) = try await call("POST", address("api/singing/align"), json: body)
        return try decode(SingAlignAnswer.self, data)
    }

    /// The editor's notes to sing, as a form without a file.
    func singNotes(fields: [(String, String)]) async throws -> Components.Schemas.SingTake {
        try decode(Components.Schemas.SingTake.self, try await postForm(address("api/singing"), fields: fields, accept: [202]))
    }

    /// A multipart form of text fields only.
    private func postForm(_ url: URL, fields: [(String, String)], accept: Set<Int>) async throws -> Data {
        let boundary = "tonwerk-\(UUID().uuidString)"
        var body = Data()
        for (name, value) in fields {
            body.append(Data("--\(boundary)\r\nContent-Disposition: form-data; name=\"\(name)\"\r\n\r\n\(value)\r\n".utf8))
        }
        body.append(Data("--\(boundary)--\r\n".utf8))
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")
        request.timeoutInterval = 120
        let (answer, response) = try await URLSession.shared.upload(for: request, from: body)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        guard accept.contains(status) else {
            throw ServerError(problem: ServerError.text(of: answer) ?? String(localized: "Der Server antwortete \(status)."))
        }
        return answer
    }

    /// A recording of one's own singing made into a voice (`POST api/singing/voices`).
    func addSingingVoice(label: String, lyrics: String, file: PickedFile) async throws -> Components.Schemas.SingingVoiceCreated {
        let (data, _) = try await uploadFile(
            address("api/singing/voices"), fields: [("label", label), ("lyrics", lyrics)], fileField: "file", file: file,
            accept: [201])
        return try decode(Components.Schemas.SingingVoiceCreated.self, data)
    }

    func deleteSingingVoice(_ id: String) async throws {
        _ = try await call("DELETE", address("api/singing/voices", id), accept: [204, 404])
    }

    func singingVoiceAudio(_ id: String) throws -> URL { try address("api/singing/voices", id, "audio") }

    func deleteSingTake(_ id: String) async throws {
        _ = try await call("DELETE", address("api/singing", id), accept: [204, 404])
    }

    func singTakeAudio(_ id: String) throws -> URL { try address("api/singing", id, "audio") }

    /// A take's WAV in the temporary folder, to save or share, named after its title.
    func downloadSingTake(_ take: Components.Schemas.SingTake) async throws -> URL {
        let (data, _) = try await call("GET", address("api/singing", take.id, "audio"))
        let folder = FileManager.default.temporaryDirectory
            .appending(path: "Tonwerk-Gesang", directoryHint: .isDirectory)
            .appending(path: UUID().uuidString, directoryHint: .isDirectory)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let file = folder.appending(path: "\(Self.projectName(take.title ?? "Tonwerk")).wav")
        try data.write(to: file)
        return file
    }
}
