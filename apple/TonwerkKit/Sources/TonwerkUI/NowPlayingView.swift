import SwiftUI

/// The player over the whole window, the web page's `NowPlaying.vue`: the cover large on its blurred self, the
/// position and buttons, and the lyrics with the sung line lit once the server has timed them.
struct NowPlayingView: View {
    @Environment(AppModel.self) private var model
    @Environment(Player.self) private var player
    @Environment(\.dismiss) private var dismiss
    @AppStorage("nowPlayingLyrics") private var showLyrics = true
    @AppStorage("nowPlayingAnalyzer") private var showAnalyzer = true
    @AppStorage("nowPlayingEffects") private var showEffects = true
    /// The lyrics' performance directions in round brackets; off shows only what is sung, with the section tags.
    @AppStorage("nowPlayingDirections") private var showDirections = false
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var beat = BeatLevels()
    @State private var times: [LineTimes]?
    @State private var scrubbing: Double?
    @State private var sharing: Song?

    var body: some View {
        if let song = player.current {
            content(song)
                // Asked only while the lyrics show, as the web page does: timing an older song costs Whisper a run.
                .task(id: "\(song.id)|\(showLyrics)") { await loadTiming(song) }
        } else {
            Color.clear.onAppear { dismiss() }
        }
    }

    private func content(_ song: Song) -> some View {
        let lyricsShown = showLyrics && !song.lyrics.isEmpty
        return ZStack {
            backdrop(song)
            VStack(spacing: 18) {
                topBar(song)
                if lyricsShown {
                    compactHeader(song)
                    LyricsPanel(lyrics: song.lyrics, directions: showDirections, times: times)
                } else {
                    Spacer(minLength: 0)
                    bigCover(song)
                    Spacer(minLength: 0)
                    titleBlock(song)
                }
                if showAnalyzer {
                    SpectrumBars(analyzer: player.analyzer, bars: 40, peaks: true, colour: colour, active: player.isPlaying)
                        .frame(height: 56)
                        .transition(.opacity)
                }
                progress
                controls
            }
            .padding(.horizontal, 24)
            .padding(.vertical, 16)
            .frame(maxWidth: 640)
            .animation(.easeInOut(duration: 0.3), value: lyricsShown)
            .animation(.easeInOut(duration: 0.25), value: showAnalyzer)
        }
        .foregroundStyle(.white)
        .environment(\.colorScheme, .dark)
        .exportSheet(for: $sharing)
    }

    private func backdrop(_ song: Song) -> some View {
        ZStack {
            Color.black
            CoverBackdrop(song: song, opacity: 1)
            if effectsOn {
                glows
            }
            LinearGradient(colors: [.black.opacity(0.2), .black.opacity(0.65)], startPoint: .top, endPoint: .bottom)
        }
        .ignoresSafeArea()
    }

