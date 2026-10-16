import Foundation
import Observation
import TonwerkKit

/// The transcription page's state (`TranscribePanel.vue`, `AudioMidiPanel.vue`), kept by ``AppModel`` once opened
/// like the voices page's, so a chosen file or a MIDI result survives a page change. The live transcriptions come
/// from the event stream (``AppModel/liveTranscriptions``); the finished ones on disk are loaded here.
@MainActor
@Observable
final class TranscriptionSession {
    /// Which melodies SheetSage2 writes into the score.
    enum Melody: String, CaseIterable, Identifiable {
        case full = "melody-full"
        case vocal = "melody-vocal"

        var id: String { rawValue }

        static func name(of task: String?) -> String {
            task == Melody.vocal.rawValue ? String(localized: "Nur Gesang") : String(localized: "Gesang und Instrumente")
        }
    }

    struct Notice: Equatable {
        let text: String
        let error: Bool
    }

    private(set) var items: [Components.Schemas.TranscriptionInfo] = []
    /// Nil until the list answered; false while SheetSage2 is not installed, which disables the start.
    private(set) var installed: Bool?
    private(set) var loaded = false
    var problem: String?

    var file: PickedFile?
    var melody = Melody.full
    private(set) var sending = false
    private(set) var notice: Notice?
    /// Scores shown with "ABC ansehen" or taken into the form, by transcription id.
    private(set) var scores: [String: String] = [:]

    // A recording to MIDI with Basic Pitch.
    var midiFile: PickedFile?
    var midiMono = true
    var midiQuantize = false
    var midiBends = false
    /// Without a tempo the server writes 120 bpm and cannot quantize.
    var tempoKnown = false
    var tempo = 120.0
    private(set) var midiInstalled: Bool?
    private(set) var midi: MidiFile?
    private(set) var midiError: String?
    private(set) var convertingMidi = false

    private unowned let model: AppModel

    init(model: AppModel) {
        self.model = model
    }

    var canStart: Bool { file != nil && !sending && installed != false }

    var canConvertMidi: Bool {
        midiFile != nil && !convertingMidi && midiInstalled != false && (tempoKnown || !midiQuantize)
    }

    func load() async {
        do {
            let list = try await model.transcriptions()
            items = list.items
            installed = list.installed
            problem = nil
        } catch {
            problem = error.localizedDescription
        }
        loaded = true
        if midiInstalled == nil {
            midiInstalled = await model.audioMidiInstalled()
        }
    }

    func start() async {
        guard let file else { return }
        sending = true
        notice = nil
        defer { sending = false }
        do {
            let started = try await model.transcribe(file, melody: melody)
            notice = Notice(
                text: started.stage == "queued"
                    ? String(localized: "Wartet in der Warteschlange und startet von selbst.")
                    : String(localized: "Läuft. Das dauert einige Minuten; die App darf dabei zu sein."),
                error: false)
            self.file = nil
        } catch {
            notice = Notice(text: error.localizedDescription, error: true)
        }
    }

    func score(of item: Components.Schemas.TranscriptionInfo) async -> String? {
        if let known = scores[item.id] { return known }
        do {
            let score = try await model.transcriptionScore(item.id)
            scores[item.id] = score
            return score
        } catch {
            problem = error.localizedDescription
            return nil
        }
    }

    /// The melody as the form's score; without chords the planning becomes "melody only", so the accompaniment
    /// follows the new style.
    func useScore(_ item: Components.Schemas.TranscriptionInfo) async {
        guard let score = await score(of: item) else { return }
        model.form.useScore(score)
        model.saveForm()
        model.page = .create
    }

    func delete(_ item: Components.Schemas.TranscriptionInfo) async {
        do {
            try await model.deleteTranscription(item.id)
            scores[item.id] = nil
            items.removeAll { $0.id == item.id }
        } catch {
            problem = error.localizedDescription
        }
    }

    func convertMidi() async {
        guard let midiFile else { return }
        convertingMidi = true
        midiError = nil
        midi = nil
        defer { convertingMidi = false }
        do {
            midi = try await model.audioToMidi(
                midiFile, mono: midiMono, quantize: midiQuantize, bends: midiBends && midiMono,
                tempo: tempoKnown ? tempo : nil)
        } catch {
            midiError = error.localizedDescription
        }
    }

    /// A transcription that just finished belongs in the list.
    func finished() {
        Task { await load() }
    }
}

extension AppModel {
    func openTranscription() {
        if transcriptionPage == nil {
            transcriptionPage = TranscriptionSession(model: self)
        }
        page = .transcribe
    }

    /// The transcriptions SheetSage2 is on, oldest first; one runs, the others wait in the queue.
    var transcriptionJobs: [Components.Schemas.TranscriptionState] {
        liveTranscriptions.values
            .filter { $0.finished != true }
            .sorted { ($0.updatedAt ?? .distantPast) < ($1.updatedAt ?? .distantPast) }
    }

