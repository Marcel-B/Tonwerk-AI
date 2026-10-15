import ImageIO
import PhotosUI
import SwiftUI
import TonwerkKit
import UniformTypeIdentifiers

/// A song's own cover, what the web page's library offers: a photo of one's own, cut square, or a picture FLUX.2 Klein
/// paints on the Mac (`ImageDialog.vue`), or none again, which brings the drawn cover back.
enum CoverPhoto {
    /// The photo as the web page's `export.ts` sends it: cut square around its middle, at most `side` pixels, a JPEG.
    /// ImageIO reads HEIC and turns the picture as its EXIF says.
    static func squareJPEG(from data: Data, side: Int = 1000) -> Data? {
        guard let source = CGImageSourceCreateWithData(data as CFData, nil),
              let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
              let width = properties[kCGImagePropertyPixelWidth] as? Int,
              let height = properties[kCGImagePropertyPixelHeight] as? Int,
              width > 0, height > 0
        else { return nil }
        // The thumbnail's size is its longer side, so the shorter one comes out at `side` where the photo has it.
        let longest = Int((Double(max(width, height)) * Double(side) / Double(min(width, height))).rounded(.up))
        let options: [CFString: Any] = [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceCreateThumbnailWithTransform: true,
            kCGImageSourceThumbnailMaxPixelSize: longest,
        ]
        guard let scaled = CGImageSourceCreateThumbnailAtIndex(source, 0, options as CFDictionary) else { return nil }
        let edge = min(scaled.width, scaled.height, side)
        let square = CGRect(x: (scaled.width - edge) / 2, y: (scaled.height - edge) / 2, width: edge, height: edge)
        guard let cut = scaled.cropping(to: square) else { return nil }
        let output = NSMutableData()
        guard let destination = CGImageDestinationCreateWithData(output, UTType.jpeg.identifier as CFString, 1, nil)
        else { return nil }
        CGImageDestinationAddImage(destination, cut, [kCGImageDestinationLossyCompressionQuality: 0.88] as CFDictionary)
        return CGImageDestinationFinalize(destination) ? output as Data : nil
    }
}

enum CoverPrompt {
    /// A description from the song's title and style, as `images.ts` `suggestPrompt` writes it. English, since Klein
    /// reads it through Qwen3; only what should be seen, since distilled Klein has no negative prompt and draws what a
    /// prompt names ("no text" brings text). Tempo and vocal parts of the style have nothing to show.
    static func suggest(title: String, style: String, withTitle: Bool) -> String {
        let skipped = try! NSRegularExpression(
            pattern: "\\d|bpm|tempo|vocal|voice|singer|sung|lyrics|language", options: .caseInsensitive)
        let mood = style.split(separator: ",", omittingEmptySubsequences: false)
            .map { part in
                part.split(whereSeparator: \.isWhitespace).joined(separator: " ")
            }
            .filter { part in
                !part.isEmpty && skipped.firstMatch(in: part, range: NSRange(part.startIndex..., in: part)) == nil
            }
            .joined(separator: ", ")
        var sentences = [
            "A square painting, one striking scene with a strong central motif, rich colour and dramatic light, painterly, high detail."
        ]
        if !mood.isEmpty {
            sentences.append("Its mood and colours evoke \(mood) music.")
        }
        if withTitle && !title.isEmpty {
            sentences.append("The words \"\(title)\" are written once in elegant, legible lettering.")
        }
        return sentences.joined(separator: " ")
    }
}

// MARK: Server

extension AppModel {
    /// A JPEG as the song's own cover. Multipart by hand, like the export; the `library` event that follows brings
    /// the new cover's address.
    func setCover(_ song: Song, jpeg: Data) async throws {
        guard let server else { throw ServerError(String(localized: "Kein Server verbunden.")) }
        let boundary = "tonwerk-\(UUID().uuidString)"
        var body = Data(
            "--\(boundary)\r\nContent-Disposition: form-data; name=\"cover\"; filename=\"cover.jpg\"\r\nContent-Type: image/jpeg\r\n\r\n".utf8)
        body.append(jpeg)
        body.append(Data("\r\n--\(boundary)--\r\n".utf8))
        var request = URLRequest(url: server.appending(path: "api/songs/\(song.run)/\(song.name)/cover"))
        request.httpMethod = "PUT"
        request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")
        let (answer, response) = try await URLSession.shared.upload(for: request, from: body)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        guard status == 204 else {
            throw ServerError(problem: ServerError.text(of: answer)
                ?? String(localized: "Der Server antwortete \(status)."))
        }
        await reloadLibrary()
    }

