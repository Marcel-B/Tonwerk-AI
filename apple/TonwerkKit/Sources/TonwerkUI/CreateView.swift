import PhotosUI
import SwiftUI
import TonwerkKit

/// The create page: style, lyrics (typed or drafted from keywords), and how many songs in which quality.
struct CreateView: View {
    @Environment(AppModel.self) private var model
    @State private var models: Components.Schemas.LyricsModels?
    @State private var sending = false
    @State private var sent: String?
    @State private var error: String?
    @State private var replacing = false
    /// The photo a draft tells of, scaled down; kept with the page, not the stored form.
    @State private var photo: Data?
    @State private var photoItem: PhotosPickerItem?
    @State private var pickingPhoto = false
    @State private var importingPhoto = false
    @State private var photoProblem: String?

    var body: some View {
        @Bindable var model = model
        Form {
            Section {
                TemplatePicker()
            }

            Section {
                TextField("Titel (optional)", text: $model.form.title)
                VStack(alignment: .leading, spacing: 4) {
                    Text("Stil")
                    TextField("Stil", text: $model.form.style, prompt: Text("German, pop, female vocal, warm piano, 90 bpm"), axis: .vertical)
                        .labelsHidden()
                        .lineLimit(2...6)
                }
                StyleBlocksView(style: $model.form.style, instrumental: model.form.instrumental)
            } header: {
                CardHeading("Song", systemImage: "music.note")
            }

            Section {
                Toggle("Instrumental", isOn: $model.form.instrumental)
                TextEditor(text: $model.form.lyrics)
                    .font(.body.monospaced())
                    .frame(minHeight: model.form.instrumental ? 60 : 220)
                    .scrollContentBackground(.hidden)
                SectionTagsView(lyrics: $model.form.lyrics)
                if !model.form.instrumental {
                    draft
                }
            } header: {
                CardHeading("Songtext", systemImage: "text.quote")
            }

            ScoreSection()

            Section {
                Stepper(value: $model.form.batch, in: 1...10) {
                    LabeledContent("Anzahl", value: "\(model.form.batch)")
                }
                Picker("Qualität", selection: $model.form.full) {
                    Text("Entwurf").tag(false)
                    Text("Voll").tag(true)
                }
                .pickerStyle(.segmented)
                TextField("Seed", text: $model.form.seed, prompt: Text("leer = zufällig"))
                    #if os(iOS)
                    .keyboardType(.numberPad)
                    #endif
            } header: {
                CardHeading("Erzeugen", systemImage: "wand.and.stars")
            }

            AdvancedSection()
        }
        .formStyle(.grouped)
        .safeAreaInset(edge: .bottom, spacing: 0) { generateBar }
        .navigationTitle("Erstellen")
        .toolbar {
            ToolbarItem {
                Button(role: .destructive) {
                    let keep = model.form
                    model.form = CreateForm()
                    model.form.german = keep.german
                    model.form.lyricsModel = keep.lyricsModel
                    model.form.lyricsMachine = keep.lyricsMachine
                    model.form.lyricsReasoning = keep.lyricsReasoning
                } label: {
                    Label("Formular leeren", systemImage: "trash")
                }
            }
        }
        .onChange(of: model.form) { model.saveForm() }
        .onChange(of: draftState?.finished) { take(draftState) }
        .task {
            await model.loadMachines()
            models = await model.lyricsModels(on: model.form.lyricsMachine)
        }
        .onChange(of: model.form.lyricsMachine) {
            Task { models = await model.lyricsModels(on: model.form.lyricsMachine) }
        }
        .photosPicker(isPresented: $pickingPhoto, selection: $photoItem, matching: .images)
        .onChange(of: photoItem) {
            guard let item = photoItem else { return }
            photoItem = nil
            Task { await usePhoto { try await item.loadTransferable(type: Data.self) } }
        }
        .fileImporter(isPresented: $importingPhoto, allowedContentTypes: [.image]) { result in
            guard case .success(let url) = result else { return }
            Task {
                await usePhoto {
                    let scoped = url.startAccessingSecurityScopedResource()
                    defer { if scoped { url.stopAccessingSecurityScopedResource() } }
                    return try Data(contentsOf: url)
                }
            }
        }
        .confirmationDialog("Den Songtext durch einen neuen Entwurf ersetzen?", isPresented: $replacing) {
            Button("Ersetzen", role: .destructive) { startDraft() }
        }
    }

