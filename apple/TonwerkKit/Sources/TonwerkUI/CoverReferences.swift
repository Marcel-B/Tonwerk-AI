import PhotosUI
import SwiftUI
import TonwerkKit

/// Reference pictures for a painted cover and the cover styles that keep them, as the web page's `coverStyles.ts`
/// decides: what the chosen model takes of them, and a style's text at the description's end.
enum CoverReferences {
    /// The roles one chooses; `start` is given by the model (Z-Image and Krea 2 paint over the first picture).
    static let lookedRoles = ["style", "figure", "place"]

    /// The start picture's strength when nothing else is said: half of its layout stays.
    static let defaultStartStrength = 0.5

    /// A reference in the dialog: what is sent, and what shows it.
    struct Item: Equatable {
        var source: String
        var id: String?
        var role: String
        var preview: URL?
        var label: String
        /// The picture itself, for an upload, which has no address on the server to show it from.
        var data: Data?

        /// Unique in the dialog, so an upload and a style picture never share a row.
        var key: String { "\(source):\(id ?? "")" }
    }

    static func roleLabel(_ role: String) -> String {
        switch role {
        case "figure": String(localized: "Figur")
        case "place": String(localized: "Ort")
        case "start": String(localized: "Ausgangsbild")
        default: String(localized: "Stil")
        }
    }

    /// What the model takes of the references: the first ones it can look at, or the first as a start picture, or
    /// none. The others stay in the dialog, so switching back to Klein finds them again.
    static func fit(
        _ items: [Item], model: Components.Schemas.ImageModelInfo?, strength: Double = defaultStartStrength
    ) -> [Components.Schemas.ImageReferenceChoice] {
        let looked = Int(model?.references ?? 0)
        if looked > 0 {
            return items.prefix(looked).map {
                .init(source: $0.source, id: $0.id, role: $0.role == "start" ? "style" : $0.role, strength: nil)
            }
        }
        if model?.startImage == true, let first = items.first {
            return [.init(source: first.source, id: first.id, role: "start", strength: strength)]
        }
        return []
    }

    /// A style's pictures as references.
    static func items(of style: Components.Schemas.CoverStyle, url: (Int64, String) -> URL?) -> [Item] {
        style.images.map {
            Item(source: "style", id: "\(style.id)/\($0.id)", role: $0.role, preview: url(style.id, $0.id), label: style.name)
        }
    }

    /// The description with one style's text swapped for another's: the old text is taken out where it still
    /// stands, the new one added at the end, so choosing a style twice never doubles it.
    static func withStyleText(_ prompt: String, previous: String?, next: String?) -> String {
        var text = prompt
        if let previous = previous?.trimmingCharacters(in: .whitespacesAndNewlines), !previous.isEmpty,
           let range = text.range(of: previous, options: .backwards)
        {
            text.removeSubrange(range)
            while text.contains("  ") { text = text.replacingOccurrences(of: "  ", with: " ") }
        }
        text = text.trimmingCharacters(in: .whitespacesAndNewlines)
        if let next = next?.trimmingCharacters(in: .whitespacesAndNewlines), !next.isEmpty, !text.contains(next) {
            text = text.isEmpty ? next : "\(text) \(next)"
        }
        return text
    }
}

// MARK: Server

extension AppModel {
    func coverStyles() async throws -> [Components.Schemas.CoverStyle] {
        let (data, _) = try await call("GET", address("api/cover-styles"))
        return try decode([Components.Schemas.CoverStyle].self, data)
    }

    func createCoverStyle(_ name: String) async throws -> Components.Schemas.CoverStyle {
        let (data, _) = try await call("POST", address("api/cover-styles"), json: ["name": name], accept: [201])
        return try decode(Components.Schemas.CoverStyle.self, data)
    }

    /// Nil leaves a field as it is; an empty text clears prompt or model.
    func updateCoverStyle(
        _ id: Int64, name: String? = nil, prompt: String? = nil, model: String? = nil, loras: [ImageLoras.Choice]? = nil
    ) async throws -> Components.Schemas.CoverStyle {
        var body: [String: Any] = [:]
        if let name { body["name"] = name }
        if let prompt { body["prompt"] = prompt }
        if let model { body["model"] = model }
        if let loras { body["loras"] = loras.map { ["name": $0.name, "strength": $0.strength] } }
        let (data, _) = try await call("PUT", address("api/cover-styles", String(id)), json: body)
        return try decode(Components.Schemas.CoverStyle.self, data)
    }

    func deleteCoverStyle(_ id: Int64) async throws {
        _ = try await call("DELETE", address("api/cover-styles", String(id)), accept: [204, 404])
    }