    func removeCover(_ song: Song) async throws {
        guard let tonwerk else { return }
        guard case .noContent = try await tonwerk.api.deleteSongCover(path: .init(run: song.run, song: song.name))
        else { throw ServerError(String(localized: "Das Cover wurde nicht entfernt.")) }
        await reloadLibrary()
    }

    func imageInfo() async -> Components.Schemas.ImageInfo? {
        try? await tonwerk?.api.getImageInfo().ok.body.json
    }

    func images(of song: Song) async -> [Components.Schemas.ImageState] {
        (try? await tonwerk?.api.listSongImages(path: .init(run: song.run, song: song.name)).ok.body.json) ?? []
    }

    func paint(_ song: Song, prompt: String, model: String) async throws -> Components.Schemas.ImageState {
        guard let tonwerk else { throw ServerError(String(localized: "Kein Server verbunden.")) }
        switch try await tonwerk.api.createSongImage(
            path: .init(run: song.run, song: song.name),
            body: .json(.init(prompt: prompt, model: model, seed: nil)))
        {
        case .accepted(let accepted):
            let image = try accepted.body.json
            noteImage(image)
            return image
        case .badRequest(let refused):
            let problem = try? refused.body.applicationProblemJson
            throw ServerError(problem: problem?.errors?.additionalProperties.values.first?.first ?? problem?.detail)
        case .notImplemented(let refused):
            throw ServerError(problem: try? refused.body.applicationProblemJson.detail)
        case .notFound:
            throw ServerError(String(localized: "Den Song gibt es nicht mehr."))
        case .undocumented(let status, _):
            throw ServerError(String(localized: "Der Server antwortete \(status)."))
        }
    }

    /// A candidate as the song's cover; it stays among the candidates, so another can be tried.
    func takeImage(_ image: Components.Schemas.ImageState) async throws {
        guard let tonwerk else { return }
        guard case .noContent = try await tonwerk.api.setImageAsCover(path: .init(id: image.id))
        else { throw ServerError(String(localized: "Das Bild ist nicht mehr da.")) }
        await reloadLibrary()
    }

    /// Deletes a candidate, or cancels one still waiting or being painted.
    func deleteImage(_ image: Components.Schemas.ImageState) async {
        _ = try? await tonwerk?.api.deleteImage(path: .init(id: image.id))
        images[image.id] = nil
    }

    func imageURL(_ image: Components.Schemas.ImageState) -> URL? {
        server?.appending(path: "api/images/\(image.id)")
    }
}

// MARK: The cover in the song's details

/// The large cover with what can be done to it: a menu on its corner and the same as its context menu.
struct CoverEditor: View {
    @Environment(AppModel.self) private var model
    let song: Song
    @State private var pickingPhoto = false
    @State private var photo: PhotosPickerItem?
    @State private var importing = false
    @State private var painting: Song?
    @State private var confirmRemove = false
    @State private var working = false
    @State private var problem: String?

    private var ownCover: Bool {
        model.server.flatMap { song.coverURL(on: $0) } != nil
    }

