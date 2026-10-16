import AVFoundation
import Foundation
import Observation
import TonwerkKit

/// A voice recorded with the device's microphone for the speech lab, as the web page's `MediaRecorder` does: AAC in
/// the temporary folder, at most `maxSeconds`, which ffmpeg on the server reads. Without the phone's call processing,
/// since noise suppression and gain control change the timbre the model copies.
@MainActor
@Observable
final class VoiceRecorder {
    /// Enough for any model; the server keeps 30 seconds at most.
    static let maxSeconds = 30

    private(set) var recording = false
    private(set) var seconds = 0

    @ObservationIgnored private var recorder: AVAudioRecorder?
    @ObservationIgnored private var timer: Task<Void, Never>?
    @ObservationIgnored private var started = Date()
    @ObservationIgnored private var finished: (@MainActor (PickedFile?) -> Void)?

    /// Starts recording; `done` gets the file once stopped by hand or at the limit.
    func start(_ done: @escaping @MainActor (PickedFile?) -> Void) async throws {
        guard await AVAudioApplication.requestRecordPermission() else {
            throw ServerError(String(localized: "Tonwerk darf das Mikrofon nicht benutzen. Das lässt sich in den Einstellungen erlauben."))
        }
        #if os(iOS)
        let session = AVAudioSession.sharedInstance()
        try session.setCategory(.playAndRecord, mode: .measurement, options: [.defaultToSpeaker, .allowBluetoothA2DP])
        try session.setActive(true)
        #endif
        let folder = FileManager.default.temporaryDirectory
            .appending(path: "Tonwerk-Uploads", directoryHint: .isDirectory)
            .appending(path: UUID().uuidString, directoryHint: .isDirectory)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let url = folder.appending(path: "recording.m4a")
        let recorder = try AVAudioRecorder(url: url, settings: [
            AVFormatIDKey: kAudioFormatMPEG4AAC, AVSampleRateKey: 48_000, AVNumberOfChannelsKey: 1,
            AVEncoderAudioQualityKey: AVAudioQuality.high.rawValue,
        ])
        guard recorder.record(forDuration: TimeInterval(Self.maxSeconds)) else {
            throw ServerError(String(localized: "Die Aufnahme ließ sich nicht starten."))
        }
        self.recorder = recorder
        finished = done
        started = Date()
        seconds = 0
        recording = true
        timer = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .milliseconds(250))
                guard let self, self.recording else { return }
                self.seconds = Int(Date().timeIntervalSince(self.started))
                if self.recorder?.isRecording != true {
                    self.stop()
                }
            }
        }
    }

    func stop() {
        guard recording, let recorder else { return }
        timer?.cancel()
        recorder.stop()
        recording = false
        #if os(iOS)
        try? AVAudioSession.sharedInstance().setCategory(.playback)
        #endif
        let file = PickedFile(url: recorder.url, name: "recording.m4a", seconds: Double(seconds))
        self.recorder = nil
        let done = finished
        finished = nil
        done?(seconds > 0 ? file : nil)
    }
}

/// The speech lab's state (`SpeechLab.vue`, `SpeechCleanPanel.vue`): recorded voices, a text spoken by the chosen
/// local text-to-speech models to compare them, and speech recordings cleaned of noise. Kept by ``AppModel`` once
/// opened, so a typed text or a chosen file survives a page change.
@MainActor
@Observable
final class SpeechSession {
    private(set) var info: Components.Schemas.SpeechInfo?
    private(set) var loading = false
    var problem: String?

    // Voices
    /// The recorded voice the models clone; nil for each model's own.
    var voiceId: String?
    var voiceName = ""
    var readAloud = String(localized: "Hallo, das ist meine Stimme. Ich lese diesen Satz ruhig und deutlich vor, damit das Modell hören kann, wie ich klinge.")
    /// The recording or chosen file, until it is saved.
    var recorded: PickedFile?
    private(set) var savingVoice = false
    let recorder = VoiceRecorder()

    // Speaking
    var text = String(localized: "Willkommen zu einer neuen Folge! Heute geht es darum, wie Musik auf dem eigenen Rechner entsteht, ganz ohne Cloud. Klingt das hier natürlich? Hört man die Pausen, die Betonung, vielleicht sogar ein Lächeln? Genau das wollen wir herausfinden.")
    var picked: Set<String> = []
    private(set) var sending = false

    // Cleaning
    var cleanModel: String?
    var cleanFile: PickedFile?
    private(set) var cleaning = false

    let preview = AudioPreview()

    private unowned let model: AppModel

    init(model: AppModel) {
        self.model = model
        preview.songPlayer = model.player
    }

