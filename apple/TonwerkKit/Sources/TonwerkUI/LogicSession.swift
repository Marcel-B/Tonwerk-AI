import Foundation
import Observation
import TonwerkKit

/// What a score comes from: a library song, whose files the server reads, or a MIDI file or score.abc of one's own,
/// uploaded as `file` (the server reads a MIDI file back into a score first).
enum ScoreSource: Equatable {
    case song(Song)
    case file(PickedFile)

    /// The same song stays the same source when the library reloads with a new rating or title.
    var id: String {
        switch self {
        case .song(let song): "song:\(song.id)"
        case .file(let file): "file:\(file.url.path)"
        }
    }

    var song: Song? {
        if case .song(let song) = self { return song }
        return nil
    }

    /// The song's title or the file's name without its extension, for file names.
    var baseName: String {
        switch self {
        case .song(let song): song.displayTitle
        case .file(let file): file.baseName
        }
    }

    static func == (lhs: ScoreSource, rhs: ScoreSource) -> Bool { lhs.id == rhs.id }
}

/// The Logic page's state, the native counterpart of `LogicPage.vue`: one song's score converted on the server,
/// shown and played track by track, and exported as a Logic project. Kept by ``AppModel`` once opened, so a page
/// change does not lose the conversion or stop the instruments.
@MainActor
@Observable
final class LogicSession {
    private(set) var source: ScoreSource?
    var song: Song? { source?.song }
    var form: LogicForm {
        didSet {
            guard form != oldValue else { return }
            form.save(to: defaults)
            scheduleSave()
        }
    }
    private(set) var conversion: LogicConversion?
    private(set) var converting = false
    /// The form the conversion was made with, so showing the page again converts only after a change.
    private(set) var convertedForm: LogicForm?
    private(set) var problem: String?
    private(set) var instruments: [Components.Schemas.Instrument] = []
    private(set) var assignments: [String: Int64] = [:]
    private(set) var mixer = LogicMixer()
    /// Track names muted or soloed, kept across conversions like the web page's routing.
    private(set) var muted: Set<String> = []
    private(set) var soloed: Set<String> = []
    var playsRecording: Bool {
        didSet {
            defaults.set(playsRecording, forKey: "logicRecording")
            Task { await loadRecording() }
        }
    }

    /// The library song's stem sets as listed when it was opened; `AppModel.liveStems` lays newer ones over them.
    private var loadedStems: [Components.Schemas.StemSetState] = []
    /// Set by the picker; until then the newest set with vocals is taken, as on the web page. Empty for none.
    private var pickedStems: String?
    private(set) var stemsConfigured = false

    /// The saved session the page saves into (`logic/sessions.ts`), and the list to open one from.
    private(set) var saved: SavedLogicSession?
    private(set) var savedSessions: [SavedLogicSession] = []
    private var sessionsLoaded = false
    /// The source the saved session belongs to; it is only saved into while that source is open.
    private var savedSource: String?
    /// The source whose session was just deleted: none is made for it again until another source is opened.
    private var declinedSource: String?
    private var restoring = false
    private var creating = false
    private var lastSaved = ""
    private var saveTask: Task<Void, Never>?
    /// Basic Pitch, which bass and drums from the stems need.
    private(set) var midiInstalled = false

    let player = LogicPlayer()
    let outputs = MidiOutputs()

    private unowned let model: AppModel
    private let defaults: UserDefaults
    private var recordingFile: URL?
    private var mixerSave: Task<Void, Never>?
    /// Instruments, assignments and mixer; the first conversion waits for them, since channels come from them.
    private var library: Task<Void, Never>?

    init(model: AppModel, defaults: UserDefaults = .standard) {
        self.model = model
        self.defaults = defaults
        form = LogicForm.load(from: defaults)
        playsRecording = defaults.object(forKey: "logicRecording") as? Bool ?? true
        if let saved = defaults.stringArray(forKey: "logicMuted") {
            muted = Set(saved)
        }
        library = Task { await loadLibrary() }
    }

    var score: LogicScore? { conversion?.score }

    var sounds: SoundLibrary { model.soundLibrary }

    var voices: [LogicScore.Voice] {
        score?.playableVoices(includeChords: form.includeChords) ?? []
    }