    var body: some View {
        VStack(spacing: 8) {
            CoverView(song: song)
                .aspectRatio(1, contentMode: .fit)
                .frame(maxWidth: 220)
                .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
                .overlay {
                    if working {
                        ProgressView()
                            .padding(14)
                            .background(.regularMaterial, in: Circle())
                    }
                }
                .overlay(alignment: .bottomTrailing) {
                    Menu {
                        items
                    } label: {
                        Image(systemName: "photo.badge.plus")
                            .font(.body.weight(.semibold))
                            .frame(width: 36, height: 36)
                            .background(.regularMaterial, in: Circle())
                    }
                    .menuStyle(.button)
                    .menuIndicator(.hidden)
                    .buttonStyle(.plain)
                    .fixedSize()
                    .padding(8)
                    .accessibilityLabel("Cover")
                }
                .contextMenu { items }
                .shadow(color: .black.opacity(0.35), radius: 16, y: 8)
            if let problem {
                Label(problem, systemImage: "exclamationmark.triangle")
                    .font(.caption)
                    .foregroundStyle(.red)
            }
        }
        .photosPicker(isPresented: $pickingPhoto, selection: $photo, matching: .images)
        .onChange(of: photo) {
            guard let item = photo else { return }
            photo = nil
            Task { await use { try await item.loadTransferable(type: Data.self) } }
        }
        .fileImporter(isPresented: $importing, allowedContentTypes: [.image]) { result in
            guard case .success(let url) = result else { return }
            Task {
                await use {
                    let scoped = url.startAccessingSecurityScopedResource()
                    defer { if scoped { url.stopAccessingSecurityScopedResource() } }
                    return try Data(contentsOf: url)
                }
            }
        }
        .confirmationDialog("Cover entfernen?", isPresented: $confirmRemove) {
            Button("Entfernen", role: .destructive) { Task { await remove() } }
        } message: {
            Text("Der Song bekommt wieder das gezeichnete Cover.")
        }
        .paintSheet(for: $painting)
    }

    @ViewBuilder
    private var items: some View {
        Button {
            pickingPhoto = true
        } label: {
            Label("Foto wählen …", systemImage: "photo.on.rectangle")
        }
        Button {
            importing = true
        } label: {
            Label("Bilddatei wählen …", systemImage: "folder")
        }
        Button {
            painting = song
        } label: {
            Label("Cover malen …", systemImage: "paintbrush.pointed")
        }
        if ownCover {
            Divider()
            Button(role: .destructive) {
                confirmRemove = true
            } label: {
                Label("Cover entfernen", systemImage: "trash")
            }
        }
    }

    private func use(_ load: () async throws -> Data?) async {
        working = true
        problem = nil
        defer { working = false }
        do {
            guard let data = try await load(), let jpeg = CoverPhoto.squareJPEG(from: data) else {
                throw ServerError(String(localized: "Das Bild ließ sich nicht lesen."))
            }
            try await model.setCover(song, jpeg: jpeg)
        } catch {
            problem = error.localizedDescription
        }
    }

    private func remove() async {
        working = true
        problem = nil
        defer { working = false }
        do {
            try await model.removeCover(song)
        } catch {
            problem = error.localizedDescription
        }
    }
}

// MARK: Painting

