import Foundation
import Observation
import TonwerkKit

/// The Logic page's state, the native counterpart of `LogicPage.vue`: one song's score converted on the server,
/// shown and played track by track, and exported as a Logic project. Kept by ``AppModel`` once opened, so a page
/// change does not lose the conversion or stop the instruments.
@MainActor
@Observable
final class LogicSession {
    private(set) var song: Song?
    var form: LogicForm {
        didSet {
            guard form != oldValue else { return }
            form.save(to: defaults)
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

    func open(_ song: Song) {
        guard song.id != self.song?.id else { return }
        self.song = song
        conversion = nil
        player.stop()
        recordingFile = nil
        player.setRecording(nil)
        Task {
            await library?.value
            await convert()
            await loadRecording()
        }
    }

    func convert() async {
        guard let song else { return }
        converting = true
        problem = nil
        defer { converting = false }
        do {
            let asked = form
            let options = asked.options(assignments: assignments, instruments: instruments)
            let result = try await model.convertLogic(song, options: options)
            guard song.id == self.song?.id else { return }
            conversion = result
            convertedForm = asked
            if let score = result.score {
                player.load(score, tracks: tracks, mixer: mixer)
            } else if let error = result.diagnostics.first(where: { $0.severity == "Error" }) {
                problem = error.message
            }
        } catch {
            problem = error.localizedDescription
        }
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
    }

    /// After a device came or went, a mute or solo or a sound changed.
    func reroute() {
        player.update(tracks)
    }

    func toggleMute(_ track: String) {
        muted.formSymmetricDifference([track])
        defaults.set(Array(muted), forKey: "logicMuted")
        reroute()
    }

    func toggleSolo(_ track: String) {
        soloed.formSymmetricDifference([track])
        reroute()
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
    }

    // MARK: Export

    func export() async throws -> (URL, [LogicDiagnostic]) {
        guard let song else { throw ServerError(String(localized: "Kein Song gewählt.")) }
        return try await model.exportLogic(
            song, options: form.options(assignments: assignments, instruments: instruments),
            splitSections: form.splitSections,
            instruments: LogicForm.exportInstruments(assignments, instruments))
    }
}

// MARK: Server

extension AppModel {
    func openInLogic(_ song: Song) {
        if logic == nil {
            logic = LogicSession(model: self)
        }
        logic?.open(song)
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

    private func logicForm(_ fields: [(String, String)]) -> (String, Data) {
        let boundary = "tonwerk-\(UUID().uuidString)"
        var body = Data()
        for (name, value) in fields {
            body.append(Data("--\(boundary)\r\nContent-Disposition: form-data; name=\"\(name)\"\r\n\r\n\(value)\r\n".utf8))
        }
        body.append(Data("--\(boundary)--\r\n".utf8))
        return (boundary, body)
    }

    private static func json(_ object: Any) -> String {
        (try? JSONSerialization.data(withJSONObject: object)).flatMap { String(data: $0, encoding: .utf8) } ?? "{}"
    }

    /// The song's score through the converter; a score that cannot be converted answers its errors (422).
    func convertLogic(_ song: Song, options: [String: Any]) async throws -> LogicConversion {
        guard let server else { throw ServerError(String(localized: "Kein Server verbunden.")) }
        let (boundary, body) = logicForm([("song", song.id), ("options", Self.json(options))])
        var request = URLRequest(url: server.appending(path: "api/logic/convert"))
        request.httpMethod = "POST"
        request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")
        request.timeoutInterval = 120
        let (data, response) = try await URLSession.shared.upload(for: request, from: body)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        guard status == 200 || status == 422 else {
            throw ServerError(problem: ServerError.text(of: data) ?? String(localized: "Der Server antwortete \(status)."))
        }
        return try JSONDecoder().decode(LogicConversion.self, from: data)
    }

    /// The Logic project as a ZIP in the temporary folder, with the export's warnings.
    func exportLogic(
        _ song: Song, options: [String: Any], splitSections: Bool, instruments: [String: Any]
    ) async throws -> (URL, [LogicDiagnostic]) {
        guard let server else { throw ServerError(String(localized: "Kein Server verbunden.")) }
        let name = Self.projectName(song.displayTitle)
        var fields = [
            ("song", song.id), ("options", Self.json(options)), ("name", name),
            ("splitSections", splitSections ? "true" : "false"),
        ]
        if !instruments.isEmpty {
            fields.append(("instruments", Self.json(instruments)))
        }
        let (boundary, body) = logicForm(fields)
        var request = URLRequest(url: server.appending(path: "api/logic/export"))
        request.httpMethod = "POST"
        request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")
        request.timeoutInterval = 300
        let (data, response) = try await URLSession.shared.upload(for: request, from: body)
        let http = response as? HTTPURLResponse
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
