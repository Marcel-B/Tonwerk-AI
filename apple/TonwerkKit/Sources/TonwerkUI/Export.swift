import ImageIO
import SwiftUI
import TonwerkKit
import UniformTypeIdentifiers

/// Sharing or exporting a song, the web page's `ExportDialog.vue`: a small AAC for a messenger, or MP3, M4A or FLAC
/// for a music library, with title, lyrics, artist, genre and cover in its tags. The server writes the tags and puts
/// in the song's own cover; only a song without one gets the drawn cover, made here.
enum ExportFormat: String, CaseIterable, Identifiable {
    case small
    case mp3
    case m4a
    case flac

    var id: String { rawValue }

    var name: String {
        self == .small ? String(localized: "Klein") : rawValue.uppercased()
    }

    var hint: String {
        switch self {
        case .small: String(localized: "AAC mit 128 kbit/s, ein Zehntel der FLAC: zum Verschicken per Messenger.")
        case .mp3: String(localized: "Läuft überall, 320 kbit/s.")
        case .m4a: String(localized: "AAC mit 256 kbit/s, passt am besten zu Apple Musik.")
        case .flac: String(localized: "Verlustfrei, etwa fünfmal so groß.")
        }
    }

    var fileExtension: String { self == .small ? "m4a" : rawValue }
}

enum Genre {
    /// The genre building blocks, longest first, so "synthpop" wins over "pop" and "indie rock" over "rock".
    static let known = StyleBlocks.Category.genre.tags.sorted { $0.count > $1.count }

    /// The style's first genre, as `export.ts` `genreOf` finds it: a building block that is a whole part of the style,
    /// else the first named anywhere in it, else nothing.
    static func of(_ style: String) -> String {
        let parts = style.split(separator: ",")
            .map { $0.trimmingCharacters(in: .whitespaces).lowercased() }
            .filter { !$0.isEmpty }
        for part in parts {
            if let genre = known.first(where: { $0.lowercased() == part }) {
                return capitalized(genre)
            }
        }
        let text = " \(parts.joined(separator: " ")) "
        return known.first { text.contains(" \($0.lowercased()) ") }.map(capitalized) ?? ""
    }

    private static func capitalized(_ text: String) -> String {
        text.prefix(1).uppercased() + text.dropFirst()
    }
}

/// The cover a song without its own goes out with: its title on the gradient of its drawn cover, as `drawCover`.
struct ExportCover: View {
    let song: Song

    var body: some View {
        GeometryReader { box in
            let size = box.size.width
            ZStack(alignment: .bottomLeading) {
                DrawnCover(id: song.id, symbol: false)
                VStack(alignment: .leading, spacing: size * 0.03) {
                    Text(song.title.isEmpty ? "Tonwerk" : song.title)
                        .font(.system(size: size * 0.11, weight: .bold))
                        .lineLimit(4)
                        .minimumScaleFactor(0.4)
                    Text(genreLine)
                        .font(.system(size: size * 0.036, weight: .medium))
                        .opacity(0.7)
                }
                .foregroundStyle(.white)
                .padding(size * 0.08)
            }
        }
        .aspectRatio(1, contentMode: .fit)
    }

    private var genreLine: String {
        let genre = Genre.of(song.style)
        return genre.isEmpty ? "Tonwerk" : "Tonwerk · \(genre)"
    }

    /// A 1000 px JPEG, square and large enough for a car's screen, as the web page sends it.
    @MainActor
    static func jpeg(of song: Song) -> Data? {
        let renderer = ImageRenderer(content: ExportCover(song: song).frame(width: 1000, height: 1000))
        renderer.scale = 1
        guard let image = renderer.cgImage else { return nil }
        let data = NSMutableData()
        guard let destination = CGImageDestinationCreateWithData(data, UTType.jpeg.identifier as CFString, 1, nil)
        else { return nil }
        CGImageDestinationAddImage(destination, image, [kCGImageDestinationLossyCompressionQuality: 0.88] as CFDictionary)
        return CGImageDestinationFinalize(destination) ? data as Data : nil
    }
}

