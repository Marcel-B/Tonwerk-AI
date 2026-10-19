import SwiftUI
import TonwerkKit
import UniformTypeIdentifiers

/// The singing page: notes and words in, a sung voice out, by SoulX-Singer on the server. The melody is a MIDI file
/// (with its lyric events, if it has them), a score.abc or a library song's score.
struct SingView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        Group {
            if let session = model.singPage {
                SingSessionView(session: session)
            } else {
                ProgressView().onAppear { model.openSing() }
            }
        }
        .navigationTitle("Singen")
    }
}

private struct SingSessionView: View {
    @Environment(AppModel.self) private var model
    @Bindable var session: SingSession
    @State private var importing: Importing?
    @State private var saving: URL?
    @State private var deletingVoice: Components.Schemas.SingingVoiceInfo?

    enum Importing {
        case melody, voice
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                Text("Eine Melodie und ihre Worte, gesungen von SoulX-Singer. Jede Note bekommt eine Silbe, Pausen bleiben still. Am besten klingt Englisch; YuE2, das Textmodell und die anderen großen Modelle warten so lange.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                if let problem = session.problem {
                    Label(problem, systemImage: "exclamationmark.triangle").foregroundStyle(.red)
                }
                if let info = session.info {
                    if !info.installed || !info.modelDownloaded {
                        notInstalled(info)
                    }
                    melodyCard
                    wordsCard(info)
                    voicesCard
                    takesCard
                } else if session.loading {
                    ProgressView().frame(maxWidth: .infinity).padding(.top, 40)
                }
            }
            .padding()
            .frame(maxWidth: 1200, alignment: .leading)
            .frame(maxWidth: .infinity)
        }
        .task { await session.load() }
        .task(id: session.editor.revision) {
            // A moment after the last change, so typing a word asks once.
            try? await Task.sleep(for: .milliseconds(700))
            guard !Task.isCancelled else { return }
            await session.readPhonemes()
        }
        .refreshable { await session.load() }
        .onChange(of: model.connection) {
            // A take may have finished while the stream was away.
            if model.connection == .live { Task { await session.load() } }
        }
        .fileImporter(
            isPresented: Binding(get: { importing != nil }, set: { if !$0 { importing = nil } }),
            allowedContentTypes: importing == .voice
                ? [.audio, .movie]
                : [.midi, UTType(filenameExtension: "abc") ?? .plainText, .plainText]
        ) { result in
            let target = importing
            guard case .success(let url) = result else { return }
            Task {
                if target == .voice {
                    await session.pickVoice(url)
                } else {
                    await session.pick(url)
                }
            }
        }
        .confirmationDialog(
            "Die Stimme „\(deletingVoice?.label ?? "")“ löschen? Fertige Ergebnisse bleiben.",
            isPresented: Binding(get: { deletingVoice != nil }, set: { if !$0 { deletingVoice = nil } }),
            titleVisibility: .visible
        ) {
            Button("Löschen", role: .destructive) {
                if let voice = deletingVoice {
                    Task { await session.deleteVoice(voice) }
                }
            }
        }
        .fileMover(isPresented: Binding(get: { saving != nil }, set: { if !$0 { saving = nil } }), file: saving) { _ in }
    }

    private func notInstalled(_ info: Components.Schemas.SingingInfo) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Label("SoulX-Singer ist noch nicht eingerichtet. Einmal auf dem Mac im Ordner des Repositorys ausführen:", systemImage: "exclamationmark.triangle")
            Text("deploy/install-singing.sh")
                .font(.system(.body, design: .monospaced))
                .textSelection(.enabled)
            Text("Der Server sucht Python unter \(info.python) und das Modell unter \(info.model).")
                .font(.caption).foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(12)
        .background(Color.orange.opacity(0.12), in: RoundedRectangle(cornerRadius: 10, style: .continuous))
    }

    // MARK: Melody

    private var melodyCard: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 8) {
                CardHeading("Melodie", systemImage: "pianokeys")
                Spacer(minLength: 4)
                if session.busy {
                    ProgressView().controlSize(.small)
                }
                Menu {
                    Button("Neu (leer)", systemImage: "doc") { session.clear() }
                    Button("MIDI-Datei oder score.abc …", systemImage: "folder") { importing = .melody }
                    let songs = model.songs.filter(\.hasScore)
                    Menu("Aus der Bibliothek") {
                        if songs.isEmpty {
                            Text("Kein Song mit Partitur")
                        }
                        ForEach(songs.prefix(60)) { song in
                            Button(song.displayTitle) { Task { await session.open(.song(song)) } }
                        }
                    }
                    Divider()
                    Button("Als MIDI-Datei sichern …", systemImage: "square.and.arrow.down") {
                        do {
                            saving = try session.midiFile()
                        } catch {
                            session.problem = error.localizedDescription
                        }
                    }
                    .disabled(session.editor.notes.isEmpty)
                } label: {
                    Label("Melodie", systemImage: "ellipsis.circle")
                }
                .fixedSize()
            }
            TextField("Titel", text: $session.title)
                .textFieldStyle(.roundedBorder)
            SingEditorView(editor: session.editor, play: { Task { await session.play() } })
            Text("Tippen auf eine freie Stelle setzt eine Note, Ziehen verschiebt sie, ihr rechter Rand ändert die Länge. Eine gewählte Note bekommt unten ihre Silbe; mehrere Wörter verteilen sich auf die folgenden Noten. „Gezogen“ (–) singt die Silbe davor auf dieser Tonhöhe weiter, so läuft eine Silbe über mehrere Noten. Eine Note ohne Silbe bleibt still.")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .card()
    }

    // MARK: Words and voice

    private func wordsCard(_ info: Components.Schemas.SingingInfo) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            CardHeading("Worte und Stimme", systemImage: "text.quote")
            TextEditor(text: $session.lyrics)
                .frame(minHeight: 100)
                .scrollContentBackground(.hidden)
                .padding(6)
                .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 8, style: .continuous))
            HStack(spacing: 10) {
                Button {
                    Task { await session.spread() }
                } label: {
                    Label("Auf die Noten verteilen", systemImage: "text.badge.plus")
                }
                .disabled(session.editor.notes.isEmpty || session.lyrics.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || !session.installed || session.busy)
                if session.editor.leftover > 0 {
                    Text("\(session.editor.leftover) Silben ohne Note").font(.caption).foregroundStyle(.orange)
                }
            }
            Text("Verteilt den Text neu auf alle Noten: ein Wort auf so viele Noten, wie es Silben hat, Bindestriche legen die Silben selbst fest (to-mor-row), ein allein stehendes „-“ zieht das Wort davor über die nächste Note. [Verse] und andere Marken werden übersprungen.")
                .font(.caption)
                .foregroundStyle(.secondary)
            Picker("Stimme", selection: $session.voiceId) {
                ForEach(info.voices, id: \.id) { voice in
                    Text(voice.label).tag(Optional(voice.id))
                }
            }
            Toggle("In die Lage der Stimme verschieben", isOn: $session.autoShift)
            #if os(macOS)
                .toggleStyle(.checkbox)
            #endif
            if !session.autoShift {
                Stepper(value: $session.pitchShift, in: -24...24) {
                    Text(session.pitchShift == 0
                        ? String(localized: "Tonhöhe wie geschrieben")
                        : String(localized: "\(session.pitchShift > 0 ? "+" : "")\(session.pitchShift) Halbtöne"))
                }
            }
            MachinePicker(selection: $session.machine, engine: "singing")
            HStack(spacing: 10) {
                Button {
                    Task { await session.sing() }
                } label: {
                    if session.sending {
                        ProgressView().controlSize(.small)
                    } else {
                        Label("Singen", systemImage: "music.mic")
                    }
                }
                .buttonStyle(.borderedProminent)
                .disabled(!session.canSing)
                if session.editor.notes.isEmpty {
                    Text("Erst Noten setzen oder eine Melodie laden.").font(.caption).foregroundStyle(.secondary)
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .card()
    }

    // MARK: Own voices

    private var voicesCard: some View {
        VStack(alignment: .leading, spacing: 10) {
            CardHeading("Eigene Stimmen", systemImage: "person.wave.2")
            Text("Aus einer Aufnahme deines Gesangs macht der Server eine Stimme: Er hört die Noten heraus, legt den Text darauf und behält die erste Phrase von bis zu zwölf Sekunden.")
                .font(.caption)
                .foregroundStyle(.secondary)
            ForEach(session.ownVoices, id: \.id) { voice in
                HStack(spacing: 8) {
                    Button {
                        if let url = try? model.singingVoiceAudio(voice.id) { session.preview.toggle("voice:\(voice.id)", url: url) }
                    } label: {
                        Image(systemName: session.preview.plays("voice:\(voice.id)") ? "pause.fill" : "play.fill")
                    }
                    .buttonStyle(PlayButtonStyle(size: 30))
                    .help("Anhören")
                    .accessibilityLabel("Anhören")
                    VStack(alignment: .leading, spacing: 1) {
                        Text(voice.label).fontWeight(.semibold).lineLimit(1)
                        Text([voice.text.map { "„\($0)“" }, voice.seconds.map(Song.clock), Self.range(voice)].compactMap { $0 }
                            .joined(separator: " · "))
                            .font(.caption).foregroundStyle(.secondary).lineLimit(2)
                    }
                    Spacer(minLength: 4)
                    Button(role: .destructive) {
                        deletingVoice = voice
                    } label: {
                        Image(systemName: "trash")
                    }
                    .buttonStyle(.borderless)
                    .help("Stimme löschen")
                }
            }
            if let heard = session.heard {
                Text(SingSession.heardNote(heard)).font(.caption).foregroundStyle(heard.leftover > 0 ? .orange : .secondary)
            }
            Divider()
            Text("Neue Stimme").font(.headline)
            Text("Gesungener Text (Englisch, genau so, wie du ihn singst)").font(.caption).foregroundStyle(.secondary)
            TextEditor(text: $session.voiceLyrics)
                .frame(minHeight: 60)
                .scrollContentBackground(.hidden)
                .padding(6)
                .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 8, style: .continuous))
            Text("So klappt es am besten: eine Phrase von 6 bis 12 Sekunden am Stück, in der Lage und dem Stil, in dem die Stimme später singen soll. Ruhiger, trockener Raum ohne Hall und Musik, gleicher Abstand zum Mikrofon (eine Handbreit), nicht übersteuern. Gerade Töne, eine Silbe pro Ton, wenig Vibrato und keine langen Schleifer; Silben deutlich absetzen. Ein anderer englischer Text geht auch, er muss nur genau stimmen.")
                .font(.caption)
                .foregroundStyle(.secondary)
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
                        Label("WAV oder andere Datei", systemImage: "folder")
                    }
                    .buttonStyle(.borderless)
                    .disabled(session.savingVoice)
                }
            }
            if let recorded = session.recorded, !session.recorder.recording {
                HStack(spacing: 10) {
                    Button {
                        session.preview.toggle("new-voice", url: recorded.url)
                    } label: {
                        Image(systemName: session.preview.plays("new-voice") ? "pause.fill" : "play.fill")
                    }
                    .buttonStyle(PlayButtonStyle(size: 30))
                    .accessibilityLabel("Anhören")
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
                            Label("Stimme anlegen", systemImage: "square.and.arrow.down")
                        }
                    }
                    .buttonStyle(.borderedProminent)
                    .disabled(!session.canSaveVoice)
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .card()
    }

    /// The notes the voice sang, as Logic names them (C3 = 60).
    private static func range(_ voice: Components.Schemas.SingingVoiceInfo) -> String? {
        guard let low = voice.low, let high = voice.high else { return nil }
        let names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"]
        let name = { (note: Int) in "\(names[((note % 12) + 12) % 12])\(note / 12 - 2)" }
        return "\(name(Int(low)))–\(name(Int(high)))"
    }

    // MARK: Takes

    private var takesCard: some View {
        VStack(alignment: .leading, spacing: 10) {
            CardHeading("Ergebnisse", systemImage: "waveform")
            let takes = session.takes
            if takes.isEmpty {
                Text("Noch nichts gesungen.").foregroundStyle(.secondary)
            }
            ForEach(takes, id: \.id) { take in
                VStack(alignment: .leading, spacing: 4) {
                    header(take)
                    if take.stage == "singing", let progress = take.progress {
                        ProgressView(value: min(max(progress, 0), 1))
                    }
                    if let alignment = SingSession.alignment(take) {
                        Text(alignment).font(.caption).foregroundStyle(.secondary)
                    }
                    if take.stage == "done" {
                        Text(SingSession.stats(take)).font(.caption).foregroundStyle(.secondary)
                    }
                    if take.stage == "failed", let message = take.message, !message.isEmpty {
                        Text(message).font(.caption).foregroundStyle(.red)
                    }
                    if SingSession.finished(take) {
                        Button("In den Editor") { Task { await session.open(take) } }
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

    private func header(_ take: Components.Schemas.SingTake) -> some View {
        HStack(spacing: 8) {
            if take.stage == "done" {
                Button {
                    if let url = try? model.singTakeAudio(take.id) { session.preview.toggle(take.id, url: url) }
                } label: {
                    Image(systemName: session.preview.plays(take.id) ? "pause.fill" : "play.fill")
                }
                .buttonStyle(PlayButtonStyle(size: 30))
                .help("Anhören")
                .accessibilityLabel("Anhören")
            }
            VStack(alignment: .leading, spacing: 1) {
                Text(take.title ?? "").fontWeight(.semibold).lineLimit(1)
                Text([take.voiceLabel, take.seconds.map(Song.clock), model.ranOnNote(take.ranOn)].compactMap { $0 }
                    .joined(separator: " · "))
                    .font(.caption).foregroundStyle(.secondary).lineLimit(1)
            }
            Spacer(minLength: 4)
            Text(SingSession.stageName(take))
                .font(.caption.weight(.semibold))
                .padding(.horizontal, 8)
                .padding(.vertical, 3)
                .foregroundStyle(take.stage == "done" ? Theme.accent : take.stage == "failed" ? .red : .secondary)
                .background(.quaternary, in: Capsule())
            if take.stage == "done" {
                Button {
                    Task {
                        do {
                            saving = try await model.downloadSingTake(take)
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
            let finished = SingSession.finished(take)
            Button(role: finished ? .destructive : nil) {
                Task { await session.delete(take) }
            } label: {
                Image(systemName: finished ? "trash" : "xmark")
            }
            .buttonStyle(.borderless)
            .help(finished ? "Ergebnis löschen" : "Abbrechen")
        }
    }
}
