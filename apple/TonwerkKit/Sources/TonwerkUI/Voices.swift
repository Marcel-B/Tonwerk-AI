import AVFoundation
import Foundation
import Observation
import TonwerkKit

/// How a voice sings a song or a recording (`voiceChoices.ts`), kept by ``AppModel`` between songs so a second
/// version with the same voice needs no choosing.
struct VoiceSettings: Equatable {
    var voice: String?
    /// Whole octaves only: any other shift would sing out of key over the song's own accompaniment.
    var semitones = 0
    var strength = 0.7
    var steps = 50
    var keepReverb = true

    static let octaves: [(value: Int, name: String)] = [
        (-12, String(localized: "−1 Oktave")), (0, String(localized: "Original")), (12, String(localized: "+1 Oktave")),
    ]
    static let strengths: [(value: Double, name: String)] = [
        (0.5, String(localized: "Leicht")), (0.7, String(localized: "Mittel")), (0.9, String(localized: "Stark")),
    ]
    static let stepChoices: [(value: Int, name: String)] = [
        (25, String(localized: "Schnell")), (50, String(localized: "Normal")), (100, String(localized: "Fein")),
    ]
}

/// A file chosen on the device, copied into the temporary folder: the security scope of the picker's URL ends with
/// the callback, while the upload and the preview come later.
struct PickedFile: Equatable {
    let url: URL
    let name: String
    /// Known once AVFoundation has read the header; nil for a format it cannot play.
    var seconds: Double?

    static func copy(from picked: URL) async throws -> PickedFile {
        let scoped = picked.startAccessingSecurityScopedResource()
        defer { if scoped { picked.stopAccessingSecurityScopedResource() } }
        let folder = FileManager.default.temporaryDirectory
            .appending(path: "Tonwerk-Uploads", directoryHint: .isDirectory)
            .appending(path: UUID().uuidString, directoryHint: .isDirectory)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let copy = folder.appending(path: picked.lastPathComponent)
        try FileManager.default.copyItem(at: picked, to: copy)
        var file = PickedFile(url: copy, name: picked.lastPathComponent)
        if let duration = try? await AVURLAsset(url: copy).load(.duration), duration.seconds.isFinite,
           duration.seconds > 0
        {
            file.seconds = duration.seconds
        }
        return file
    }

    /// The name without its extension, a good start for a voice's name.
    var baseName: String { (name as NSString).deletingPathExtension }
}

/// One player for the short things on the voices page (a reference voice, a stem, a swap), apart from the song
/// player: they are something to compare, not to queue. Switching to another stem of the same set keeps the time,
/// so the parts can be heard one after the other at the same bar.
@MainActor
@Observable
final class AudioPreview {
    /// What plays or is paused, by a key the page chooses (`set/stem`, `voice:id`).
    private(set) var current: String?
    private(set) var isPlaying = false
    private(set) var position: Double = 0

    @ObservationIgnored private let player = AVPlayer()
    @ObservationIgnored private var observer: Any?
    @ObservationIgnored private var ended: NSObjectProtocol?
    /// Where "play the part" stops.
    @ObservationIgnored private var stopAt: Double?
    @ObservationIgnored weak var songPlayer: Player?

    init() {
        observer = player.addPeriodicTimeObserver(
            forInterval: CMTime(seconds: 0.05, preferredTimescale: 600), queue: .main
        ) { [weak self] time in
            MainActor.assumeIsolated { self?.tick(time.seconds) }
        }
    }

    private func tick(_ seconds: Double) {
        guard seconds.isFinite else { return }
        position = seconds
        if let stopAt, seconds >= stopAt {
            pause()
            self.stopAt = nil
        }
    }

    func plays(_ key: String) -> Bool { current == key && isPlaying }

