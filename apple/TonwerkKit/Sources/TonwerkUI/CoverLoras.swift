import SwiftUI
import TonwerkKit
import UniformTypeIdentifiers

/// The image LoRAs a picture is painted with, as the web page's `CoverLoras.vue` chooses them: only those made for the
/// chosen model, at most `maxPerPicture`, each at its own strength.
enum ImageLoras {
    /// As the server's `ImageLoraLibrary.MaxPerPicture`.
    static let maxPerPicture = 3

    struct Choice: Equatable {
        let name: String
        var strength: Double
    }

    /// The LoRA in or out; a fourth is not taken.
    static func toggle(_ lora: Components.Schemas.ImageLoraInfo, in chosen: [Choice]) -> [Choice] {
        if chosen.contains(where: { $0.name == lora.name }) {
            return chosen.filter { $0.name != lora.name }
        }
        guard chosen.count < maxPerPicture else { return chosen }
        return chosen + [Choice(name: lora.name, strength: lora.strength)]
    }

    /// What still fits after the model changed or a LoRA went or moved to another model.
    static func kept(_ chosen: [Choice], fitting: [Components.Schemas.ImageLoraInfo]) -> [Choice] {
        chosen.filter { choice in fitting.contains { $0.name == choice.name } }
    }

    static func fitting(_ list: Components.Schemas.ImageLoraList?, model: String) -> [Components.Schemas.ImageLoraInfo] {
        (list?.loras ?? []).filter { $0.model == model }
    }
}

// MARK: Server

extension AppModel {
    func imageLoras() async throws -> Components.Schemas.ImageLoraList {
        let (data, _) = try await call("GET", address("api/image-loras"))
        return try decode(Components.Schemas.ImageLoraList.self, data)
    }

    /// A `.safetensors` of one's own; the server reads the model it fits from its header where it can.
    func uploadImageLora(_ file: PickedFile, model: String?) async throws {
        var fields: [(String, String)] = []
        if let model { fields.append(("model", model)) }
        _ = try await uploadFile(address("api/image-loras"), fields: fields, fileField: "file", file: file, accept: [201])
    }

    /// One of Krea's from Hugging Face; a few hundred MB, so it takes a while.
    func fetchImageLora(_ name: String) async throws {
        _ = try await call("POST", address("api/image-loras/suggestions", name), accept: [201, 202])
    }

    /// Nil leaves a field as it is, an empty text clears it.
    func updateImageLora(_ name: String, model: String? = nil, trigger: String? = nil) async throws {
        var body: [String: Any] = [:]
        if let model { body["model"] = model }
        if let trigger { body["trigger"] = trigger }
        _ = try await call("PUT", address("api/image-loras", name), json: body)
    }

    func deleteImageLora(_ name: String) async throws {
        _ = try await call("DELETE", address("api/image-loras", name), accept: [204, 404])
    }
}

// MARK: The section in the painting dialog

struct CoverLorasSection: View {
    @Environment(AppModel.self) private var model
    @Binding var chosen: [ImageLoras.Choice]
    let imageModel: Components.Schemas.ImageModelInfo?
    let models: [Components.Schemas.ImageModelInfo]
    @AppStorage("coverLorasManage") private var managing = false
    @State private var list: Components.Schemas.ImageLoraList?
    @State private var busy: String?
    @State private var importing = false
    @State private var problem: String?

    private var takesLoras: Bool { imageModel?.loras == true }
    private var fitting: [Components.Schemas.ImageLoraInfo] { ImageLoras.fitting(list, model: imageModel?.id ?? "") }
    private var unassigned: Int { (list?.loras ?? []).filter { $0.model == nil }.count }

    var body: some View {
        Section {
            if let imageModel, !takesLoras {
                Text("\(imageModel.label) nimmt keine LoRAs.")
                    .foregroundStyle(.secondary)
            } else if list != nil {
                if fitting.isEmpty {
                    Text("Noch keine LoRAs für \(imageModel?.label ?? "dieses Modell"). Unter „Verwalten“ hochladen oder Kreas eigene laden.")
                        .foregroundStyle(.secondary)
                } else {
                    FlowLayout {
                        ForEach(fitting, id: \.name) { lora in
                            let isChosen = chosen.contains { $0.name == lora.name }
                            Chip(title: lora.label, chosen: isChosen) {
                                chosen = ImageLoras.toggle(lora, in: chosen)
                            }
                            .disabled(!isChosen && chosen.count >= ImageLoras.maxPerPicture)
                            .help(lora.trigger.map { "Auslöser: \($0)" } ?? "")
                        }
                    }
                }
                ForEach($chosen, id: \.name) { $choice in
                    HStack {
                        Text(label(of: choice.name))
                            .lineLimit(1)
                            .frame(maxWidth: 120, alignment: .leading)
                        Slider(value: $choice.strength, in: 0...2, step: 0.05)
                            .accessibilityLabel("Stärke von \(label(of: choice.name))")
                        Text(choice.strength.formatted(.number.precision(.fractionLength(2))))
                            .monospacedDigit()
                            .frame(width: 40, alignment: .trailing)
                    }
                }
                if unassigned > 0 && !managing {
                    Text("\(unassigned) LoRA(s) ohne bekanntes Modell, unter „Verwalten“ zuordnen.")
                        .foregroundStyle(.secondary)
                }
            }
            if managing, let list {
                manage(list)
            }
            if let problem {
                Label(problem, systemImage: "exclamationmark.triangle")
                    .foregroundStyle(.red)
            }
        } header: {
            HStack {
                CardHeading("LoRAs", systemImage: "wand.and.stars")
                Spacer()
                Button(managing ? "Fertig" : "Verwalten", systemImage: managing ? "chevron.up" : "gearshape") {
                    managing.toggle()
                }
                .font(.caption)
                .buttonStyle(.borderless)
            }
        }
        .font(.callout)
        .task { await load() }
        .onChange(of: imageModel?.id) { prune() }
        .fileImporter(
            isPresented: $importing,
            allowedContentTypes: [UTType(filenameExtension: "safetensors") ?? .data, .data]
        ) { result in
            guard case .success(let url) = result else { return }
            Task { await upload(url) }
        }
    }

