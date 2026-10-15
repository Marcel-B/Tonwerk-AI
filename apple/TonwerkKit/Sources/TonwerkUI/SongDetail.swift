import SwiftUI
import TonwerkKit

/// One song beside the list, what the web page's `SongRow.vue` unfolds: title to rename, stars, what it was made with,
/// its versions with other voices, the note, style and lyrics. Made anew per song (`.id`), so its fields start from
/// that song.
struct SongDetail: View {
    @Environment(AppModel.self) private var model
    @Environment(Player.self) private var player
    let song: Song
    /// The list it was chosen from, for what plays after it.
    let list: [Song]
    @State private var title: String
    @State private var note: String
    @State private var problem: String?
    @State private var sharing: Song?

    init(song: Song, list: [Song]) {
        self.song = song
        self.list = list
        _title = State(initialValue: song.title)
        _note = State(initialValue: song.note ?? "")
    }

    var body: some View {
        ScrollView {
            VStack(spacing: 14) {
                header
                VStack(spacing: 12) {
                    infos
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
    }

    private var playing: Bool {
        player.current?.id == song.id && player.current?.version == nil && player.isPlaying
    }

    // MARK: Header

    private var header: some View {
        VStack(spacing: 12) {
            CoverView(song: song)
                .aspectRatio(1, contentMode: .fit)
                .frame(maxWidth: 220)
                .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
                .shadow(color: .black.opacity(0.35), radius: 16, y: 8)
                .padding(.bottom, 4)
            TextField("Titel", text: $title)
                .textFieldStyle(.plain)
                .font(.title2.weight(.bold))
                .multilineTextAlignment(.center)
                .onSubmit { Task { await rename() } }
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

    private func rename() async {
        guard title.trimmingCharacters(in: .whitespacesAndNewlines) != song.title else { return }
        do {
            try await model.rename(song, to: title)
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
    @Environment(Player.self) private var player
    let song: Song
    let version: Song.Version

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
        }
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