    /// Plays `url` from `at`, or from where the same group (the part before `/`) was, as stems of one set do.
    func toggle(_ key: String, url: URL, at start: Double? = nil, until end: Double? = nil) {
        if current == key, start == nil {
            isPlaying ? pause() : resume()
            return
        }
        let group = { (key: String) in key.split(separator: "/").first.map(String.init) }
        let from = start ?? (current.flatMap(group) == group(key) && group(key) != key ? position : 0)
        play(key, url: url, at: from, until: end)
    }

    func seek(_ key: String, url: URL, to seconds: Double) {
        if current == key {
            player.seek(to: CMTime(seconds: seconds, preferredTimescale: 600))
            position = seconds
        } else {
            play(key, url: url, at: seconds, until: nil)
        }
    }

    private func play(_ key: String, url: URL, at seconds: Double, until end: Double?) {
        // Two songs at once help nobody.
        if songPlayer?.isPlaying == true {
            songPlayer?.pause()
        }
        #if os(iOS)
        try? AVAudioSession.sharedInstance().setCategory(.playback)
        try? AVAudioSession.sharedInstance().setActive(true)
        #endif
        let item = AVPlayerItem(url: url)
        if let ended {
            NotificationCenter.default.removeObserver(ended)
        }
        ended = NotificationCenter.default.addObserver(
            forName: .AVPlayerItemDidPlayToEndTime, object: item, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.stop() }
        }
        player.replaceCurrentItem(with: item)
        current = key
        position = seconds
        stopAt = end
        if seconds > 0 {
            player.seek(to: CMTime(seconds: seconds, preferredTimescale: 600))
        }
        player.play()
        isPlaying = true
    }

    private func resume() {
        player.play()
        isPlaying = true
    }

    func pause() {
        player.pause()
        isPlaying = false
    }

    func stop() {
        player.pause()
        player.replaceCurrentItem(with: nil)
        current = nil
        isPlaying = false
        position = 0
        stopAt = nil
    }
}

/// A MIDI file made from a stem, in the temporary folder, with how many notes it holds.
struct MidiFile: Equatable {
    let url: URL
    let notes: Int
}

/// The voices page's state (`VoicesPanel.vue`, `StemsPanel.vue`, `VoiceSwapPanel.vue`), kept by ``AppModel`` once
/// opened like the Logic page's, so a chosen file or a running conversion survives a page change.
@MainActor
@Observable
final class VoicesSession {
    enum Tab: String, CaseIterable, Identifiable {
        case voices
        case stems
        case swap

        var id: String { rawValue }

        var name: String {
            switch self {
            case .voices: String(localized: "Stimmen")
            case .stems: String(localized: "Stems")
            case .swap: String(localized: "Spur tauschen")
            }
        }
    }

    enum StemSource: Hashable {
        case song
        case file
    }

    var tab = Tab.voices
    private(set) var info: Components.Schemas.VoiceInfo?
    private(set) var voices: [Components.Schemas.ReferenceVoice] = []
    private(set) var voicesProblem: String?
    private(set) var models: [Components.Schemas.StemModel] = []
    private var loadedStems: [Components.Schemas.StemSetState] = []
    private var loadedSwaps: [Components.Schemas.SwapState] = []
    /// Deleted here; the server's `cancelled` event may come after the list was read again.
    private var removed: Set<String> = []
    var problem: String?

    let preview = AudioPreview()

    // New reference voice.
    var newLabel = ""
    var newFile: PickedFile? {
        didSet {
            guard newFile != oldValue else { return }
            clipStart = 0
            clipEnd = min(newFile?.seconds ?? 0, Self.keptSeconds)
            if let newFile, newLabel.trimmingCharacters(in: .whitespaces).isEmpty {
                newLabel = newFile.baseName
            }
        }
    }
    var clipStart: Double = 0
    var clipEnd: Double = 0
    private(set) var adding = false

    /// The server's limits (Seed-VC's): it keeps 25 seconds and refuses a part shorter than 3.
    static let keptSeconds = 25.0
    static let minSeconds = 3.0

