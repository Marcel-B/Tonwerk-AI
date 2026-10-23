import Foundation
import TonwerkKit

/// A session of the Logic page as the server keeps it (`/api/logic/sessions`, the web page's `logic/sessions.ts`): the
/// page's state for one library song or uploaded score, so the work goes on later, here or in a browser. The settings
/// stay JSON: the web page writes fields the app does not have (chord voicing, swing, …), which the app keeps as they
/// were when it saves.
struct SavedLogicSession: Identifiable {
    let id: Int64
    var name: String
    /// The library song (`run/songN`), nil for an uploaded score.
    let songId: String?
    /// The uploaded score's file name as it was chosen.
    let scoreFile: String?
    var updatedAt: String
    var settings: [String: Any]

    init?(json: Any?) {
        guard let value = json as? [String: Any], let id = (value["id"] as? NSNumber)?.int64Value,
              let name = value["name"] as? String
        else { return nil }
        self.id = id
        self.name = name
        songId = value["songId"] as? String
        scoreFile = value["scoreFile"] as? String
        updatedAt = value["updatedAt"] as? String ?? ""
        settings = value["settings"] as? [String: Any] ?? [:]
    }

    var updated: Date? { try? TonwerkDates().decode(updatedAt) }

    /// Its name and when it was last changed, for the menus.
    var listLabel: String {
        guard let updated else { return name }
        return "\(name) · \(updated.formatted(date: .abbreviated, time: .shortened))"
    }

    /// The newest session of a library song, the one to go on with when the song is opened again (`sessionOfSong`).
    static func newest(of songId: String, in sessions: [SavedLogicSession]) -> SavedLogicSession? {
        sessions.filter { $0.songId == songId }.max { $0.updatedAt < $1.updatedAt }
    }
}

/// What a session holds besides the form, read from its JSON with every field of the wrong shape left out
/// (`readSettings`).
struct LogicSessionSettings {
    var form: LogicForm
    /// The stem set whose vocals go into the project: nil for the newest, empty for none (as `LogicSession.pickedStems`).
    var stems: String?
    var muted: Set<String> = []
    var soloed: Set<String> = []
    var mixer: LogicMixer?
    /// Per track name its sound, nil for the default of its kind.
    var sounds: [String: SoundLibrary.TrackSound?] = [:]
    /// Per track name its MIDI instrument, nil for the app's sound.
    var instruments: [String: Int64?] = [:]

    init(json settings: [String: Any]) {
        form = LogicForm(session: settings["form"] as? [String: Any] ?? [:], stemParts: settings["stemParts"] as? [String: Any])
        if settings["stems"] is NSNull {
            stems = ""
        } else {
            stems = settings["stems"] as? String
        }
        muted = Set(settings["muted"] as? [String] ?? [])
        soloed = Set(settings["solo"] as? [String] ?? [])
        if let mix = settings["mixer"] as? [String: Any] {
            mixer = LogicMixer(json: mix)
        }
        for (track, entry) in settings["sounds"] as? [String: Any] ?? [:] {
            if entry is NSNull {
                sounds[track] = .some(nil)
            } else if let sound = entry as? [String: Any], let patch = sound["patch"] as? [String: Any] {
                sounds[track] = .some(SoundLibrary.TrackSound(patch: patch, preset: sound["preset"] as? String))
            }
        }
        for (track, entry) in settings["instruments"] as? [String: Any] ?? [:] {
            if entry is NSNull {
                instruments[track] = .some(nil)
            } else if let id = (entry as? NSNumber)?.int64Value {
                instruments[track] = .some(id)
            }
        }
    }

