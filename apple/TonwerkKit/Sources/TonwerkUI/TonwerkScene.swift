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
        .commands { PlayerCommands(player: model.player) }
        #endif

        #if os(macOS)
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

    var body: some Commands {
        CommandMenu("Wiedergabe") {
            Button(player.isPlaying ? "Pause" : "Abspielen") { player.toggle() }
                .disabled(player.current == nil)
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