    // Stems.
    var stemSource = StemSource.song
    var stemSong: String?
    var stemFile: PickedFile?
    var stemModel: String? {
        didSet { defaults.set(stemModel, forKey: "stemModel") }
    }
    var dereverb = false
    private(set) var separating = false

    // MIDI from stems.
    var midiMono = true
    var midiQuantize = false
    var midiBends = false
    /// An upload has no score to take a tempo from.
    var midiTempo = 120.0
    private(set) var midi: [String: MidiFile] = [:]
    private(set) var midiErrors: [String: String] = [:]
    private(set) var converting: String?

    // Swapping a recording's voice.
    var swapFile: PickedFile?
    var swapSeparate = false
    private(set) var swapping = false

    private unowned let model: AppModel
    private let defaults: UserDefaults

    init(model: AppModel, defaults: UserDefaults = .standard) {
        self.model = model
        self.defaults = defaults
        stemModel = defaults.string(forKey: "stemModel")
        preview.songPlayer = model.player
    }

    func load() async {
        info = await model.voiceInfo()
        async let voiceList: Void = loadVoices()
        async let stemList: Void = loadStems()
        async let swapList: Void = loadSwaps()
        _ = await (voiceList, stemList, swapList)
        if models.isEmpty, info?.stemsConfigured == true {
            await loadModels()
        }
    }

    func loadVoices() async {
        guard info?.voicesConfigured == true else { return }
        do {
            voices = try await model.referenceVoices()
            voicesProblem = nil
        } catch {
            voicesProblem = error.localizedDescription
        }
    }

    private func loadStems() async {
        if let sets = try? await model.stemSets() {
            loadedStems = sets
        }
    }

    private func loadSwaps() async {
        if let list = try? await model.swaps() {
            loadedSwaps = list
        }
    }

    private func loadModels() async {
        do {
            models = try await model.stemModels()
            if !models.contains(where: { $0.id == stemModel }) {
                stemModel = models.first { $0.isDefault == true }?.id ?? models.first?.id
            }
        } catch {
            problem = error.localizedDescription
        }
    }

    // MARK: Reference voices

    var clipSeconds: Double { max(0, clipEnd - clipStart) }

    var canAdd: Bool {
        guard let newFile, !adding, !newLabel.trimmingCharacters(in: .whitespaces).isEmpty else { return false }
        return newFile.seconds == nil || clipSeconds >= Self.minSeconds
    }

    func addVoice() async {
        guard canAdd, let file = newFile else { return }
        adding = true
        defer { adding = false }
        // Only what differs from the whole file is sent, so an untouched choice is a plain upload.
        let start = file.seconds != nil && clipStart > 0 ? clipStart : nil
        let end = file.seconds.flatMap { clipEnd < $0 ? clipEnd : nil }
        do {
            try await model.addReferenceVoice(newLabel.trimmingCharacters(in: .whitespaces), file: file, start: start, end: end)
            if preview.current == "new" {
                preview.stop()
            }
            newFile = nil
            newLabel = ""
            problem = nil
            await loadVoices()
        } catch {
            problem = error.localizedDescription
        }
    }

    /// Refused while a job still waits for the voice.
    func delete(_ voice: Components.Schemas.ReferenceVoice) async {
        if preview.current == "voice:\(voice.id)" {
            preview.stop()
        }
        do {
            try await model.deleteReferenceVoice(voice.id)
            voices.removeAll { $0.id == voice.id }
            problem = nil
        } catch {
            problem = error.localizedDescription
        }
    }

    /// Versions in the works and those finished since, newest first.
    var versions: [Components.Schemas.VersionState] {
        model.liveVersions.values
            .filter { $0.stage != "cancelled" && !removed.contains($0.id) }
            .sorted { ($0.createdAt ?? .distantPast) > ($1.createdAt ?? .distantPast) }
    }

    // MARK: Stems

