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

    init(song: Song, list: [Song]) {
        self.song = song
        self.list = list
        _title = State(initialValue: song.title)
        _note = State(initialValue: song.note ?? "")
    }

    var body: some View {
        Form {
            Section {
                VStack(spacing: 12) {
                    CoverView(song: song)
                        .aspectRatio(1, contentMode: .fit)
                        .frame(maxWidth: 220)
                        .shadow(color: .black.opacity(0.25), radius: 8, y: 4)
                    RatingView(rating: song.rating) { stars in
                        Task { await model.rate(song, stars) }
                    }
                    .font(.title3)
                    HStack {
                        Button {
                            player.play(song, in: list)
                        } label: {
                            Label("Abspielen", systemImage: "play.fill")
                        }
                        .buttonStyle(.borderedProminent)
                        .disabled(!song.hasAudio)
                        Button {
                            Task { await model.useInForm(song) }
                        } label: {
                            Label("Ins Formular", systemImage: "square.and.pencil")
                        }
                        .buttonStyle(.bordered)
                    }
                }
                .frame(maxWidth: .infinity)
                TextField("Titel", text: $title)
                    .onSubmit { Task { await rename() } }
                if song.originalTitle != song.title {
                    LabeledContent("Ursprünglich", value: song.originalTitle)
                        .foregroundStyle(.secondary)
                }
                if song.number != nil {
                    Text("Der Titel gilt für alle Songs dieses Durchlaufs.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                if let problem {
                    Label(problem, systemImage: "exclamationmark.triangle")
                        .foregroundStyle(.red)
                }
            }

            Section("Infos") {
                if let created = song.createdAt {
                    LabeledContent("Erstellt", value: created.formatted(date: .abbreviated, time: .shortened))
                }
                if let duration = song.duration {
                    LabeledContent("Länge", value: duration)
                }
                LabeledContent("Modell", value: song.engine == "ace" ? "ACE-Step" : "YuE2")
                if let quality = song.quality {
                    LabeledContent("Qualität", value: quality == "full" ? String(localized: "Voll") : String(localized: "Entwurf"))
                }
                if let seed = song.seed {
                    LabeledContent("Seed") {
                        Text(String(seed)).textSelection(.enabled).monospacedDigit()
                    }
                }
                if let tempo = song.tempo {
                    LabeledContent("Tempo", value: "\(Int(tempo.rounded())) BPM")
                }
                if let key = song.key {
                    LabeledContent("Tonart", value: AbcScore.keyLabel(key))
                }
            }

            if !song.versions.isEmpty {
                Section("Andere Stimmen") {
                    ForEach(song.versions) { version in
                        VersionRow(song: song, version: version)
                    }
                }
            }

            Section {
                TextEditor(text: $note)
                    .frame(minHeight: 70)
                    .scrollContentBackground(.hidden)
            } header: {
                Text("Notiz")
            } footer: {
                Text("Wird beim Tippen gespeichert, für alle Geräte.")
            }

            if !song.style.isEmpty {
                Section("Stil") {
                    Text(song.style)
                        .textSelection(.enabled)
                }
            }

            if !song.lyrics.isEmpty {
                Section("Songtext") {
                    Text(song.lyrics)
                        .textSelection(.enabled)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
            }
        }
        .formStyle(.grouped)
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
