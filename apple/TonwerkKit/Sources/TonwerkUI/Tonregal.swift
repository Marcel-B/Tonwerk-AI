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
            throw ServerError(problem: ServerError.text(of: answer) ?? String(localized: "Der Server antwortete \(status)."))
        }
        return try JSONDecoder.tonwerk().decode(Components.Schemas.TonregalTransfer.self, from: answer)
    }
}