    // MARK: Generate

    /// The button under the form, always in reach, with what came of the last request above it.
    private var generateBar: some View {
        VStack(spacing: 8) {
            if let problem = model.form.problem {
                Text(problem)
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }
            if let sent {
                HStack {
                    Label(sent, systemImage: "checkmark.circle.fill")
                        .foregroundStyle(Theme.accent)
                    Spacer()
                    Button("Zur Warteschlange") { model.page = .queue }
                        .buttonStyle(.borderless)
                }
                .font(.callout)
            }
            if let error {
                Label(error, systemImage: "exclamationmark.triangle")
                    .font(.callout)
                    .foregroundStyle(.red)
            }
            Button(action: generate) {
                HStack(spacing: 8) {
                    if sending {
                        ProgressView().controlSize(.small).tint(.white)
                    } else {
                        Image(systemName: "waveform")
                    }
                    Text(model.form.batch == 1 ? "Song erzeugen" : "\(model.form.batch) Songs erzeugen")
                }
                .font(.headline)
                .frame(maxWidth: .infinity)
            }
            .buttonStyle(GenerateButtonStyle())
            .disabled(sending || model.form.problem != nil)
            .keyboardShortcut(.return, modifiers: .command)
        }
        .frame(maxWidth: 640)
        .padding(.horizontal, 16)
        .padding(.vertical, 12)
        .frame(maxWidth: .infinity)
        .background(.bar)
        .overlay(alignment: .top) { Divider() }
    }

    // MARK: Lyrics draft

    private var draftState: Components.Schemas.LyricsState? {
        model.form.draftId.isEmpty ? nil : model.drafts[model.form.draftId]
    }

    private var drafting: Bool {
        !model.form.draftId.isEmpty && draftState?.finished != true
    }

