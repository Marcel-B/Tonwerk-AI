import PhotosUI
import SwiftUI
import TonwerkKit

/// The cover styles page (`CoverStylesPage.vue`): a look under a name for all the songs of a project, with the
/// sentence for the description, the model, LoRAs and up to four reference pictures. "Cover malen" chooses one.
struct CoverStylesView: View {
    @Environment(AppModel.self) private var model
    @State private var styles: [Components.Schemas.CoverStyle] = []
    @State private var models: [Components.Schemas.ImageModelInfo] = []
    @State private var newName = ""
    @State private var problem: String?
    @State private var loaded = false

    var body: some View {
        Form {
            Section {
                Text("Ein Cover-Stil hält einen Look für alle Songs eines Projekts: einen Satz für die Beschreibung, das Modell, LoRAs und bis zu vier Referenzbilder. Unter „Cover malen“ oben bei den Referenzbildern wählen.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                HStack {
                    TextField("Neuer Stil", text: $newName)
                        .onSubmit(create)
                    Button("Anlegen", systemImage: "plus", action: create)
                        .disabled(newName.trimmingCharacters(in: .whitespaces).isEmpty)
                }
                if let problem {
                    Label(problem, systemImage: "exclamationmark.triangle").foregroundStyle(.red)
                } else if loaded && styles.isEmpty {
                    Text("Noch keine Cover-Stile.").foregroundStyle(.secondary)
                }
            }
            ForEach(styles, id: \.id) { style in
                CoverStyleEditor(style: style, models: models) { changed in
                    if let changed {
                        styles = styles.map { $0.id == changed.id ? changed : $0 }
                    } else {
                        styles.removeAll { $0.id == style.id }
                    }
                }
                .id(style.updatedAt)
            }
        }
        .formStyle(.grouped)
        .navigationTitle("Cover-Stile")
        .task { await load() }
        .refreshable { await load() }
    }

    private func load() async {
        do {
            async let list = model.coverStyles()
            async let info = model.imageInfo()
            styles = try await list
            models = await info?.models ?? []
            problem = nil
        } catch {
            problem = error.localizedDescription
        }
        loaded = true
    }

    private func create() {
        let name = newName.trimmingCharacters(in: .whitespaces)
        guard !name.isEmpty else { return }
        Task {
            do {
                styles.append(try await model.createCoverStyle(name))
                styles.sort { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
                newName = ""
                problem = nil
            } catch {
                problem = error.localizedDescription
            }
        }
    }
}

/// One style as a section: its fields saved together, its pictures changed one at a time.
private struct CoverStyleEditor: View {
    @Environment(AppModel.self) private var model
    let style: Components.Schemas.CoverStyle
    let models: [Components.Schemas.ImageModelInfo]
    /// The style as the server now has it; nil once deleted.
    let changed: (Components.Schemas.CoverStyle?) -> Void
    @State private var name: String
    @State private var prompt: String
    @State private var modelId: String
    @State private var loras: [ImageLoras.Choice]
    @State private var photo: PhotosPickerItem?
    @State private var busy = false
    @State private var deleting = false
    @State private var problem: String?

    init(
        style: Components.Schemas.CoverStyle, models: [Components.Schemas.ImageModelInfo],
        changed: @escaping (Components.Schemas.CoverStyle?) -> Void
    ) {
        self.style = style
        self.models = models
        self.changed = changed
        _name = State(initialValue: style.name)
        _prompt = State(initialValue: style.prompt ?? "")
        _modelId = State(initialValue: style.model ?? "")
        _loras = State(initialValue: style.loras.map { ImageLoras.Choice(name: $0.name, strength: $0.strength ?? 1) })
    }

    private var imageModel: Components.Schemas.ImageModelInfo? { models.first { $0.id == modelId } }

    private var edited: Bool {
        name != style.name || prompt != (style.prompt ?? "") || modelId != (style.model ?? "")
            || loras != style.loras.map { ImageLoras.Choice(name: $0.name, strength: $0.strength ?? 1) }
    }

    var body: some View {
        Section {
            TextField("Name", text: $name)
            TextField("Satz für die Beschreibung", text: $prompt, prompt: Text("Am besten auf Englisch"), axis: .vertical)
                .lineLimit(2...5)
            Picker("Modell", selection: $modelId) {
                Text("Beliebiges Modell").tag("")
                ForEach(models, id: \.id) { Text($0.label).tag($0.id) }
            }
            .pickerStyle(.menu)
            if imageModel?.loras == true {
                LoraChips(chosen: $loras, modelId: modelId)
            }
            Button("Speichern", systemImage: "checkmark", action: save)
                .disabled(!edited || busy || name.trimmingCharacters(in: .whitespaces).isEmpty)
            pictures
            if let problem {
                Label(problem, systemImage: "exclamationmark.triangle").foregroundStyle(.red)
            }
        } header: {
            HStack {
                CardHeading("\(style.name)", systemImage: "paintpalette")
                Spacer()
                Button(role: .destructive) {
                    deleting = true
                } label: {
                    Image(systemName: "trash")
                }
                .buttonStyle(.borderless)
                .accessibilityLabel("Stil löschen")
            }
        }
        .font(.callout)
        .onChange(of: photo) {
            guard let photo else { return }
            self.photo = nil
            Task { await add(photo) }
        }
        .confirmationDialog(
            String(localized: "Den Cover-Stil „\(style.name)“ mit seinen Bildern löschen? Schon gemalte Bilder bleiben."),
            isPresented: $deleting,
            titleVisibility: .visible
        ) {
            Button("Löschen", role: .destructive) {
                Task { await run { try await model.deleteCoverStyle(style.id); return nil } }
            }
            Button("Behalten", role: .cancel) {}
        }
    }