    var warnings: [LogicDiagnostic] {
        conversion?.diagnostics.filter { $0.severity != "Info" } ?? []
    }

    // MARK: Song and conversion

    /// A source to work on; a library song goes on with its newest saved session, `saved` opens that one.
    func open(_ source: ScoreSource, saved session: SavedLogicSession? = nil) {
        guard source.id != self.source?.id || session != nil else { return }
        saveTask?.cancel()
        saved = nil
        savedSource = nil
        self.source = source
        conversion = nil
        player.stop()
        recordingFile = nil
        player.setRecording(nil)
        loadedStems = []
        pickedStems = nil
        Task {
            await library?.value
            await loadSessions()
            guard source.id == self.source?.id else { return }
            if let found = session ?? source.song.flatMap({ SavedLogicSession.newest(of: $0.id, in: savedSessions) }) {
                await restore(found, for: source)
            }
            await loadStems()
            await convert()
            await loadRecording()
        }
    }

    func convert() async {
        guard let source else { return }
        converting = true
        problem = nil
        defer { converting = false }
        do {
            let asked = form
            let options = asked.options(assignments: assignments, instruments: instruments)
            let result = try await model.convertScore(source, options: options, fields: stemPartFields)
            guard source.id == self.source?.id else { return }
            conversion = result
            convertedForm = asked
            scheduleSave()
            if let score = result.score {
                await player.load(score, tracks: tracks, mixer: mixer)
            } else if let error = result.diagnostics.first(where: { $0.severity == "Error" }) {
                problem = error.message
            }
        } catch {
            problem = error.localizedDescription
        }
    }

    // MARK: Stems

    func loadStems() async {
        guard let song else { return }
        async let sets = try? model.stemSets()
        async let info = model.voiceInfo()
        async let midi = model.audioMidiInstalled()
        let (list, voices, installed) = await (sets, info, midi)
        guard song.id == self.song?.id else { return }
        loadedStems = (list ?? []).filter { $0.songId == song.id }
        stemsConfigured = voices?.stemsConfigured == true
        midiInstalled = installed == true
    }

    /// The song's finished stem sets, newest first.
    private var doneStems: [Components.Schemas.StemSetState] {
        guard let song else { return [] }
        var sets = Dictionary(loadedStems.map { ($0.id, $0) }, uniquingKeysWith: { _, last in last })
        for set in model.liveStems.values where set.songId == song.id {
            sets[set.id] = set
        }
        return sets.values.filter { $0.stage == "done" }
            .sorted { ($0.createdAt ?? .distantPast) > ($1.createdAt ?? .distantPast) }
    }

    /// Sets with the vocals the Logic template has tracks for.
    var vocalStems: [Components.Schemas.StemSetState] {
        doneStems.filter { set in (set.stems ?? []).contains { $0.name == "vocals" || $0.name == "vocals_dry" } }
    }

    /// Whether the song's stems are being separated right now.
    var separatingStems: Bool {
        guard let song else { return false }
        return model.liveStems.values.contains { $0.songId == song.id && $0.finished != true && $0.stage != "cancelled" }
    }

    /// The set whose vocals go into the project, nil for none.
    var stemSetId: String? {
        get {
            let usable = vocalStems
            if let pickedStems {
                return usable.contains { $0.id == pickedStems } ? pickedStems : nil
            }
            return usable.first?.id
        }
        set {
            pickedStems = newValue ?? ""
            scheduleSave()
            if form.bassFromStems || form.drumsFromStems {
                Task { await convert() }
            }
        }
    }

    /// The picked set where it has the stem, else the newest that does: only some models separate bass and drums.
    func stems(with stem: String) -> Components.Schemas.StemSetState? {
        let sets = doneStems.filter { set in (set.stems ?? []).contains { $0.name == stem } }
        return sets.first { $0.id == stemSetId } ?? sets.first
    }

    /// The export takes the picked set's vocals as audio tracks besides what every conversion sends.
    private var exportFields: [(String, String)] {
        var fields = stemPartFields
        if song != nil, let set = stemSetId {
            fields.append(("stems", set))
        }
        return fields
    }