/// The dialog: format, artist and genre, then the file is made and shared or saved.
struct ExportView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    let song: Song
    @AppStorage("exportFormat") private var format = ExportFormat.small
    @AppStorage("exportArtist") private var artist = ""
    @State private var genre: String
    @State private var working = false
    @State private var file: URL?
    @State private var saving = false
    @State private var message: String?
    @State private var failed = false

    init(song: Song) {
        self.song = song
        _genre = State(initialValue: Genre.of(song.style))
    }

    private var ownCover: Bool {
        model.server.flatMap { song.coverURL(on: $0) } != nil
    }

    var body: some View {
        NavigationStack {
            Form {
                Section { header }
                Section {
                    Picker("Format", selection: $format) {
                        ForEach(ExportFormat.allCases) { format in
                            Text(format.name).tag(format)
                        }
                    }
                    .pickerStyle(.segmented)
                    .labelsHidden()
                    Text(format.hint)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                } header: {
                    CardHeading("Format", systemImage: "waveform")
                }
                Section {
                    TextField("Interpret", text: $artist, prompt: Text("z. B. dein Name"))
                    TextField("Genre", text: $genre)
                } header: {
                    CardHeading("Tags", systemImage: "tag")
                } footer: {
                    Text(ownCover
                        ? "Das Cover des Songs geht mit hinein."
                        : "Der Song hat kein eigenes Cover, das gezeichnete geht mit hinein.")
                }
                Section { actions }
            }
            .formStyle(.grouped)
            .navigationTitle("Teilen")
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Fertig") { dismiss() }
                }
            }
            .fileMover(isPresented: $saving, file: file) { result in
                if case .success = result {
                    file = nil
                    failed = false
                    message = String(localized: "Gesichert.")
                }
            }
            .onChange(of: format) { file = nil }
            .onChange(of: artist) { file = nil }
            .onChange(of: genre) { file = nil }
        }
        #if os(macOS)
        .frame(minWidth: 440, idealWidth: 480, minHeight: 520, idealHeight: 560)
        #endif
    }

    private var header: some View {
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
            VStack(alignment: .leading, spacing: 3) {
                Text(song.displayTitle)
                    .font(.headline)
                    .lineLimit(2)
                Text(song.style)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(2)
            }
        }
    }

    @ViewBuilder
    private var actions: some View {
        if let file {
            ShareLink(item: file) {
                Label("Teilen", systemImage: "square.and.arrow.up")
            }
            Button {
                saving = true
            } label: {
                Label("Sichern …", systemImage: "square.and.arrow.down")
            }
            Text("„\(file.lastPathComponent)“ ist bereit.")
                .font(.caption)
                .foregroundStyle(.secondary)
        } else {
            Button(action: make) {
                HStack(spacing: 8) {
                    if working {
                        ProgressView().controlSize(.small)
                    }
                    Text(working ? "Wird erstellt …" : "Datei erstellen")
                }
                .frame(maxWidth: .infinity)
            }
            .buttonStyle(.borderedProminent)
            .disabled(working)
        }
        if let message {
            Label(message, systemImage: failed ? "exclamationmark.triangle" : "checkmark.circle")
                .foregroundStyle(failed ? Color.red : Theme.accent)
        }
    }

    private func make() {
        working = true
        message = nil
        Task {
            do {
                let cover = ownCover ? nil : ExportCover.jpeg(of: song)
                file = try await model.export(
                    song, format: format, artist: artist.trimmingCharacters(in: .whitespaces),
                    genre: genre.trimmingCharacters(in: .whitespaces), cover: cover)
            } catch {
                failed = true
                message = error.localizedDescription
            }
            working = false
        }
    }
}

extension View {
    /// The share dialog for the song in `song`, while there is one.
    func exportSheet(for song: Binding<Song?>) -> some View {
        sheet(item: song) { song in
            ExportView(song: song)
        }
    }
}

extension AppModel {
    /// Has the server make the file and keeps it in a folder of its own under the temporary directory, named as the
    /// server names it. Multipart by hand, like the web page's `exportSongFile`.
    func export(_ song: Song, format: ExportFormat, artist: String, genre: String, cover: Data?) async throws -> URL {
        guard let server else { throw ServerError(String(localized: "Kein Server verbunden.")) }
        let boundary = "tonwerk-\(UUID().uuidString)"
        var body = Data()
        func field(_ name: String, _ value: String) {
            body.append(Data("--\(boundary)\r\nContent-Disposition: form-data; name=\"\(name)\"\r\n\r\n\(value)\r\n".utf8))
        }
        field("format", format.rawValue)
        field("artist", artist)
        field("genre", genre)
        if let cover {
            body.append(Data(
                "--\(boundary)\r\nContent-Disposition: form-data; name=\"cover\"; filename=\"cover.jpg\"\r\nContent-Type: image/jpeg\r\n\r\n".utf8))
            body.append(cover)
            body.append(Data("\r\n".utf8))
        }
        body.append(Data("--\(boundary)--\r\n".utf8))

        var request = URLRequest(url: server.appending(path: "api/songs/\(song.run)/\(song.name)/export"))
        request.httpMethod = "POST"
        request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")
        request.timeoutInterval = 300
        let (download, response) = try await URLSession.shared.upload(for: request, from: body)
        let http = response as? HTTPURLResponse
        guard http?.statusCode == 200 else {
            throw ServerError(problem: ServerError.text(of: download)
                ?? String(localized: "Der Server antwortete \(http?.statusCode ?? 0)."))
        }
        let name = Self.fileName(http?.value(forHTTPHeaderField: "Content-Disposition"))
            ?? "\(song.displayTitle).\(format.fileExtension)"
        let folder = FileManager.default.temporaryDirectory
            .appending(path: "Tonwerk-Export", directoryHint: .isDirectory)
            .appending(path: UUID().uuidString, directoryHint: .isDirectory)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let file = folder.appending(path: name.replacingOccurrences(of: "/", with: "-"))
        try download.write(to: file)
        return file
    }

    /// The file name of a Content-Disposition header: the UTF-8 `filename*` where there is one, else `filename`.
    static func fileName(_ header: String?) -> String? {
        guard let header else { return nil }
        for part in header.split(separator: ";").map({ $0.trimmingCharacters(in: .whitespaces) }) {
            if part.lowercased().hasPrefix("filename*="), let quote = part.range(of: "''") {
                if let name = String(part[quote.upperBound...]).removingPercentEncoding, !name.isEmpty {
                    return name
                }
            }
        }
        for part in header.split(separator: ";").map({ $0.trimmingCharacters(in: .whitespaces) }) {
            if part.lowercased().hasPrefix("filename=") {
                let name = part.dropFirst("filename=".count).trimmingCharacters(in: CharacterSet(charactersIn: "\""))
                if !name.isEmpty { return name }
            }
        }
        return nil
    }
}