    /// The page's state over what the session held (`settingsOf`): fields another client wrote stay, and the tracks
    /// stay as saved until there is a conversion to take them from. `sounds` is keyed in upper case, as
    /// ``SoundLibrary/tracks``.
    static func json(
        form: LogicForm, stems: String?, tracks: [String]?, muted: Set<String>, soloed: Set<String>, mixer: LogicMixer,
        sounds: [String: SoundLibrary.TrackSound], assignments: [String: Int64], over before: [String: Any]
    ) -> [String: Any] {
        var settings = before
        var stored = before["form"] as? [String: Any] ?? [:]
        stored.merge(form.sessionJSON) { _, new in new }
        settings["form"] = stored
        settings["stemParts"] = ["bass": form.bassFromStems, "drums": form.drumsFromStems]
        if let stems {
            settings["stems"] = stems.isEmpty ? NSNull() : stems
        }
        guard let tracks else { return settings }
        let keys = Set(tracks.map(LogicMixer.key))
        settings["muted"] = tracks.filter(muted.contains)
        settings["solo"] = tracks.filter(soloed.contains)
        var mix = mixer.json
        mix["tracks"] = mixer.tracks.filter { keys.contains($0.key) }.mapValues { ["volume": $0.volume, "pan": $0.pan] }
        settings["mixer"] = mix
        settings["sounds"] = Dictionary(uniqueKeysWithValues: tracks.map { track -> (String, Any) in
            guard let sound = sounds[track.uppercased()] else { return (track, NSNull()) }
            return (track, ["patch": sound.patch, "preset": sound.preset.map { $0 as Any } ?? NSNull()] as [String: Any])
        })
        settings["instruments"] = Dictionary(uniqueKeysWithValues: tracks.map { track -> (String, Any) in
            (track, assignments[track].map { $0 as Any } ?? NSNull())
        })
        return settings
    }
}

extension LogicForm {
    /// The fields the app has, in the web form's names and values (`FormState`): `as-written` and `off` for what the
    /// app leaves empty.
    var sessionJSON: [String: Any] {
        [
            "includeChords": includeChords,
            "chordPattern": chordPattern.isEmpty ? "as-written" : chordPattern,
            "bass": bass.isEmpty ? "off" : bass,
            "drums": drums.isEmpty ? "off" : drums,
            "splitDrums": splitDrums,
            "crash": crash,
            "countIn": countIn,
            "splitSections": splitSections,
            "harmonyParts": harmonyParts,
            "harmonyKey": harmonyKey.map { $0 as Any } ?? NSNull(),
            "harmonyChorusOnly": harmonyChorusOnly,
            "doubleVocal": doubleVocal,
            "doubleOctave": doubleOctave,
        ]
    }

    /// A form from a session, the web page's or the app's; what is missing or unknown takes the default.
    init(session form: [String: Any], stemParts: [String: Any]?) {
        self.init()
        if let value = form["includeChords"] as? Bool { includeChords = value }
        if let value = form["chordPattern"] as? String {
            chordPattern = Self.chordPatterns.contains(value) ? value : ""
        }
        if let value = form["bass"] as? String {
            bass = Self.bassPatterns.contains(value) ? value : ""
        }
        if let value = form["drums"] as? String {
            drums = Self.drumPatterns.contains(value) ? value : ""
        }
        if let value = form["splitDrums"] as? Bool { splitDrums = value }
        if let value = form["crash"] as? Bool { crash = value }
        if let value = (form["countIn"] as? NSNumber)?.intValue { countIn = max(0, value) }
        if let value = form["splitSections"] as? Bool { splitSections = value }
        if let value = form["harmonyParts"] as? [String] { harmonyParts = value.filter(HarmonySettings.parts.contains) }
        if let value = form["harmonyKey"] as? String, HarmonySettings.keys.contains(value) { harmonyKey = value }
        if let value = form["harmonyChorusOnly"] as? Bool { harmonyChorusOnly = value }
        if let value = form["doubleVocal"] as? Bool { doubleVocal = value }
        if let value = (form["doubleOctave"] as? NSNumber)?.intValue, Self.doubleOctaves.contains(value) { doubleOctave = value }
        bassFromStems = stemParts?["bass"] as? Bool ?? false
        drumsFromStems = stemParts?["drums"] as? Bool ?? false
    }
}