    /// A picture of one's own (`jpeg`) or a painted candidate (`candidate`) kept in the style.
    func addCoverStyleImage(_ id: Int64, jpeg: Data? = nil, candidate: String? = nil, role: String = "style")
        async throws -> Components.Schemas.CoverStyle
    {
        var fields = [("role", role)]
        if let candidate { fields.append(("candidate", candidate)) }
        let data = try await postForm(
            address("api/cover-styles", String(id), "images"), fields: fields, picture: jpeg, accept: [201])
        return try decode(Components.Schemas.CoverStyle.self, data)
    }

    func setCoverStyleImageRole(_ id: Int64, image: String, role: String) async throws -> Components.Schemas.CoverStyle {
        let (data, _) = try await call("PUT", address("api/cover-styles", String(id), "images", image), json: ["role": role])
        return try decode(Components.Schemas.CoverStyle.self, data)
    }

    func deleteCoverStyleImage(_ id: Int64, image: String) async throws -> Components.Schemas.CoverStyle {
        let (data, _) = try await call("DELETE", address("api/cover-styles", String(id), "images", image))
        return try decode(Components.Schemas.CoverStyle.self, data)
    }

    func coverStyleImageURL(_ id: Int64, _ image: String) -> URL? {
        try? address("api/cover-styles", String(id), "images", image)
    }

    /// A picture for one cover only; its id goes into the painting's references as source `upload`.
    func uploadImageReference(_ jpeg: Data) async throws -> String {
        let data = try await postForm(address("api/image-references"), fields: [], picture: jpeg, accept: [201])
        return try decode(Components.Schemas.ImageReferenceUpload.self, data).id
    }

    /// A small multipart form, held in memory: a reference picture is about a megapixel.
    private func postForm(_ url: URL, fields: [(String, String)], picture: Data?, accept: Set<Int>) async throws -> Data {
        let boundary = "tonwerk-\(UUID().uuidString)"
        var body = Data()
        for (name, value) in fields {
            body.append(Data("--\(boundary)\r\nContent-Disposition: form-data; name=\"\(name)\"\r\n\r\n\(value)\r\n".utf8))
        }
        if let picture {
            body.append(Data(
                "--\(boundary)\r\nContent-Disposition: form-data; name=\"file\"; filename=\"reference.jpg\"\r\nContent-Type: image/jpeg\r\n\r\n".utf8))
            body.append(picture)
            body.append(Data("\r\n".utf8))
        }
        body.append(Data("--\(boundary)--\r\n".utf8))
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.timeoutInterval = 120
        request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")
        let (data, response) = try await URLSession.shared.upload(for: request, from: body)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        guard accept.contains(status) else {
            throw ServerError(problem: ServerError.text(of: data) ?? String(localized: "Der Server antwortete \(status)."))
        }
        return data
    }
}

// MARK: The section in the painting dialog

struct CoverReferencesSection: View {
    @Environment(AppModel.self) private var model
    @Binding var items: [CoverReferences.Item]
    @Binding var strength: Double
    @Binding var style: Components.Schemas.CoverStyle?
    let imageModel: Components.Schemas.ImageModelInfo?
    /// The song's own cover, when it has one.
    let cover: URL?
    /// A style chosen or none: the dialog puts its text, model and LoRAs in place.
    let choose: (Components.Schemas.CoverStyle?, Components.Schemas.CoverStyle?) -> Void
    @Binding var styles: [Components.Schemas.CoverStyle]
    @State private var photo: PhotosPickerItem?
    @State private var uploading = false
    @State private var problem: String?

    private var looked: Int { Int(imageModel?.references ?? 0) }
    private var sent: Int { CoverReferences.fit(items, model: imageModel, strength: strength).count }

