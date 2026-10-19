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
    @State private var fromLibrary = false
    @State private var importing = false
    @State private var saving: URL?

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
                    ViewThatFits(in: .horizontal) {
                        HStack(alignment: .top, spacing: 14) {
                            melodyCard.frame(minWidth: 340)
                            wordsCard(info).frame(minWidth: 340)
                        }
                        VStack(alignment: .leading, spacing: 14) {
                            melodyCard
                            wordsCard(info)
                        }
                    }
                    takesCard
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
        .onAppear {
            if case .song = session.source {
                fromLibrary = true
            }
        }
        .fileImporter(
            isPresented: $importing,
            allowedContentTypes: [.midi, UTType(filenameExtension: "abc") ?? .plainText, .plainText]
        ) { result in
            guard case .success(let url) = result else { return }
            Task { await session.pick(url) }
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
            CardHeading("Melodie", systemImage: "music.note")
            Picker("Quelle", selection: $fromLibrary) {
                Text("Datei").tag(false)
                Text("Aus der Bibliothek").tag(true)
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            if fromLibrary {
                let songs = model.songs.filter(\.hasScore)
                Picker("Song", selection: Binding<String?>(
                    get: { session.source?.song?.id },
                    set: { id in session.source = songs.first { $0.id == id }.map { .song($0) } }
                )) {
                    Text("Song wählen").tag(String?.none)
                    ForEach(songs) { song in
                        Text(song.displayTitle).tag(Optional(song.id))
                    }
                }
                Text(songs.isEmpty
                    ? "Kein Song mit Partitur in der Bibliothek."
                    : "Gesungen wird die Gesangsstimme der Partitur, ohne eigene Worte mit dem Songtext des Songs.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            } else {
                HStack {
                    if case .file(let file) = session.source {
                        Label(file.name, systemImage: "doc").lineLimit(1)
                        Spacer()
                        Button("Entfernen", role: .destructive) { session.source = nil }
                    } else {
                        Text("MIDI-Datei oder score.abc").foregroundStyle(.secondary)
                        Spacer()
                    }
                    Button("Datei wählen …") { importing = true }
                }
                Text("Aus einer MIDI-Datei nimmt der Server die Spur, die nach Gesang heißt (Vocal, Lead, Melodie …), sonst die mit Liedtext, sonst die vollste. Liedtext-Ereignisse in der Datei ersetzen die Worte unten.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            TextField("Titel", text: $session.title)
                .textFieldStyle(.roundedBorder)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .card()
    }

    // MARK: Words and voice

    private func wordsCard(_ info: Components.Schemas.SingingInfo) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            CardHeading("Worte und Stimme", systemImage: "text.quote")
            TextEditor(text: $session.lyrics)
                .frame(minHeight: 130)
                .scrollContentBackground(.hidden)
                .padding(6)
                .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 8, style: .continuous))
            Text("Eine Silbe je Note, der Reihe nach; ein Wort bekommt so viele Noten, wie es Silben hat. [Verse] und andere Marken werden übersprungen, Bindestriche legen die Silben selbst fest (to-mor-row), ein allein stehendes „-“ hält das vorige Wort über die nächste Note.")
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
                if session.source == nil {
                    Text("Erst eine Melodie wählen.").font(.caption).foregroundStyle(.secondary)
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
                    if let lyrics = take.lyrics, !lyrics.isEmpty, lyrics != session.lyrics {
                        Button("Worte übernehmen") { session.lyrics = lyrics }
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
                Text([take.voiceLabel, take.seconds.map(Song.clock)].compactMap { $0 }.joined(separator: " · "))
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