    @ViewBuilder
    private var draft: some View {
        @Bindable var model = model
        TextField("Stichworte für einen Entwurf", text: $model.form.idea, axis: .vertical)
            .lineLimit(1...4)
        photoRow
        Picker("Sprache", selection: $model.form.german) {
            Text("Deutsch").tag(true)
            Text("Englisch").tag(false)
        }
        MachinePicker(selection: $model.form.lyricsMachine, engine: "lyrics")
        if let models {
            Picker("Modell", selection: $model.form.lyricsModel) {
                Text("Standard (\(models.models.first { $0.id == models._default }?.name ?? models._default))").tag("")
                ForEach(models.models, id: \.id) { choice in
                    Text(Self.label(choice)).tag(choice.id)
                }
            }
        }
        if !cloudModel {
            Picker("Nachdenken", selection: $model.form.lyricsReasoning) {
                Text("Wie in LM Studio").tag("")
                Text("Aus").tag(Components.Schemas.LyricsReasoning.off.rawValue)
                Text("Wenig").tag(Components.Schemas.LyricsReasoning.low.rawValue)
                Text("Mittel").tag(Components.Schemas.LyricsReasoning.medium.rawValue)
                Text("Viel").tag(Components.Schemas.LyricsReasoning.high.rawValue)
                Text("An").tag(Components.Schemas.LyricsReasoning.on.rawValue)
            }
        }
        HStack {
            Spacer()
            if drafting {
                ProgressView().controlSize(.small)
                Text([draftStage, model.ranOnNote(draftState?.ranOn)].compactMap { $0 }.joined(separator: " · "))
                    .foregroundStyle(.secondary)
                Button("Nicht mehr warten") { model.form.draftId = "" }
                    .buttonStyle(.borderless)
            } else {
                Button("Text entwerfen") {
                    if model.form.lyrics.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                        startDraft()
                    } else {
                        replacing = true
                    }
                }
                .disabled(model.form.idea.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && photo == nil)
            }
        }
        if let message = draftState?.message, draftState?.finished == true, draftState?.lyrics == nil {
            Label(message, systemImage: "exclamationmark.triangle").foregroundStyle(.red)
        }
    }

    private static func label(_ choice: Components.Schemas.LyricsModel) -> String {
        var name = choice.cloud == true ? "\(choice.name) ☁︎" : choice.name
        if let note = choice.memoryNote ?? choice.roleNote {
            name += " · \(note)"
        }
        if choice.vision == true {
            name += String(localized: " · sieht Fotos")
        }
        return name
    }

    /// Claude has its own effort on the server, so the thinking picker is LM Studio's only.
    private var cloudModel: Bool {
        guard let models else { return false }
        let id = model.form.lyricsModel.isEmpty ? models._default : model.form.lyricsModel
        return models.models.first { $0.id == id }?.cloud == true
    }

    /// The chosen model, or the default, cannot see: LM Studio says so through its capabilities.
    private var blindModel: Bool {
        guard let models else { return false }
        let id = model.form.lyricsModel.isEmpty ? models._default : model.form.lyricsModel
        return models.models.first { $0.id == id }?.vision == false
    }

    @ViewBuilder
    private var photoRow: some View {
        if let photo, let image = CoverPhoto.image(photo) {
            HStack(alignment: .top, spacing: 10) {
                Image(decorative: image, scale: 1)
                    .resizable()
                    .scaledToFill()
                    .frame(width: 64, height: 64)
                    .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
                VStack(alignment: .leading, spacing: 4) {
                    Text("Der Entwurf erzählt von diesem Foto; Stichwörter lenken, was er daraus nimmt.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                    if blindModel {
                        Text("Dieses Sprachmodell kann keine Fotos lesen – wähl eines, das „sieht Fotos“ zeigt.")
                            .font(.caption)
                            .foregroundStyle(.orange)
                    }
                    Button("Foto entfernen", systemImage: "xmark.circle", role: .destructive) { self.photo = nil }
                        .buttonStyle(.borderless)
                        .font(.caption)
                }
            }
        } else {
            Menu {
                Button("Aus Fotos …", systemImage: "photo.on.rectangle") { pickingPhoto = true }
                Button("Aus Datei …", systemImage: "folder") { importingPhoto = true }
            } label: {
                Label("Foto als Vorlage", systemImage: "photo")
            }
        }
        if let photoProblem {
            Label(photoProblem, systemImage: "exclamationmark.triangle").foregroundStyle(.red)
        }
    }

    private func usePhoto(_ load: () async throws -> Data?) async {
        photoProblem = nil
        guard let data = try? await load(), let scaled = CoverPhoto.scaledJPEG(from: data) else {
            photoProblem = String(localized: "Das Foto ließ sich nicht lesen.")
            return
        }
        photo = scaled
    }

    private var draftStage: String {
        switch draftState?.stage {
        case "queued": String(localized: "wartet …")
        case "loading": String(localized: "lädt das Modell …")
        default: String(localized: "schreibt …")
        }
    }

    private func startDraft() {
        error = nil
        Task {
            do {
                model.form.draftId = try await model.draftLyrics(for: model.form, photo: photo)
            } catch {
                self.error = error.localizedDescription
            }
        }
    }

    private func take(_ state: Components.Schemas.LyricsState?) {
        guard let state, state.finished == true, state.id == model.form.draftId else { return }
        if let lyrics = state.lyrics, !lyrics.isEmpty {
            model.form.lyrics = lyrics
            model.form.draftId = ""
        }
    }

    // MARK: Song

    private func generate() {
        sending = true
        sent = nil
        error = nil
        Task {
            do {
                let job = try await model.generate(model.form)
                let title = job?.title ?? model.form.request.title ?? String(localized: "Der Song")
                if job == nil {
                    sent = model.form.batch == 1
                        ? String(localized: "„\(title)“ wird erzeugt.")
                        : String(localized: "\(model.form.batch) Songs „\(title)“ werden erzeugt.")
                } else {
                    sent = model.form.batch == 1
                        ? String(localized: "„\(title)“ wartet in der Warteschlange.")
                        : String(localized: "\(model.form.batch) Songs „\(title)“ warten in der Warteschlange.")
                }
            } catch {
                self.error = error.localizedDescription
            }
            sending = false
        }
    }
}