    var body: some View {
        Section {
            if !styles.isEmpty {
                Picker("Cover-Stil", selection: Binding(
                    get: { style?.id ?? -1 },
                    set: { id in
                        let previous = style
                        style = styles.first { $0.id == id }
                        choose(previous, style)
                    }
                )) {
                    Text("Kein Stil").tag(Int64(-1))
                    ForEach(styles, id: \.id) { Text($0.name).tag($0.id) }
                }
                .pickerStyle(.menu)
            }
            if let imageModel {
                Text(hint(imageModel)).foregroundStyle(.secondary)
            }
            if !items.isEmpty {
                LazyVGrid(columns: [GridItem(.adaptive(minimum: 76), spacing: 8)], spacing: 8) {
                    ForEach(Array(items.enumerated()), id: \.element.key) { index, item in
                        thumbnail(item, index: index)
                            .opacity(index < sent ? 1 : 0.45)
                    }
                }
                .padding(.vertical, 4)
                if sent > 0 && items.count > sent, let imageModel {
                    Text("Die übrigen Bilder nimmt \(imageModel.label) nicht.").foregroundStyle(.secondary)
                }
                if imageModel?.startImage == true {
                    HStack {
                        Text("Stärke")
                        Slider(value: $strength, in: 0.1...0.9, step: 0.05)
                            .accessibilityLabel("Stärke des Ausgangsbilds")
                        Text(strength.formatted(.number.precision(.fractionLength(2))))
                            .monospacedDigit()
                            .frame(width: 40, alignment: .trailing)
                    }
                }
            }
            HStack {
                PhotosPicker(selection: $photo, matching: .images) {
                    HStack(spacing: 6) {
                        if uploading { ProgressView().controlSize(.small) }
                        Label("Foto", systemImage: "photo")
                    }
                }
                .disabled(uploading)
                if let cover {
                    Button("Aktuelles Cover", systemImage: "square.on.square") {
                        items.append(.init(source: "cover", id: nil, role: "style", preview: cover, label: String(localized: "Cover")))
                    }
                    .disabled(items.contains { $0.source == "cover" })
                }
            }
            .buttonStyle(.borderless)
            if let problem {
                Label(problem, systemImage: "exclamationmark.triangle").foregroundStyle(.red)
            }
        } header: {
            CardHeading("Referenzbilder", systemImage: "photo.on.rectangle.angled")
        }
        .font(.callout)
        .onChange(of: photo) {
            guard let photo else { return }
            self.photo = nil
            Task { await upload(photo) }
        }
    }

    private func hint(_ imageModel: Components.Schemas.ImageModelInfo) -> String {
        if looked > 0 {
            return String(localized: "\(imageModel.label) schaut sich bis zu \(looked) Bilder an; die Beschreibung bekommt dazu je einen Satz.")
        }
        if imageModel.startImage == true {
            return String(localized: "\(imageModel.label) malt über das erste Bild (Ausgangsbild) und behält dessen Aufbau nach Stärke.")
        }
        return String(localized: "\(imageModel.label) nimmt keine Bilder. FLUX.2 Klein oder FLUX 3 schauen sie sich an, Z-Image und Krea 2 malen darüber.")
    }

    private func thumbnail(_ item: CoverReferences.Item, index: Int) -> some View {
        VStack(spacing: 4) {
            ZStack(alignment: .topTrailing) {
                ReferencePicture(url: item.preview, data: item.data)
                    .aspectRatio(1, contentMode: .fit)
                    .clipShape(RoundedRectangle(cornerRadius: 6, style: .continuous))
                Button {
                    items.remove(at: index)
                } label: {
                    Image(systemName: "xmark.circle.fill")
                        .symbolRenderingMode(.palette)
                        .foregroundStyle(.white, .black.opacity(0.6))
                }
                .buttonStyle(.borderless)
                .padding(3)
                .accessibilityLabel("Bild entfernen")
            }
            if looked > 0 {
                Menu(CoverReferences.roleLabel(item.role == "start" ? "style" : item.role)) {
                    ForEach(CoverReferences.lookedRoles, id: \.self) { role in
                        Button(CoverReferences.roleLabel(role)) { items[index].role = role }
                    }
                }
                .font(.caption)
            } else if index == 0 && imageModel?.startImage == true {
                Text("Ausgangsbild").font(.caption)
            }
        }
    }

    private func upload(_ photo: PhotosPickerItem) async {
        uploading = true
        defer { uploading = false }
        do {
            guard let original = try await photo.loadTransferable(type: Data.self),
                  let jpeg = CoverPhoto.scaledJPEG(from: original, maxSide: 1024)
            else { throw ServerError(String(localized: "Das Foto ließ sich nicht lesen.")) }
            let id = try await model.uploadImageReference(jpeg)
            items.append(.init(source: "upload", id: id, role: "style", preview: nil, label: String(localized: "Foto"), data: jpeg))
            problem = nil
        } catch {
            problem = error.localizedDescription
        }
    }
}

/// A reference picture from the server, or one held in memory (an upload).
struct ReferencePicture: View {
    let url: URL?
    let data: Data?

    var body: some View {
        ZStack {
            Rectangle().fill(.quaternary)
            if let data, let image = CoverPhoto.image(data) {
                Image(decorative: image, scale: 1).resizable().scaledToFill()
            } else if let url {
                AsyncImage(url: url) { picture in
                    picture.resizable().scaledToFill()
                } placeholder: {
                    ProgressView()
                }
            }
        }
    }
}
