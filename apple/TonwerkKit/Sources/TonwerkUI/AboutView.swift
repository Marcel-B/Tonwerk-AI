#if os(macOS)
import AppKit
import SwiftUI

/// The app menu's "Über Tonwerk-AI": a window of its own instead of AppKit's standard panel (icon, name, version), so
/// it can show what the app is about: sound. Behind the icon an oscilloscope of three drifting waves in the icon's
/// green; while a song plays, the player's own analyzer takes over and the window listens along.
struct AboutView: View {
    @Environment(Player.self) private var player
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        VStack(spacing: 0) {
            stage
                .frame(height: 210)
            VStack(spacing: 6) {
                Text(verbatim: "Tonwerk-AI")
                    .font(.system(size: 26, weight: .bold, design: .rounded))
                Text("Version \(Bundle.main.version)")
                    .font(.callout.monospacedDigit())
                    .foregroundStyle(.secondary)
                Text("Songs, Stimmen und Klänge mit KI, auf dem eigenen Mac.")
                    .font(.callout)
                    .multilineTextAlignment(.center)
                    .padding(.top, 6)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .padding(.horizontal, 28)
            .padding(.top, 4)
            links
                .padding(.top, 18)
            Text(verbatim: "© 2026 Marcel Benders")
                .font(.caption)
                .foregroundStyle(.tertiary)
                .padding(.top, 12)
                .padding(.bottom, 20)
        }
        .frame(width: 380)
        .background(.background)
        .ignoresSafeArea()
    }

    /// The icon on a dark field with the waves or the analyzer behind it.
    private var stage: some View {
        ZStack {
            LinearGradient(
                colors: [Color(red: 0.03, green: 0.10, blue: 0.08), Color(red: 0.01, green: 0.04, blue: 0.04)],
                startPoint: .top, endPoint: .bottom)
            RadialGradient(colors: [Theme.accent.opacity(0.28), .clear], center: .center, startRadius: 10, endRadius: 190)
            ZStack {
                AboutWaves(animated: !reduceMotion)
                    .opacity(player.isPlaying ? 0 : 1)
                SpectrumBars(analyzer: player.analyzer, bars: 48, peaks: true, active: player.isPlaying)
                    .frame(height: 90)
                    .padding(.horizontal, 24)
                    .frame(maxHeight: .infinity, alignment: .bottom)
                    .padding(.bottom, 14)
                    .opacity(player.isPlaying ? 0.85 : 0)
            }
            .animation(.easeInOut(duration: 0.6), value: player.isPlaying)
            AboutIcon(animated: !reduceMotion)
            if player.isPlaying, let song = player.current {
                Label {
                    Text(verbatim: song.displayTitle)
                } icon: {
                    Image(systemName: "waveform")
                        .symbolEffect(.variableColor.iterative, isActive: !reduceMotion)
                }
                .font(.caption.weight(.medium))
                .foregroundStyle(.white.opacity(0.8))
                .lineLimit(1)
                .padding(.horizontal, 10)
                .padding(.vertical, 4)
                .background(.black.opacity(0.35), in: Capsule())
                .frame(maxHeight: .infinity, alignment: .top)
                .padding(.top, 14)
                .padding(.horizontal, 60)
                .transition(.opacity)
            }
        }
        .clipped()
        .overlay(alignment: .bottom) {
            Rectangle().fill(Theme.accent.opacity(0.35)).frame(height: 1)
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(Text(verbatim: "Tonwerk-AI"))
    }

    private var links: some View {
        HStack(spacing: 14) {
            Link("Website", destination: URL(string: "https://tonwerk.studio/ai/")!)
            Link("Impressum", destination: URL(string: "https://tonwerk.studio/impressum.html")!)
            Link("Datenschutz", destination: URL(string: "https://tonwerk.studio/ai/datenschutz.html")!)
            Link("KI und Verantwortung", destination: URL(string: "https://tonwerk.studio/ai/ki.html")!)
            Link("Lizenzen", destination: URL(string: "https://github.com/Marcel-B/Tonwerk-AI/blob/main/NOTICE")!)
        }
        .font(.caption)
        .tint(Theme.accent)
    }
}

/// The app's icon with a green glow that breathes slowly, as a held note swells.
private struct AboutIcon: View {
    let animated: Bool

