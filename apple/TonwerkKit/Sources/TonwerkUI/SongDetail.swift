import SwiftUI
import TonwerkKit

/// One song beside the list, what the web page's `SongRow.vue` unfolds: title with its rename dialog, stars, what it was made with,
/// its versions with other voices, the note, style and lyrics. Made anew per song (`.id`), so its fields start from
/// that song.
struct SongDetail: View {
    @Environment(AppModel.self) private var model
    @Environment(Player.self) private var player
    let song: Song
    /// The list it was chosen from, for what plays after it.
    let list: [Song]
    /// The song while its rename dialog is open.
    @State private var renaming: Song?
    @State private var note: String
    @State private var problem: String?
    @State private var sharing: Song?
    @State private var asking = false
    /// Said once the server took the render, until the worker's own state shows it.
    @State private var sent: String?

    init(song: Song, list: [Song]) {
        self.song = song
        self.list = list
        _note = State(initialValue: song.note ?? "")
    }

    var body: some View {
        ScrollView {
            VStack(spacing: 14) {
                header
                VStack(spacing: 12) {
                    infos
                    if song.rendersFull {
                        renderCard
                    }
                    if !song.versions.isEmpty {
                        versions
                    }
                    noteCard
                    if !song.style.isEmpty {
                        textCard("Stil", systemImage: "paintpalette", text: song.style)
                    }
                    if !song.lyrics.isEmpty {
                        textCard("Songtext", systemImage: "text.quote", text: song.lyrics)
                    }
                }
                .padding(.horizontal, 14)
                .padding(.bottom, 20)
            }
        }
        .background {
            // The cover blurred behind the whole panel, as the web page's full-screen player does it.
            ZStack {
                Rectangle().fill(.background)
                CoverBackdrop(song: song, opacity: 0.35)
            }
            .ignoresSafeArea()
        }
        // Saved a moment after the last keystroke, like the web page's note (`SongNote.vue`), and when the song
        // changes or the panel closes.
        .task(id: note) {
            try? await Task.sleep(for: .seconds(1))
            guard !Task.isCancelled else { return }
            await saveNote()
        }
        .onDisappear {
            let text = note
            Task { try? await model.setNote(song, text) }
        }
        .renamingSong($renaming)
    }

    private var playing: Bool {
        player.current?.id == song.id && player.current?.version == nil && player.isPlaying
    }

    // MARK: Header