    /// Bass and drums heard in the stems go along with every conversion and the export.
    private var stemPartFields: [(String, String)] {
        guard song != nil, midiInstalled else { return [] }
        var fields: [(String, String)] = []
        if form.bassFromStems, let set = stems(with: "bass") {
            fields.append(("bassStems", set.id))
        }
        if form.drumsFromStems, let set = stems(with: "drums") {
            fields.append(("drumStems", set.id))
        }
        return fields
    }

    private func loadRecording() async {
        guard let song, playsRecording else {
            player.setRecording(nil)
            return
        }
        if recordingFile == nil {
            recordingFile = try? await model.downloadAudio(of: song)
        }
        guard song.id == self.song?.id else { return }
        player.setRecording(recordingFile)
    }

    // MARK: Instruments and routing

    func loadLibrary() async {
        async let instruments = model.logicInstruments()
        async let assignments = model.logicAssignments()
        async let mixer = model.logicMixer()
        let (list, assigned, mix) = await (instruments, assignments, mixer)
        if !model.soundLibrary.loaded {
            await model.soundLibrary.load()
        }
        self.instruments = list
        self.assignments = assigned.filter { _, id in list.contains { $0.id == id } }
        if let mix {
            self.mixer = mix
            player.apply(mix)
        }
    }

    func instrument(of track: String) -> Components.Schemas.Instrument? {
        assignments[track].flatMap { id in instruments.first { $0.id == id } }
    }

    /// The instrument's output, when it is connected.
    func output(of track: String) -> MidiOutputs.Output? {
        instrument(of: track).flatMap { outputs.output(named: $0.port) }
    }

    /// A track to an instrument, or back to the app's sound with nil. The score is converted again, since the
    /// track's channel and a drum machine's notes come from the instrument.
    func assign(_ track: String, to instrument: Int64?) async {
        let before = assignments
        assignments[track] = instrument
        do {
            try await model.setLogicAssignment(track, instrument)
        } catch {
            assignments = before
            problem = error.localizedDescription
            return
        }
        scheduleSave()
        await convert()
    }

    var tracks: [LogicPlayer.Track] {
        voices.map { voice in
            var route = LogicPlayer.Route.app
            if let instrument = instrument(of: voice.id), let output = outputs.output(named: instrument.port) {
                route = .midi(output.endpoint, channel: UInt8(min(16, max(1, instrument.channel)) - 1))
            }
            return LogicPlayer.Track(
                voice: voice, route: route, muted: muted.contains(voice.id), soloed: soloed.contains(voice.id),
                sound: voice.isDrums ? nil : model.soundLibrary.sound(forTrack: voice.id, kind: voice.kind))
        }
    }

    /// A track to one of the named sounds, or its default with nil; either way it plays in the app again.
    func give(_ track: String, sound name: String?) async {
        if assignments[track] != nil {
            await assign(track, to: nil)
        }
        await model.soundLibrary.give(track, named: name)
        reroute()
        scheduleSave()
    }

    /// After a device came or went, a mute or solo or a sound changed.
    func reroute() {
        player.update(tracks)
    }

    func toggleMute(_ track: String) {
        muted.formSymmetricDifference([track])
        defaults.set(Array(muted), forKey: "logicMuted")
        reroute()
        scheduleSave()
    }

    func toggleSolo(_ track: String) {
        soloed.formSymmetricDifference([track])
        reroute()
        scheduleSave()
    }

    // MARK: Mixer

    func setMix(_ track: String?, volume: Float? = nil, pan: Float? = nil) {
        if let track {
            var mix = mixer[track]
            if let volume { mix.volume = volume }
            if let pan { mix.pan = pan }
            mixer[track] = mix
        } else if let volume {
            mixer.master = volume
        }
        player.apply(mixer)
        // Saved shortly after, so a fader drag sends one request.
        mixerSave?.cancel()
        let settings = mixer
        mixerSave = Task { [model] in
            try? await Task.sleep(for: .milliseconds(600))
            guard !Task.isCancelled else { return }
            await model.saveLogicMixer(settings)
        }
        scheduleSave()
    }

    // MARK: Saved sessions

    /// The saved session while its source is open.
    var attachedSaved: SavedLogicSession? {
        guard let saved, savedSource == source?.id else { return nil }
        return saved
    }