    var body: some View {
        TimelineView(.animation(minimumInterval: 1.0 / 30, paused: !animated)) { timeline in
            let t = timeline.date.timeIntervalSinceReferenceDate
            let swell = animated ? (sin(t * 2 * .pi / 4) + 1) / 2 : 0.5
            Image(nsImage: NSApplication.shared.applicationIconImage)
                .resizable()
                .interpolation(.high)
                .frame(width: 112, height: 112)
                .shadow(color: Theme.accent.opacity(0.35 + 0.3 * swell), radius: 14 + 10 * swell)
                .scaleEffect(1 + 0.015 * swell)
        }
    }
}

/// Three sine waves across the field, as on an oscilloscope: each with its own frequency, drifting phase and a swell
/// that rises and falls, faded out at both edges so they seem to come out of the dark. Drawn once more blurred
/// underneath for the glow of a phosphor screen.
private struct AboutWaves: View {
    let animated: Bool

    private struct Layer {
        let cycles: Double
        let speed: Double
        let amplitude: Double
        let width: CGFloat
        let opacity: Double
    }

    private let layers = [
        Layer(cycles: 2.2, speed: 0.9, amplitude: 0.30, width: 2.2, opacity: 0.95),
        Layer(cycles: 3.7, speed: -1.4, amplitude: 0.18, width: 1.4, opacity: 0.55),
        Layer(cycles: 5.9, speed: 2.1, amplitude: 0.10, width: 1.0, opacity: 0.35),
    ]

    var body: some View {
        TimelineView(.animation(minimumInterval: 1.0 / 60, paused: !animated)) { timeline in
            let t = animated ? timeline.date.timeIntervalSinceReferenceDate.truncatingRemainder(dividingBy: 3600) : 0
            Canvas { context, size in
                var paths: [(Path, Layer)] = []
                for (index, layer) in layers.enumerated() {
                    let swell = 0.65 + 0.35 * sin(t * (0.5 + 0.17 * Double(index)) + Double(index) * 1.3)
                    paths.append((wave(layer, in: size, time: t, swell: swell), layer))
                }
                context.drawLayer { glow in
                    glow.addFilter(.blur(radius: 6))
                    for (path, layer) in paths {
                        glow.stroke(path, with: .color(Theme.accent.opacity(layer.opacity)), lineWidth: layer.width * 3)
                    }
                }
                for (path, layer) in paths {
                    context.stroke(
                        path, with: .color(Theme.accent.opacity(layer.opacity)),
                        style: StrokeStyle(lineWidth: layer.width, lineCap: .round, lineJoin: .round))
                }
            }
        }
        .accessibilityHidden(true)
    }

    private func wave(_ layer: Layer, in size: CGSize, time: Double, swell: Double) -> Path {
        var path = Path()
        let middle = size.height / 2
        let steps = max(2, Int(size.width / 2))
        for step in 0...steps {
            let x = Double(step) / Double(steps)
            // A Hann window over the width: silent at the edges, full in the middle.
            let window = 0.5 - 0.5 * cos(2 * .pi * x)
            let phase = 2 * .pi * (layer.cycles * x) + time * layer.speed
            // A slower second partial bends the sine so it reads as a sound rather than a test tone.
            let value = sin(phase) + 0.25 * sin(2 * phase + time * 0.7)
            let y = middle - CGFloat(value * layer.amplitude * swell * window) * size.height
            let point = CGPoint(x: CGFloat(x) * size.width, y: y)
            if step == 0 { path.move(to: point) } else { path.addLine(to: point) }
        }
        return path
    }
}

/// Replaces the app menu's standard "Über Tonwerk-AI" with the window above.
struct AboutCommands: Commands {
    @Environment(\.openWindow) private var openWindow

    var body: some Commands {
        CommandGroup(replacing: .appInfo) {
            Button("Über Tonwerk-AI") { openWindow(id: AboutWindow.id) }
        }
    }
}

enum AboutWindow {
    static let id = "about"
}
#endif
