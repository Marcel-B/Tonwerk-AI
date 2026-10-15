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

    var body: some View {
        @Bindable var model = model
        Form {
            Section("Song") {
                TextField("Titel (optional)", text: $model.form.title)
                TextField("Stil", text: $model.form.style, prompt: Text("German, pop, female vocal, warm piano, 90 bpm"), axis: .vertical)
                    .lineLimit(2...6)
                StyleBlocksView(style: $model.form.style, instrumental: model.form.instrumental)
            }

            Section("Songtext") {
                Toggle("Instrumental", isOn: $model.form.instrumental)
                TextEditor(text: $model.form.lyrics)
                    .font(.body.monospaced())
                    .frame(minHeight: model.form.instrumental ? 60 : 220)
                    .scrollContentBackground(.hidden)
                SectionTagsView(lyrics: $model.form.lyrics)
                if !model.form.instrumental {
                    draft
                }
            }

            ScoreSection()

            Section("Erzeugen") {
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
            }

            AdvancedSection()

            Section {
                Button(action: generate) {
                    HStack {
                        if sending { ProgressView().controlSize(.small) }
                        Text(model.form.batch == 1 ? "Song erzeugen" : "\(model.form.batch) Songs erzeugen")
                    }
                    .frame(maxWidth: .infinity)
                }
                .buttonStyle(.borderedProminent)
                .controlSize(.large)
                .disabled(sending || model.form.problem != nil)
                .keyboardShortcut(.return, modifiers: .command)
                if let problem = model.form.problem {
                    Text(problem).foregroundStyle(.secondary)
                }
                if let sent {
                    HStack {
                        Label(sent, systemImage: "checkmark.circle").foregroundStyle(.green)
                        Spacer()
                        Button("Zur Warteschlange") { model.page = .queue }
                    }
                }
                if let error {
                    Label(error, systemImage: "exclamationmark.triangle").foregroundStyle(.red)
                }
            }
        }
        .formStyle(.grouped)
        .navigationTitle("Erstellen")
        .toolbar {
            ToolbarItem {
                Button(role: .destructive) {
                    let keep = model.form
                    model.form = CreateForm()
                    model.form.german = keep.german
                    model.form.lyricsModel = keep.lyricsModel
                } label: {
                    Label("Formular leeren", systemImage: "trash")
                }
            }
        }
        .onChange(of: model.form) { model.saveForm() }
        .onChange(of: draftState?.finished) { take(draftState) }
        .task { models = await model.lyricsModels() }
        .confirmationDialog("Den Songtext durch einen neuen Entwurf ersetzen?", isPresented: $replacing) {
            Button("Ersetzen", role: .destructive) { startDraft() }
        }
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
        Picker("Sprache", selection: $model.form.german) {
            Text("Deutsch").tag(true)
            Text("Englisch").tag(false)
        }
        if let models {
            Picker("Modell", selection: $model.form.lyricsModel) {
                Text("Standard (\(models.models.first { $0.id == models._default }?.name ?? models._default))").tag("")
                ForEach(models.models, id: \.id) { choice in
                    Text(choice.cloud == true ? "\(choice.name) ☁︎" : choice.name).tag(choice.id)
                }
            }
        }
        HStack {
            Spacer()
            if drafting {
                ProgressView().controlSize(.small)
                Text(draftStage).foregroundStyle(.secondary)
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
                .disabled(model.form.idea.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
        }
        if let message = draftState?.message, draftState?.finished == true, draftState?.lyrics == nil {
            Label(message, systemImage: "exclamationmark.triangle").foregroundStyle(.red)
        }
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
                model.form.draftId = try await model.draftLyrics(for: model.form)
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
                sent = model.form.batch == 1
                    ? String(localized: "„\(job.title)“ ist bestellt.")
                    : String(localized: "\(model.form.batch) Songs „\(job.title)“ sind bestellt.")
            } catch {
                self.error = error.localizedDescription
            }
            sending = false
        }
    }
}

extension AppModel {
    func generate(_ form: CreateForm) async throws -> Components.Schemas.QueuedJob {
        guard let tonwerk else { throw ServerError(String(localized: "Kein Server verbunden.")) }
        switch try await tonwerk.api.generate(body: .json(form.request)) {
        case .accepted(let accepted):
            return try accepted.body.json
        case .badRequest(let refused):
            throw ServerError(problem: try? refused.body.applicationProblemJson.detail)
        case .notImplemented(let refused):
            throw ServerError(problem: try? refused.body.applicationProblemJson.detail)
        case .serviceUnavailable(let refused):
            throw ServerError(problem: try? refused.body.applicationProblemJson.detail)
        case .undocumented(let status, _):
            throw ServerError(String(localized: "Der Server antwortete \(status)."))
        }
    }

    /// Starts a draft and answers its id; the text arrives as `lyrics` events.
    func draftLyrics(for form: CreateForm) async throws -> String {
        guard let tonwerk else { throw ServerError(String(localized: "Kein Server verbunden.")) }
        let request = Components.Schemas.LyricsRequest(
            keywords: form.idea,
            style: form.style,
            language: form.german ? .german : .english,
            model: form.lyricsModel.isEmpty ? nil : form.lyricsModel)
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

    /// The lyrics models LM Studio offers plus Claude's, or nil while LM Studio is down and no key is set.
    func lyricsModels() async -> Components.Schemas.LyricsModels? {
        try? await tonwerk?.api.listLyricsModels().ok.body.json
    }

    /// A song's request laid over the form, its own score along switched off, then the form shown.
    func useInForm(_ song: Song) async {
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
}
