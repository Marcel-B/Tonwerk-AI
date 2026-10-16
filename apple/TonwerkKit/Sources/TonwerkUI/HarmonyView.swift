import SwiftUI
import TonwerkKit
import UniformTypeIdentifiers

/// The backing vocals page (`HarmonyPage.vue`): a melody in, a MIDI file with one track per part out.
struct HarmonyView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        Group {
            if let session = model.harmonyPage {
                HarmonySessionView(session: session)
            } else {
                ProgressView().onAppear { model.openHarmony() }
            }
        }
        .navigationTitle("Begleitstimmen")
    }
}

private struct HarmonySessionView: View {
    @Environment(AppModel.self) private var model
    @Bindable var session: HarmonySession
    @State private var fromLibrary = false
    @State private var importing = false
    @State private var saving: URL?
    @State private var saveProblem: String?

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                melody
                settings
                actions
                if let score = session.score {
                    LogicTransport(player: session.player)
                    PianoRoll(score: score, voices: session.voices, player: session.player)
                        .frame(height: 240)
                        .card(padding: 0)
                    legend
                }
            }
            .padding(16)
            .frame(maxWidth: 900)
            .frame(maxWidth: .infinity)
        }
        .fileImporter(
            isPresented: $importing,
            allowedContentTypes: [.midi, UTType(filenameExtension: "abc") ?? .plainText, .plainText]
        ) { result in
            guard case .success(let url) = result else { return }
            Task { await session.pick(url) }
        }
        .fileMover(isPresented: Binding(get: { saving != nil }, set: { if !$0 { saving = nil } }), file: saving) { _ in }
        .onAppear {
            if case .song = session.source {
                fromLibrary = true
            }
        }
        .onDisappear { session.stop() }
    }

    // MARK: Melody

    private var melody: some View {
        VStack(alignment: .leading, spacing: 10) {
            CardHeading("Melodie", systemImage: "music.note")
            Text("Eine Melodie rein, Begleitstimmen raus: jede Stimme singt im Rhythmus der Melodie, in der Tonart und auf den Akkorden (ohne Akkorde aus der Melodie geraten). Zurück kommt eine MIDI-Datei mit einer Spur je Stimme.")
                .font(.caption)
                .foregroundStyle(.secondary)
            Picker("Quelle", selection: $fromLibrary) {
                Text("Datei").tag(false)
                Text("Aus der Bibliothek").tag(true)
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            if fromLibrary {
                let songs = model.songs.filter(\.hasScore)
                Picker("Song", selection: Binding<String?>(
                    get: {
                        if case .song(let song) = session.source { return song.id }
                        return nil
                    },
                    set: { id in session.source = songs.first { $0.id == id }.map { .song($0) } }
                )) {
                    Text("Song wählen").tag(String?.none)
                    ForEach(songs) { song in
                        Text(song.displayTitle).tag(Optional(song.id))
                    }
                }
                if songs.isEmpty {
                    Text("Kein Song mit Partitur in der Bibliothek.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            } else {
                HStack {
                    if case .file(let file) = session.source {
                        Label(file.name, systemImage: "doc").lineLimit(1)
                        Spacer()
                        Button("Entfernen", role: .destructive) { session.source = nil }
                    } else {
                        Text("Melodie als MIDI-Datei oder score.abc")
                            .foregroundStyle(.secondary)
                        Spacer()
                    }
                    Button("Datei wählen …") { importing = true }
                }
            }
        }
        .card()
    }

    // MARK: Settings

    private var settings: some View {
        VStack(alignment: .leading, spacing: 10) {
            CardHeading("Stimmen", systemImage: "person.3")
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 170), alignment: .leading)], alignment: .leading, spacing: 8) {
                ForEach(HarmonySettings.parts, id: \.self) { part in
                    Toggle(HarmonySettings.name(part), isOn: Binding(
                        get: { session.settings.parts.contains(part) },
                        set: { on in
                            if on {
                                session.settings.parts.append(part)
                            } else {
                                session.settings.parts.removeAll { $0 == part }
                            }
                        }))
                    #if os(macOS)
                    .toggleStyle(.checkbox)
                    #endif
                }
            }
            Picker("Tonart", selection: $session.settings.key) {
                Text("Automatisch").tag(String?.none)
                ForEach(HarmonySettings.keys, id: \.self) { key in
                    Text(key).tag(Optional(key))
                }
            }
            Toggle("Nur im Refrain (Abschnitt „chorus“)", isOn: $session.settings.chorusOnly)
            Toggle("Akkordspur mit in die MIDI-Datei", isOn: $session.settings.withChords)
            if session.settings.parts.isEmpty {
                Text("Wähle mindestens eine Stimme.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .card()
    }

    // MARK: Generating

    private var actions: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Button {
                    Task { await session.generate() }
                } label: {
                    HStack(spacing: 8) {
                        if session.busy { ProgressView().controlSize(.small) }
                        Text(session.busy ? "Wird erzeugt …" : "Stimmen erzeugen")
                    }
                }
                .buttonStyle(.borderedProminent)
                .disabled(!session.canGenerate)
                if session.conversion?.midi != nil {
                    Button {
                        do {
                            saving = try session.midiFile()
                            saveProblem = nil
                        } catch {
                            saveProblem = error.localizedDescription
                        }
                    } label: {
                        Label("MIDI sichern …", systemImage: "square.and.arrow.down")
                    }
                    .buttonStyle(.bordered)
                    .disabled(session.stale)
                }
                if let source = session.source {
                    Button {
                        openInLogic(source)
                    } label: {
                        Label("Mit allen Optionen auf der Seite Logic", systemImage: "pianokeys")
                    }
                    .buttonStyle(.borderless)
                }
            }
            if session.stale {
                Text("Die Einstellungen haben sich geändert; „Stimmen erzeugen“ macht die Vorschau und die MIDI-Datei neu.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            if let problem = session.problem ?? saveProblem {
                Label(problem, systemImage: "exclamationmark.triangle").foregroundStyle(.red)
            }
            ForEach(session.notes, id: \.self) { note in
                Label(note.message, systemImage: note.severity == "Info" ? "info.circle" : "exclamationmark.circle")
                    .font(.caption)
                    .foregroundStyle(note.severity == "Error" ? Color.red : note.severity == "Warning" ? Color.orange : Color.secondary)
            }
        }
    }

    /// The melody, not the parts: the Logic page reads a MIDI file's `Harmony …` tracks as generated and leaves
    /// them out, so it makes its own from the melody.
    private func openInLogic(_ source: ScoreSource) {
        switch source {
        case .song(let song): model.openInLogic(song)
        case .file(let file): model.openInLogic(file: file)
        }
    }

    /// Which colour in the piano roll is which part.
    private var legend: some View {
        LazyVGrid(columns: [GridItem(.adaptive(minimum: 140), alignment: .leading)], alignment: .leading, spacing: 6) {
            ForEach(Array(session.voices.enumerated()), id: \.element.id) { index, voice in
                Label {
                    Text(voice.displayName).font(.caption)
                } icon: {
                    Circle().fill(PianoRoll.colour(index)).frame(width: 9, height: 9)
                }
            }
        }
    }
}
