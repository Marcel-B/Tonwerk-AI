import SwiftUI

/// First start: the server's address. Nothing is built in, the server is reached through Tailscale.
struct SetupView: View {
    @Environment(AppModel.self) private var model
    @State private var address = ""
    @State private var error: String?
    @State private var connecting = false

    var body: some View {
        VStack(spacing: 20) {
            Image(systemName: "waveform")
                .font(.system(size: 48))
                .foregroundStyle(.tint)
            Text("Tonwerk-AI")
                .font(.largeTitle.bold())
            Text("Die Adresse deines Tonwerk-Servers, wie du sie im Browser öffnest.")
                .multilineTextAlignment(.center)
                .foregroundStyle(.secondary)
            TextField("mac-mini.tailnet.ts.net:8443", text: $address)
                .textFieldStyle(.roundedBorder)
                .autocorrectionDisabled()
                #if os(iOS)
                .textInputAutocapitalization(.never)
                .keyboardType(.URL)
                #endif
                .onSubmit(connect)
                .frame(maxWidth: 420)
            if let error {
                Text(error)
                    .foregroundStyle(.red)
                    .multilineTextAlignment(.center)
            }
            Button(action: connect) {
                if connecting {
                    ProgressView().controlSize(.small)
                } else {
                    Text("Verbinden")
                }
            }
            .buttonStyle(.borderedProminent)
            .keyboardShortcut(.defaultAction)
            .disabled(address.isEmpty || connecting)
            Text("Ohne Tailscale auf diesem Gerät ist der Server nicht erreichbar.")
                .font(.footnote)
                .foregroundStyle(.secondary)
            Divider().frame(maxWidth: 420)
            VStack(spacing: 6) {
                Button("Ohne Server ausprobieren") { model.connectDemo() }
                    .buttonStyle(.bordered)
                Text("Eine Beispielbibliothek mit drei Songs. Neue Songs erzeugt erst dein eigener Tonwerk-Server.")
                    .font(.footnote)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                    .frame(maxWidth: 420)
            }
        }
        .padding(32)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private func connect() {
        guard !address.isEmpty, !connecting else { return }
        connecting = true
        error = nil
        Task {
            do {
                try await model.connect(to: address)
            } catch {
                self.error = String(localized: "Keine Verbindung: \(error.localizedDescription)")
            }
            connecting = false
        }
    }
}

/// The server in use, and the way to another one.
struct SettingsView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        Form {
            Section("Server") {
                if model.isDemo {
                    LabeledContent("Adresse", value: String(localized: "Demo ohne Server"))
                    Button("Eigenen Server verbinden") { model.disconnect() }
                } else {
                    LabeledContent("Adresse", value: model.server?.absoluteString ?? "–")
                    LabeledContent("Verbindung") { ConnectionLabel() }
                    Button("Anderen Server verwenden", role: .destructive) { model.disconnect() }
                }
            }
            if !model.isDemo {
                TonregalSettingsSection()
            }
            #if os(macOS) && canImport(Sparkle)
            UpdateSection()
            #endif
            Section {
                LabeledContent("Version", value: Bundle.main.version)
            }
            AboutSection()
        }
        .formStyle(.grouped)
    }
}

/// What the app must say about itself now that it is handed out outside a store: who made it, the privacy policy,
/// what is AI-made and the licences (also of the packages inside it, NOTICE).
struct AboutSection: View {
    var body: some View {
        Section("Über Tonwerk-AI") {
            Link("Impressum", destination: URL(string: "https://tonwerk.studio/impressum.html")!)
            Link("Datenschutz", destination: URL(string: "https://tonwerk.studio/ai/datenschutz.html")!)
            Link("KI und Verantwortung", destination: URL(string: "https://tonwerk.studio/ai/ki.html")!)
            Link("Lizenzen", destination: URL(string: "https://github.com/Marcel-B/Tonwerk-AI/blob/main/NOTICE")!)
        }
    }
}

struct ConnectionLabel: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        switch model.connection {
        case .none:
            Text("–")
        case .connecting:
            Label("Verbinde …", systemImage: "circle.dotted")
                .foregroundStyle(.secondary)
        case .live:
            Label("Verbunden", systemImage: "circle.fill")
                .foregroundStyle(.green)
        case .lost(let reason):
            Label(reason, systemImage: "exclamationmark.circle")
                .foregroundStyle(.orange)
        }
    }
}

extension Bundle {
    var version: String {
        let short = infoDictionary?["CFBundleShortVersionString"] as? String ?? "?"
        let build = infoDictionary?["CFBundleVersion"] as? String ?? "?"
        return "\(short) (\(build))"
    }
}
