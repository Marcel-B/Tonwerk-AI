import Foundation
import Observation
import TonwerkKit

/// The storage page's state (`StoragePage.vue`): where the disk goes, measured anew on every visit, what can be
/// cleaned up in one go, and each part's largest entries, deleted through the endpoints the other pages use.
@MainActor
@Observable
final class StorageSession {
    /// One thing to clean up in one go, with what it frees.
    struct Suggestion: Identifiable, Equatable {
        let target: String
        let name: String
        let hint: String
        let bytes: Int64

        var id: String { target }
    }

    static let groups: [(id: String, name: String)] = [
        ("work", String(localized: "Deine Werke")), ("app", String(localized: "Von der App")),
        ("models", String(localized: "Modelle und Programme")),
    ]

    private(set) var overview: Components.Schemas.StorageOverview?
    private(set) var loading = false
    var problem: String?
    var notice: String?
    /// The cleanup or entry being deleted, so only one runs.
    private(set) var busy: String?

    private unowned let model: AppModel

    init(model: AppModel) {
        self.model = model
    }

    func load() async {
        loading = true
        defer { loading = false }
        do {
            overview = try await model.storageOverview()
            problem = nil
        } catch {
            problem = String(localized: "Der Speicher konnte nicht gemessen werden: \(error.localizedDescription)")
        }
    }

    var categories: [Components.Schemas.StorageCategory] {
        overview?.categories.filter { $0.bytes > 0 } ?? []
    }

    func categories(in group: String) -> [Components.Schemas.StorageCategory] {
        categories.filter { $0.group == group }.sorted { $0.bytes > $1.bytes }
    }

    var largest: Int64 { max(1, categories.map(\.bytes).max() ?? 1) }

    /// The volume the database lies on; parts elsewhere say where they are.
    var homeVolume: String {
        overview?.categories.first { $0.key == "database" }?.volume ?? "/"
    }

    /// What Tonwerk takes on the volume.
    func tonwerkBytes(on volume: Components.Schemas.StorageVolume) -> Int64 {
        categories.filter { $0.volume == volume.mount }.reduce(0) { $0 + $1.bytes }
    }

    var suggestions: [Suggestion] {
        guard let overview else { return [] }
        return Self.suggestions(of: overview)
    }

    nonisolated static func suggestions(of overview: Components.Schemas.StorageOverview) -> [Suggestion] {
        let bytes = { (key: String) in overview.categories.first { $0.key == key }?.bytes ?? 0 }
        return [
            Suggestion(
                target: "stream", name: String(localized: "Streaming-Kopien"),
                hint: String(localized: "Der Player macht sie beim nächsten Abspielen neu."), bytes: bytes("stream")),
            Suggestion(
                target: "images", name: String(localized: "Bildvorschläge"),
                hint: String(localized: "Gemalte Bilder; was als Cover übernommen wurde, bleibt."),
                bytes: bytes("images")),
            Suggestion(
                target: "videos", name: String(localized: "Alle Musikvideos"),
                hint: String(localized: "Lassen sich im Song-Menü neu erstellen."), bytes: bytes("videos")),
            Suggestion(
                target: "failed", name: String(localized: "Abgebrochenes mit Resten"),
                hint: String(localized: "Fehlgeschlagene oder abgebrochene Stimmenversionen, Stems, Stimmentausche und Videos, die noch Dateien belegen."),
                bytes: overview.failed),
            Suggestion(
                target: "orphans", name: String(localized: "Verwaiste Dateien"),
                hint: String(localized: "Ohne Eintrag in der Datenbank und älter als ein Tag, etwa nach einem Absturz."),
                bytes: overview.categories.reduce(0) { $0 + $1.orphaned }),
        ].filter { $0.bytes > 0 }
    }

    /// Songs given one star: rated as not worth keeping, so they are offered for deletion as a whole.
    var oneStar: [Song] { model.songs.filter { $0.rating == 1 } }

    func cleanUp(_ suggestion: Suggestion) async {
        await run(suggestion.target) {
            let result = try await self.model.cleanUpStorage(suggestion.target)
            self.notice = String(localized: "\(Self.size(result.bytes)) frei geworden.")
        }
    }

    func deleteOneStar() async {
        let songs = oneStar
        let bytes = songs.reduce(0) { $0 + $1.bytes }
        await run("oneStar") {
            // One after another through the library's own endpoint, which refuses a song the worker is on.
            for song in songs {
                try await self.model.deleteStorageItem(kind: "song", id: song.id, songId: song.id)
            }
            self.notice = String(localized: "\(Self.size(bytes)) frei geworden.")
        }
    }