/// A description goes to the Mac, which paints with FLUX.2 Klein once the memory is free; the candidates are listed
/// here (and in the queue while in the works), and "Als Cover" makes one the song's cover.
struct PaintCoverView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    let song: Song
    @AppStorage("imageModel") private var modelId = ""
    @AppStorage("imageWithTitle") private var withTitle = false
    @State private var prompt = ""
    /// Whether the description is still the suggestion, so that the title switch may rewrite it.
    @State private var suggested = true
    @State private var info: Components.Schemas.ImageInfo?
    @State private var loaded: [Components.Schemas.ImageState] = []
    @State private var sending = false
    @State private var taking: String?
    @State private var message: String?
    @State private var failed = false

    var body: some View {
        NavigationStack {
            Form {
                if let info, !info.installed {
                    Section {
                        Text("Zum Malen braucht der Mac mflux mit FLUX.2 Klein. Auf dem Mac einmal ausführen: deploy/install-images.sh (oder setup-mac.sh mit --images).")
                            .font(.callout)
                            .foregroundStyle(.secondary)
                    }
                }
                describe
                modelSection
                Section { start }
                if !candidates.isEmpty {
                    Section {
                        LazyVGrid(columns: [GridItem(.adaptive(minimum: 140), spacing: 12)], spacing: 14) {
                            ForEach(candidates, id: \.id) { image in
                                candidate(image)
                            }
                        }
                        .padding(.vertical, 6)
                    } header: {
                        CardHeading("Vorschläge", systemImage: "photo.stack")
                    }
                }
            }
            .formStyle(.grouped)
            .navigationTitle("Cover malen")
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Fertig") { dismiss() }
                }
            }
            .task { await load() }
            .onChange(of: withTitle) {
                if suggested { prompt = suggestion }
            }
        }
        #if os(macOS)
        .frame(minWidth: 480, idealWidth: 540, minHeight: 600, idealHeight: 720)
        #endif
    }

    private var suggestion: String {
        CoverPrompt.suggest(title: song.title, style: song.style, withTitle: withTitle)
    }

    private var chosen: Components.Schemas.ImageModelInfo? {
        info?.models.first { $0.id == modelId }
    }

    /// What the server listed, with newer states from the event stream laid over it; deleted ones leave.
    private var candidates: [Components.Schemas.ImageState] {
        var byId = Dictionary(loaded.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
        for image in model.images.values where image.songId == song.id {
            if let known = byId[image.id], (known.updatedAt ?? .distantPast) > (image.updatedAt ?? .distantPast) {
                continue
            }
            byId[image.id] = image
        }
        return byId.values
            .filter { $0.stage != "cancelled" }
            .sorted { ($0.createdAt ?? .distantPast) > ($1.createdAt ?? .distantPast) }
    }

    private var describe: some View {
        Section {
            TextField("Bildbeschreibung", text: $prompt, axis: .vertical)
                .lineLimit(4...10)
                .onChange(of: prompt) { suggested = prompt == suggestion }
            Toggle("Titel ins Bild schreiben", isOn: $withTitle)
        } header: {
            HStack {
                CardHeading("Bildbeschreibung", systemImage: "text.quote")
                Spacer()
                if !suggested {
                    Button("Vorschlag", systemImage: "arrow.uturn.backward") {
                        prompt = suggestion
                        suggested = true
                    }
                    .font(.caption)
                    .buttonStyle(.borderless)
                }
            }
        } footer: {
            Text("Am besten auf Englisch. Nur beschreiben, was zu sehen sein soll: „no text“ oder „no logo“ bringen Text und Logos eher ins Bild, einen Negativ-Prompt kennt FLUX Klein nicht.")
        }
    }

    @ViewBuilder
    private var modelSection: some View {
        if let info, !info.models.isEmpty {
            Section {
                if info.models.count > 1 {
                    Picker("Modell", selection: $modelId) {
                        ForEach(info.models, id: \.id) { model in
                            Text(model.label).tag(model.id)
                        }
                    }
                    .pickerStyle(.segmented)
                    .labelsHidden()
                }
                if let chosen {
                    Text(chosen.commercial
                        ? "\(chosen.license), auch für veröffentlichte Songs"
                        : "\(chosen.license), nur privat, nicht für veröffentlichte Songs")
                        .foregroundStyle(chosen.commercial ? Color.secondary : Color.red)
                    if !chosen.downloaded {
                        Text("Lädt beim ersten Bild etwa \(chosen.downloadGb.formatted(.number.precision(.fractionLength(0...1)))) GB herunter.")
                            .foregroundStyle(.secondary)
                    }
                    if !chosen.tokenFound {
                        Text("Braucht einen Hugging-Face-Token: Lizenz auf huggingface.co annehmen, Token in ~/.config/tonwerk/huggingface.token auf dem Mac legen.")
                            .foregroundStyle(.red)
                    }
                }
            } header: {
                CardHeading("Modell", systemImage: "cpu")
            } footer: {
                Text("Der Mac malt im Hintergrund, sobald kein anderes Modell den Speicher braucht (etwa \((chosen?.memoryGb ?? 9).formatted(.number.precision(.fractionLength(0...1)))) GB, unter einer Minute pro Bild). Jedes Bild ist ein Vorschlag, erst „Als Cover“ macht es zum Cover.")
            }
            .font(.callout)
        }
    }

    @ViewBuilder
    private var start: some View {
        Button(action: paint) {
            HStack(spacing: 8) {
                if sending {
                    ProgressView().controlSize(.small)
                } else {
                    Image(systemName: "paintbrush.pointed")
                }
                Text("Malen")
            }
            .frame(maxWidth: .infinity)
        }
        .buttonStyle(.borderedProminent)
        .disabled(sending || info?.installed != true || chosen == nil
            || prompt.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
        if let message {
            Label(message, systemImage: failed ? "exclamationmark.triangle" : "checkmark.circle")
                .foregroundStyle(failed ? Color.red : Theme.accent)
        }
    }

    private func candidate(_ image: Components.Schemas.ImageState) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            picture(image)
                .aspectRatio(1, contentMode: .fit)
                .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
            Text("\(image.modelLabel) · Seed \(image.seed.map(String.init) ?? "–")")
                .font(.caption2)
                .foregroundStyle(.secondary)
                .lineLimit(1)
                .help(image.prompt)
            HStack(spacing: 6) {
                if image.stage == "done" {
                    Button {
                        Task { await take(image) }
                    } label: {
                        HStack(spacing: 4) {
                            if taking == image.id {
                                ProgressView().controlSize(.mini)
                            }
                            Text("Als Cover")
                        }
                        .frame(maxWidth: .infinity)
                    }
                    .buttonStyle(.borderedProminent)
                    .controlSize(.small)
                    .disabled(taking != nil)
                } else {
                    Spacer()
                }
                Button(role: .destructive) {
                    Task { await model.deleteImage(image); loaded.removeAll { $0.id == image.id } }
                } label: {
                    Image(systemName: "trash")
                }
                .buttonStyle(.borderless)
                .accessibilityLabel(image.finished == true ? "Löschen" : "Abbrechen")
            }
        }
    }

    @ViewBuilder
    private func picture(_ image: Components.Schemas.ImageState) -> some View {
        ZStack {
            Rectangle().fill(.quaternary)
            switch image.stage ?? "" {
            case "done":
                AsyncImage(url: model.imageURL(image)) { picture in
                    picture.resizable().scaledToFill()
                } placeholder: {
                    ProgressView()
                }
            case "failed":
                VStack(spacing: 6) {
                    Image(systemName: "exclamationmark.triangle")
                    Text("Fehlgeschlagen: \(image.message ?? "")")
                        .font(.caption2)
                        .multilineTextAlignment(.center)
                        .lineLimit(5)
                }
                .foregroundStyle(.red)
                .padding(8)
            default:
                VStack(spacing: 8) {
                    if image.stage != "queued" {
                        ProgressView()
                    }
                    Text(Self.stage(of: image))
                        .font(.caption.weight(.medium))
                        .padding(.horizontal, 8)
                        .padding(.vertical, 3)
                        .background(.thinMaterial, in: Capsule())
                }
            }
        }
    }

    static func stage(of image: Components.Schemas.ImageState) -> String {
        switch image.stage ?? "" {
        case "painting": "\(Int(((image.fraction ?? 0) * 100).rounded())) %"
        case "loading": String(localized: "Lädt Modell")
        default: String(localized: "Wartet")
        }
    }

    private func load() async {
        prompt = suggestion
        suggested = true
        async let about = model.imageInfo()
        async let listed = model.images(of: song)
        let (info, images) = await (about, listed)
        self.info = info
        loaded = images
        if let info, !info.models.contains(where: { $0.id == modelId }) {
            modelId = info.models.first?.id ?? ""
        }
    }

    private func paint() {
        sending = true
        message = nil
        Task {
            do {
                let image = try await model.paint(song, prompt: prompt, model: modelId)
                loaded = [image] + loaded.filter { $0.id != image.id }
            } catch {
                failed = true
                message = String(localized: "Das Bild ließ sich nicht starten: \(error.localizedDescription)")
            }
            sending = false
        }
    }

    private func take(_ image: Components.Schemas.ImageState) async {
        taking = image.id
        defer { taking = nil }
        do {
            try await model.takeImage(image)
            failed = false
            message = String(localized: "Das Bild ist jetzt das Cover des Songs.")
        } catch {
            failed = true
            message = error.localizedDescription
        }
    }
}

extension View {
    /// The painting dialog for the song in `song`, while there is one.
    func paintSheet(for song: Binding<Song?>) -> some View {
        sheet(item: song) { song in
            PaintCoverView(song: song)
        }
    }
}
