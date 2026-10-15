import SwiftUI

/// Tonwerk's look in one place, so every page shares it: the web page's accent (Aura's emerald, the colour of the
/// app's icon), cards on a material, and the cover blurred behind what plays.
enum Theme {
    /// Emerald 500 in light mode, 400 in dark mode, as PrimeVue's Aura preset uses them on the web page.
    static let accent = Color(light: (16, 185, 129), dark: (52, 211, 153))

    static let cardRadius: CGFloat = 14
}

extension Color {
    /// A colour for each appearance; SwiftUI has no initializer for that without an asset catalog in the package.
    init(light: (Int, Int, Int), dark: (Int, Int, Int)) {
        func color(_ rgb: (Int, Int, Int)) -> (Double, Double, Double) {
            (Double(rgb.0) / 255, Double(rgb.1) / 255, Double(rgb.2) / 255)
        }
        let (lr, lg, lb) = color(light)
        let (dr, dg, db) = color(dark)
        #if os(macOS)
        self.init(nsColor: NSColor(name: nil) { appearance in
            appearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua
                ? NSColor(srgbRed: dr, green: dg, blue: db, alpha: 1)
                : NSColor(srgbRed: lr, green: lg, blue: lb, alpha: 1)
        })
        #else
        self.init(uiColor: UIColor { traits in
            traits.userInterfaceStyle == .dark
                ? UIColor(red: dr, green: dg, blue: db, alpha: 1)
                : UIColor(red: lr, green: lg, blue: lb, alpha: 1)
        })
        #endif
    }
}

/// A card: a rounded material with a hairline, like the web page's `Card`.
struct CardStyle: ViewModifier {
    var padding: CGFloat = 14

    func body(content: Content) -> some View {
        content
            .padding(padding)
            .background(.background.secondary, in: RoundedRectangle(cornerRadius: Theme.cardRadius, style: .continuous))
            .overlay {
                RoundedRectangle(cornerRadius: Theme.cardRadius, style: .continuous)
                    .strokeBorder(Color.primary.opacity(0.08), lineWidth: 1)
            }
    }
}

extension View {
    func card(padding: CGFloat = 14) -> some View {
        modifier(CardStyle(padding: padding))
    }
}

/// The round play button of the player and the detail panel: filled with the accent, the symbol changing in place.
struct PlayButtonStyle: ButtonStyle {
    var size: CGFloat = 36

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.system(size: size * 0.42, weight: .semibold))
            .foregroundStyle(.white)
            .frame(width: size, height: size)
            .background(Theme.accent, in: Circle())
            .shadow(color: Theme.accent.opacity(0.35), radius: configuration.isPressed ? 2 : 6, y: 2)
            .scaleEffect(configuration.isPressed ? 0.92 : 1)
            .animation(.spring(duration: 0.25), value: configuration.isPressed)
    }
}

/// The cover blown up and blurred behind a material, the native counterpart of the web player's blurred backdrop.
struct CoverBackdrop: View {
    let song: Song
    var opacity: Double = 0.55

    var body: some View {
        // The square cover filled into a wide bar is far taller than it; laid over a clear view, it takes that view's
        // frame, so clipping cuts it there. Clipping the cover itself cut nothing, and it covered the page above.
        Color.clear
            .overlay {
                CoverView(song: song)
                    .scaledToFill()
                    .blur(radius: 40, opaque: true)
                    .opacity(opacity)
            }
            .clipped()
            .allowsHitTesting(false)
            .accessibilityHidden(true)
    }
}

/// Whether a song plays now: bars that move while it plays and stand still in pause.
struct PlayingIndicator: View {
    let playing: Bool

    var body: some View {
        Image(systemName: "waveform")
            .symbolEffect(.variableColor.iterative.dimInactiveLayers, isActive: playing)
            .accessibilityLabel(playing ? "Spielt" : "Pausiert")
    }
}