    private func topBar(_ song: Song) -> some View {
        HStack {
            Button {
                dismiss()
            } label: {
                Image(systemName: "chevron.down")
                    .font(.title3.weight(.semibold))
                    .frame(width: 36, height: 36)
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .keyboardShortcut(.cancelAction)
            .accessibilityLabel("Schließen")
            Spacer()
            Button {
                dismiss()
                model.reveal(song)
            } label: {
                Image(systemName: "music.note.list")
                    .font(.title3)
                    .frame(width: 36, height: 36)
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .help("In der Bibliothek zeigen")
            .accessibilityLabel("In der Bibliothek zeigen")
            Button {
                sharing = song
            } label: {
                Image(systemName: "square.and.arrow.up")
                    .font(.title3)
                    .frame(width: 36, height: 36)
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .accessibilityLabel("Teilen")
            switchButton("chart.bar.fill", on: $showAnalyzer, label: "Analyzer")
            switchButton("sparkles", on: $showEffects, label: "Effekte")
                .help(reduceMotion ? "Das System wünscht weniger Bewegung, die Effekte bleiben still." : "Effekte")
            if showLyrics && ShownLine.hasDirections(song.lyrics) {
                switchButton("theatermasks", on: $showDirections, label: "Regieanweisungen")
                    .help("Regieanweisungen in runden Klammern im Text zeigen")
            }
            if !song.lyrics.isEmpty {
                Button {
                    showLyrics.toggle()
                } label: {
                    Image(systemName: showLyrics ? "quote.bubble.fill" : "quote.bubble")
                        .font(.title3)
                        .frame(width: 36, height: 36)
                        .contentShape(Rectangle())
                        .contentTransition(.symbolEffect(.replace))
                }
                .buttonStyle(.plain)
                .accessibilityLabel(showLyrics ? "Songtext ausblenden" : "Songtext zeigen")
            }
        }
    }

    private func switchButton(_ symbol: String, on: Binding<Bool>, label: String) -> some View {
        Button {
            on.wrappedValue.toggle()
        } label: {
            Image(systemName: symbol)
                .font(.title3)
                .frame(width: 36, height: 36)
                .contentShape(Rectangle())
                .opacity(on.wrappedValue ? 1 : 0.4)
        }
        .buttonStyle(.plain)
        .accessibilityLabel(label)
        .accessibilityValue(on.wrappedValue ? "An" : "Aus")
    }

    private var colour: Color {
        Color(cgColor: VideoArt.cgColor(hex: player.coverColour ?? VideoArt.accent))
    }

    private var effectsOn: Bool { showEffects && !reduceMotion }

    /// Three glows in the cover's colour that swell with bass, mids and highs (`NowPlaying.vue`); only scale and
    /// opacity change per frame.
    private var glows: some View {
        let base = VideoArt.cgColor(hex: player.coverColour ?? VideoArt.accent)
        let tints = [
            Color(cgColor: base).opacity(0.55),
            Self.mix(base, with: (1, 1, 1)).opacity(0.4),
            Self.mix(base, with: (0.545, 0.361, 0.965)).opacity(0.4),
        ]
        let places: [UnitPoint] = [UnitPoint(x: 0.25, y: 0.8), UnitPoint(x: 0.85, y: 0.3), UnitPoint(x: 0.2, y: 0.15)]
        return GeometryReader { geometry in
            let diameter = max(geometry.size.width, geometry.size.height) * 0.8
            TimelineView(.animation(minimumInterval: 1.0 / 60, paused: !player.isPlaying)) { timeline in
                let levels = beat.step(player.analyzer, at: timeline.date)
                ZStack {
                    ForEach(0..<3, id: \.self) { index in
                        Circle()
                            .fill(RadialGradient(
                                stops: [.init(color: tints[index], location: 0), .init(color: .clear, location: 0.65)],
                                center: .center, startRadius: 0, endRadius: diameter / 2))
                            .frame(width: diameter, height: diameter)
                            .scaleEffect(0.6 + levels[index] * 0.9)
                            .opacity(0.1 + levels[index] * 0.9)
                            .position(
                                x: geometry.size.width * places[index].x, y: geometry.size.height * places[index].y)
                    }
                }
            }
        }
        .allowsHitTesting(false)
        .accessibilityHidden(true)
    }

    /// The cover's colour, 60 % of it mixed with another, as the web page's `color-mix` does.
    private static func mix(_ colour: CGColor, with other: (Double, Double, Double)) -> Color {
        let parts = colour.components ?? [0, 0, 0, 1]
        guard parts.count >= 3 else { return Color(cgColor: colour) }
        return Color(
            red: parts[0] * 0.6 + other.0 * 0.4, green: parts[1] * 0.6 + other.1 * 0.4,
            blue: parts[2] * 0.6 + other.2 * 0.4)
    }

    @ViewBuilder
    private func bigCover(_ song: Song) -> some View {
        if effectsOn {
            // The cover beats with the bass.
            TimelineView(.animation(minimumInterval: 1.0 / 60, paused: !player.isPlaying)) { timeline in
                plainCover(song)
                    .scaleEffect(1 + beat.step(player.analyzer, at: timeline.date)[0] * 0.05)
            }
        } else {
            plainCover(song)
        }
    }

    private func plainCover(_ song: Song) -> some View {
        CoverView(song: song)
            .aspectRatio(1, contentMode: .fit)
            .frame(maxWidth: 420)
            .clipShape(RoundedRectangle(cornerRadius: 16, style: .continuous))
            .shadow(color: .black.opacity(0.45), radius: 24, y: 12)
            .scaleEffect(player.isPlaying ? 1 : 0.88)
            .animation(.spring(duration: 0.45, bounce: 0.25), value: player.isPlaying)
            .id(song.id)
            .transition(.opacity.combined(with: .scale(scale: 0.95)))
    }

    private func titleBlock(_ song: Song) -> some View {
        HStack(alignment: .center, spacing: 12) {
            VStack(alignment: .leading, spacing: 4) {
                Text(song.displayTitle)
                    .font(.title2.weight(.bold))
                    .lineLimit(2)
                Text(song.style)
                    .font(.callout)
                    .foregroundStyle(.white.opacity(0.65))
                    .lineLimit(2)
            }
            Spacer(minLength: 8)
            RatingView(rating: model.song(song.id)?.rating ?? song.rating, font: .body) { stars in
                Task { await model.rate(song, stars) }
            }
        }
    }

    private func compactHeader(_ song: Song) -> some View {
        HStack(spacing: 12) {
            CoverView(song: song)
                .frame(width: 56, height: 56)
                .shadow(color: .black.opacity(0.4), radius: 8, y: 4)
            VStack(alignment: .leading, spacing: 2) {
                Text(song.displayTitle)
                    .font(.headline)
                    .lineLimit(1)
                Text(song.style)
                    .font(.caption)
                    .foregroundStyle(.white.opacity(0.65))
                    .lineLimit(1)
            }
            Spacer(minLength: 8)
            RatingView(rating: model.song(song.id)?.rating ?? song.rating) { stars in
                Task { await model.rate(song, stars) }
            }
        }
    }

    private var progress: some View {
        VStack(spacing: 4) {
            Slider(
                value: Binding(get: { scrubbing ?? player.elapsed }, set: { scrubbing = $0 }),
                in: 0...max(player.duration, 1)
            ) { editing in
                if !editing, let target = scrubbing {
                    player.seek(to: target)
                    scrubbing = nil
                }
            }
            .tint(.white)
            HStack {
                Text(Song.clock(scrubbing ?? player.elapsed))
                Spacer()
                Text("-" + Song.clock(max(0, player.duration - (scrubbing ?? player.elapsed))))
            }
            .font(.caption.monospacedDigit())
            .foregroundStyle(.white.opacity(0.65))
        }
    }

    private var controls: some View {
        HStack(spacing: 44) {
            Button { player.previous() } label: {
                Image(systemName: "backward.fill").font(.title)
            }
            .buttonStyle(.plain)
            Button { player.toggle() } label: {
                Image(systemName: player.isPlaying ? "pause.fill" : "play.fill")
                    .contentTransition(.symbolEffect(.replace))
            }
            .buttonStyle(PlayButtonStyle(size: 68))
            .keyboardShortcut(.space, modifiers: [])
            Button { player.next() } label: {
                Image(systemName: "forward.fill").font(.title)
            }
            .buttonStyle(.plain)
            .disabled(!player.hasNext)
            .opacity(player.hasNext ? 1 : 0.4)
        }
        .padding(.bottom, 8)
    }

    /// The song's line times, asked again every ten seconds while the server still listens.
    private func loadTiming(_ song: Song) async {
        times = nil
        guard showLyrics, !song.lyrics.isEmpty else { return }
        var ask = true
        while !Task.isCancelled {
            let answer = await model.timing(of: song, ask: ask)
            ask = false
            if let lines = answer.lines {
                times = lines
                return
            }
            guard answer.waiting else { return }
            try? await Task.sleep(for: .seconds(10))
        }
    }
}

/// The lyrics, line by line; with times the sung line is lit and kept in the middle, and a tap on a line jumps there.
private struct LyricsPanel: View {
    @Environment(Player.self) private var player
    let lyrics: String
    let directions: Bool
    let times: [LineTimes]?

    var body: some View {
        let shown = ShownLine.lines(of: lyrics, directions: directions)
        let active = times.flatMap { LineTimes.line(at: player.elapsed, in: $0) }
        ScrollViewReader { proxy in
            ScrollView {
                VStack(alignment: .leading, spacing: 12) {
                    ForEach(Array(shown.enumerated()), id: \.offset) { index, line in
                        row(line, active: active)
                            .id(index)
                    }
                }
                .padding(.vertical, 48)
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            .scrollIndicators(.hidden)
            .onChange(of: active) { _, new in
                guard let new, let index = shown.firstIndex(where: { $0.timed == new }) else { return }
                withAnimation(.easeInOut(duration: 0.45)) { proxy.scrollTo(index, anchor: .center) }
            }
        }
        .mask {
            LinearGradient(
                stops: [
                    .init(color: .clear, location: 0), .init(color: .black, location: 0.08),
                    .init(color: .black, location: 0.92), .init(color: .clear, location: 1),
                ],
                startPoint: .top, endPoint: .bottom)
        }
    }

    @ViewBuilder
    private func row(_ line: ShownLine, active: Int?) -> some View {
        if line.section {
            Text(line.text)
                .font(.caption.weight(.semibold))
                .foregroundStyle(.white.opacity(0.45))
                .padding(.top, 10)
        } else if line.direction {
            Text(line.text.dropFirst().dropLast())
                .font(.callout.italic())
                .foregroundStyle(.white.opacity(0.55))
        } else if line.text.isEmpty {
            Color.clear.frame(height: 2)
        } else {
            let current = line.timed != nil && line.timed == active
            Text(line.text)
                .font(.title2.weight(.bold))
                .foregroundStyle(.white.opacity(times == nil || current ? 1 : 0.38))
                .scaleEffect(current ? 1.04 : 1, anchor: .leading)
                .animation(.easeOut(duration: 0.3), value: current)
                .frame(maxWidth: .infinity, alignment: .leading)
                .contentShape(Rectangle())
                .onTapGesture { jump(to: line.timed) }
        }
    }

    private func jump(to timed: Int?) {
        guard let timed, let times, times.indices.contains(timed) else { return }
        player.seek(to: times[timed].start)
    }
}

extension View {
    /// Shows the full-screen player: over the whole screen on the iPhone, as a large sheet on the Mac.
    func nowPlaying(isPresented: Binding<Bool>) -> some View {
        #if os(iOS)
        fullScreenCover(isPresented: isPresented) { NowPlayingView() }
        #else
        sheet(isPresented: isPresented) {
            NowPlayingView()
                .frame(minWidth: 480, idealWidth: 560, minHeight: 640, idealHeight: 760)
        }
        #endif
    }
}
