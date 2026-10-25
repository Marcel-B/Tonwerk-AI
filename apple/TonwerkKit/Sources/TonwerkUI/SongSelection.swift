import Foundation

/// What deleting the chosen songs takes, as the web library's `deletionPlan` (`librarySelection.ts`): whole runs where
/// every song is chosen (one request, which takes the run's own files too), single songs otherwise.
struct SongDeletion: Equatable {
    var runs: [String] = []
    var songs: [Song] = []
    /// Songs in all, those of whole runs included.
    var count = 0
    var bytes: Int64 = 0

    /// Ids no longer in the library (deleted elsewhere meanwhile) are left out.
    init(picked: Set<Song.ID>, library: [Song]) {
        var order: [String] = []
        var byRun: [String: [Song]] = [:]
        for song in library {
            if byRun[song.run] == nil {
                order.append(song.run)
            }
            byRun[song.run, default: []].append(song)
        }
        for run in order {
            let all = byRun[run] ?? []
            let chosen = all.filter { picked.contains($0.id) }
            guard !chosen.isEmpty else { continue }
            if chosen.count == all.count {
                runs.append(run)
            } else {
                songs.append(contentsOf: chosen)
            }
            count += chosen.count
            bytes += chosen.reduce(0) { $0 + $1.bytes }
        }
    }

    var size: String { ByteCountFormatter.string(fromByteCount: bytes, countStyle: .file) }

    /// The confirmation's text; it always asks, deleting cannot be undone.
    var question: String {
        if count == 1 { return String(localized: "Einen Song (\(size)) endgültig löschen?") }
        return String(localized: "\(count) Songs (\(size)) endgültig löschen?")
    }

    var detail: String {
        var text = String(localized: "Die Songs werden mit ihren Versionen, Stems, Videos und Covern vom Server gelöscht. Das lässt sich nicht rückgängig machen.")
        if runs.count == 1 {
            text += " " + String(localized: "Ein Eintrag verliert damit alle Songs und verschwindet aus der Bibliothek.")
        } else if runs.count > 1 {
            text += " " + String(localized: "\(runs.count) Einträge verlieren damit alle Songs und verschwinden aus der Bibliothek.")
        }
        return text
    }
}

extension AppModel {
    /// One request after another through the library's own endpoints, which refuse a song the worker is on (409); a
    /// refusal does not stop the rest. Answers the ids that are gone and the first problem, if any.
    func deleteSongs(_ deletion: SongDeletion) async -> (deleted: Set<Song.ID>, problem: String?, failed: Int) {
        var deleted = Set<Song.ID>()
        var problem: String?
        var failed = 0
        for run in deletion.runs {
            let ids = songs.filter { $0.run == run }.map(\.id)
            do {
                _ = try await call("DELETE", address("api/runs", run), accept: [200, 202, 204, 404])
                deleted.formUnion(ids)
            } catch {
                failed += ids.count
                problem = problem ?? error.localizedDescription
            }
        }
        for song in deletion.songs {
            do {
                _ = try await call("DELETE", address("api/songs", song.run, song.name), accept: [200, 202, 204, 404])
                deleted.insert(song.id)
            } catch {
                failed += 1
                problem = problem ?? error.localizedDescription
            }
        }
        await reloadLibrary()
        return (deleted, problem, failed)
    }
}
