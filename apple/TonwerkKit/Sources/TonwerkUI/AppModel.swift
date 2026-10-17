import Foundation
import Observation
import TonwerkKit

/// The app's state, the native counterpart of `App.vue`: which server, the library and playlists, and the live state
/// from the event stream. One per app, created by ``TonwerkScene``.
@MainActor
@Observable
public final class AppModel {
    public enum Connection: Equatable {
        case none
        case connecting
        case live
        /// The stream dropped or the server did not answer; it is tried again every few seconds.
        case lost(String)
    }

    private static let serverKey = "server"

    public private(set) var server: URL?
    public private(set) var connection: Connection = .none
    public private(set) var songs: [Song] = []
    public private(set) var playlists: [Components.Schemas.PlaylistInfo] = []
    /// Waiting jobs, as the queue page lists them.
    public private(set) var queue: [Components.Schemas.QueuedJob] = []
    /// Songs and renders the worker has, by id; finished ones leave once the library has them.
    public private(set) var working: [String: Components.Schemas.SongState] = [:]
    /// Finished songs the queue page no longer shows ("Erledigte ausblenden"), kept for this run of the app only, as the
    /// web page keeps them per tab.
    public private(set) var hiddenFinished: Set<String> = []
    /// The worker's log, the snapshot's and every `log` event since, at most ``logCapacity`` lines like the web page.
    public private(set) var workerLog: [Components.Schemas.LogEntry] = []
    private let logCapacity = 300
    public private(set) var libraryError: String?
    /// The page in the sidebar; here rather than in the view, so "use in the form" can switch to the form.
    var page: Page? = .library
    /// The song the share dialog is open for, from a song's menu.
    var sharing: Song?
    /// The Logic page's state, made when it is first opened.
    var logic: LogicSession?
    /// The synthesizers' named and per-track sounds, made on first use (``soundLibrary``).
    @ObservationIgnored private var sounds: SoundLibrary?
    /// The prompt templates, made on first use (``templateStore``).
    @ObservationIgnored private var templateList: TemplateStore?
    /// The instruments page's state, kept like the Logic page's.
    var instrumentsPage: InstrumentsSession?
    /// The voices page's state, kept like the Logic page's.
    var voicesPage: VoicesSession?
    /// The song the sing-with-a-voice dialog is open for, from a song's menu.
    var singing: Song?
    /// How the last version was asked for, offered again for the next.
    var voiceSettings = VoiceSettings()
    /// Versions, stem sets and swaps in the works or finished since, by id, from their events.
    var liveVersions: [String: Components.Schemas.VersionState] = [:]
    var liveStems: [String: Components.Schemas.StemSetState] = [:]
    var liveSwaps: [String: Components.Schemas.SwapState] = [:]
    /// The song the music video dialog is open for, from a song's menu.
    var filming: Song?
    /// The YuE2 worker's state and the large model besides it that holds the memory, for the queue's overview.
    var worker: Components.Schemas.WorkerInfo?
    var memoryHolder: Components.Schemas.LargeModel?
    /// Music videos in the works or finished since, by id, from `video` events.
    var liveVideos: [String: Components.Schemas.VideoState] = [:]
    /// Transcriptions running, waiting or finished since, by id, from `transcription` events.
    var liveTranscriptions: [String: Components.Schemas.TranscriptionState] = [:]
    /// The transcription page's state, kept like the voices page's.
    var transcriptionPage: TranscriptionSession?
    /// Speech lab takes in the works or finished since, by id, from the snapshot and `speech` events.
    var liveSpeech: [String: Components.Schemas.SpeechTake] = [:]
    /// The speech lab's state, kept like the transcription page's.
    var speechPage: SpeechSession?
    /// ACE-Step's jobs in the works or finished since, by id, from the snapshot and `ace` events.
    var liveAce: [String: Components.Schemas.AceJob] = [:]
    /// The ACE-Step page's state, kept like the speech lab's.
    var acePage: AceSession?
    /// The ACE-Step page's form, here so "use everything" on an ACE-Step song fills it before the page opens.
    var aceForm = AceForm()
    /// The last report of what the rated songs were made with, for its page and the proven style blocks.
    var insights: Components.Schemas.InsightReport?
    /// The sketch page's state, kept like the ACE-Step page's.
    var sketchPage: SketchSession?
    /// The backing vocals page's state, so its melody and preview outlast a page change.
    var harmonyPage: HarmonySession?
    /// The log page's state, kept so its filter outlasts a page change.
    var logPage: LogSession?
    /// The storage page's state, kept like the log's.
    var storagePage: StorageSession?