    /// The last one that ended without a score, so its reason stays visible until the next start.
    var lastTranscriptionFailure: Components.Schemas.TranscriptionState? {
        guard transcriptionJobs.isEmpty,
              let last = liveTranscriptions.values.max(by: { ($0.updatedAt ?? .distantPast) < ($1.updatedAt ?? .distantPast) }),
              last.stage == "failed" || last.stage == "cancelled"
        else { return nil }
        return last
    }

    /// The transcriptions waiting in the server's queue.
    var waitingTranscriptions: [Components.Schemas.QueuedJob] {
        queue.filter { $0.kind == .transcription }
    }

    nonisolated static func transcriptionStage(_ stage: String?) -> String {
        switch stage ?? "queued" {
        case "starting": String(localized: "Startet")
        case "queued": String(localized: "Wartet")
        case "progress": String(localized: "Transkribiert")
        case "done": String(localized: "Fertig")
        case "failed": String(localized: "Fehlgeschlagen")
        case "cancelled": String(localized: "Abgebrochen")
        case let other: other
        }
    }

    nonisolated static func failureText(_ state: Components.Schemas.TranscriptionState) -> String {
        let reason = state.message.map { " – \($0)" } ?? ""
        return "\(state.fileName): \(transcriptionStage(state.stage))\(reason)"
    }

    func transcriptions() async throws -> Components.Schemas.TranscriptionList {
        let (data, _) = try await call("GET", address("api/transcriptions"))
        return try decode(Components.Schemas.TranscriptionList.self, data)
    }

    func transcribe(_ file: PickedFile, melody: TranscriptionSession.Melody) async throws
        -> Components.Schemas.TranscriptionState
    {
        let (data, _) = try await uploadFile(
            address("api/transcriptions"), fields: [("task", melody.rawValue)], fileField: "file", file: file,
            accept: [202])
        let state = try decode(Components.Schemas.TranscriptionState.self, data)
        if liveTranscriptions[state.id] == nil {
            liveTranscriptions[state.id] = state
        }
        return state
    }

    func cancelTranscription(_ id: String) async throws {
        _ = try await call("POST", address("api/transcriptions", id, "cancel"), accept: [202, 404])
    }

    func transcriptionScore(_ id: String) async throws -> String {
        let (data, _) = try await call("GET", address("api/transcriptions", id, "score"))
        return String(decoding: data, as: UTF8.self)
    }

    func deleteTranscription(_ id: String) async throws {
        _ = try await call("DELETE", address("api/transcriptions", id), accept: [204, 404])
        liveTranscriptions[id] = nil
    }

    /// The score or the ZIP with SheetSage2's MIDI parts and annotations, in the temporary folder to save or share.
    func downloadTranscription(_ item: Components.Schemas.TranscriptionInfo, zip: Bool) async throws -> URL {
        let (data, _) = try await call("GET", address("api/transcriptions", item.id, zip ? "zip" : "score"))
        let folder = FileManager.default.temporaryDirectory
            .appending(path: "Tonwerk-Transkriptionen", directoryHint: .isDirectory)
            .appending(path: UUID().uuidString, directoryHint: .isDirectory)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let name = Self.projectName((item.sourceName as NSString).deletingPathExtension)
        let file = folder.appending(path: "\(name).\(zip ? "zip" : "abc")")
        try data.write(to: file)
        return file
    }

    func audioMidiInstalled() async -> Bool? {
        guard let url = try? address("api/audio-midi"), let answer = try? await call("GET", url),
              let info = try? decode(Components.Schemas.AudioMidiInfo.self, answer.0)
        else { return nil }
        return info.installed
    }

    /// A recording (a sung track, typically) to MIDI with Basic Pitch; it takes seconds and is answered at once.
    func audioToMidi(_ file: PickedFile, mono: Bool, quantize: Bool, bends: Bool, tempo: Double?) async throws
        -> MidiFile
    {
        var fields = [
            ("mono", mono ? "true" : "false"), ("quantize", quantize ? "true" : "false"),
            ("bends", bends ? "true" : "false"),
        ]
        if let tempo { fields.append(("tempo", String(tempo))) }
        let (data, response) = try await uploadFile(
            address("api/audio-midi"), fields: fields, fileField: "file", file: file, accept: [200])
        let folder = FileManager.default.temporaryDirectory
            .appending(path: "Tonwerk-MIDI", directoryHint: .isDirectory)
            .appending(path: UUID().uuidString, directoryHint: .isDirectory)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let url = folder.appending(path: "\(Self.projectName(file.baseName)).mid")
        try data.write(to: url)
        let notes = response.value(forHTTPHeaderField: "X-Audio-Midi-Notes").flatMap(Int.init) ?? 0
        return MidiFile(url: url, notes: notes)
    }
}

extension AppModel {
    /// Those past the queue: SheetSage2 runs one at a time.
    var runningTranscriptions: [Components.Schemas.TranscriptionState] {
        transcriptionJobs.filter { $0.stage != "queued" }
    }
}
