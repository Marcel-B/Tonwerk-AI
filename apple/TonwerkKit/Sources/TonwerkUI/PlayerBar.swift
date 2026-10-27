import SwiftUI

/// The player under every page, like the web page's `PlayerBar.vue`: cover, title, position and the buttons, on the
/// cover blurred behind a material.
struct PlayerBar: View {
    @Environment(AppModel.self) private var model
    @Environment(Player.self) private var player
    @State private var scrubbing: Double?
    @State private var expanded = false

    var body: some View {
        if let song = player.current {
            VStack(spacing: 6) {
                HStack(spacing: 12) {
                    Button {
                        expanded = true
                    } label: {
                        nowPlayingLabel(song)
                    }
                    .buttonStyle(.plain)
                    .help("Player groß öffnen")
                    .contextMenu {
                        Button {
                            model.reveal(song)
                        } label: {
                            Label("In der Bibliothek zeigen", systemImage: "music.note.list")
                        }
                        if model.tonregalReady {
                            Button {
                                model.handingOver = song
                            } label: {
                                Label("In Tonregal übernehmen …", systemImage: "books.vertical")
                            }
                        }
                    }
                    Spacer(minLength: 8)
                    // The small analyzer, as in the web player's bar; a tap opens the large one.
                    SpectrumBars(analyzer: player.analyzer, bars: 10, colour: barColour, active: player.isPlaying)
                        .frame(width: 40, height: 22)
                        .contentShape(Rectangle())
                        .onTapGesture { expanded = true }
                    #if os(macOS)
                    RatingView(rating: model.song(song.id)?.rating ?? song.rating) { stars in
                        Task { await model.rate(song, stars) }
                    }
                    #endif
                    controls
                }
                HStack(spacing: 8) {
                    Text(Song.clock(scrubbing ?? player.elapsed))
                    Slider(
                        value: Binding(
                            get: { scrubbing ?? player.elapsed },
                            set: { scrubbing = $0 }),
                        in: 0...max(player.duration, 1)
                    ) { editing in
                        if !editing, let target = scrubbing {
                            player.seek(to: target)
                            scrubbing = nil
                        }
                    }
                    .controlSize(.mini)
                    Text(Song.clock(player.duration))
                }
                .font(.caption2.monospacedDigit())
                .foregroundStyle(.secondary)
            }
            .padding(.horizontal, 14)
            .padding(.vertical, 10)
            .background {
                ZStack {
                    CoverBackdrop(song: song, opacity: 0.6)
                    Rectangle().fill(.regularMaterial)
                }
                .clipped()
            }
            .overlay(alignment: .top) { Divider() }
            .animation(.easeInOut(duration: 0.3), value: song.id)
            .nowPlaying(isPresented: $expanded)
        }
    }

    /// Cover and title; a tap opens the full-screen player.
    private func nowPlayingLabel(_ song: Song) -> some View {
        HStack(spacing: 12) {
            CoverView(song: song)
                .frame(width: 46, height: 46)
                .shadow(color: .black.opacity(0.3), radius: 6, y: 3)
                .id(song.id)
                .transition(.opacity.combined(with: .scale(scale: 0.9)))
            VStack(alignment: .leading, spacing: 2) {
                Text(song.displayTitle)
                    .font(.callout.weight(.semibold))
                    .lineLimit(1)
                Text(song.style)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
        }
        .contentShape(Rectangle())
    }

    private var barColour: Color {
        Color(cgColor: VideoArt.cgColor(hex: player.coverColour ?? VideoArt.accent))
    }

    private var controls: some View {
        HStack(spacing: 14) {
            Button { player.previous() } label: {
                Image(systemName: "backward.fill")
            }
            .buttonStyle(.plain)
            Button { player.toggle() } label: {
                Image(systemName: player.isPlaying ? "pause.fill" : "play.fill")
                    .contentTransition(.symbolEffect(.replace))
            }
            .buttonStyle(PlayButtonStyle(size: 38))
            Button { player.next() } label: {
                Image(systemName: "forward.fill")
            }
            .buttonStyle(.plain)
            .disabled(!player.hasNext)
        }
        .labelStyle(.iconOnly)
    }
}
