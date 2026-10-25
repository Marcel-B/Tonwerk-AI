import SwiftUI
import TonwerkKit
import UniformTypeIdentifiers

/// The transcription page (`TranscribePanel.vue`, `AudioMidiPanel.vue`): a recording to a melody score with
/// SheetSage2, the finished ones taken into the form, saved or deleted, and a recording to MIDI with Basic Pitch.
struct TranscriptionView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        Group {
            if let session = model.transcriptionPage {
                TranscriptionSessionView(session: session)
            } else {
                ProgressView().onAppear { model.openTranscription() }
            }
        }
        .navigationTitle("Transkription")
    }
}

/// How far SheetSage2 is; it only sends heartbeats for long stretches, then the bar just shows that it works.
struct TranscriptionProgress: View {
    let state: Components.Schemas.TranscriptionState

    private var detail: some View {
        Text(state.detail.flatMap { $0.isEmpty ? nil : $0 } ?? String(localized: "Läuft …"))
            .font(.caption)
            .foregroundStyle(.secondary)
            .lineLimit(2)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            if let fraction = state.fraction {
                ProgressView(value: min(max(fraction, 0), 1))
                detail
            } else {
                HStack(spacing: 6) {
                    ProgressView().controlSize(.small)
                    detail
                }
            }
        }
    }
}

private struct TranscriptionSessionView: View {
    @Environment(AppModel.self) private var model
    @Bindable var session: TranscriptionSession
    @State private var importing: Importing?
    /// Apart from `importing`, since dismissing the importer sets this false before its completion runs, which still
    /// has to know what the file was picked for.
    @State private var picking = false
    @State private var deleting: Components.Schemas.TranscriptionInfo?
    @State private var saving: URL?
    @State private var shown: Set<String> = []

    enum Importing {
        case transcription
        case midi
    }

