#if os(macOS) && canImport(Sparkle)
import Sparkle
import SwiftUI

/// Updates for the Mac app downloaded from GitHub (release-mac.yml): Sparkle reads the newest release's appcast,
/// shows what is new and, on "Installieren", downloads the DMG, checks its EdDSA signature against `SUPublicEDKey`,
/// replaces the app and starts it again. In the sandbox it installs through its own launcher service
/// (`SUEnableInstallerLauncherService`, the mach-lookup exception in Tonwerk.entitlements); the app's network
/// entitlement lets it download itself. Builds from Xcode carry no key (release-mac.yml puts it in), so there the
/// updater stays off rather than complaining about a missing key.
@MainActor
@Observable
public final class AppUpdates {
    public static let shared = AppUpdates()

    @ObservationIgnored private let controller: SPUStandardUpdaterController?
    @ObservationIgnored private var observation: NSKeyValueObservation?
    /// Mirrors `SPUUpdater.canCheckForUpdates`, false while a check runs.
    public private(set) var canCheck = false

    public var available: Bool { controller != nil }

    /// Whether Sparkle looks once a day by itself; the user switches it in the settings, Sparkle keeps it.
    public var checksAutomatically: Bool = false {
        didSet { controller?.updater.automaticallyChecksForUpdates = checksAutomatically }
    }

    private init() {
        let key = Bundle.main.object(forInfoDictionaryKey: "SUPublicEDKey") as? String ?? ""
        guard !key.isEmpty else {
            controller = nil
            return
        }
        let controller = SPUStandardUpdaterController(startingUpdater: true, updaterDelegate: nil, userDriverDelegate: nil)
        self.controller = controller
        canCheck = controller.updater.canCheckForUpdates
        checksAutomatically = controller.updater.automaticallyChecksForUpdates
        observation = controller.updater.observe(\.canCheckForUpdates, options: [.new]) { [weak self] updater, _ in
            let value = updater.canCheckForUpdates
            Task { @MainActor in self?.canCheck = value }
        }
    }

    public func check() {
        controller?.checkForUpdates(nil)
    }
}

/// "Nach Updates suchen …" in the app menu, where Mac apps keep it.
struct UpdateCommands: Commands {
    let updates: AppUpdates

    var body: some Commands {
        CommandGroup(after: .appInfo) {
            if updates.available {
                Button("Nach Updates suchen …") { updates.check() }
                    .disabled(!updates.canCheck)
            }
        }
    }
}

/// The settings' section: check now, and whether to look by itself.
struct UpdateSection: View {
    var body: some View {
        @Bindable var updates = AppUpdates.shared
        if updates.available {
            Section("Updates") {
                Toggle("Automatisch nach Updates suchen", isOn: $updates.checksAutomatically)
                Button("Jetzt nach Updates suchen") { updates.check() }
                    .disabled(!updates.canCheck)
            }
        }
    }
}
#endif