    func loadSessions(again: Bool = false) async {
        guard again || !sessionsLoaded, let list = try? await model.logicSessions() else { return }
        savedSessions = list
        sessionsLoaded = true
    }

    /// A saved session from the list: its song from the library, or its uploaded score fetched again.
    func openSaved(_ session: SavedLogicSession) async {
        if let songId = session.songId {
            guard let song = model.song(songId) else {
                problem = String(localized: "Den Song dieser Sitzung gibt es nicht mehr.")
                return
            }
            open(.song(song), saved: session)
        } else {
            do {
                open(.file(try await model.logicSessionFile(session)), saved: session)
            } catch {
                problem = error.localizedDescription
            }
        }
    }

    /// Sounds, instruments and mixer belong to track names for every song; a session lays its song's back over them.
    private func restore(_ session: SavedLogicSession, for source: ScoreSource) async {
        restoring = true
        defer { restoring = false }
        let settings = LogicSessionSettings(json: session.settings)
        form = settings.form
        pickedStems = settings.stems
        muted = settings.muted
        soloed = settings.soloed
        defaults.set(Array(muted), forKey: "logicMuted")
        if let stored = settings.mixer {
            mixer.master = stored.master
            mixer.tracks.merge(stored.tracks) { _, new in new }
            player.apply(mixer)
            let mix = mixer
            Task { [model] in await model.saveLogicMixer(mix) }
        }
        if !model.soundLibrary.loaded {
            await model.soundLibrary.load()
        }
        for (track, sound) in settings.sounds {
            await model.soundLibrary.restore(track, to: sound)
        }
        for (track, id) in settings.instruments where assignments[track] != id {
            if let id, !instruments.contains(where: { $0.id == id }) {
                continue
            }
            assignments[track] = id
            try? await model.setLogicAssignment(track, id)
        }
        saved = session
        savedSource = source.id
        declinedSource = nil
        lastSaved = Self.text(of: snapshot())
    }

    private func snapshot() -> [String: Any] {
        LogicSessionSettings.json(
            form: form, stems: song != nil && !vocalStems.isEmpty ? (stemSetId ?? "") : nil,
            tracks: conversion?.score == nil ? nil : voices.map(\.id), muted: muted, soloed: soloed, mixer: mixer,
            sounds: model.soundLibrary.tracks, assignments: assignments, over: attachedSaved?.settings ?? [:])
    }

    private static func text(of settings: [String: Any]) -> String {
        (try? JSONSerialization.data(withJSONObject: settings, options: [.sortedKeys]))
            .flatMap { String(data: $0, encoding: .utf8) } ?? ""
    }

    /// Saved a second after the last change, so a fader drag sends one request.
    private func scheduleSave() {
        guard !restoring else { return }
        saveTask?.cancel()
        saveTask = Task { [weak self] in
            try? await Task.sleep(for: .seconds(1))
            guard !Task.isCancelled else { return }
            await self?.saveSession()
        }
    }

    /// Into the attached session, or a new one once the score is converted.
    private func saveSession() async {
        guard !restoring, sessionsLoaded, let source else { return }
        let settings = snapshot()
        let text = Self.text(of: settings)
        do {
            if let current = attachedSaved {
                guard text != lastSaved else { return }
                lastSaved = text
                keep(try await model.updateLogicSession(current.id, settings: settings))
            } else if conversion?.score != nil, source.id != declinedSource, !creating {
                creating = true
                defer { creating = false }
                let stored = try await model.createLogicSession(name: Self.name(of: source), source: source, settings: settings)
                savedSessions.insert(stored, at: 0)
                if self.source?.id == source.id {
                    saved = stored
                    savedSource = source.id
                    lastSaved = text
                }
            }
        } catch {
            lastSaved = ""
            problem = error.localizedDescription
        }
    }

    private static func name(of source: ScoreSource) -> String {
        switch source {
        case .song(let song): song.displayTitle
        case .file(let file): file.baseName
        }
    }

    private func keep(_ stored: SavedLogicSession) {
        savedSessions.removeAll { $0.id == stored.id }
        savedSessions.insert(stored, at: 0)
        if saved?.id == stored.id {
            saved = stored
        }
    }

