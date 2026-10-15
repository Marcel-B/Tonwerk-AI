import SwiftUI

/// The player under every page, like the web page's `PlayerBar.vue`: cover, title, position and the buttons.
struct PlayerBar: View {
    @Environment(AppModel.self) private var model
    @Environment(Player.self) private var player
    @State private var scrubbing: Double?

    var body: some View {
        if let song = player.current {
            VStack(spacing: 6) {
                HStack(spacing: 12) {
                    CoverView(song: song)
                        .frame(width: 40, height: 40)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(song.displayTitle)
                            .font(.callout.weight(.semibold))
                            .lineLimit(1)
                        Text(song.style)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            .lineLimit(1)
                    }
                    Spacer(minLength: 8)
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
            .padding(.vertical, 8)
            .background(.bar)
            .overlay(alignment: .top) { Divider() }
        }
    }

    private var controls: some View {
        HStack(spacing: 16) {
            Button { player.previous() } label: {
                Image(systemName: "backward.fill")
            }
            Button { player.toggle() } label: {
                Image(systemName: player.isPlaying ? "pause.fill" : "play.fill")
                    .font(.title2)
                    .frame(width: 28)
            }
            Button { player.next() } label: {
                Image(systemName: "forward.fill")
            }
            .disabled(!player.hasNext)
        }
        .buttonStyle(.plain)
        .labelStyle(.iconOnly)
    }
}