    /// The live state wins: it is at least as new as the list.
    var stemSets: [Components.Schemas.StemSetState] {
        var byId = Dictionary(loadedStems.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
        for set in model.liveStems.values {
            byId[set.id] = set
        }
        return byId.values
            .filter { $0.stage != "cancelled" && !removed.contains($0.id) }
            .sorted { ($0.createdAt ?? .distantPast) > ($1.createdAt ?? .distantPast) }
    }

    var chosenModel: Components.Schemas.StemModel? {
        models.first { $0.id == stemModel }
    }

    var canSeparate: Bool {
        !separating && (stemSource == .song ? stemSong != nil : stemFile != nil)
    }

    /// What the model makes and roughly how long it takes for the chosen song.
    var modelHint: String {
        guard let chosen = chosenModel else { return "" }
        var parts: [String] = []
        if let stems = chosen.stems, !stems.isEmpty {
            parts.append(stems.map(Self.stemName).joined(separator: ", "))
        }
        let seconds = stemSource == .song ? stemSong.flatMap { model.song($0)?.seconds } : stemFile?.seconds
        if let factor = chosen.realtimeFactor, factor > 0, let seconds {
            parts.append(String(localized: "etwa \(Song.clock(seconds / factor))"))
        }
        return parts.joined(separator: " · ")
    }

    func separate() async {
        guard canSeparate else { return }
        separating = true
        defer { separating = false }
        do {
            let set: Components.Schemas.StemSetState
            if stemSource == .song, let id = stemSong, let song = model.song(id) {
                set = try await model.separate(song, model: stemModel, dereverb: dereverb)
            } else if let file = stemFile {
                set = try await model.separate(file, model: stemModel, dereverb: dereverb)
                stemFile = nil
            } else {
                return
            }
            loadedStems.removeAll { $0.id == set.id }
            loadedStems.insert(set, at: 0)
            problem = nil
        } catch {
            problem = error.localizedDescription
        }
    }

    /// Deleting a set in the works cancels it.
    func delete(_ set: Components.Schemas.StemSetState) async {
        if preview.current?.hasPrefix("\(set.id)/") == true {
            preview.stop()
        }
        do {
            try await model.deleteStemSet(set.id)
            removed.insert(set.id)
            problem = nil
        } catch {
            problem = error.localizedDescription
        }
    }

    func midiKey(_ set: Components.Schemas.StemSetState, _ stem: Components.Schemas.StemFile) -> String {
        "\(set.id)/\(stem.name)"
    }

    func toMidi(_ set: Components.Schemas.StemSetState, _ stem: Components.Schemas.StemFile) async {
        let key = midiKey(set, stem)
        guard converting == nil else { return }
        converting = key
        midiErrors[key] = nil
        defer { converting = nil }
        do {
            midi[key] = try await model.stemMidi(
                set: set.id, stem: stem.name, mono: midiMono, quantize: midiQuantize, bends: midiBends,
                tempo: set.upload == true ? midiTempo : nil)
        } catch {
            midi[key] = nil
            midiErrors[key] = error.localizedDescription
        }
    }

    /// StemMyWav's file names, as people call them; an unknown one as it is.
    nonisolated static func stemName(_ name: String) -> String {
        switch name.lowercased() {
        case "vocals": String(localized: "Gesang")
        case "vocals_dry": String(localized: "Gesang (trocken)")
        case "vocals_reverb": String(localized: "Hall des Gesangs")
        case "instrumental", "no_vocals": String(localized: "Instrumental")
        case "drums": String(localized: "Schlagzeug")
        case "bass": String(localized: "Bass")
        case "other": String(localized: "Übriges")
        case "piano": String(localized: "Klavier")
        case "guitar": String(localized: "Gitarre")
        default: name
        }
    }

    /// How the list names a set: run title and song, or the uploaded file.
    func name(of set: Components.Schemas.StemSetState) -> String {
        if set.upload == true { return set.title ?? String(localized: "Datei") }
        return model.song(set.songId)?.displayTitle ?? set.title ?? set.songId
    }

    func modelName(_ id: String) -> String {
        models.first { $0.id == id }?.name ?? id
    }

    // MARK: Swaps

    var swaps: [Components.Schemas.SwapState] {
        var byId = Dictionary(loadedSwaps.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
        for swap in model.liveSwaps.values {
            byId[swap.id] = swap
        }
        return byId.values
            .filter { $0.stage != "cancelled" && !removed.contains($0.id) }
            .sorted { ($0.createdAt ?? .distantPast) > ($1.createdAt ?? .distantPast) }
    }

    var canSwap: Bool {
        !swapping && swapFile != nil && model.voiceSettings.voice != nil
            && (!swapSeparate || info?.stemsConfigured == true)
    }

    func swap() async {
        guard canSwap, let file = swapFile else { return }
        swapping = true
        defer { swapping = false }
        do {
            let swap = try await model.swapVoice(of: file, settings: model.voiceSettings, separate: swapSeparate)
            loadedSwaps.removeAll { $0.id == swap.id }
            loadedSwaps.insert(swap, at: 0)
            swapFile = nil
            problem = nil
        } catch {
            problem = error.localizedDescription
        }
    }

    func delete(_ swap: Components.Schemas.SwapState) async {
        if preview.current?.hasPrefix("swap:\(swap.id)") == true {
            preview.stop()
        }
        do {
            try await model.deleteSwap(swap.id)
            removed.insert(swap.id)
            problem = nil
        } catch {
            problem = error.localizedDescription
        }
    }

    /// The voice the settings name, or the first one, once the list is known.
    func chooseVoice() {
        if !voices.contains(where: { $0.id == model.voiceSettings.voice }) {
            model.voiceSettings.voice = voices.first?.id
        }
    }
}

// MARK: Stages

enum VoiceStages {
    static func name(_ stage: String?) -> String {
        switch stage ?? "queued" {
        case "queued": String(localized: "Wartet")
        case "separating": String(localized: "Trennt Spuren")
        case "converting": String(localized: "Singt")
        case "mixing": String(localized: "Mischt")
        case "done": String(localized: "Fertig")
        case "failed": String(localized: "Fehlgeschlagen")
        case "cancelled": String(localized: "Abgebrochen")
        case let other: other
        }
    }
}

// MARK: In the works

/// A version, stem set or swap that the voices' queue has not finished, as the queue page lists it.
enum VoiceJob: Identifiable {
    case version(Components.Schemas.VersionState)
    case stems(Components.Schemas.StemSetState)
    case swap(Components.Schemas.SwapState)

    var id: String {
        switch self {
        case .version(let version): "version:\(version.id)"
        case .stems(let set): "stems:\(set.id)"
        case .swap(let swap): "swap:\(swap.id)"
        }
    }

    var stage: String? {
        switch self {
        case .version(let version): version.stage
        case .stems(let set): set.stage
        case .swap(let swap): swap.stage
        }
    }

    var fraction: Double? {
        switch self {
        case .version(let version): version.fraction
        case .stems: nil
        case .swap(let swap): swap.fraction
        }
    }

    var createdAt: Date? {
        switch self {
        case .version(let version): version.createdAt
        case .stems(let set): set.createdAt
        case .swap(let swap): swap.createdAt
        }
    }

    var symbol: String {
        switch self {
        case .version: "music.mic"
        case .stems: "square.3.layers.3d"
        case .swap: "arrow.triangle.2.circlepath"
        }
    }
}

extension AppModel {
    /// Versions, stem sets and swaps in the works, oldest first, as the voices' queue makes them.
    var voiceJobs: [VoiceJob] {
        let jobs: [VoiceJob] = liveVersions.values.filter { $0.finished != true }.map(VoiceJob.version)
            + liveStems.values.filter { $0.finished != true }.map(VoiceJob.stems)
            + liveSwaps.values.filter { $0.finished != true }.map(VoiceJob.swap)
        return jobs.sorted { ($0.createdAt ?? .distantPast) < ($1.createdAt ?? .distantPast) }
    }

    func title(of job: VoiceJob) -> (title: String, detail: String) {
        switch job {
        case .version(let version):
            (song(version.songId)?.displayTitle ?? version.title ?? version.songId,
             String(localized: "Mit \(version.voiceLabel) singen"))
        case .stems(let set):
            (set.upload == true ? set.title ?? "" : song(set.songId)?.displayTitle ?? set.title ?? set.songId,
             String(localized: "Stems trennen"))
        case .swap(let swap):
            (swap.fileName, String(localized: "Stimme tauschen mit \(swap.voiceLabel)"))
        }
    }

    /// Deleting a job in the works cancels it.
    func cancel(_ job: VoiceJob) async {
        switch job {
        case .version(let version):
            if let song = song(version.songId) {
                try? await deleteVersion(song, Song.Version(id: version.id, voice: version.voiceLabel))
            }
        case .stems(let set):
            try? await deleteStemSet(set.id)
        case .swap(let swap):
            try? await deleteSwap(swap.id)
        }
    }
}

// MARK: Server

extension AppModel {
    func openVoices(_ tab: VoicesSession.Tab? = nil) {
        if voicesPage == nil {
            voicesPage = VoicesSession(model: self)
        }
        if let tab {
            voicesPage?.tab = tab
        }
        page = .voices
    }

    /// The voices page's stems, with the song chosen ("Stems erzeugen" in a song's menu).
    func openStems(_ song: Song) {
        openVoices(.stems)
        voicesPage?.stemSource = .song
        voicesPage?.stemSong = song.id
    }

    private func address(_ path: String, _ components: String...) throws -> URL {
        guard let server else { throw ServerError(String(localized: "Kein Server verbunden.")) }
        return components.reduce(server.appending(path: path)) { $0.appending(component: $1) }
    }

    /// One call by hand, decoding with the generated types; the voices' answers vary by which service is configured
    /// (501, 502), and what matters is the problem's words.
    private func call(
        _ method: String, _ url: URL, json: [String: Any]? = nil, accept: Set<Int> = [200]
    ) async throws -> (Data, HTTPURLResponse) {
        var request = URLRequest(url: url)
        request.httpMethod = method
        request.timeoutInterval = 300
        if let json {
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.httpBody = try JSONSerialization.data(withJSONObject: json)
        }
        let (data, response) = try await URLSession.shared.data(for: request)
        let http = response as? HTTPURLResponse
        let status = http?.statusCode ?? 0
        guard let http, accept.contains(status) else {
            throw ServerError(problem: ServerError.text(of: data) ?? Self.refusal(status))
        }
        return (data, http)
    }

    private static func refusal(_ status: Int) -> String {
        switch status {
        case 404: String(localized: "Das gibt es nicht mehr.")
        case 409: String(localized: "Das ist gerade in Arbeit.")
        case 501: String(localized: "Das ist auf dem Server nicht eingerichtet.")
        case 502: String(localized: "Der Stimmendienst antwortet nicht.")
        default: String(localized: "Der Server antwortete \(status).")
        }
    }

    private func decode<T: Decodable>(_ type: T.Type, _ data: Data) throws -> T {
        try JSONDecoder.tonwerk().decode(type, from: data)
    }

    /// A multipart form with one file, written to a temporary file so a long song is not held twice in memory.
    private func upload(
        _ url: URL, fields: [(String, String)], fileField: String, file: PickedFile, accept: Set<Int>
    ) async throws -> Data {
        let boundary = "tonwerk-\(UUID().uuidString)"
        let body = FileManager.default.temporaryDirectory.appending(path: "upload-\(UUID().uuidString)")
        FileManager.default.createFile(atPath: body.path, contents: nil)
        defer { try? FileManager.default.removeItem(at: body) }
        let handle = try FileHandle(forWritingTo: body)
        for (name, value) in fields {
            try handle.write(contentsOf: Data(
                "--\(boundary)\r\nContent-Disposition: form-data; name=\"\(name)\"\r\n\r\n\(value)\r\n".utf8))
        }
        let filename = file.name.replacingOccurrences(of: "\"", with: "'")
        try handle.write(contentsOf: Data(
            "--\(boundary)\r\nContent-Disposition: form-data; name=\"\(fileField)\"; filename=\"\(filename)\"\r\nContent-Type: application/octet-stream\r\n\r\n".utf8))
        let source = try FileHandle(forReadingFrom: file.url)
        while let chunk = try source.read(upToCount: 1 << 20), !chunk.isEmpty {
            try handle.write(contentsOf: chunk)
        }
        try source.close()
        try handle.write(contentsOf: Data("\r\n--\(boundary)--\r\n".utf8))
        try handle.close()

        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.timeoutInterval = 600
        request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")
        let (data, response) = try await URLSession.shared.upload(for: request, fromFile: body)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        guard accept.contains(status) else {
            throw ServerError(problem: ServerError.text(of: data) ?? Self.refusal(status))
        }
        return data
    }

    func voiceInfo() async -> Components.Schemas.VoiceInfo? {
        guard let url = try? address("api/voice"), let answer = try? await call("GET", url) else { return nil }
        return try? decode(Components.Schemas.VoiceInfo.self, answer.0)
    }

    func referenceVoices() async throws -> [Components.Schemas.ReferenceVoice] {
        let (data, _) = try await call("GET", address("api/voices"))
        return try decode([Components.Schemas.ReferenceVoice].self, data)
            .sorted { $0.label.localizedStandardCompare($1.label) == .orderedAscending }
    }

    func referenceVoiceURL(_ id: String) -> URL? {
        try? address("api/voices", id, "audio")
    }

    func addReferenceVoice(_ label: String, file: PickedFile, start: Double?, end: Double?) async throws {
        var fields = [("label", label)]
        if let start { fields.append(("startSeconds", String(start))) }
        if let end { fields.append(("endSeconds", String(end))) }
        _ = try await upload(address("api/voices"), fields: fields, fileField: "file", file: file, accept: [201])
    }

    func deleteReferenceVoice(_ id: String) async throws {
        _ = try await call("DELETE", address("api/voices", id), accept: [204, 404])
    }

    /// A version of the song sung with a voice; the library lists it under the song while it is made.
    func sing(_ song: Song, settings: VoiceSettings) async throws -> Components.Schemas.VersionState {
        guard let voice = settings.voice else { throw ServerError(String(localized: "Wähle eine Stimme.")) }
        let (data, _) = try await call(
            "POST", address("api/songs", song.run, song.name, "versions"),
            json: [
                "voiceId": voice, "semiToneShift": settings.semitones, "strength": settings.strength,
                "diffusionSteps": settings.steps, "keepReverb": settings.keepReverb,
            ],
            accept: [202])
        let version = try decode(Components.Schemas.VersionState.self, data)
        if liveVersions[version.id] == nil {
            liveVersions[version.id] = version
        }
        await reloadLibrary()
        return version
    }

    func deleteVersion(_ song: Song, _ version: Song.Version) async throws {
        _ = try await call("DELETE", address("api/songs", song.run, song.name, "versions", version.id), accept: [204, 404])
        liveVersions[version.id] = nil
        await reloadLibrary()
    }

    func stemModels() async throws -> [Components.Schemas.StemModel] {
        let (data, _) = try await call("GET", address("api/stems/models"))
        return try decode([Components.Schemas.StemModel].self, data)
    }

    func stemSets() async throws -> [Components.Schemas.StemSetState] {
        let (data, _) = try await call("GET", address("api/stems"))
        return try decode([Components.Schemas.StemSetState].self, data)
    }

    func separate(_ song: Song, model: String?, dereverb: Bool) async throws -> Components.Schemas.StemSetState {
        var body: [String: Any] = ["dereverb": dereverb]
        if let model { body["model"] = model }
        let (data, _) = try await call(
            "POST", address("api/songs", song.run, song.name, "stems"), json: body, accept: [202])
        return try decode(Components.Schemas.StemSetState.self, data)
    }

    func separate(_ file: PickedFile, model: String?, dereverb: Bool) async throws -> Components.Schemas.StemSetState {
        var fields = [("dereverb", dereverb ? "true" : "false")]
        if let model { fields.append(("model", model)) }
        let data = try await upload(address("api/stems"), fields: fields, fileField: "file", file: file, accept: [202])
        return try decode(Components.Schemas.StemSetState.self, data)
    }

    func stemURL(_ set: String, _ stem: String) -> URL? {
        try? address("api/stems", set, stem)
    }

    func deleteStemSet(_ id: String) async throws {
        _ = try await call("DELETE", address("api/stems", id), accept: [204, 404])
        liveStems[id] = nil
    }

    /// The stem as a MIDI file in the temporary folder; an upload's at the tempo given, a song's at its score's.
    func stemMidi(
        set: String, stem: String, mono: Bool, quantize: Bool, bends: Bool, tempo: Double?
    ) async throws -> MidiFile {
        var body: [String: Any] = ["mono": mono, "quantize": quantize, "bends": bends && mono]
        if let tempo { body["tempo"] = tempo }
        let (data, response) = try await call("POST", address("api/stems", set, stem, "midi"), json: body)
        let folder = FileManager.default.temporaryDirectory
            .appending(path: "Tonwerk-MIDI", directoryHint: .isDirectory)
            .appending(path: UUID().uuidString, directoryHint: .isDirectory)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let file = folder.appending(path: "\(Self.projectName(stem)).mid")
        try data.write(to: file)
        let notes = response.value(forHTTPHeaderField: "X-Audio-Midi-Notes").flatMap(Int.init) ?? 0
        return MidiFile(url: file, notes: notes)
    }

    /// A stem's FLAC in the temporary folder, to save or share.
    func downloadStem(_ set: Components.Schemas.StemSetState, _ stem: Components.Schemas.StemFile, title: String)
        async throws -> URL
    {
        let (data, _) = try await call("GET", address("api/stems", set.id, stem.name))
        let folder = FileManager.default.temporaryDirectory
            .appending(path: "Tonwerk-Stems", directoryHint: .isDirectory)
            .appending(path: UUID().uuidString, directoryHint: .isDirectory)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let ext = (stem.file as NSString).pathExtension
        let file = folder.appending(
            path: "\(Self.projectName("\(title) - \(VoicesSession.stemName(stem.name))")).\(ext.isEmpty ? "flac" : ext)")
        try data.write(to: file)
        return file
    }

    func swaps() async throws -> [Components.Schemas.SwapState] {
        let (data, _) = try await call("GET", address("api/swaps"))
        return try decode([Components.Schemas.SwapState].self, data)
    }

    func swapVoice(of file: PickedFile, settings: VoiceSettings, separate: Bool) async throws
        -> Components.Schemas.SwapState
    {
        guard let voice = settings.voice else { throw ServerError(String(localized: "Wähle eine Stimme.")) }
        let fields = [
            ("voiceId", voice), ("semiToneShift", String(settings.semitones)), ("strength", String(settings.strength)),
            ("diffusionSteps", String(settings.steps)), ("separate", separate ? "true" : "false"),
            ("keepReverb", settings.keepReverb ? "true" : "false"),
        ]
        let data = try await upload(address("api/swaps"), fields: fields, fileField: "file", file: file, accept: [202])
        return try decode(Components.Schemas.SwapState.self, data)
    }

    func swapURL(_ id: String, source: Bool) -> URL? {
        try? address("api/swaps", id, source ? "source" : "audio")
    }

    func deleteSwap(_ id: String) async throws {
        _ = try await call("DELETE", address("api/swaps", id), accept: [204, 404])
        liveSwaps[id] = nil
    }
}
