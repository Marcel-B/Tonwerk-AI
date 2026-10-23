import Foundation
import Observation

/// The synthesizers' sounds as the server keeps them (`logic/synths.ts`): named sounds to reuse, and per track the
/// sound it was given. Held once by ``AppModel``, so the Logic page and the instruments page play the same state.
/// Patches stay JSON until they are played or edited, where ``SynthSound`` fills in what the track's kind needs.
@MainActor
@Observable
final class SoundLibrary {
    struct Named: Identifiable {
        let name: String
        let patch: [String: Any]

        var id: String { name.uppercased() }
        var engine: SynthSound.Engine { patch["engine"] as? String == "fm" ? .fm : .analog }
    }

    struct TrackSound {
        let patch: [String: Any]
        /// The named sound it was taken from; the track follows it while that exists, the copy covers a deleted one.
        let preset: String?
    }

    private(set) var named: [Named] = []
    /// Keyed by track name in upper case, as the server matches them.
    private(set) var tracks: [String: TrackSound] = [:]
    private(set) var problem: String?
    private(set) var loaded = false

    private unowned let model: AppModel

    init(model: AppModel) {
        self.model = model
    }

    func load() async {
        do {
            async let presets = model.synthPresets()
            async let trackSounds = model.trackSynths()
            let (list, assigned) = try await (presets, trackSounds)
            named = list.sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
            tracks = Dictionary(assigned.map { ($0.0.uppercased(), $0.1) }) { _, last in last }
            problem = nil
            loaded = true
        } catch {
            // Tracks still play, on their default sounds.
            problem = error.localizedDescription
        }
    }

    func preset(named name: String) -> Named? {
        named.first { $0.id == name.uppercased() }
    }

    /// What a track plays: its named sound while that exists, else its own copy, else its kind's default.
    func sound(forTrack track: String, kind: String) -> SynthSound {
        guard let entry = tracks[track.uppercased()] else { return .defaults(for: kind) }
        if let name = entry.preset, let sound = preset(named: name) {
            return SynthSound(json: sound.patch, kind: kind)
        }
        return SynthSound(json: entry.patch, kind: kind)
    }

    func preset(ofTrack track: String) -> String? {
        tracks[track.uppercased()]?.preset.flatMap { preset(named: $0) != nil ? $0 : nil }
    }

    /// A track to a named sound (keeping a copy of it), or back to its default with nil.
    func give(_ track: String, named name: String?) async {
        let key = track.uppercased()
        do {
            if let name, let sound = preset(named: name) {
                tracks[key] = TrackSound(patch: sound.patch, preset: sound.name)
                try await model.setTrackSynth(track, patch: sound.patch, preset: sound.name)
            } else {
                tracks[key] = nil
                try await model.deleteTrackSynth(track)
            }
            problem = nil
        } catch {
            problem = error.localizedDescription
        }
    }

    /// A session's sound for the track laid back: its copy and the named sound it followed, or the default with nil.
    /// Unchanged sounds send nothing.
    func restore(_ track: String, to sound: TrackSound?) async {
        let key = track.uppercased()
        do {
            if let sound {
                if let current = tracks[key], current.preset == sound.preset, Self.same(current.patch, sound.patch) {
                    return
                }
                tracks[key] = sound
                try await model.setTrackSynth(track, patch: sound.patch, preset: sound.preset)
            } else if tracks[key] != nil {
                tracks[key] = nil
                try await model.deleteTrackSynth(track)
            }
            problem = nil
        } catch {
            problem = error.localizedDescription
        }
    }

    private static func same(_ a: [String: Any], _ b: [String: Any]) -> Bool {
        let options: JSONSerialization.WritingOptions = [.sortedKeys]
        return (try? JSONSerialization.data(withJSONObject: a, options: options))
            == (try? JSONSerialization.data(withJSONObject: b, options: options))
    }

    /// Saves under a name; the server keeps a name's spelling as first saved.
    func save(_ sound: SynthSound, as name: String) async -> Bool {
        do {
            try await model.saveSynthPreset(name, patch: sound.json)
            await load()
            return true
        } catch {
            problem = error.localizedDescription
            return false
        }
    }

    func delete(_ name: String) async {
        do {
            try await model.deleteSynthPreset(name)
            named.removeAll { $0.id == name.uppercased() }
            problem = nil
        } catch {
            problem = error.localizedDescription
        }
    }
}

// MARK: Server

extension AppModel {
    private func synthURL(_ parts: String...) throws -> URL {
        guard let server else { throw ServerError(String(localized: "Kein Server verbunden.")) }
        return parts.reduce(server.appending(path: "api/logic/synths")) { $0.appending(component: $1) }
    }

    private func synthJSON(_ url: URL) async throws -> [[String: Any]] {
        let (data, response) = try await URLSession.shared.data(from: url)
        guard (response as? HTTPURLResponse)?.statusCode == 200,
              let list = try JSONSerialization.jsonObject(with: data) as? [[String: Any]]
        else { throw ServerError(String(localized: "Die Klänge ließen sich nicht laden.")) }
        return list
    }

    private func synthSend(_ method: String, _ url: URL, _ body: [String: Any]? = nil) async throws {
        var request = URLRequest(url: url)
        request.httpMethod = method
        if let body {
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.httpBody = try JSONSerialization.data(withJSONObject: body)
        }
        let (_, response) = try await URLSession.shared.data(for: request)
        guard let status = (response as? HTTPURLResponse)?.statusCode, (200..<300).contains(status) else {
            throw ServerError(String(localized: "Der Klang wurde nicht gespeichert."))
        }
    }

    func synthPresets() async throws -> [SoundLibrary.Named] {
        try await synthJSON(synthURL("presets")).compactMap { entry in
            guard let name = entry["name"] as? String else { return nil }
            return SoundLibrary.Named(name: name, patch: entry["patch"] as? [String: Any] ?? [:])
        }
    }

    func trackSynths() async throws -> [(String, SoundLibrary.TrackSound)] {
        try await synthJSON(synthURL("tracks")).compactMap { entry in
            guard let track = entry["track"] as? String else { return nil }
            return (track, SoundLibrary.TrackSound(
                patch: entry["patch"] as? [String: Any] ?? [:], preset: entry["preset"] as? String))
        }
    }

    func setTrackSynth(_ track: String, patch: [String: Any], preset: String?) async throws {
        try await synthSend("PUT", synthURL("tracks", track), ["patch": patch, "preset": preset.map { $0 as Any } ?? NSNull()])
    }

    func deleteTrackSynth(_ track: String) async throws {
        try await synthSend("DELETE", synthURL("tracks", track))
    }

    func saveSynthPreset(_ name: String, patch: [String: Any]) async throws {
        try await synthSend("PUT", synthURL("presets", name), ["patch": patch])
    }

    func deleteSynthPreset(_ name: String) async throws {
        try await synthSend("DELETE", synthURL("presets", name))
    }
}
