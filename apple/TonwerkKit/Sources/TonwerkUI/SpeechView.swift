import SwiftUI
import TonwerkKit
import UniformTypeIdentifiers

/// The speech lab (`SpeechLab.vue`): record a voice, have a text spoken by local text-to-speech models with it and
/// compare what they make of it, and clean speech recordings of noise. Nothing here reaches the song library.
struct SpeechView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        Group {
            if let session = model.speechPage {
                SpeechSessionView(session: session)
            } else {
                ProgressView().onAppear { model.openSpeech() }
            }
        }
        .navigationTitle("Sprachlabor")
    }
}

private struct SpeechSessionView: View {
    @Environment(AppModel.self) private var model
    @Bindable var session: SpeechSession
    @State private var importing: Importing?
    @State private var saving: URL?
    @State private var deletingVoice: Components.Schemas.SpeechVoice?

    enum Importing {
        case voice
        case cleanup
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                Text("Hier lässt sich ausprobieren, welches lokale Sprachmodell eine Stimme natürlich klingen lässt, etwa für einen Podcast. Jedes gewählte Modell spricht den Text nacheinander, auf Wunsch mit einer hier aufgenommenen Stimme. YuE2, das Textmodell und Stimmumwandlungen warten so lange.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                if let problem = session.problem {
                    Label(problem, systemImage: "exclamationmark.triangle").foregroundStyle(.red)
                }
                if let info = session.info {
                    if !info.installed {
                        notInstalled(info)
                    }
                    ViewThatFits(in: .horizontal) {
                        HStack(alignment: .top, spacing: 14) {
                            voiceCard(info).frame(minWidth: 340)
                            textCard(info).frame(minWidth: 340)
                        }
                        VStack(alignment: .leading, spacing: 14) {
                            voiceCard(info)
                            textCard(info)
                        }
                    }
                    takesCard
                    if !info.cleanModels.isEmpty {
                        cleanCard(info)
                    }
                } else if session.loading {
                    ProgressView().frame(maxWidth: .infinity).padding(.top, 40)
                }
            }
            .padding()
            .frame(maxWidth: 1000, alignment: .leading)
            .frame(maxWidth: .infinity)
        }
        .task { await session.load() }
        .refreshable { await session.load() }
        .onChange(of: model.connection) {
            // A take may have finished while the stream was away.
            if model.connection == .live { Task { await session.load() } }
        }
        .fileImporter(
            isPresented: Binding(get: { importing != nil }, set: { if !$0 { importing = nil } }),
            allowedContentTypes: [.audio, .movie]
        ) { result in
            let target = importing
            guard case .success(let url) = result else { return }
            Task { await take(url, for: target) }
        }
        .fileMover(isPresented: Binding(get: { saving != nil }, set: { if !$0 { saving = nil } }), file: saving) { _ in }
        .confirmationDialog(
            "Die Stimme „\(deletingVoice?.label ?? "")“ löschen? Fertige Ergebnisse bleiben.",
            isPresented: Binding(get: { deletingVoice != nil }, set: { if !$0 { deletingVoice = nil } }),
            titleVisibility: .visible
        ) {
            if let voice = deletingVoice {
                Button("Löschen", role: .destructive) { Task { await session.deleteVoice(voice) } }
            }
            Button("Behalten", role: .cancel) {}
        }
    }

    private func take(_ url: URL, for target: Importing?) async {
        do {
            let file = try await PickedFile.copy(from: url)
            switch target {
            case .voice:
                session.recorded = file
                if session.voiceName.trimmingCharacters(in: .whitespaces).isEmpty {
                    session.voiceName = file.baseName
                }
            case .cleanup:
                session.cleanFile = file
            case nil:
                break
            }
            session.problem = nil
        } catch {
            session.problem = error.localizedDescription
        }
    }

    private func notInstalled(_ info: Components.Schemas.SpeechInfo) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Label("Die Sprachmodelle sind noch nicht eingerichtet. Einmal auf dem Mac im Ordner des Repositorys ausführen:", systemImage: "exclamationmark.triangle")
            Text("deploy/install-speech.sh")
                .font(.system(.body, design: .monospaced))
                .textSelection(.enabled)
            Text("Der Server sucht Python unter \(info.python).").font(.caption).foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(12)
        .background(Color.orange.opacity(0.12), in: RoundedRectangle(cornerRadius: 10, style: .continuous))
    }

    private func playButton(_ key: String, url: URL?, label: LocalizedStringKey = "Anhören") -> some View {
        Button {
            if let url { session.preview.toggle(key, url: url) }
        } label: {
            Image(systemName: session.preview.plays(key) ? "pause.fill" : "play.fill")
        }
        .buttonStyle(PlayButtonStyle(size: 30))
        .help(label)
        .accessibilityLabel(label)
    }

    // MARK: Voice

    private func voiceCard(_ info: Components.Schemas.SpeechInfo) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            CardHeading("Stimme", systemImage: "person.wave.2")
            voiceRow(nil, label: String(localized: "Stimme des Modells"), detail: nil)
            ForEach(info.voices, id: \.id) { voice in
                voiceRow(
                    voice, label: voice.label,
                    detail: "\(Song.clock(voice.seconds)) · \(voice.createdAt.formatted(date: .abbreviated, time: .shortened))")
            }
            Divider()
            newVoice(info)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .card()
    }

    private func voiceRow(_ voice: Components.Schemas.SpeechVoice?, label: String, detail: String?) -> some View {
        HStack(spacing: 10) {
            Button {
                session.voiceId = voice?.id
            } label: {
                HStack(spacing: 10) {
                    Image(systemName: session.voiceId == voice?.id ? "largecircle.fill.circle" : "circle")
                        .foregroundStyle(session.voiceId == voice?.id ? Theme.accent : Color.secondary)
                    VStack(alignment: .leading, spacing: 1) {
                        Text(label).fontWeight(session.voiceId == voice?.id ? .semibold : .regular).lineLimit(1)
                        if let detail {
                            Text(detail).font(.caption).foregroundStyle(.secondary)
                        }
                    }
                    Spacer(minLength: 0)
                }
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            if let voice {
                playButton("voice:\(voice.id)", url: try? model.speechVoiceAudio(voice.id))
                Button(role: .destructive) {
                    deletingVoice = voice
                } label: {
                    Image(systemName: "trash")
                }
                .buttonStyle(.borderless)
                .help("Stimme löschen")
            }
        }
    }

    private func newVoice(_ info: Components.Schemas.SpeechInfo) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Neue Stimme").font(.headline)
            if !info.ffmpegInstalled {
                Text("ffmpeg fehlt auf dem Server, deshalb lassen sich keine Aufnahmen speichern.")
                    .font(.caption).foregroundStyle(.red)
            }
            Text("Vorzulesender Satz").font(.caption).foregroundStyle(.secondary)
            TextEditor(text: $session.readAloud)
                .frame(minHeight: 70)
                .scrollContentBackground(.hidden)
                .padding(6)
                .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 8, style: .continuous))
            Text("Mehrere Modelle brauchen den genauen Wortlaut der Aufnahme. Ruhig und deutlich lesen, 5 bis 15 Sekunden; die Stille davor und danach schneidet der Server ab.")
                .font(.caption).foregroundStyle(.secondary)
            HStack(spacing: 10) {
                if session.recorder.recording {
                    Button(role: .destructive) {
                        session.recorder.stop()
                    } label: {
                        Label("Stopp", systemImage: "stop.circle")
                    }
                    .buttonStyle(.borderedProminent)
                    .tint(.red)
                    Text("Nimmt auf … \(session.recorder.seconds) s").font(.callout).foregroundStyle(.red)
                } else {
                    Button {
                        Task { await session.record() }
                    } label: {
                        Label(session.recorded == nil ? "Aufnehmen" : "Neu aufnehmen", systemImage: "mic")
                    }
                    .buttonStyle(.bordered)
                    .disabled(session.savingVoice)
                    Button {
                        importing = .voice
                    } label: {
                        Label("Datei wählen", systemImage: "folder")
                    }
                    .buttonStyle(.borderless)
                    .disabled(session.savingVoice)
                }
            }
            if let recorded = session.recorded, !session.recorder.recording {
                HStack(spacing: 10) {
                    playButton("new", url: recorded.url)
                    Text(recorded.name).lineLimit(1).truncationMode(.middle)
                    if let seconds = recorded.seconds {
                        Text(Song.clock(seconds)).font(.caption).foregroundStyle(.secondary)
                    }
                }
                HStack(spacing: 10) {
                    TextField("Name der Stimme", text: $session.voiceName)
                        .textFieldStyle(.roundedBorder)
                        .autocorrectionDisabled()
                    Button {
                        Task { await session.saveVoice() }
                    } label: {
                        if session.savingVoice {
                            ProgressView().controlSize(.small)
                        } else {
                            Label("Stimme speichern", systemImage: "square.and.arrow.down")
                        }
                    }
                    .buttonStyle(.borderedProminent)
                    .disabled(session.voiceName.trimmingCharacters(in: .whitespaces).isEmpty || !info.ffmpegInstalled
                        || session.savingVoice)
                }
            }
        }
    }

    // MARK: Text

    private func textCard(_ info: Components.Schemas.SpeechInfo) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            CardHeading("Text", systemImage: "text.alignleft")
            TextEditor(text: $session.text)
                .frame(minHeight: 130)
                .scrollContentBackground(.hidden)
                .padding(6)
                .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 8, style: .continuous))
            Text("Modelle").font(.caption).foregroundStyle(.secondary)
            ForEach(info.models, id: \.id) { option in
                Toggle(isOn: Binding(
                    get: { session.picked.contains(option.id) },
                    set: { if $0 { session.picked.insert(option.id) } else { session.picked.remove(option.id) } }
                )) {
                    VStack(alignment: .leading, spacing: 2) {
                        HStack(spacing: 4) {
                            Text(option.label).fontWeight(.semibold)
                            Text("· \(option.license) · \(SpeechSession.download(option))")
                                .font(.caption).foregroundStyle(.secondary)
                        }
                        Text(SpeechSession.note(option)).font(.caption).foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }
                #if os(macOS)
                .toggleStyle(.checkbox)
                #endif
            }
            HStack(spacing: 10) {
                Button {
                    Task { await session.speak() }
                } label: {
                    if session.sending {
                        ProgressView().controlSize(.small)
                    } else {
                        Label("Sprechen", systemImage: "speaker.wave.2")
                    }
                }
                .buttonStyle(.borderedProminent)
                .disabled(!info.installed || session.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
                    || session.picked.isEmpty || session.sending)
                if session.picked.isEmpty {
                    Text("Mindestens ein Modell wählen.").font(.caption).foregroundStyle(.secondary)
                } else if info.models.contains(where: { session.picked.contains($0.id) && !$0.downloaded }) {
                    Text("Das erste Mal lädt ein Modell sich herunter, das kann dauern.")
                        .font(.caption).foregroundStyle(.secondary)
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .card()
    }

    // MARK: Takes

    private var takesCard: some View {
        VStack(alignment: .leading, spacing: 10) {
            CardHeading("Ergebnisse", systemImage: "waveform")
            let spoken = session.spoken
            if spoken.isEmpty {
                Text("Noch nichts gesprochen.").foregroundStyle(.secondary)
            }
            ForEach(spoken, id: \.id) { take in
                VStack(alignment: .leading, spacing: 4) {
                    takeHeader(take, title: take.modelLabel,
                               subtitle: [take.voiceLabel ?? String(localized: "Stimme des Modells"),
                                          take.seconds.map(Song.clock)].compactMap { $0 }.joined(separator: " · "))
                    Text(take.text).font(.callout).lineLimit(2)
                    if take.stage == "done" {
                        Text(SpeechSession.stats(take)).font(.caption).foregroundStyle(.secondary)
                    }
                    failure(take)
                    if take.text != session.text {
                        Button("Text übernehmen") { session.text = take.text }
                            .buttonStyle(.borderless)
                            .font(.caption)
                    }
                }
                .padding(10)
                .background(.quaternary.opacity(0.4), in: RoundedRectangle(cornerRadius: 10, style: .continuous))
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .card()
    }

    private func takeHeader(_ take: Components.Schemas.SpeechTake, title: String, subtitle: String) -> some View {
        HStack(spacing: 8) {
            if take.stage == "done" {
                playButton(take.id, url: try? model.speechTakeAudio(take.id))
            }
            VStack(alignment: .leading, spacing: 1) {
                Text(title).fontWeight(.semibold).lineLimit(1)
                Text(subtitle).font(.caption).foregroundStyle(.secondary).lineLimit(1)
            }
            Spacer(minLength: 4)
            Text(SpeechSession.stageName(take))
                .font(.caption.weight(.semibold))
                .padding(.horizontal, 8)
                .padding(.vertical, 3)
                .foregroundStyle(take.stage == "done" ? Theme.accent : take.stage == "failed" ? .red : .secondary)
                .background(.quaternary, in: Capsule())
            if take.stage == "done" {
                Button {
                    Task {
                        do {
                            saving = try await model.downloadSpeechTake(take)
                        } catch {
                            session.problem = error.localizedDescription
                        }
                    }
                } label: {
                    Image(systemName: "square.and.arrow.down")
                }
                .buttonStyle(.borderless)
                .help("Herunterladen")
            }
            Button(role: take.finished == true ? .destructive : nil) {
                Task { await session.delete(take) }
            } label: {
                Image(systemName: take.finished == true ? "trash" : "xmark")
            }
            .buttonStyle(.borderless)
            .help(take.finished == true ? "Ergebnis löschen" : "Abbrechen")
        }
    }

    @ViewBuilder
    private func failure(_ take: Components.Schemas.SpeechTake) -> some View {
        if take.stage == "failed", let message = take.message, !message.isEmpty {
            Text(message).font(.caption).foregroundStyle(.red)
        }
    }

    // MARK: Cleaning

    private func cleanCard(_ info: Components.Schemas.SpeechInfo) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            CardHeading("Sprache säubern", systemImage: "waveform.badge.minus")
            Text("Eine Aufnahme von Sprache, etwa ein Vortrag, ein Interview oder eine Sprachnotiz, ohne Rauschen und Störgeräusche. Auch ein Video geht, genommen wird nur der Ton. Sie wartet in derselben Reihe wie die gesprochenen Ergebnisse.")
                .font(.caption)
                .foregroundStyle(.secondary)
            HStack(spacing: 10) {
                Button {
                    importing = .cleanup
                } label: {
                    Label("Datei wählen", systemImage: "folder")
                }
                Text(session.cleanFile?.name ?? String(localized: "Keine Datei gewählt"))
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .truncationMode(.middle)
            }
            Text("Modell").font(.caption).foregroundStyle(.secondary)
            ForEach(info.cleanModels, id: \.id) { option in
                Button {
                    session.cleanModel = option.id
                } label: {
                    HStack(alignment: .top, spacing: 10) {
                        Image(systemName: session.cleanModel == option.id ? "largecircle.fill.circle" : "circle")
                            .foregroundStyle(session.cleanModel == option.id ? Theme.accent : Color.secondary)
                        VStack(alignment: .leading, spacing: 2) {
                            HStack(spacing: 4) {
                                Text(option.label).fontWeight(.semibold)
                                Text("· \(option.license) · \(SpeechSession.download(option))")
                                    .font(.caption).foregroundStyle(.secondary)
                            }
                            Text(SpeechSession.note(option)).font(.caption).foregroundStyle(.secondary)
                                .fixedSize(horizontal: false, vertical: true)
                        }
                        Spacer(minLength: 0)
                    }
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
            }
            HStack(spacing: 10) {
                Button {
                    Task { await session.clean() }
                } label: {
                    if session.cleaning {
                        HStack(spacing: 6) {
                            ProgressView().controlSize(.small)
                            Text("Lädt hoch …")
                        }
                    } else {
                        Label("Säubern", systemImage: "line.3.horizontal.decrease.circle")
                    }
                }
                .buttonStyle(.borderedProminent)
                .disabled(!info.installed || !info.ffmpegInstalled || session.cleanFile == nil
                    || session.cleanModel == nil || session.cleaning)
                if !info.ffmpegInstalled {
                    Text("ffmpeg fehlt auf dem Server, deshalb lassen sich keine Aufnahmen speichern.")
                        .font(.caption).foregroundStyle(.red)
                }
            }
            ForEach(session.cleanups, id: \.id) { take in
                VStack(alignment: .leading, spacing: 4) {
                    takeHeader(take, title: take.source ?? "",
                               subtitle: [take.modelLabel, take.seconds.map(Song.clock)].compactMap { $0 }
                                   .joined(separator: " · "))
                    if take.stage == "cleaning", let progress = take.progress {
                        ProgressView(value: min(max(progress, 0), 1))
                    }
                    if take.stage == "done" {
                        let key = "\(take.id):source"
                        Button {
                            if let url = try? model.speechTakeSource(take.id) {
                                session.preview.toggle(key, url: url)
                            }
                        } label: {
                            Label(session.preview.plays(key) ? "Original anhalten" : "Original anhören",
                                  systemImage: session.preview.plays(key) ? "pause" : "clock.arrow.circlepath")
                        }
                        .buttonStyle(.borderless)
                        .font(.caption)
                    }
                    failure(take)
                }
                .padding(10)
                .background(.quaternary.opacity(0.4), in: RoundedRectangle(cornerRadius: 10, style: .continuous))
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .card()
    }
}