    private var pictures: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Bilder (\(style.images.count) von 4)").fontWeight(.medium)
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 76), spacing: 8)], spacing: 8) {
                ForEach(style.images, id: \.id) { image in
                    VStack(spacing: 4) {
                        ZStack(alignment: .topTrailing) {
                            ReferencePicture(url: model.coverStyleImageURL(style.id, image.id), data: nil)
                                .aspectRatio(1, contentMode: .fit)
                                .clipShape(RoundedRectangle(cornerRadius: 6, style: .continuous))
                            Button {
                                Task { await run { try await model.deleteCoverStyleImage(style.id, image: image.id) } }
                            } label: {
                                Image(systemName: "xmark.circle.fill")
                                    .symbolRenderingMode(.palette)
                                    .foregroundStyle(.white, .black.opacity(0.6))
                            }
                            .buttonStyle(.borderless)
                            .padding(3)
                            .accessibilityLabel("Bild entfernen")
                        }
                        Menu(CoverReferences.roleLabel(image.role)) {
                            ForEach(CoverReferences.lookedRoles + ["start"], id: \.self) { role in
                                Button(CoverReferences.roleLabel(role)) {
                                    Task { await run { try await model.setCoverStyleImageRole(style.id, image: image.id, role: role) } }
                                }
                            }
                        }
                        .font(.caption)
                    }
                }
            }
            if style.images.count < 4 {
                PhotosPicker(selection: $photo, matching: .images) {
                    Label("Bild hinzufügen", systemImage: "photo.badge.plus")
                }
                .buttonStyle(.borderless)
                .disabled(busy)
            }
        }
    }

    private func save() {
        Task {
            await run {
                try await model.updateCoverStyle(
                    style.id, name: name.trimmingCharacters(in: .whitespaces), prompt: prompt, model: modelId, loras: loras)
            }
        }
    }

    private func add(_ item: PhotosPickerItem) async {
        await run {
            guard let original = try await item.loadTransferable(type: Data.self),
                  let jpeg = CoverPhoto.scaledJPEG(from: original, maxSide: 1024)
            else { throw ServerError(String(localized: "Das Foto ließ sich nicht lesen.")) }
            return try await model.addCoverStyleImage(style.id, jpeg: jpeg)
        }
    }

    private func run(_ work: () async throws -> Components.Schemas.CoverStyle?) async {
        busy = true
        defer { busy = false }
        do {
            changed(try await work())
            problem = nil
        } catch {
            problem = error.localizedDescription
        }
    }
}

/// The model's LoRAs as chips with a strength each, as the painting dialog offers them, without the library's
/// management (that stays in the dialog).
private struct LoraChips: View {
    @Environment(AppModel.self) private var model
    @Binding var chosen: [ImageLoras.Choice]
    let modelId: String
    @State private var list: Components.Schemas.ImageLoraList?

    var body: some View {
        let fitting = ImageLoras.fitting(list, model: modelId)
        Group {
            if !fitting.isEmpty {
                FlowLayout {
                    ForEach(fitting, id: \.name) { lora in
                        Chip(title: lora.label, chosen: chosen.contains { $0.name == lora.name }) {
                            chosen = ImageLoras.toggle(lora, in: chosen)
                        }
                    }
                }
                ForEach($chosen, id: \.name) { $choice in
                    HStack {
                        Text(fitting.first { $0.name == choice.name }?.label ?? choice.name)
                            .lineLimit(1)
                            .frame(maxWidth: 120, alignment: .leading)
                        Slider(value: $choice.strength, in: 0...2, step: 0.05)
                        Text(choice.strength.formatted(.number.precision(.fractionLength(2))))
                            .monospacedDigit()
                            .frame(width: 40, alignment: .trailing)
                    }
                }
            }
        }
        .task { list = try? await model.imageLoras() }
        .onChange(of: modelId) {
            let kept = ImageLoras.kept(chosen, fitting: ImageLoras.fitting(list, model: modelId))
            if kept != chosen { chosen = kept }
        }
    }
}
