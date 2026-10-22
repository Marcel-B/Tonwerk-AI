import SwiftUI
import TonwerkKit

/// A picture seen large (`ImageViewer.vue`): a painted candidate or a song's cover, to share or save, with the text it was
/// painted with to read and copy.
struct ViewedImage: Identifiable {
    let id = UUID()
    let url: URL
    /// The name a saved file gets.
    let fileName: String
    let title: String
    /// How it was painted; nil for a photo, or while a cover's is read.
    var painting: Painting?
    /// A song's cover: its painting is read from the server (`GET .../cover/painting`) when it opens.
    var coverOf: Song?

    /// What the viewer shows of how a picture was painted, from a candidate or a cover's painting.
    struct Painting {
        var prompt: String
        var paintedPrompt: String?
        var sentPrompt: String?
        /// Model, LoRAs with their strengths, references and seed in one line.
        var details: String

        init(_ image: Components.Schemas.ImageState) {
            prompt = image.prompt
            paintedPrompt = image.paintedPrompt
            sentPrompt = image.sentPrompt
            details = Self.details(
                model: image.modelLabel, loras: image.loras ?? [], references: image.references ?? [], seed: image.seed)
        }

        init(_ painting: Components.Schemas.CoverPainting) {
            prompt = painting.prompt
            paintedPrompt = painting.paintedPrompt
            sentPrompt = painting.sentPrompt
            details = Self.details(
                model: painting.modelLabel, loras: painting.loras ?? [], references: painting.references ?? [],
                seed: painting.seed)
        }

        /// The texts worth copying, each once: a translation equal to the description, or a sent text equal to what came
        /// before it, is left out.
        var texts: [Shown] {
            let english = paintedPrompt.flatMap { $0 != prompt ? $0 : nil }
            let sent = sentPrompt.flatMap { $0 != (english ?? prompt) ? $0 : nil }
            var texts = [Shown(id: "prompt", label: String(localized: "Beschreibung"), text: prompt)]
            if let english {
                texts.append(Shown(id: "english", label: String(localized: "Was das Modell gelesen hat"), text: english))
            }
            if let sent {
                texts.append(Shown(id: "sent", label: String(localized: "Vollständig an das Modell geschickt"), text: sent))
            }
            return texts
        }

        struct Shown: Identifiable {
            let id: String
            let label: String
            let text: String
        }

        private static func details(
            model: String, loras: [Components.Schemas.ImageLoraUsed], references: [Components.Schemas.ImageReferenceUsed],
            seed: Int64?
        ) -> String {
            var parts = [model]
            for lora in loras {
                parts.append("\(lora.label) \(lora.strength.formatted(.number.precision(.fractionLength(2))))")
            }
            for reference in references {
                parts.append("\(CoverReferences.roleLabel(reference.role)): \(reference.label)")
            }
            parts.append("Seed \(seed.map(String.init) ?? "–")")
            return parts.joined(separator: " · ")
        }
    }
}

struct ImageViewerView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    let image: ViewedImage
    @State private var painting: ViewedImage.Painting?
    @State private var reading = false
    @State private var file: URL?
    @State private var saving = false
    @State private var copied: String?
    @State private var problem: String?

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(alignment: .leading, spacing: 14) {
                    AsyncImage(url: image.url) { picture in
                        picture.resizable().scaledToFit()
                    } placeholder: {
                        ProgressView().frame(maxWidth: .infinity, minHeight: 240)
                    }
                    .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
                    .frame(maxWidth: .infinity)
                    if reading {
                        ProgressView()
                    } else if let painting {
                        Text(painting.details)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                        ForEach(painting.texts) { item in
                            VStack(alignment: .leading, spacing: 4) {
                                HStack {
                                    Text(item.label).font(.callout.weight(.medium))
                                    Spacer()
                                    Button {
                                        copied = Clipboard.copy(item.text) ? item.id : nil
                                    } label: {
                                        Label(
                                            copied == item.id ? "Kopiert" : "Kopieren",
                                            systemImage: copied == item.id ? "checkmark" : "doc.on.doc")
                                    }
                                    .buttonStyle(.borderless)
                                    .font(.callout)
                                }
                                Text(item.text)
                                    .font(.callout)
                                    .textSelection(.enabled)
                                    .frame(maxWidth: .infinity, alignment: .leading)
                                    .padding(10)
                                    .background(.quaternary, in: RoundedRectangle(cornerRadius: 8, style: .continuous))
                            }
                        }
                    } else if image.coverOf != nil {
                        Text("Zu diesem Cover ist kein Text gespeichert: ein Foto, oder vor dieser Version übernommen.")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    if let problem {
                        Label(problem, systemImage: "exclamationmark.triangle")
                            .font(.caption)
                            .foregroundStyle(.red)
                    }
                }
                .padding()
            }
            .navigationTitle(image.title)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Fertig") { dismiss() }
                }
                ToolbarItemGroup(placement: .primaryAction) {
                    if let file {
                        ShareLink(item: file) {
                            Label("Teilen", systemImage: "square.and.arrow.up")
                        }
                        Button {
                            saving = true
                        } label: {
                            Label("Sichern …", systemImage: "square.and.arrow.down")
                        }
                    } else {
                        ProgressView().controlSize(.small)
                    }
                }
            }
            .fileMover(isPresented: $saving, file: file) { _ in }
            .task { await load() }
        }
        #if os(macOS)
        .frame(minWidth: 520, idealWidth: 720, minHeight: 600, idealHeight: 820)
        #endif
    }

    private func load() async {
        painting = image.painting
        async let download = model.downloadPicture(image.url, name: image.fileName)
        if let song = image.coverOf {
            reading = true
            do {
                painting = try await model.coverPainting(song).map(ViewedImage.Painting.init)
            } catch {
                problem = error.localizedDescription
            }
            reading = false
        }
        do {
            file = try await download
        } catch {
            problem = error.localizedDescription
        }
    }
}

extension AppModel {
    /// How the song's cover was painted, or nil for a photo or one taken before this was kept.
    func coverPainting(_ song: Song) async throws -> Components.Schemas.CoverPainting? {
        let (data, response) = try await call(
            "GET", address("api/songs", song.run, song.name, "cover", "painting"), accept: [200, 404])
        return response.statusCode == 404 ? nil : try decode(Components.Schemas.CoverPainting.self, data)
    }

    /// A picture in the temporary folder, to share or save under its name.
    func downloadPicture(_ url: URL, name: String) async throws -> URL {
        let (data, _) = try await call("GET", url)
        let folder = FileManager.default.temporaryDirectory
            .appending(path: "Tonwerk-Bilder", directoryHint: .isDirectory)
            .appending(path: UUID().uuidString, directoryHint: .isDirectory)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let file = folder.appending(path: name)
        try data.write(to: file)
        return file
    }

    /// A file name from a title, as `imageFileName` in `imageViewer.ts`.
    nonisolated static func pictureName(_ title: String, _ suffix: String) -> String {
        "\(projectName(title))\(suffix.isEmpty ? "" : " \(suffix)").jpg"
    }
}

extension View {
    /// The image viewer for the picture in `image`, while there is one.
    func imageViewer(for image: Binding<ViewedImage?>) -> some View {
        sheet(item: image) { image in
            ImageViewerView(image: image)
        }
    }
}