    @ViewBuilder
    private func manage(_ list: Components.Schemas.ImageLoraList) -> some View {
        Text("Ordner auf dem Mac: \(list.folder). Dort abgelegte Dateien erscheinen hier.")
            .font(.caption)
            .foregroundStyle(.secondary)
            .textSelection(.enabled)
        ForEach(list.loras, id: \.name) { lora in
            VStack(alignment: .leading, spacing: 6) {
                HStack {
                    Text(lora.label).fontWeight(.medium).lineLimit(1)
                    Spacer()
                    Text(ByteCountFormatter.string(fromByteCount: lora.sizeBytes, countStyle: .file))
                        .font(.caption)
                        .foregroundStyle(.secondary)
                    Button(role: .destructive) {
                        Task { await run(lora.name) { try await model.deleteImageLora(lora.name) } }
                    } label: {
                        Image(systemName: "trash")
                    }
                    .buttonStyle(.borderless)
                    .accessibilityLabel("\(lora.label) löschen")
                }
                Picker("Modell", selection: Binding(
                    get: { lora.model ?? "" },
                    set: { value in Task { await run(lora.name) { try await model.updateImageLora(lora.name, model: value) } } }
                )) {
                    Text("Modell unbekannt").tag("")
                    ForEach(models.filter { $0.loras == true }, id: \.id) { m in
                        Text(m.label).tag(m.id)
                    }
                }
                .pickerStyle(.menu)
                TriggerField(trigger: lora.trigger ?? "") { text in
                    Task { await run(lora.name) { try await model.updateImageLora(lora.name, trigger: text) } }
                }
            }
            .padding(.vertical, 2)
        }
        Button {
            importing = true
        } label: {
            HStack(spacing: 6) {
                if busy == "upload" { ProgressView().controlSize(.small) }
                Label(".safetensors hochladen", systemImage: "square.and.arrow.up")
            }
        }
        .disabled(busy != nil)
        if !list.suggestions.isEmpty {
            VStack(alignment: .leading, spacing: 4) {
                Text("Von Krea").fontWeight(.medium)
                Text("Stil-LoRAs für Krea 2 Turbo, von Hugging Face geladen (je einige 100 MB).")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            ForEach(list.suggestions, id: \.name) { suggestion in
                HStack {
                    VStack(alignment: .leading) {
                        Text(suggestion.label).lineLimit(1)
                        Text(suggestion.trigger).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                    }
                    Spacer()
                    if busy == suggestion.name || list.fetching.contains(suggestion.name) {
                        ProgressView().controlSize(.small)
                    } else {
                        Button("Laden", systemImage: "arrow.down.circle") {
                            Task { await run(suggestion.name) { try await model.fetchImageLora(suggestion.name) } }
                        }
                        .buttonStyle(.borderless)
                        .disabled(busy != nil)
                    }
                }
            }
        }
    }

    private func label(of name: String) -> String {
        list?.loras.first { $0.name == name }?.label ?? name
    }

    private func prune() {
        let kept = takesLoras ? ImageLoras.kept(chosen, fitting: fitting) : []
        if kept != chosen { chosen = kept }
    }

    private func load() async {
        do {
            list = try await model.imageLoras()
            problem = nil
        } catch {
            problem = error.localizedDescription
        }
        prune()
    }

    /// One change to the library, then the list read again.
    private func run(_ name: String, _ change: () async throws -> Void) async {
        busy = name
        defer { busy = nil }
        do {
            try await change()
        } catch {
            problem = error.localizedDescription
            return
        }
        await load()
    }

    private func upload(_ url: URL) async {
        await run("upload") {
            let file = try await PickedFile.copy(from: url)
            try await model.uploadImageLora(file, model: takesLoras ? imageModel?.id : nil)
        }
    }
}

/// The trigger words, sent when the field is left or submitted rather than on every key.
private struct TriggerField: View {
    @State private var text: String
    let trigger: String
    let save: (String) -> Void
    @FocusState private var focused: Bool

    init(trigger: String, save: @escaping (String) -> Void) {
        _text = State(initialValue: trigger)
        self.trigger = trigger
        self.save = save
    }

    var body: some View {
        TextField("Auslöser", text: $text, prompt: Text("Auslöser (ans Ende der Beschreibung)"))
            .focused($focused)
            .onSubmit(send)
            .onChange(of: focused) { if !focused { send() } }
    }

    private func send() {
        if text != trigger { save(text) }
    }
}
