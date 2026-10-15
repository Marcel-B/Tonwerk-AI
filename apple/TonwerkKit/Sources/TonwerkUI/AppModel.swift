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
    public private(set) var libraryError: String?

    public let player = Player()

    private var tonwerk: Tonwerk?
    private var live: Task<Void, Never>?
    private let defaults: UserDefaults

    public init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
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
            working = Dictionary(snapshot.songs.map { ($0.id, $0) }, uniquingKeysWith: { _, last in last })
        case .queue(let jobs):
            queue = jobs
        case .song(let state):
            working[state.id] = state
            if state.finished == true {
                Task { await reloadLibrary() }
            }
        case .library:
            Task { await reloadLibrary() }
        default:
            break
        }
    }

    /// Songs the worker is on and not done with, newest first.
    public var inProgress: [Components.Schemas.SongState] {
        working.values
            .filter { $0.finished != true }
            .sorted { ($0.updatedAt ?? .distantPast) > ($1.updatedAt ?? .distantPast) }
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

    public func song(_ id: String) -> Song? {
        songs.first { $0.id == id }
    }

    /// Stars at once in the list and the player; the `library` event that follows confirms them.
    public func rate(_ song: Song, _ stars: Int?) async {
        guard let tonwerk else { return }
        let rating = stars.flatMap { (1...5).contains($0) ? $0 : nil }
        replace(song.id) { old in
            Song(
                run: old.run, name: old.name, title: old.title, number: old.number, style: old.style,
                lyrics: old.lyrics, createdAt: old.createdAt, seconds: old.seconds, rating: rating, note: old.note,
                coverUpdatedAt: old.coverUpdatedAt, hasAudio: old.hasAudio)
        }
        _ = try? await tonwerk.api.rateSong(
            path: .init(run: song.run, song: song.name),
            body: .json(.init(rating: rating.map { Int32($0) })))
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

    public enum ConnectError: LocalizedError {
        case address

        public var errorDescription: String? {
            String(localized: "Das ist keine Adresse. Beispiel: mac-mini.tailnet.ts.net:8443")
        }
    }
}