    func delete(_ item: Components.Schemas.StorageItem) async {
        await run("\(item.kind):\(item.id)") {
            try await self.model.deleteStorageItem(kind: item.kind, id: item.id, songId: item.songId)
        }
    }

    /// One deletion at a time; the page measures again afterwards, so every number matches the disk.
    private func run(_ key: String, _ action: @escaping () async throws -> Void) async {
        busy = key
        notice = nil
        do {
            try await action()
        } catch {
            problem = error.localizedDescription
        }
        busy = nil
        await model.reloadLibrary()
        await load()
    }

    nonisolated static func size(_ bytes: Int64) -> String {
        bytes.formatted(.byteCount(style: .file))
    }

    nonisolated static func categoryName(_ key: String) -> String {
        switch key {
        case "songs": String(localized: "Songs")
        case "transcriptions": String(localized: "Transkriptionen")
        case "versions": String(localized: "Stimmenversionen")
        case "stems": String(localized: "Stems")
        case "swaps": String(localized: "Stimmentausch")
        case "videos": String(localized: "Musikvideos")
        case "covers": String(localized: "Cover")
        case "images": String(localized: "Gemalte Bilder")
        case "speech": String(localized: "Sprachlabor")
        case "voices": String(localized: "Referenzstimmen")
        case "loras": "LoRAs"
        case "stream": String(localized: "Streaming-Kopien")
        case "logs": String(localized: "Protokoll")
        case "database": String(localized: "Datenbank und Einstellungen")
        case "yue2": String(localized: "YuE2-Modelle")
        case "ane": String(localized: "Neural-Engine-Cache")
        case "lmstudio": String(localized: "LM-Studio-Modelle")
        case "speechModels": String(localized: "Sprachmodelle")
        case "imageModels": String(localized: "Bildmodelle (FLUX.2)")
        case "separatorModels": String(localized: "Stem-Trennung")
        case "seedvcModels": "Seed-VC"
        case "montageModels": String(localized: "CLIP für Fotovideos")
        case "aceModels": String(localized: "ACE-Step-Modelle")
        case "programs": String(localized: "Python-Umgebungen und Quellen")
        default: key
        }
    }

    nonisolated static func volumeName(_ mount: String) -> String {
        mount == "/" ? "Mac" : (mount.split(separator: "/").last.map(String.init) ?? mount)
    }

    nonisolated static func detail(_ item: Components.Schemas.StorageItem) -> String? {
        guard item.kind == "video", let detail = item.detail else { return item.detail }
        switch detail {
        case "landscape": return "YouTube 16:9"
        case "portrait": return "Shorts/Reels 9:16"
        default: return detail
        }
    }
}

extension AppModel {
    func openStorage() {
        if storagePage == nil {
            storagePage = StorageSession(model: self)
        }
        page = .storage
    }

    func storageOverview() async throws -> Components.Schemas.StorageOverview {
        let (data, _) = try await call("GET", address("api/storage/overview"))
        return try decode(Components.Schemas.StorageOverview.self, data)
    }

    func cleanUpStorage(_ target: String) async throws -> Components.Schemas.StorageCleanup {
        let (data, _) = try await call("POST", address("api/storage/cleanup", target))
        return try decode(Components.Schemas.StorageCleanup.self, data)
    }

    /// The endpoint that deletes an entry of the overview, the same the library and the other pages use; nil for a
    /// kind this app does not know (a newer server).
    nonisolated static func deletePath(kind: String, id: String, songId: String?) -> String? {
        switch kind {
        // A song's and a cover's id is the song's `run/songN`.
        case "song": "api/songs/\(id)"
        case "cover": "api/songs/\(id)/cover"
        case "version": songId.map { "api/songs/\($0)/versions/\(id)" }
        case "stems": "api/stems/\(id)"
        case "swap": "api/swaps/\(id)"
        case "video": "api/videos/\(id)"
        case "image": "api/images/\(id)"
        case "transcription": "api/transcriptions/\(id)"
        case "take": "api/speech/takes/\(id)"
        default: nil
        }
    }

    func deleteStorageItem(kind: String, id: String, songId: String?) async throws {
        guard let path = Self.deletePath(kind: kind, id: id, songId: songId) else {
            throw ServerError(String(localized: "Das lässt sich hier nicht löschen."))
        }
        _ = try await call("DELETE", address(path), accept: [200, 202, 204, 404])
    }
}