    private func pick(_ target: Importing) {
        importing = target
        picking = true
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                if let problem = session.problem {
                    Label(problem, systemImage: "exclamationmark.triangle")
                        .foregroundStyle(.red)
                }
                startCard
                if !model.transcriptionJobs.isEmpty || model.lastTranscriptionFailure != nil {
                    jobsCard
                }
                finishedCard
                midiCard
            }
            .padding()
            .frame(maxWidth: 900, alignment: .leading)
            .frame(maxWidth: .infinity)
        }
        .task { await session.load() }
        .refreshable { await session.load() }
        .fileImporter(
            isPresented: $picking,
            allowedContentTypes: [.audio, .movie]
        ) { result in
            let target = importing
            guard case .success(let url) = result else { return }
            Task { await take(url, for: target) }
        }
        .fileMover(
            isPresented: Binding(get: { saving != nil }, set: { if !$0 { saving = nil } }), file: saving
        ) { _ in }
        .confirmationDialog(
            "Die Transkription von „\(deleting?.sourceName ?? "")“ mit Partitur und MIDI-Dateien endgültig löschen?",
            isPresented: Binding(get: { deleting != nil }, set: { if !$0 { deleting = nil } }),
            titleVisibility: .visible
        ) {
            if let item = deleting {
                Button("Löschen", role: .destructive) { Task { await session.delete(item) } }
            }
            Button("Behalten", role: .cancel) {}
        }
    }

    private func take(_ url: URL, for target: Importing?) async {
        do {
            let file = try await PickedFile.copy(from: url)
            switch target {
            case .transcription: session.file = file
            case .midi: session.midiFile = file
            case nil: break
            }
            session.problem = nil
        } catch {
            session.problem = error.localizedDescription
        }
    }

    private func filePicker(_ file: PickedFile?, for target: Importing) -> some View {
        HStack(spacing: 10) {
            Button {
                pick(target)
            } label: {
                Label("Datei wählen", systemImage: "folder")
            }
            Text(file?.name ?? String(localized: "Keine Datei gewählt"))
                .foregroundStyle(.secondary)
                .lineLimit(1)
                .truncationMode(.middle)
        }
    }

    // MARK: SheetSage2

    private var startCard: some View {
        VStack(alignment: .leading, spacing: 10) {
            CardHeading("Transkription (SheetSage2)", systemImage: "music.quarternote.3")
            Text("Macht aus einer Aufnahme eine Melodie-Partitur (ABC), die sich als eigene Partitur für einen neuen Song nutzen lässt, etwa für ein Cover. SheetSage2 läuft auf der CPU und braucht einige Minuten.")
                .font(.caption)
                .foregroundStyle(.secondary)
            if session.installed == false {
                Label("SheetSage2 ist noch nicht installiert. Einmal in YuE Studio „Transcribe recording“ öffnen und „Install transcription support“ wählen (etwa 2 GB), oder auf dem Mac deploy/setup-mac.sh --transcription.", systemImage: "exclamationmark.triangle")
                    .font(.callout)
                    .foregroundStyle(.orange)
            }
            filePicker(session.file, for: .transcription)
            Picker("Melodie", selection: $session.melody) {
                ForEach(TranscriptionSession.Melody.allCases) { melody in
                    Text(TranscriptionSession.Melody.name(of: melody.rawValue)).tag(melody)
                }
            }
            Text("„Gesang und Instrumente“ schreibt die Gesangsmelodie in „Vocal“ und die Instrumentalmelodie in „Ins“; „Nur Gesang“ nur die Gesangsmelodie.")
                .font(.caption)
                .foregroundStyle(.secondary)
            HStack(spacing: 12) {
                Button {
                    Task { await session.start() }
                } label: {
                    if session.sending {
                        ProgressView().controlSize(.small)
                    } else {
                        Text("Transkribieren")
                    }
                }
                .buttonStyle(.borderedProminent)
                .disabled(!session.canStart)
                if let notice = session.notice {
                    Text(notice.text)
                        .font(.caption)
                        .foregroundStyle(notice.error ? Color.red : Color.secondary)
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .card()
    }

    private var jobsCard: some View {
        VStack(alignment: .leading, spacing: 10) {
            CardHeading("In Arbeit", systemImage: "hourglass")
            ForEach(model.runningTranscriptions, id: \.id) { state in
                VStack(alignment: .leading, spacing: 6) {
                    HStack {
                        Text(state.fileName).fontWeight(.semibold).lineLimit(1)
                        Text(AppModel.transcriptionStage(state.stage)).foregroundStyle(.secondary)
                        Spacer()
                        Button("Abbrechen") {
                            Task {
                                do {
                                    try await model.cancelTranscription(state.id)
                                } catch {
                                    session.problem = error.localizedDescription
                                }
                            }
                        }
                        .buttonStyle(.borderless)
                    }
                    TranscriptionProgress(state: state)
                }
            }
            ForEach(model.waitingTranscriptions, id: \.id) { job in
                HStack(spacing: 8) {
                    Image(systemName: "clock").foregroundStyle(.secondary)
                    Text(job.title).lineLimit(1)
                    Spacer()
                    Text(TranscriptionSession.Melody.name(of: job.transcriptionTask))
                        .font(.caption).foregroundStyle(.secondary)
                    Button(role: .destructive) {
                        Task { await model.remove(job) }
                    } label: {
                        Image(systemName: "xmark")
                    }
                    .buttonStyle(.borderless)
                    .help("Aus der Warteschlange nehmen")
                }
            }
            if let failure = model.lastTranscriptionFailure {
                Text(verbatim: AppModel.failureText(failure))
                    .font(.callout)
                    .foregroundStyle(.red)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .card()
    }

    private var finishedCard: some View {
        VStack(alignment: .leading, spacing: 10) {
            CardHeading("Fertige Transkriptionen", systemImage: "doc.text")
            if session.loaded && session.items.isEmpty {
                Text("Noch keine.").foregroundStyle(.secondary)
            }
            ForEach(Array(session.items.enumerated()), id: \.element.id) { index, item in
                if index > 0 {
                    Divider()
                }
                row(item)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .card()
    }

    private func row(_ item: Components.Schemas.TranscriptionInfo) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(alignment: .firstTextBaseline) {
                VStack(alignment: .leading, spacing: 2) {
                    Text(item.sourceName).fontWeight(.semibold).lineLimit(2)
                    Text(details(item)).font(.caption).foregroundStyle(.secondary)
                }
                Spacer()
                actions(item)
            }
            if !item.warnings.isEmpty {
                Text("Hinweise von SheetSage2: \(item.warnings.joined(separator: "; "))")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            Button {
                Task { await session.useScore(item) }
            } label: {
                Label("Als Partitur übernehmen", systemImage: "arrow.right.doc.on.clipboard")
            }
            .buttonStyle(.bordered)
            .controlSize(.small)
            DisclosureGroup("ABC ansehen", isExpanded: Binding(
                get: { shown.contains(item.id) },
                set: { open in
                    if open {
                        shown.insert(item.id)
                        Task { _ = await session.score(of: item) }
                    } else {
                        shown.remove(item.id)
                    }
                }
            )) {
                ScrollView {
                    Text(session.scores[item.id] ?? "…")
                        .font(.system(.caption, design: .monospaced))
                        .textSelection(.enabled)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
                .frame(maxHeight: 280)
                .padding(8)
                .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 6, style: .continuous))
            }
            .font(.callout)
        }
    }

    private func details(_ item: Components.Schemas.TranscriptionInfo) -> String {
        var parts = [TranscriptionSession.Melody.name(of: item.task)]
        if let created = item.createdAt {
            parts.append(created.formatted(date: .abbreviated, time: .shortened))
        }
        parts.append(item.bytes.formatted(.byteCount(style: .file)))
        return parts.joined(separator: " · ")
    }

    private func actions(_ item: Components.Schemas.TranscriptionInfo) -> some View {
        Menu {
            Button {
                Task { await save(item, zip: false) }
            } label: {
                Label("Partitur sichern …", systemImage: "music.note")
            }
            Button {
                Task { await save(item, zip: true) }
            } label: {
                Label("Alle Dateien sichern …", systemImage: "doc.zipper")
            }
            Divider()
            Button(role: .destructive) {
                deleting = item
            } label: {
                Label("Löschen", systemImage: "trash")
            }
        } label: {
            Image(systemName: "ellipsis.circle")
        }
        .menuStyle(.button)
        .buttonStyle(.borderless)
        .menuIndicator(.hidden)
        .fixedSize()
    }

    private func save(_ item: Components.Schemas.TranscriptionInfo, zip: Bool) async {
        do {
            saving = try await model.downloadTranscription(item, zip: zip)
        } catch {
            session.problem = error.localizedDescription
        }
    }

    // MARK: Basic Pitch

    private var midiCard: some View {
        VStack(alignment: .leading, spacing: 10) {
            CardHeading("Audio zu MIDI (Basic Pitch)", systemImage: "pianokeys")
            Text("Anders als die Transkription oben schreibt Basic Pitch keine Partitur im Takt, sondern hört jede Note mit Einsatz und Länge. Am saubersten mit dem trockenen Gesang aus den Stems. Die Spur heißt „Vocal“, so lesen die Logic-Seite und die Begleitstimmen sie als Melodie.")
                .font(.caption)
                .foregroundStyle(.secondary)
            if session.midiInstalled == false {
                Label("Basic Pitch ist auf dem Server nicht installiert (deploy/setup-mac.sh --midi).", systemImage: "exclamationmark.triangle")
                    .font(.callout)
                    .foregroundStyle(.orange)
            }
            filePicker(session.midiFile, for: .midi)
            Toggle("Einstimmig", isOn: $session.midiMono)
            Toggle("Pitch Bends", isOn: $session.midiBends)
                .disabled(!session.midiMono)
            Toggle("Auf Sechzehntel quantisieren", isOn: $session.midiQuantize)
            Toggle("Tempo bekannt", isOn: $session.tempoKnown)
            if session.tempoKnown {
                Stepper(value: $session.tempo, in: 20...300, step: 1) {
                    Text("\(Int(session.tempo)) BPM")
                }
            } else if session.midiQuantize {
                Text("Zum Quantisieren braucht es das Tempo.").font(.caption).foregroundStyle(.red)
            }
            HStack(spacing: 12) {
                Button {
                    Task { await session.convertMidi() }
                } label: {
                    if session.convertingMidi {
                        ProgressView().controlSize(.small)
                    } else {
                        Text("In MIDI umwandeln")
                    }
                }
                .buttonStyle(.borderedProminent)
                .disabled(!session.canConvertMidi)
                if let error = session.midiError {
                    Text(error).font(.caption).foregroundStyle(.red)
                }
            }
            if let midi = session.midi {
                HStack(spacing: 12) {
                    Text("\(midi.notes) Noten").foregroundStyle(.secondary)
                    ShareLink(item: midi.url) {
                        Label("Teilen", systemImage: "square.and.arrow.up")
                    }
                    Button {
                        saving = midi.url
                    } label: {
                        Label("Sichern …", systemImage: "square.and.arrow.down")
                    }
                }
                .font(.callout)
                .buttonStyle(.borderless)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .card()
    }
}
