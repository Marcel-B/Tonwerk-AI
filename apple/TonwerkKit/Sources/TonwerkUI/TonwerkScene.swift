import SwiftUI

/// The whole app, so the Xcode project's entry point is one line: `var body: some Scene { TonwerkScene() }`.
public struct TonwerkScene: Scene {
    @State private var model = AppModel()

    public init() {}

    public var body: some Scene {
        WindowGroup {
            RootView()
                .tint(Theme.accent)
                .environment(model)
                .environment(model.player)
        }
        #if os(macOS)
        .defaultSize(width: 1000, height: 700)
        .commands {
            AboutCommands()
            PlayerCommands(player: model.player, model: model)
            #if canImport(Sparkle)
            UpdateCommands(updates: AppUpdates.shared)
            #endif
        }
        #endif

        #if os(macOS)
        Window("Über Tonwerk-AI", id: AboutWindow.id) {
            AboutView()
                .environment(model.player)
        }
        .windowStyle(.hiddenTitleBar)
        .windowResizability(.contentSize)
        .defaultPosition(.center)

        Settings {
            SettingsView()
                .environment(model)
                .frame(width: 460)
                .padding()
        }
        #endif
    }
}

/// The address once, then the app.
struct RootView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        if model.server == nil {
            SetupView()
        } else {
            MainView()
        }
    }
}

#if os(macOS)
/// ⌘→ and ⌘← skip, as in Music. Play and pause stay on the media keys: a space without ⌘ would be taken from the
/// search field.
struct PlayerCommands: Commands {
    let player: Player
    let model: AppModel

    var body: some Commands {
        CommandMenu("Wiedergabe") {
            Button(player.isPlaying ? "Pause" : "Abspielen") { player.toggle() }
                .disabled(player.current == nil)
            // A way out that never depends on the page answering: the menu bar is AppKit's and works while the
            // window's buttons do not. Never disabled, since a stale state could hide it just when it is needed.
            Button("Logic-Seite anhalten") { model.logic?.player.pause() }
                .keyboardShortcut(".", modifiers: .command)
            Button("Nächster Song") { player.next() }
                .keyboardShortcut(.rightArrow, modifiers: .command)
                .disabled(!player.hasNext)
            Button("Voriger Song") { player.previous() }
                .keyboardShortcut(.leftArrow, modifiers: .command)
                .disabled(player.current == nil)
        }
    }
}
#endif