    private var header: some View {
        VStack(spacing: 12) {
            CoverEditor(song: song)
                .padding(.bottom, 4)
            HStack(alignment: .firstTextBaseline, spacing: 6) {
                Text(song.title)
                    .font(.title2.weight(.bold))
                    .multilineTextAlignment(.center)
                    .textSelection(.enabled)
                Button {
                    renaming = song
                } label: {
                    Image(systemName: "pencil")
                }
                .buttonStyle(.borderless)
                .help("Umbenennen")
                .accessibilityLabel("Umbenennen")
            }
            if song.originalTitle != song.title {
                Text("Ursprünglich „\(song.originalTitle)“")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            if song.number != nil {
                Text("Der Titel gilt für alle Songs dieses Durchlaufs.")
                    .font(.caption2)
                    .foregroundStyle(.tertiary)
            }
            RatingView(rating: song.rating, font: .title3) { stars in
                Task { await model.rate(song, stars) }
            }
            actions
            if let problem {
                Label(problem, systemImage: "exclamationmark.triangle")
                    .font(.callout)
                    .foregroundStyle(.red)
            }
        }
        .padding(.horizontal, 16)
        .padding(.top, 20)
        .frame(maxWidth: .infinity)
    }

    private var actions: some View {
        HStack(spacing: 14) {
            Button {
                if player.current?.id == song.id && player.current?.version == nil {
                    player.toggle()
                } else {
                    player.play(song, in: list)
                }
            } label: {
                Image(systemName: playing ? "pause.fill" : "play.fill")
                    .contentTransition(.symbolEffect(.replace))
            }
            .buttonStyle(PlayButtonStyle(size: 50))
            .disabled(!song.hasAudio)
            .accessibilityLabel(playing ? "Pause" : "Abspielen")
            Button {
                Task { await model.useInForm(song) }
            } label: {
                Label("Ins Formular", systemImage: "square.and.pencil")
            }
            .buttonStyle(.bordered)
            .buttonBorderShape(.capsule)
            .controlSize(.large)
            Button {
                sharing = song
            } label: {
                Image(systemName: "square.and.arrow.up")
            }
            .buttonStyle(.bordered)
            .buttonBorderShape(.circle)
            .controlSize(.large)
            .disabled(!song.hasAudio)
            .accessibilityLabel("Teilen")
        }
        .padding(.top, 4)
        .exportSheet(for: $sharing)
    }

    // MARK: Cards

    private var infos: some View {
        LazyVGrid(columns: [GridItem(.adaptive(minimum: 120), spacing: 8)], spacing: 8) {
            if let duration = song.duration {
                InfoTile(label: "Länge", symbol: "clock", value: duration)
            }
            if let tempo = song.tempo {
                InfoTile(label: "Tempo", symbol: "metronome", value: "\(Int(tempo.rounded())) BPM")
            }
            if let key = song.key {
                InfoTile(label: "Tonart", symbol: "music.quarternote.3", value: AbcScore.keyLabel(key))
            }
            InfoTile(label: "Modell", symbol: "cpu", value: song.engine == "ace" ? "ACE-Step" : "YuE2")
            if let quality = song.quality {
                InfoTile(
                    label: "Qualität", symbol: "dial.medium",
                    value: quality == "full" ? String(localized: "Voll") : String(localized: "Entwurf"))
            }
            if let seed = song.seed {
                InfoTile(label: "Seed", symbol: "number", value: String(seed), selectable: true)
            }
            if let created = song.createdAt {
                InfoTile(label: "Erstellt", symbol: "calendar", value: created.formatted(date: .abbreviated, time: .shortened))
            }
        }
    }

    private var versions: some View {
        VStack(alignment: .leading, spacing: 8) {
            CardHeading("Andere Stimmen", systemImage: "person.wave.2")
            ForEach(song.versions) { version in
                VersionRow(song: song, version: version)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .card(onBackdrop: true)
    }

    private var noteCard: some View {
        VStack(alignment: .leading, spacing: 6) {
            CardHeading("Notiz", systemImage: "note.text")
            TextField("Was dir zu diesem Song einfällt", text: $note, axis: .vertical)
                .textFieldStyle(.plain)
                .lineLimit(2...10)
            Text("Wird beim Tippen gespeichert, für alle Geräte.")
                .font(.caption2)
                .foregroundStyle(.tertiary)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .card(onBackdrop: true)
    }

    private func textCard(_ title: LocalizedStringKey, systemImage: String, text: String) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            CardHeading(title, systemImage: systemImage)
            Text(text)
                .textSelection(.enabled)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
        .card(onBackdrop: true)
    }

    /// A draft synthesized again from its saved tokens at full quality, the same song, written over in place.
    private var renderCard: some View {
        let rendering = model.isRendering(song)
        return VStack(alignment: .leading, spacing: 8) {
            Button {
                Task { await render() }
            } label: {
                Label(
                    rendering ? "Wird in voller Qualität gerendert …" : "In voller Qualität rendern",
                    systemImage: "waveform")
                    .frame(maxWidth: .infinity)
            }
            .buttonStyle(.bordered)
            .controlSize(.large)
            .disabled(asking || rendering)
            Text(sent ?? String(localized: "Derselbe Song aus seinen gespeicherten Tokens, neu synthetisiert; die Datei wird ersetzt."))
                .font(.footnote)
                .foregroundStyle(.secondary)
        }
        .card(onBackdrop: true)
    }

    private func render() async {
        asking = true
        defer { asking = false }
        do {
            let job = try await model.renderFull(song)
            sent = job == nil
                ? String(localized: "Wird gerendert, der Fortschritt steht in der Warteschlange.")
                : String(localized: "Wartet in der Warteschlange.")
            problem = nil
        } catch {
            problem = error.localizedDescription
        }
    }

    private func saveNote() async {
        do {
            try await model.setNote(song, note)
            problem = nil
        } catch {
            problem = error.localizedDescription
        }
    }
}

extension AppModel {
    /// Asks the worker to synthesize a draft again at full quality. Nil when it started at once: the server answers
    /// 202 without a body then, which the generated client cannot decode, so this goes by hand like ``generate(_:)``.
    func renderFull(_ song: Song) async throws -> Components.Schemas.QueuedJob? {
        guard let server = tonwerk?.server else { throw ServerError(String(localized: "Kein Server verbunden.")) }
        var request = URLRequest(url: server.appending(path: "api/songs/\(song.run)/\(song.name)/render"))
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONEncoder().encode(["quality": "full"])
        let (answer, response) = try await URLSession.shared.data(for: request)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        guard status == 202 else {
            throw ServerError(problem: ServerError.text(of: answer) ?? String(localized: "Der Server antwortete \(status)."))
        }
        return answer.isEmpty ? nil : try JSONDecoder.tonwerk().decode(Components.Schemas.QueuedJob.self, from: answer)
    }

    /// Whether the worker renders the song now or the queue holds a render of it.
    func isRendering(_ song: Song) -> Bool {
        if let state = working[song.id], state.render == true, state.finished != true { return true }
        return queue.contains { $0.kind == .render && $0.songId == song.id }
    }
}

/// A small heading inside a card: an accent symbol and the name.
struct CardHeading: View {
    let title: LocalizedStringKey
    let systemImage: String

    init(_ title: LocalizedStringKey, systemImage: String) {
        self.title = title
        self.systemImage = systemImage
    }

    var body: some View {
        Label {
            Text(title)
        } icon: {
            Image(systemName: systemImage).foregroundStyle(Theme.accent)
        }
        .font(.subheadline.weight(.semibold))
    }
}

/// One fact about the song in a small tile of the grid under the header.
private struct InfoTile: View {
    let label: LocalizedStringKey
    let symbol: String
    let value: String
    var selectable = false

    var body: some View {
        VStack(alignment: .leading, spacing: 3) {
            Label(label, systemImage: symbol)
                .font(.caption)
                .foregroundStyle(.secondary)
            Group {
                if selectable {
                    Text(value).textSelection(.enabled)
                } else {
                    Text(value)
                }
            }
            .font(.callout.weight(.semibold).monospacedDigit())
            .lineLimit(1)
            .minimumScaleFactor(0.75)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .card(padding: 10, onBackdrop: true)
    }
}

/// A version with another voice: playable once it is done, else its stage.
private struct VersionRow: View {
    @Environment(AppModel.self) private var model
    @Environment(Player.self) private var player
    let song: Song
    let version: Song.Version
    @State private var confirmDelete = false

    private var playing: Bool {
        player.current?.id == song.id && player.current?.version?.id == version.id && player.isPlaying
    }

    var body: some View {
        HStack {
            if version.playable {
                Button {
                    if playing {
                        player.pause()
                    } else {
                        let track = song.with(version: .some(version))
                        player.play(track, in: [track])
                    }
                } label: {
                    Image(systemName: playing ? "pause.fill" : "play.fill")
                }
                .buttonStyle(.borderless)
            }
            Text(version.voice)
            if version.semitones != 0 {
                Text(octaves)
                    .foregroundStyle(.secondary)
            }
            Spacer()
            if !version.playable {
                Text(stageName)
                    .font(.caption)
                    .foregroundStyle(version.stage == "failed" ? Color.red : Color.secondary)
            }
            // Visible, not only in the context menu, which nobody finds on the Mac or the phone.
            Button(role: .destructive) {
                confirmDelete = true
            } label: {
                Image(systemName: version.playable ? "trash" : "xmark")
            }
            .buttonStyle(.borderless)
            .help(version.playable ? "Version löschen" : "Abbrechen")
            .accessibilityLabel(version.playable ? "Version löschen" : "Abbrechen")
        }
        .contextMenu {
            Button(role: .destructive) {
                confirmDelete = true
            } label: {
                Label(version.playable ? "Version löschen" : "Abbrechen", systemImage: "trash")
            }
        }
        .confirmationDialog(
            version.playable ? "Die Version mit \(version.voice) löschen?" : "Die Version mit \(version.voice) abbrechen?",
            isPresented: $confirmDelete,
            titleVisibility: .visible
        ) {
            Button(version.playable ? "Version löschen" : "Version abbrechen", role: .destructive) { delete() }
            Button("Behalten", role: .cancel) {}
        } message: {
            Text("Der Song selbst bleibt erhalten.")
        }
    }

    private func delete() {
        if player.current?.version?.id == version.id {
            player.stop()
        }
        Task { try? await model.deleteVersion(song, version) }
    }

    /// Only whole octaves are offered when a version is asked for.
    private var octaves: String {
        let count = version.semitones / 12
        return count > 0 ? String(localized: "+\(count) Okt.") : String(localized: "\(count) Okt.")
    }

    private var stageName: String {
        switch version.stage {
        case "failed": String(localized: "fehlgeschlagen")
        case "cancelled": String(localized: "abgebrochen")
        case "queued": String(localized: "wartet")
        default: String(localized: "in Arbeit")
        }
    }
}