    func renameSaved(_ name: String) async {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let current = attachedSaved, !trimmed.isEmpty, trimmed != current.name else { return }
        do {
            keep(try await model.updateLogicSession(current.id, name: trimmed))
        } catch {
            problem = error.localizedDescription
        }
    }

    /// The page stays as it is; a session for it is only made again after another source was opened.
    func deleteSaved() async {
        guard let current = attachedSaved else { return }
        do {
            try await model.deleteLogicSession(current.id)
            savedSessions.removeAll { $0.id == current.id }
            declinedSource = savedSource
            saved = nil
            savedSource = nil
        } catch {
            problem = error.localizedDescription
        }
    }

    // MARK: Export

    func export() async throws -> (URL, [LogicDiagnostic]) {
        guard let source else { throw ServerError(String(localized: "Kein Song gewählt.")) }
        return try await model.exportLogic(
            source, options: form.options(assignments: assignments, instruments: instruments),
            splitSections: form.splitSections,
            instruments: LogicForm.exportInstruments(assignments, instruments),
            fields: exportFields)
    }
}

// MARK: Server

extension AppModel {
    func openInLogic(_ song: Song) {
        if logic == nil {
            logic = LogicSession(model: self)
        }
        logic?.open(.song(song))
        page = .logic
    }

    /// A MIDI file or score.abc of one's own, from the file picker or handed over by another page.
    func openInLogic(file: PickedFile) {
        if logic == nil {
            logic = LogicSession(model: self)
        }
        logic?.open(.file(file))
        page = .logic
    }

    func logicInstruments() async -> [Components.Schemas.Instrument] {
        (try? await tonwerk?.api.listInstruments().ok.body.json) ?? []
    }

    func logicAssignments() async -> [String: Int64] {
        (try? await tonwerk?.api.listTrackAssignments().ok.body.json.additionalProperties) ?? [:]
    }

    func setLogicAssignment(_ track: String, _ instrument: Int64?) async throws {
        guard let tonwerk else { return }
        guard case .noContent = try await tonwerk.api.setTrackAssignment(
            path: .init(track: track), body: .json(.init(instrumentId: instrument)))
        else { throw ServerError(String(localized: "Die Zuordnung wurde nicht gespeichert.")) }
    }

    func logicMixer() async -> LogicMixer? {
        guard let server,
              let (data, _) = try? await URLSession.shared.data(from: server.appending(path: "api/logic/synths/mixer")),
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
        else { return nil }
        return LogicMixer(json: object["settings"])
    }

    /// The web page's mixer keeps its settings as one opaque JSON row; tracks this score lacks stay as they were.
    func saveLogicMixer(_ mixer: LogicMixer) async {
        guard let server else { return }
        var settings = mixer.json
        if let stored = await logicMixer() {
            var tracks = stored.tracks
            tracks.merge(mixer.tracks) { _, new in new }
            settings = LogicMixer(json: ["master": mixer.master]).with(tracks: tracks).json
        }
        var request = URLRequest(url: server.appending(path: "api/logic/synths/mixer"))
        request.httpMethod = "PUT"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try? JSONSerialization.data(withJSONObject: ["settings": settings])
        _ = try? await URLSession.shared.data(for: request)
    }

    /// The song's FLAC in the temporary folder, for the recording lane.
    func downloadAudio(of song: Song) async throws -> URL {
        guard let server else { throw ServerError(String(localized: "Kein Server verbunden.")) }
        let (file, response) = try await URLSession.shared.download(
            from: server.appending(path: "api/songs/\(song.run)/\(song.name)/audio"))
        guard (response as? HTTPURLResponse)?.statusCode == 200 else {
            throw ServerError(String(localized: "Die Aufnahme ließ sich nicht laden."))
        }
        let target = FileManager.default.temporaryDirectory
            .appending(path: "Tonwerk-Logic-\(song.run)-\(song.name).flac")
        try? FileManager.default.removeItem(at: target)
        try FileManager.default.moveItem(at: file, to: target)
        return target
    }

    private static func json(_ object: Any) -> String {
        (try? JSONSerialization.data(withJSONObject: object)).flatMap { String(data: $0, encoding: .utf8) } ?? "{}"
    }