extension AppModel {
    /// Asks for the song. Nil when it started at once: the server answers 202 then without a body (only a job that
    /// waits comes back), which the generated client cannot decode, so this call goes by hand.
    func generate(_ form: CreateForm) async throws -> Components.Schemas.QueuedJob? {
        guard let server = tonwerk?.server else { throw ServerError(String(localized: "Kein Server verbunden.")) }
        var request = URLRequest(url: server.appending(path: "api/generate"))
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONEncoder().encode(form.request)
        let (answer, response) = try await URLSession.shared.data(for: request)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        guard status == 202 else {
            throw ServerError(problem: ServerError.text(of: answer) ?? String(localized: "Der Server antwortete \(status)."))
        }
        return answer.isEmpty ? nil : try JSONDecoder.tonwerk().decode(Components.Schemas.QueuedJob.self, from: answer)
    }

    /// Starts a draft and answers its id; the text arrives as `lyrics` events.
    func draftLyrics(for form: CreateForm, photo: Data? = nil) async throws -> String {
        guard let tonwerk else { throw ServerError(String(localized: "Kein Server verbunden.")) }
        let request = Components.Schemas.LyricsRequest(
            keywords: form.idea,
            style: form.style,
            language: form.german ? .german : .english,
            model: form.lyricsModel.isEmpty ? nil : form.lyricsModel,
            image: photo.map { "data:image/jpeg;base64,\($0.base64EncodedString())" },
            machine: form.lyricsMachine.isEmpty ? nil : form.lyricsMachine,
            reasoning: Components.Schemas.LyricsReasoning(rawValue: form.lyricsReasoning))
        switch try await tonwerk.api.writeLyrics(body: .json(request)) {
        case .accepted(let accepted):
            let state = try accepted.body.json
            noteDraft(state)
            return state.id
        case .badRequest(let refused):
            throw ServerError(problem: try? refused.body.applicationProblemJson.detail)
        case .undocumented(let status, _):
            throw ServerError(String(localized: "Der Server antwortete \(status)."))
        }
    }

    /// The lyrics models LM Studio offers plus Claude's, or nil while LM Studio is down and no key is set. With a
    /// machine, that Mac's LM Studio.
    func lyricsModels(on machine: String = "") async -> Components.Schemas.LyricsModels? {
        try? await tonwerk?.api.listLyricsModels(query: .init(machine: machine.isEmpty ? nil : machine)).ok.body.json
    }

    /// A song's request laid over the form, its own score along switched off, then the form shown.
    func useInForm(_ song: Song) async {
        if song.engine == "ace" {
            await useAceSong(song)
            return
        }
        guard let tonwerk,
              let request = try? await tonwerk.api.getSongRequest(path: .init(run: song.run, song: song.name)).ok.body.json
        else { return }
        let own = request.abc == nil ? await ownScore(of: song) : nil
        form.take(request, ownScore: own)
        saveForm()
        page = .create
    }
}

/// A refusal with the server's own words where it gave some.
struct ServerError: LocalizedError {
    let errorDescription: String?

    init(_ text: String) {
        errorDescription = text
    }

    init(problem: String??) {
        errorDescription = (problem ?? nil) ?? String(localized: "Der Server hat abgelehnt.")
    }

    /// The words of a problem answer: the first field error of a validation problem, else its detail or title.
    static func text(of problem: Data) -> String? {
        guard let object = try? JSONSerialization.jsonObject(with: problem) as? [String: Any] else { return nil }
        if let errors = object["errors"] as? [String: [String]],
           let first = errors.keys.sorted().first.flatMap({ errors[$0]?.first })
        {
            return first
        }
        return (object["detail"] as? String) ?? (object["title"] as? String)
    }
}
