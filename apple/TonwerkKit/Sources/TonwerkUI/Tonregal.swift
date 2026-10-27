import SwiftUI
import TonwerkKit

/// Handing a song to Tonregal, the music library (`TonregalEndpoints`, the web page's export dialog opened with
/// `tonregal: true`): the server sends the export's FLAC with the same tags plus tempo and key and says where
/// Tonregal filed it. Artist and genre as in the share dialog, which shares their stored artist.
struct TonregalView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    let song: Song
    @AppStorage("exportArtist") private var artist = ""
    @State private var genre: String
    @State private var working = false
    @State private var transfer: Components.Schemas.TonregalTransfer?
    @State private var message: String?

    init(song: Song) {
        self.song = song
        _genre = State(initialValue: Genre.of(song.style))
    }

    private var ownCover: Bool {
        model.server.flatMap { song.coverURL(on: $0) } != nil
    }

    private var trimmedArtist: String { artist.trimmingCharacters(in: .whitespaces) }

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    HStack(spacing: 12) {
                        Group {
                            if ownCover {
                                CoverView(song: song)
                            } else {
                                ExportCover(song: song)
                                    .clipShape(RoundedRectangle(cornerRadius: 6, style: .continuous))
                            }
                        }
                        .frame(width: 72, height: 72)
                        .shadow(color: .black.opacity(0.25), radius: 6, y: 3)
                        Text(song.displayTitle)
                            .font(.headline)
                            .lineLimit(2)
                    }
                }
                Section {
                    TextField("Interpret", text: $artist, prompt: Text("z. B. dein Name"))
                    TextField("Genre", text: $genre)
                } header: {
                    CardHeading("Tags", systemImage: "tag")
                } footer: {
                    Text("Tonregal bekommt den Song als FLAC mit Titel, Songtext, Cover, Tempo, Tonart und KI-Kennzeichnung und legt ihn unter Interpret und Album ab.")
                }
                Section { actions }
            }
            .formStyle(.grouped)
            .navigationTitle("In Tonregal übernehmen")
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Fertig") { dismiss() }
                }
            }
        }
        #if os(macOS)
        .frame(minWidth: 440, idealWidth: 480, minHeight: 440, idealHeight: 480)
        #endif
    }

    @ViewBuilder
    private var actions: some View {
        if let transfer {
            let rejected = transfer.state == "rejected"
            Label(Self.outcome(of: transfer), systemImage: rejected ? "exclamationmark.triangle" : "checkmark.circle")
                .foregroundStyle(rejected ? Color.red : Theme.accent)
        } else {
            Button(action: send) {
                HStack(spacing: 8) {
                    if working {
                        ProgressView().controlSize(.small)
                    }
                    Text(working ? "Wird an Tonregal geschickt …" : "Übernehmen")
                }
                .frame(maxWidth: .infinity)
            }
            .buttonStyle(.borderedProminent)
            .disabled(working || trimmedArtist.isEmpty)
            if trimmedArtist.isEmpty {
                Text("Tonregal legt Songs nach Interpret ab, er muss also ausgefüllt sein.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        if let message {
            Label(message, systemImage: "exclamationmark.triangle")
                .foregroundStyle(Color.red)
        }
    }

    /// What Tonregal did with the song, as the web dialog says it.
    static func outcome(of transfer: Components.Schemas.TonregalTransfer) -> String {
        switch transfer.state {
        case "imported":
            let path = transfer.libraryPath ?? ""
            return transfer.repeated
                ? String(localized: "Tonregal hatte den Song schon: \(path)")
                : String(localized: "In Tonregal abgelegt: \(path)")
        case "rejected":
            return String(localized: "Tonregal hat den Song nicht übernommen: \(transfer.error ?? "")")
        default:
            if let error = transfer.error {
                return String(localized: "Übernahme in Tonregal ging nicht: \(error)")
            }
            return String(localized: "Tonregal hat den Song angenommen und legt ihn gleich ab.")
        }
    }

    private func send() {
        working = true
        message = nil
        Task {
            do {
                let cover = ownCover ? nil : ExportCover.jpeg(of: song)
                transfer = try await model.sendToTonregal(
                    song, artist: trimmedArtist, genre: genre.trimmingCharacters(in: .whitespaces), cover: cover)
            } catch {
                message = error.localizedDescription
            }
            working = false
        }
    }
}

extension View {
    /// The dialog that hands the song in `song` to Tonregal, while there is one.
    func tonregalSheet(for song: Binding<Song?>) -> some View {
        sheet(item: song) { song in
            TonregalView(song: song)
        }
    }
}

extension AppModel {
    /// Whether the server can hand songs to Tonregal; false where it cannot say (an older one, the demo).
    func loadTonregal() async {
        guard let tonwerk else { return }
        tonregalReady = (try? await tonwerk.api.getTonregalStatus().ok.body.json.configured) ?? false
    }

    /// Multipart by hand like ``export(_:format:artist:genre:cover:)``, since a refusal's problem text is what matters.
    func sendToTonregal(_ song: Song, artist: String, genre: String, cover: Data?) async throws
        -> Components.Schemas.TonregalTransfer {
        guard let server else { throw ServerError(String(localized: "Kein Server verbunden.")) }
        let boundary = "tonwerk-\(UUID().uuidString)"
        var body = Data()
        func field(_ name: String, _ value: String) {
            body.append(Data("--\(boundary)\r\nContent-Disposition: form-data; name=\"\(name)\"\r\n\r\n\(value)\r\n".utf8))
        }
        field("artist", artist)
        field("genre", genre)
        if let cover {
            body.append(Data(
                "--\(boundary)\r\nContent-Disposition: form-data; name=\"cover\"; filename=\"cover.jpg\"\r\nContent-Type: image/jpeg\r\n\r\n".utf8))
            body.append(cover)
            body.append(Data("\r\n".utf8))
        }
        body.append(Data("--\(boundary)--\r\n".utf8))

        var request = URLRequest(url: server.appending(path: "api/songs/\(song.run)/\(song.name)/tonregal"))
        request.httpMethod = "POST"
        request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")
        // The upload over Tailscale plus Tonregal's filing, which the server waits for.
        request.timeoutInterval = 360
        let (answer, response) = try await URLSession.shared.upload(for: request, from: body)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        guard status == 200 else {
            // Tonregal's own refusal of the key is English and says nothing about where to set it.
            if let problem = try? JSONSerialization.jsonObject(with: answer) as? [String: Any],
               problem["tonregalStatus"] as? Int == 401
            {
                throw ServerError(TonregalSettingsSection.text(of: TonregalCheckResult(state: "key", detail: nil)).0)
            }
            let text = ServerError.text(of: answer) ?? String(localized: "Der Server antwortete \(status).")
            throw ServerError(String(localized: "Übernahme in Tonregal ging nicht: \(text)"))
        }
        return try JSONDecoder.tonwerk().decode(Components.Schemas.TonregalTransfer.self, from: answer)
    }
}

/// Tonregal's address and API key on the server (`PUT api/tonregal`, written into its `~/.config/tonwerk/tonregal.env`),
/// then checked against Tonregal (`POST api/tonregal/check`), as the web page's settings do. The key is never shown;
/// an empty field keeps it.
struct TonregalSettingsSection: View {
    @Environment(AppModel.self) private var model
    @State private var status: TonregalSettingsStatus?
    @State private var url = ""
    @State private var apiKey = ""
    @State private var working = false
    @State private var message: String?
    @State private var failed = false

    var body: some View {
        Section {
            if let status {
                if !status.editable {
                    Text("Auf dem Server setzen Umgebungsvariablen Adresse und Key; hier lässt sich nichts ändern.")
                        .foregroundStyle(.secondary)
                }
                TextField("Adresse", text: $url, prompt: Text(verbatim: "http://oi-o-mat.tail6105c0.ts.net:5100"))
                    .autocorrectionDisabled()
                    #if os(iOS)
                    .textInputAutocapitalization(.never)
                    .keyboardType(.URL)
                    #endif
                    .disabled(!status.editable)
                SecureField("API-Key", text: $apiKey, prompt: Text(status.keySet ? String(localized: "gesetzt, leer lassen zum Behalten") : ""))
                    .disabled(!status.editable)
                Button {
                    save()
                } label: {
                    if working {
                        ProgressView().controlSize(.small)
                    } else {
                        Text("Speichern und prüfen")
                    }
                }
                .disabled(!status.editable || working)
                if let message {
                    Text(message)
                        .foregroundStyle(failed ? .red : .secondary)
                }
            } else {
                Text("Der Server kennt diese Einstellung noch nicht.")
                    .foregroundStyle(.secondary)
            }
        } header: {
            Text("Tonregal")
        } footer: {
            Text("Der Key unter Tonregal:Ingest:Keys:tonwerk-ai in Tonregals appsettings.Production.json. Ohne Adresse ist die Übernahme aus.")
        }
        .task(id: model.server) { await load() }
    }

    private func load() async {
        guard let server = model.server,
              let (data, response) = try? await URLSession.shared.data(from: server.appending(path: "api/tonregal")),
              (response as? HTTPURLResponse)?.statusCode == 200,
              let loaded = try? JSONDecoder().decode(TonregalSettingsStatus.self, from: data)
        else {
            status = nil
            return
        }
        status = loaded
        url = loaded.url ?? ""
    }

    private func save() {
        guard let server = model.server else { return }
        working = true
        message = nil
        Task {
            defer { working = false }
            do {
                var request = URLRequest(url: server.appending(path: "api/tonregal"))
                request.httpMethod = "PUT"
                request.setValue("application/json", forHTTPHeaderField: "Content-Type")
                let key = apiKey.trimmingCharacters(in: .whitespaces)
                request.httpBody = try JSONSerialization.data(withJSONObject: [
                    "url": url.trimmingCharacters(in: .whitespaces),
                    "apiKey": key.isEmpty ? NSNull() as Any : key as Any,
                ])
                let (answer, response) = try await URLSession.shared.data(for: request)
                guard (response as? HTTPURLResponse)?.statusCode == 200 else {
                    throw ServerError(problem: ServerError.text(of: answer))
                }
                status = try JSONDecoder().decode(TonregalSettingsStatus.self, from: answer)
                model.tonregalReady = status?.configured ?? false
                apiKey = ""

                var check = URLRequest(url: server.appending(path: "api/tonregal/check"))
                check.httpMethod = "POST"
                let (found, _) = try await URLSession.shared.data(for: check)
                let result = try JSONDecoder().decode(TonregalCheckResult.self, from: found)
                (message, failed) = Self.text(of: result)
            } catch {
                (message, failed) = (error.localizedDescription, true)
            }
        }
    }

    /// What the check found, as the web page says it.
    static func text(of check: TonregalCheckResult) -> (String, Bool) {
        let detail = check.detail ?? ""
        switch check.state {
        case "ok":
            return (String(localized: "Gespeichert. Tonregal nimmt Songs mit diesem Key an."), false)
        case "notConfigured":
            return (String(localized: "Gespeichert. Ohne Adresse ist die Übernahme in Tonregal aus."), false)
        case "key":
            return (String(localized: "Tonregal kennt den API-Key nicht. TONREGAL_API_KEY in ~/.config/tonwerk/tonregal.env muss genau der Key unter Tonregal:Ingest:Keys:tonwerk-ai in Tonregals appsettings.Production.json sein."), true)
        case "setup":
            return (String(localized: "Tonregal hat noch keinen Ingest-Key: Tonregal:Ingest:Keys in seiner appsettings.Production.json eintragen und Tonregal neu starten."), true)
        case "unreachable":
            return (String(localized: "Gespeichert, aber Tonregal ist nicht erreichbar: \(detail)"), true)
        default:
            return (String(localized: "Gespeichert, aber Tonregal antwortete unerwartet: \(detail)"), true)
        }
    }
}

/// `GET api/tonregal`, decoded apart from the generated type so a server from before the settings reads as none.
struct TonregalSettingsStatus: Decodable {
    let configured: Bool
    let url: String?
    let keySet: Bool
    let editable: Bool
}

struct TonregalCheckResult: Decodable {
    let state: String
    let detail: String?
}