    /// A Logic page request with its score: a song by its id, a file as the upload `file`. 200 and 422 both come
    /// back, since a score that cannot be converted answers its errors.
    private func postScore(
        _ path: String, _ source: ScoreSource, fields: [(String, String)], timeout: TimeInterval
    ) async throws -> (Data, HTTPURLResponse) {
        let url = try address(path)
        switch source {
        case .file(let file):
            return try await uploadFile(url, fields: fields, fileField: "file", file: file, accept: [200, 422])
        case .song(let song):
            let boundary = "tonwerk-\(UUID().uuidString)"
            var body = Data()
            for (name, value) in [("song", song.id)] + fields {
                body.append(Data("--\(boundary)\r\nContent-Disposition: form-data; name=\"\(name)\"\r\n\r\n\(value)\r\n".utf8))
            }
            body.append(Data("--\(boundary)--\r\n".utf8))
            var request = URLRequest(url: url)
            request.httpMethod = "POST"
            request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")
            request.timeoutInterval = timeout
            let (data, response) = try await URLSession.shared.upload(for: request, from: body)
            let status = (response as? HTTPURLResponse)?.statusCode ?? 0
            guard let http = response as? HTTPURLResponse, status == 200 || status == 422 else {
                throw ServerError(problem: ServerError.text(of: data) ?? String(localized: "Der Server antwortete \(status)."))
            }
            return (data, http)
        }
    }

    /// The score through the converter, for the Logic page and the backing vocals.
    func convertScore(
        _ source: ScoreSource, options: [String: Any], fields: [(String, String)] = []
    ) async throws -> LogicConversion {
        let (data, _) = try await postScore(
            "api/logic/convert", source, fields: [("options", Self.json(options))] + fields, timeout: 120)
        return try JSONDecoder().decode(LogicConversion.self, from: data)
    }

    /// The Logic project as a ZIP in the temporary folder, with the export's warnings.
    func exportLogic(
        _ source: ScoreSource, options: [String: Any], splitSections: Bool, instruments: [String: Any],
        fields extra: [(String, String)] = []
    ) async throws -> (URL, [LogicDiagnostic]) {
        let name = Self.projectName(source.baseName)
        var fields = [
            ("options", Self.json(options)), ("name", name), ("splitSections", splitSections ? "true" : "false"),
        ]
        if !instruments.isEmpty {
            fields.append(("instruments", Self.json(instruments)))
        }
        fields += extra
        let (data, response) = try await postScore("api/logic/export", source, fields: fields, timeout: 300)
        let http: HTTPURLResponse? = response
        if http?.statusCode == 422, let refused = try? JSONDecoder().decode(LogicConversion.self, from: data) {
            throw ServerError(refused.diagnostics.first { $0.severity == "Error" }?.message
                ?? String(localized: "Aus dem Song lässt sich kein Projekt machen."))
        }
        guard http?.statusCode == 200 else {
            throw ServerError(problem: ServerError.text(of: data)
                ?? String(localized: "Der Server antwortete \(http?.statusCode ?? 0)."))
        }
        let warnings = http?.value(forHTTPHeaderField: "X-YueToLogic-Diagnostics")
            .flatMap { try? JSONDecoder().decode([LogicDiagnostic].self, from: Data($0.utf8)) } ?? []
        let folder = FileManager.default.temporaryDirectory
            .appending(path: "Tonwerk-Logic", directoryHint: .isDirectory)
            .appending(path: UUID().uuidString, directoryHint: .isDirectory)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let file = folder.appending(path: "\(name).logicx.zip")
        try data.write(to: file)
        return (file, warnings)
    }

    /// A file name from the title: no slashes or colons, which the Mac's file system refuses.
    nonisolated static func projectName(_ title: String) -> String {
        let cleaned = title.components(separatedBy: CharacterSet(charactersIn: "/\\:")).joined(separator: "-")
            .trimmingCharacters(in: .whitespaces)
        return cleaned.isEmpty ? "Tonwerk" : cleaned
    }
}

extension LogicMixer {
    func with(tracks: [String: Mix]) -> LogicMixer {
        var copy = self
        copy.tracks = tracks
        return copy
    }
}