    var soundLibrary: SoundLibrary {
        if let sounds { return sounds }
        let library = SoundLibrary(model: self)
        sounds = library
        return library
    }

    var templateStore: TemplateStore {
        if let templateList { return templateList }
        let store = TemplateStore(model: self)
        templateList = store
        return store
    }
    /// The song the painting dialog is open for, from a song's menu.
    var painting: Song?
    /// Painted covers in the works or finished since, by id, from `image` events.
    var images: [String: Components.Schemas.ImageState] = [:]
    /// The create form, kept across starts like the web page's `form.ts`.
    var form = CreateForm()
    /// Lyrics drafts by id, from `lyrics` events; the form waits for the one it asked for.
    public private(set) var drafts: [String: Components.Schemas.LyricsState] = [:]

    public let player = Player()

    private(set) var tonwerk: Tonwerk?
    private var live: Task<Void, Never>?
    private let defaults: UserDefaults

    public init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        form = CreateForm.load(from: defaults)
        aceForm = AceForm.load(from: defaults)
        if let saved = defaults.string(forKey: Self.serverKey), let url = URL(string: saved) {
            use(url)
        }
    }

    /// Checks the address against the server before keeping it, so a typo does not leave an empty app behind.
    public func connect(to text: String) async throws {
        guard let url = ServerAddress.parse(text) else { throw ConnectError.address }
        let candidate = Tonwerk(server: url)
        _ = try await candidate.api.getStatus().ok.body.json
        defaults.set(url.absoluteString, forKey: Self.serverKey)
        use(url)
    }

    public func disconnect() {
        live?.cancel()
        live = nil
        player.stop()
        tonwerk = nil
        server = nil
        songs = []
        playlists = []
        queue = []
        working = [:]
        hiddenFinished = []
        workerLog = []
        liveVersions = [:]
        liveStems = [:]
        liveSwaps = [:]
        liveVideos = [:]
        liveTranscriptions = [:]
        liveSpeech = [:]
        speechPage = nil
        liveAce = [:]
        acePage = nil
        insights = nil
        sketchPage?.stop()
        sketchPage = nil
        harmonyPage?.stop()
        harmonyPage = nil
        transcriptionPage = nil
        logPage = nil
        storagePage = nil
        templateList = nil
        worker = nil
        memoryHolder = nil
        voicesPage = nil
        connection = .none
        defaults.removeObject(forKey: Self.serverKey)
    }

    private func use(_ url: URL) {
        live?.cancel()
        server = url
        let tonwerk = Tonwerk(server: url)
        self.tonwerk = tonwerk
        player.server = url
        live = Task { [weak self] in await self?.listen(to: tonwerk) }
    }

    // MARK: Live state

    /// The event stream, reconnected for as long as the app uses this server. Every connection starts with a snapshot,
    /// so nothing missed while it was down stays wrong.
    private func listen(to tonwerk: Tonwerk) async {
        while !Task.isCancelled {
            connection = .connecting
            do {
                async let library: Void = reloadLibrary()
                async let lists: Void = reloadPlaylists()
                _ = await (library, lists)
                for try await event in try await tonwerk.events() {
                    connection = .live
                    handle(event)
                }
                connection = .lost(String(localized: "Verbindung unterbrochen"))
            } catch is CancellationError {
                return
            } catch {
                connection = .lost(error.localizedDescription)
            }
            try? await Task.sleep(for: .seconds(3))
        }
    }

    private func handle(_ event: ServerEvent) {
        switch event {
        case .snapshot(let snapshot):
            queue = snapshot.queue ?? []
            worker = snapshot.worker
            memoryHolder = snapshot.memory?.holder
            if let draft = snapshot.lyrics {
                drafts[draft.id] = draft
            }
            working = Dictionary(snapshot.songs.map { ($0.id, $0) }, uniquingKeysWith: { _, last in last })
            workerLog = Array(snapshot.log.suffix(logCapacity))
            for image in snapshot.images ?? [] {
                images[image.id] = image
            }
            liveVersions = Dictionary((snapshot.versions ?? []).map { ($0.id, $0) }, uniquingKeysWith: { _, last in last })
            liveStems = Dictionary((snapshot.stems ?? []).map { ($0.id, $0) }, uniquingKeysWith: { _, last in last })
            liveSwaps = Dictionary((snapshot.swaps ?? []).map { ($0.id, $0) }, uniquingKeysWith: { _, last in last })
            liveVideos = Dictionary((snapshot.videos ?? []).map { ($0.id, $0) }, uniquingKeysWith: { _, last in last })
            liveTranscriptions = Dictionary(
                (snapshot.transcriptions ?? []).map { ($0.id, $0) }, uniquingKeysWith: { _, last in last })
            liveSpeech = Dictionary((snapshot.speech ?? []).map { ($0.id, $0) }, uniquingKeysWith: { _, last in last })
            liveAce = Dictionary((snapshot.ace ?? []).map { ($0.id, $0) }, uniquingKeysWith: { _, last in last })
        case .queue(let jobs):
            queue = jobs
        case .worker(let info):
            worker = info
        case .memory(let info):
            memoryHolder = info.holder
        case .song(let state):
            working[state.id] = state
            // A song rendered again after being hidden is news again.
            if state.finished != true {
                hiddenFinished.remove(state.id)
            }
            if state.finished == true {
                Task { await reloadLibrary() }
            }
        case .lyrics(let draft):
            drafts[draft.id] = draft
        case .image(let image):
            images[image.id] = image
        case .version(let version):
            let before = liveVersions[version.id]?.stage
            liveVersions[version.id] = version
            // The library lists a song's versions with their stage; a new or finished one changes that.
            if before == nil || (version.finished == true && before != version.stage) {
                Task { await reloadLibrary() }
            }
        case .stems(let set):
            liveStems[set.id] = set
        case .swap(let swap):
            liveSwaps[swap.id] = swap
        case .video(let video):
            liveVideos[video.id] = video
        case .transcription(let state):
            let before = liveTranscriptions[state.id]?.stage
            liveTranscriptions[state.id] = state
            if state.stage == "done" && before != "done" {
                transcriptionPage?.finished()
            }
        case .speech(let take):
            liveSpeech[take.id] = take
        case .ace(let job):
            let before = liveAce[job.id]?.stage
            liveAce[job.id] = job
            // The finished songs are a new run in the library.
            if job.stage == "done" && before != "done" {
                Task { await reloadLibrary() }
            }
        case .library:
            Task { await reloadLibrary() }
        case .log(let entry):
            workerLog.append(entry)
            if workerLog.count > logCapacity {
                workerLog.removeFirst(workerLog.count - logCapacity)
            }
        default:
            break
        }
    }

    /// Songs the worker is on and not done with, in the order they were asked for: by run, then by song, as the web
    /// page's queue does. Not by `updatedAt`, which every progress event moves, so songs of one batch would keep
    /// swapping places while they compose side by side.
    public var inProgress: [Components.Schemas.SongState] {
        working.values
            .filter { $0.finished != true }
            .sorted { ($0.run, $0.index ?? 0) < ($1.run, $1.index ?? 0) }
    }

    /// Songs the worker is done with (ready, failed, cancelled) and the queue page still shows, in the same order.
    public var finishedSongs: [Components.Schemas.SongState] {
        working.values
            .filter { $0.finished == true && !hiddenFinished.contains($0.id) }
            .sorted { ($0.run, $0.index ?? 0) < ($1.run, $1.index ?? 0) }
    }

    /// Takes the finished songs off the queue page; they stay in the library.
    public func hideFinished() {
        hiddenFinished.formUnion(working.values.filter { $0.finished == true }.map(\.id))
    }

    // MARK: Library

    public func reloadLibrary() async {
        guard let tonwerk else { return }
        do {
            songs = Song.all(in: try await tonwerk.api.listLibrary().ok.body.json)
            libraryError = nil
            player.refresh(from: songs)
        } catch {
            libraryError = error.localizedDescription
        }
    }

    /// The answer to the request, unless its events were faster.
    func noteDraft(_ draft: Components.Schemas.LyricsState) {
        if drafts[draft.id] == nil {
            drafts[draft.id] = draft
        }
    }

    /// The answer to the request, unless its events were faster.
    func noteImage(_ image: Components.Schemas.ImageState) {
        if images[image.id] == nil {
            images[image.id] = image
        }
    }

    func saveForm() {
        form.save(to: defaults)
    }

    func saveAceForm() {
        aceForm.save(to: defaults)
    }

    public func song(_ id: String) -> Song? {
        songs.first { $0.id == id }
    }

    /// Stars at once in the list and the player; the `library` event that follows confirms them.
    public func rate(_ song: Song, _ stars: Int?) async {
        guard let tonwerk else { return }
        let rating = stars.flatMap { (1...5).contains($0) ? $0 : nil }
        replace(song.id) { $0.with(rating: .some(rating)) }
        _ = try? await tonwerk.api.rateSong(
            path: .init(run: song.run, song: song.name),
            body: .json(.init(rating: rating.map { Int32($0) })))
    }

    /// A new title for the song's run (all its songs); the folder keeps its name, which is the songs' id. Empty goes
    /// back to the worker's title.
    public func rename(_ song: Song, to title: String) async throws {
        guard let tonwerk else { return }
        let trimmed = title.trimmingCharacters(in: .whitespacesAndNewlines)
        let shown = trimmed.isEmpty ? song.originalTitle : trimmed
        let response = try await tonwerk.api.renameRun(path: .init(run: song.run), body: .json(.init(title: trimmed)))
        guard case .noContent = response else {
            throw ServerError(String(localized: "Der Titel wurde nicht gespeichert."))
        }
        for other in songs where other.run == song.run {
            replace(other.id) { $0.with(title: shown) }
        }
    }

    /// The song's note, trimmed; empty deletes it.
    public func setNote(_ song: Song, _ text: String) async throws {
        guard let tonwerk else { return }
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        let note: String? = trimmed.isEmpty ? nil : trimmed
        guard note != song.note else { return }
        let response = try await tonwerk.api.setSongNote(
            path: .init(run: song.run, song: song.name), body: .json(.init(note: note)))
        guard case .noContent = response else {
            throw ServerError(String(localized: "Die Notiz wurde nicht gespeichert."))
        }
        replace(song.id) { $0.with(note: .some(note)) }
    }

    private func replace(_ id: String, with change: (Song) -> Song) {
        guard let index = songs.firstIndex(where: { $0.id == id }) else { return }
        songs[index] = change(songs[index])
        player.refresh(from: songs)
    }

    // MARK: Playlists

    public func reloadPlaylists() async {
        guard let tonwerk else { return }
        if let lists = try? await tonwerk.api.listPlaylists().ok.body.json {
            playlists = lists
        }
    }

    /// The playlist's songs that still exist, in its order.
    public func songs(of playlist: Components.Schemas.PlaylistInfo) -> [Song] {
        let byId = Dictionary(songs.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
        return playlist.songIds.compactMap { byId[$0] }
    }

    public func toggle(_ song: Song, in playlist: Components.Schemas.PlaylistInfo) async {
        var ids = playlist.songIds
        if let index = ids.firstIndex(of: song.id) {
            ids.remove(at: index)
        } else {
            ids.append(song.id)
        }
        await setSongs(ids, of: playlist)
    }

    public func setSongs(_ ids: [String], of playlist: Components.Schemas.PlaylistInfo) async {
        guard let tonwerk else { return }
        if let changed = try? await tonwerk.api.setPlaylistSongs(
            path: .init(id: playlist.id), body: .json(.init(songIds: ids))
        ).ok.body.json,
            let index = playlists.firstIndex(where: { $0.id == changed.id })
        {
            playlists[index] = changed
        }
    }

    public func createPlaylist(named name: String) async {
        guard let tonwerk else { return }
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        if let created = try? await tonwerk.api.createPlaylist(body: .json(.init(name: trimmed))).created.body.json {
            playlists.append(created)
        }
    }

    /// The finished songs the queue still knows, in the order they were made, as the web queue page's "Als Playlist":
    /// YuE2's ready songs and ACE-Step's finished runs, only those the library lists.
    public var readySongIds: [String] {
        let listed = Set(songs.map(\.id))
        var ids = working.values
            .filter { $0.stage == "ready" && listed.contains($0.id) && !hiddenFinished.contains($0.id) }
            .sorted { ($0.updatedAt ?? .distantPast) < ($1.updatedAt ?? .distantPast) }
            .map(\.id)
        let aceRuns = liveAce.values
            .filter { $0.stage == "done" && $0.run != nil }
            .sorted { ($0.updatedAt ?? .distantPast) < ($1.updatedAt ?? .distantPast) }
        for job in aceRuns {
            ids += songs.filter { $0.run == job.run }.map(\.id)
                .sorted { $0.localizedStandardCompare($1) == .orderedAscending }
        }
        var seen = Set<String>()
        return ids.filter { seen.insert($0).inserted }
    }

    /// A new playlist of the queue's finished songs, opened at once, to hear and rate them in one go.
    public func playlistOfReadySongs() async {
        guard let tonwerk else { return }
        let ids = readySongIds
        guard !ids.isEmpty else { return }
        let name = String(localized: "Fertig am \(Date().formatted(date: .numeric, time: .shortened))")
        guard let created = try? await tonwerk.api.createPlaylist(body: .json(.init(name: name))).created.body.json
        else { return }
        playlists.append(created)
        await setSongs(ids, of: created)
        page = .playlist(created.id)
    }

    public enum ConnectError: LocalizedError {
        case address

        public var errorDescription: String? {
            String(localized: "Das ist keine Adresse. Beispiel: mac-mini.tailnet.ts.net:8443")
        }
    }
}