    func load() async {
        guard !loading else { return }
        loading = true
        defer { loading = false }
        do {
            let info = try await model.speechLab()
            self.info = info
            problem = nil
            // Models picked before stay picked; the first visit starts with the first one offered.
            if picked.isEmpty, let first = info.models.first {
                picked = [first.id]
            }
            if let voiceId, !info.voices.contains(where: { $0.id == voiceId }) {
                self.voiceId = nil
            }
            if cleanModel.map({ id in !info.cleanModels.contains { $0.id == id } }) ?? true {
                cleanModel = info.cleanModels.first?.id
            }
        } catch {
            problem = String(localized: "Das Sprachlabor ließ sich nicht laden: \(error.localizedDescription)")
        }
    }

    /// What was loaded, with the live changes laid over it; a deleted take arrives as cancelled and leaves.
    var takes: [Components.Schemas.SpeechTake] {
        Self.merge(info?.takes ?? [], live: Array(model.liveSpeech.values))
    }

    nonisolated static func merge(
        _ loaded: [Components.Schemas.SpeechTake], live: [Components.Schemas.SpeechTake]
    ) -> [Components.Schemas.SpeechTake] {
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

    /// Spoken takes and cleaned recordings share the lab's line, but not its list.
    var spoken: [Components.Schemas.SpeechTake] { takes.filter { $0.source == nil } }
    var cleanups: [Components.Schemas.SpeechTake] { takes.filter { $0.source != nil } }

    private func added(_ new: [Components.Schemas.SpeechTake]) {
        guard var info else { return }
        info.takes = new + info.takes
        self.info = info
    }

    // MARK: Voices

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

    func saveVoice() async {
        guard let recorded, !voiceName.trimmingCharacters(in: .whitespaces).isEmpty, !savingVoice else { return }
        savingVoice = true
        defer { savingVoice = false }
        do {
            let voice = try await model.addSpeechVoice(
                label: voiceName.trimmingCharacters(in: .whitespaces),
                transcript: readAloud.trimmingCharacters(in: .whitespacesAndNewlines), file: recorded)
            self.recorded = nil
            voiceName = ""
            problem = nil
            await load()
            voiceId = voice.id
        } catch {
            problem = error.localizedDescription
        }
    }

    func deleteVoice(_ voice: Components.Schemas.SpeechVoice) async {
        if preview.current == "voice:\(voice.id)" {
            preview.stop()
        }
        do {
            try await model.deleteSpeechVoice(voice.id)
            await load()
        } catch {
            problem = error.localizedDescription
        }
    }

    // MARK: Speaking

    /// The chosen models in the order they are offered, as the server speaks them.
    var pickedModels: [String] {
        (info?.models ?? []).map(\.id).filter(picked.contains)
    }

    func speak() async {
        let text = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty, !pickedModels.isEmpty, !sending else { return }
        sending = true
        defer { sending = false }
        do {
            added(try await model.speak(text, voiceId: voiceId, models: pickedModels))
            problem = nil
        } catch {
            problem = error.localizedDescription
        }
    }

    /// Stops a take still in the line or deletes a finished one.
    func delete(_ take: Components.Schemas.SpeechTake) async {
        if preview.current?.hasPrefix(take.id) == true {
            preview.stop()
        }
        do {
            try await model.deleteSpeechTake(take.id)
            if var info {
                info.takes.removeAll { $0.id == take.id }
                self.info = info
            }
        } catch {
            problem = error.localizedDescription
        }
    }

    // MARK: Cleaning

    func clean() async {
        guard let cleanFile, let cleanModel, !cleaning else { return }
        cleaning = true
        defer { cleaning = false }
        do {
            added([try await model.cleanSpeech(cleanFile, model: cleanModel)])
            self.cleanFile = nil
            problem = nil
        } catch {
            problem = error.localizedDescription
        }
    }

    // MARK: Names

    nonisolated static func stageName(_ take: Components.Schemas.SpeechTake) -> String {
        switch take.stage ?? "queued" {
        case "queued": String(localized: "Wartet")
        case "loading": String(localized: "Lädt Modell")
        case "speaking": String(localized: "Spricht")
        case "cleaning":
            take.progress.map { String(localized: "Säubert \(Int(($0 * 100).rounded())) %") }
                ?? String(localized: "Säubert")
        case "done": String(localized: "Fertig")
        case "failed": String(localized: "Fehlgeschlagen")
        case "cancelled": String(localized: "Abgebrochen")
        case let other: other
        }
    }

    /// The page's own words for the offered models; a model added in the server's configuration brings its note in
    /// English.
    nonisolated static func note(_ model: Components.Schemas.SpeechModelInfo) -> String {
        switch model.id {
        case "chatterbox": String(localized: "23 Sprachen, auch Deutsch; klont aus wenigen Sekunden.")
        case "qwen3-tts": String(localized: "Klont aus 5 bis 10 Sekunden und braucht dafür den Wortlaut der Aufnahme.")
        case "higgs-v2": String(localized: "Ausdrucksstark, erkennt die Sprache am Text; braucht den Wortlaut der Aufnahme.")
        case "higgs-v3":
            String(localized: "Auf Gespräche trainiert, Tags wie <|emotion:amusement|> im Text. Für Podcasts erlaubt, wenn Boson AI genannt wird.")
        case "higgs-v3-calm":
            String(localized: "Higgs Audio v3, vorsichtiger gesampelt (Temperatur 0.7, top-k 20 statt 0.8 und 50), und jedes Stück hört neben der Aufnahme das vorige: soll weniger sirren und die Stimme zwischen den Stücken ruhiger halten, klingt vielleicht etwas gleichförmiger. Zum Vergleichen beide sprechen lassen.")
        case "moss-tts": String(localized: "48 kHz, Pausen als [pause 1.5s] im Text.")
        case "mossformer2-se": String(localized: "Nimmt Rauschen und viel vom Raumklang gründlich weg; die stärkere der beiden.")
        case "deepfilternet3":
            String(localized: "Klein und schnell, sanft: nimmt gleichmäßiges Rauschen weg und lässt die Stimme samt Hall, wie sie war.")
        default: model.note
        }
    }

    /// "geladen" or the download's size, in MB below a gigabyte as the clean models are.
    nonisolated static func download(_ model: Components.Schemas.SpeechModelInfo) -> String {
        if model.downloaded { return String(localized: "geladen") }
        let size = model.downloadGb < 1
            ? "\(max(1, Int((model.downloadGb * 1000).rounded()))) MB"
            : "\(model.downloadGb.formatted(.number.precision(.fractionLength(0...1)))) GB"
        return String(localized: "\(size) Download")
    }

    nonisolated static func stats(_ take: Components.Schemas.SpeechTake) -> String {
        let number = { (value: Double?) in value.map { $0.formatted(.number.precision(.fractionLength(0...1))) } ?? "–" }
        return String(localized: "Laden \(number(take.loadSeconds)) s · Sprechen \(number(take.speakSeconds)) s · \(number(take.peakMemoryGb)) GB Speicher")
    }
}

extension AppModel {
    func openSpeech() {
        if speechPage == nil {
            speechPage = SpeechSession(model: self)
        }
        page = .speech
    }

