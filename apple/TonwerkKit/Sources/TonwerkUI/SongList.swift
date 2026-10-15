import SwiftUI
import TonwerkKit

/// A list of songs to play, rate and sort into playlists: the library, the songs to review, a playlist.
struct SongList: View {
    @Environment(AppModel.self) private var model
    @Environment(Player.self) private var player
    let title: String
    let songs: [Song]
    var playlist: Components.Schemas.PlaylistInfo?
    var empty: String = "Noch keine Songs."
    @State private var query = ""

    private var shown: [Song] {
        songs.filter { $0.matches(query) }
    }

    var body: some View {
        Group {
            if let error = model.libraryError, model.songs.isEmpty {
                ContentUnavailableView {
                    Label("Bibliothek nicht geladen", systemImage: "wifi.exclamationmark")
                } description: {
                    Text(error)
                } actions: {
                    Button("Nochmal") { Task { await model.reloadLibrary() } }
                }
            } else if songs.isEmpty {
                ContentUnavailableView(title, systemImage: "music.note", description: Text(empty))
            } else if shown.isEmpty {
                ContentUnavailableView.search(text: query)
            } else {
                List {
                    ForEach(shown) { song in
                        SongRow(song: song, playing: player.current?.id == song.id) {
                            player.play(song, in: shown)
                        }
                        .contextMenu { menu(for: song) }
                    }
                    .onDelete(perform: playlist == nil ? nil : { remove($0) })
                }
                #if os(macOS)
                .listStyle(.inset(alternatesRowBackgrounds: true))
                #else
                .listStyle(.plain)
                #endif
            }
        }
        .navigationTitle(title)
        .searchable(text: $query, prompt: "Titel oder Stil")
        .refreshable { await model.reloadLibrary() }
        .toolbar {
            ToolbarItem {
                Button {
                    if let first = shown.first(where: \.hasAudio) {
                        player.play(first, in: shown)
                    }
                } label: {
                    Label("Alle abspielen", systemImage: "play.fill")
                }
                .disabled(!shown.contains(where: \.hasAudio))
            }
        }
    }

    @ViewBuilder
    private func menu(for song: Song) -> some View {
        Button {
            player.play(song, in: shown)
        } label: {
            Label("Abspielen", systemImage: "play")
        }
        .disabled(!song.hasAudio)
        Menu {
            ForEach(model.playlists, id: \.id) { list in
                Button {
                    Task { await model.toggle(song, in: list) }
                } label: {
                    if list.songIds.contains(song.id) {
                        Label(list.name, systemImage: "checkmark")
                    } else {
                        Text(list.name)
                    }
                }
            }
        } label: {
            Label("Playlist", systemImage: "text.badge.plus")
        }
        Menu {
            ForEach((1...5).reversed(), id: \.self) { stars in
                Button(String(repeating: "★", count: stars)) { Task { await model.rate(song, stars) } }
            }
            Button("Keine Bewertung") { Task { await model.rate(song, nil) } }
        } label: {
            Label("Bewerten", systemImage: "star")
        }
        if let playlist {
            Button(role: .destructive) {
                Task { await model.toggle(song, in: playlist) }
            } label: {
                Label("Aus der Playlist nehmen", systemImage: "minus.circle")
            }
        }
    }

    private func remove(_ offsets: IndexSet) {
        guard let playlist else { return }
        let gone = Set(offsets.map { shown[$0].id })
        Task { await model.setSongs(playlist.songIds.filter { !gone.contains($0) }, of: playlist) }
    }
}

struct SongRow: View {
    @Environment(AppModel.self) private var model
    let song: Song
    let playing: Bool
    let play: () -> Void

    var body: some View {
        HStack(spacing: 12) {
            Button(action: play) {
                HStack(spacing: 12) {
                    CoverView(song: song)
                        .frame(width: 44, height: 44)
                        .overlay {
                            if playing {
                                Image(systemName: "speaker.wave.2.fill")
                                    .foregroundStyle(.white)
                                    .shadow(radius: 2)
                            }
                        }
                    VStack(alignment: .leading, spacing: 2) {
                        Text(song.displayTitle)
                            .fontWeight(playing ? .semibold : .regular)
                            .lineLimit(1)
                        Text(song.style)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            .lineLimit(1)
                    }
                    Spacer(minLength: 8)
                    if let duration = song.duration {
                        Text(duration)
                            .font(.caption.monospacedDigit())
                            .foregroundStyle(.secondary)
                    }
                }
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .disabled(!song.hasAudio)
            RatingView(rating: song.rating) { stars in
                Task { await model.rate(song, stars) }
            }
        }
        .padding(.vertical, 2)
    }
}

/// Five stars; tapping the given rating again clears it.
struct RatingView: View {
    let rating: Int?
    let rate: (Int?) -> Void

    var body: some View {
        HStack(spacing: 2) {
            ForEach(1...5, id: \.self) { stars in
                Button {
                    rate(rating == stars ? nil : stars)
                } label: {
                    Image(systemName: stars <= (rating ?? 0) ? "star.fill" : "star")
                        .font(.caption)
                        .foregroundStyle(stars <= (rating ?? 0) ? AnyShapeStyle(.yellow) : AnyShapeStyle(.tertiary))
                }
                .buttonStyle(.plain)
                .accessibilityLabel("\(stars) Sterne")
            }
        }
        .accessibilityElement(children: .contain)
    }
}

/// The song's own cover, or one drawn from its id like the web page's (`export.ts` hues the gradient by the id).
struct CoverView: View {
    @Environment(AppModel.self) private var model
    let song: Song

    var body: some View {
        Group {
            if let server = model.server, let url = song.coverURL(on: server) {
                AsyncImage(url: url) { image in
                    image.resizable().scaledToFill()
                } placeholder: {
                    drawn
                }
            } else {
                drawn
            }
        }
        .clipShape(RoundedRectangle(cornerRadius: 6, style: .continuous))
    }

    private var drawn: some View {
        let hue = Double(song.id.unicodeScalars.reduce(0) { ($0 &* 31 &+ Int($1.value)) & 0xFFFF } % 360) / 360
        return LinearGradient(
            colors: [Color(hue: hue, saturation: 0.6, brightness: 0.75), Color(hue: (hue + 0.12).truncatingRemainder(dividingBy: 1), saturation: 0.7, brightness: 0.4)],
            startPoint: .topLeading, endPoint: .bottomTrailing)
        .overlay {
            Image(systemName: "music.note")
                .foregroundStyle(.white.opacity(0.7))
        }
    }
}