// MARK: Server

extension AppModel {
    func logicSessions() async throws -> [SavedLogicSession] {
        let (data, _) = try await call("GET", address("api/logic/sessions"))
        let list = try JSONSerialization.jsonObject(with: data) as? [Any] ?? []
        return list.compactMap(SavedLogicSession.init(json:))
    }

    /// A session for a library song, or for the uploaded score, whose file goes along.
    func createLogicSession(name: String, source: ScoreSource, settings: [String: Any]) async throws -> SavedLogicSession {
        let url = try address("api/logic/sessions")
        let settingsText = String(data: try JSONSerialization.data(withJSONObject: settings), encoding: .utf8) ?? "{}"
        let data: Data
        switch source {
        case .file(let file):
            data = try await uploadFile(
                url, fields: [("name", name), ("settings", settingsText)], fileField: "file", file: file, accept: [201]).0
        case .song(let song):
            let boundary = "tonwerk-\(UUID().uuidString)"
            var body = Data()
            for (field, value) in [("name", name), ("song", song.id), ("settings", settingsText)] {
                body.append(Data("--\(boundary)\r\nContent-Disposition: form-data; name=\"\(field)\"\r\n\r\n\(value)\r\n".utf8))
            }
            body.append(Data("--\(boundary)--\r\n".utf8))
            var request = URLRequest(url: url)
            request.httpMethod = "POST"
            request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")
            let (answer, response) = try await URLSession.shared.upload(for: request, from: body)
            guard (response as? HTTPURLResponse)?.statusCode == 201 else {
                throw ServerError(problem: ServerError.text(of: answer) ?? String(localized: "Die Sitzung wurde nicht gespeichert."))
            }
            data = answer
        }
        guard let session = SavedLogicSession(json: try JSONSerialization.jsonObject(with: data)) else {
            throw ServerError(String(localized: "Die Sitzung wurde nicht gespeichert."))
        }
        return session
    }

    /// Renames the session, replaces its settings, or both.
    func updateLogicSession(_ id: Int64, name: String? = nil, settings: [String: Any]? = nil) async throws -> SavedLogicSession {
        var change: [String: Any] = [:]
        if let name { change["name"] = name }
        if let settings { change["settings"] = settings }
        let (data, _) = try await call("PUT", address("api/logic/sessions/\(id)"), json: change)
        guard let session = SavedLogicSession(json: try JSONSerialization.jsonObject(with: data)) else {
            throw ServerError(String(localized: "Die Sitzung wurde nicht gespeichert."))
        }
        return session
    }

    func deleteLogicSession(_ id: Int64) async throws {
        _ = try await call("DELETE", address("api/logic/sessions/\(id)"), accept: [204, 404])
    }

    /// The session's uploaded score in the temporary folder, under the name it was chosen with.
    func logicSessionFile(_ session: SavedLogicSession) async throws -> PickedFile {
        let (data, _) = try await call("GET", address("api/logic/sessions/\(session.id)/file"))
        let folder = FileManager.default.temporaryDirectory
            .appending(path: "Tonwerk-Uploads", directoryHint: .isDirectory)
            .appending(path: UUID().uuidString, directoryHint: .isDirectory)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let name = session.scoreFile.map { ($0 as NSString).lastPathComponent } ?? "score.abc"
        let file = folder.appending(path: name.isEmpty ? "score.abc" : name)
        try data.write(to: file)
        return PickedFile(url: file, name: file.lastPathComponent)
    }

    /// Opens a saved session on the Logic page: its song or its uploaded score, with everything it kept.
    func openInLogic(saved session: SavedLogicSession) {
        if logic == nil {
            logic = LogicSession(model: self)
        }
        page = .logic
        Task { await logic?.openSaved(session) }
    }
}