    /// Takes spoken or cleaned and not finished, oldest first, as the queue lists them.
    var speechJobs: [Components.Schemas.SpeechTake] {
        liveSpeech.values
            .filter { $0.finished != true && $0.stage != "cancelled" }
            .sorted { ($0.createdAt ?? .distantPast) < ($1.createdAt ?? .distantPast) }
    }

    func speechLab() async throws -> Components.Schemas.SpeechInfo {
        let (data, _) = try await call("GET", address("api/speech"))
        return try decode(Components.Schemas.SpeechInfo.self, data)
    }

    func addSpeechVoice(label: String, transcript: String, file: PickedFile) async throws
        -> Components.Schemas.SpeechVoice
    {
        let (data, _) = try await uploadFile(
            address("api/speech/voices"), fields: [("label", label), ("transcript", transcript)], fileField: "file",
            file: file, accept: [201])
        return try decode(Components.Schemas.SpeechVoice.self, data)
    }

    func deleteSpeechVoice(_ id: String) async throws {
        _ = try await call("DELETE", address("api/speech/voices", id), accept: [204, 404])
    }

    func speak(_ text: String, voiceId: String?, models: [String]) async throws -> [Components.Schemas.SpeechTake] {
        let (data, _) = try await call(
            "POST", address("api/speech/takes"),
            json: ["text": text, "voiceId": voiceId.map { $0 as Any } ?? NSNull(), "models": models], accept: [202])
        return try decode([Components.Schemas.SpeechTake].self, data)
    }

    func cleanSpeech(_ file: PickedFile, model: String) async throws -> Components.Schemas.SpeechTake {
        let (data, _) = try await uploadFile(
            address("api/speech/cleanups"), fields: [("model", model)], fileField: "file", file: file, accept: [202])
        return try decode(Components.Schemas.SpeechTake.self, data)
    }

    func deleteSpeechTake(_ id: String) async throws {
        _ = try await call("DELETE", address("api/speech/takes", id), accept: [204, 404])
    }

    func speechVoiceAudio(_ id: String) throws -> URL { try address("api/speech/voices", id, "audio") }
    func speechTakeAudio(_ id: String) throws -> URL { try address("api/speech/takes", id, "audio") }
    func speechTakeSource(_ id: String) throws -> URL { try address("api/speech/takes", id, "source") }

    /// A take's WAV in the temporary folder, to save or share, named as the server names the download.
    func downloadSpeechTake(_ take: Components.Schemas.SpeechTake) async throws -> URL {
        let (data, _) = try await call("GET", address("api/speech/takes", take.id, "audio"))
        let folder = FileManager.default.temporaryDirectory
            .appending(path: "Tonwerk-Sprache", directoryHint: .isDirectory)
            .appending(path: UUID().uuidString, directoryHint: .isDirectory)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let name = take.source.map { "\(Self.projectName(($0 as NSString).deletingPathExtension))-clean" }
            ?? "\(Self.projectName(take.modelLabel))-\(take.id.prefix(8))"
        let file = folder.appending(path: "\(name).wav")
        try data.write(to: file)
        return file
    }
}
