import SwiftUI
import TonwerkKit

/// Onkel Bummel's settings (`OnkelBummelEndpoints`, the web page's `OnkelBummelSettings.vue`): the server sends a
/// message there when a song batch or another long job finishes. Address, publish token, topic and Tonwerk's own
/// address for the links go into the server's env file; saving sends a test message.
struct OnkelBummelSettingsSection: View {
    @Environment(AppModel.self) private var model
    @State private var status: OnkelBummelSettingsStatus?
    @State private var enabled = true
    @State private var url = ""
    @State private var token = ""
    @State private var topic = ""
    @State private var link = ""
    @State private var working = false
    @State private var message: String?
    @State private var failed = false

    var body: some View {
        Section {
            if let status {
                if !status.editable {
                    Text("Auf dem Server setzen Umgebungsvariablen die Werte; hier lässt sich nichts ändern.")
                        .foregroundStyle(.secondary)
                }
                Toggle("Nachrichten schicken", isOn: $enabled)
                    .disabled(!status.editable)
                TextField("Adresse", text: $url, prompt: Text(verbatim: "https://onkel-bummel.tail6105c0.ts.net"))
                    .autocorrectionDisabled()
                    #if os(iOS)
                    .textInputAutocapitalization(.never)
                    .keyboardType(.URL)
                    #endif
                    .disabled(!status.editable)
                SecureField("Publish-Token", text: $token, prompt: Text(status.tokenSet ? String(localized: "gesetzt, leer lassen zum Behalten") : "ob_…"))
                    .disabled(!status.editable)
                TextField("Topic", text: $topic, prompt: Text(verbatim: "tonwerk"))
                    .autocorrectionDisabled()
                    #if os(iOS)
                    .textInputAutocapitalization(.never)
                    #endif
                    .disabled(!status.editable)
                TextField("Adresse von Tonwerk-AI für Links", text: $link, prompt: Text(verbatim: "https://oi-o-mat.tail6105c0.ts.net:8443"))
                    .autocorrectionDisabled()
                    #if os(iOS)
                    .textInputAutocapitalization(.never)
                    .keyboardType(.URL)
                    #endif
                    .disabled(!status.editable)
                Button {
                    save()
                } label: {
                    if working {
                        ProgressView().controlSize(.small)
                    } else {
                        Text("Speichern und Test senden")
                    }
                }
                .disabled(!status.editable || working)
                if let message {
                    Text(message)
                        .foregroundStyle(failed ? .red : .secondary)
                }
            } else {
                Text("Der Server kennt diese Einstellung noch nicht.")
                    .foregroundStyle(.secondary)
            }
        } header: {
            Text(verbatim: "Onkel Bummel")
        } footer: {
            Text("Wenn ein Song-Stapel oder ein anderer langer Auftrag fertig ist, kommt eine Nachricht, ein Stapel als eine. Den Token (ob_…) in Onkel Bummel unter Verwaltung → Publish-Tokens mit dem Recht für das Topic anlegen. Ohne Adresse ist es aus.")
        }
        .task(id: model.server) { await load() }
    }

    private func load() async {
        guard let server = model.server,
              let (data, response) = try? await URLSession.shared.data(from: server.appending(path: "api/onkel-bummel")),
              (response as? HTTPURLResponse)?.statusCode == 200,
              let loaded = try? JSONDecoder().decode(OnkelBummelSettingsStatus.self, from: data)
        else {
            status = nil
            return
        }
        show(loaded)
    }

    private func show(_ loaded: OnkelBummelSettingsStatus) {
        status = loaded
        enabled = loaded.enabled
        url = loaded.url ?? ""
        topic = loaded.topic
        link = loaded.link ?? ""
    }

    private func save() {
        guard let server = model.server else { return }
        working = true
        message = nil
        Task {
            defer { working = false }
            do {
                var request = URLRequest(url: server.appending(path: "api/onkel-bummel"))
                request.httpMethod = "PUT"
                request.setValue("application/json", forHTTPHeaderField: "Content-Type")
                func orNull(_ text: String) -> Any {
                    let trimmed = text.trimmingCharacters(in: .whitespaces)
                    return trimmed.isEmpty ? NSNull() : trimmed
                }
                request.httpBody = try JSONSerialization.data(withJSONObject: [
                    "url": url.trimmingCharacters(in: .whitespaces),
                    "token": orNull(token),
                    "topic": orNull(topic),
                    "link": orNull(link),
                    "enabled": enabled,
                ])
                let (answer, response) = try await URLSession.shared.data(for: request)
                guard (response as? HTTPURLResponse)?.statusCode == 200 else {
                    throw ServerError(problem: ServerError.text(of: answer))
                }
                let saved = try JSONDecoder().decode(OnkelBummelSettingsStatus.self, from: answer)
                show(saved)
                token = ""
                guard saved.enabled else {
                    (message, failed) = (String(localized: "Gespeichert. Die Nachrichten sind ausgeschaltet."), false)
                    return
                }
                var test = URLRequest(url: server.appending(path: "api/onkel-bummel/test"))
                test.httpMethod = "POST"
                let (found, _) = try await URLSession.shared.data(for: test)
                (message, failed) = Self.text(of: try JSONDecoder().decode(OnkelBummelCheckResult.self, from: found))
            } catch {
                (message, failed) = (error.localizedDescription, true)
            }
        }
    }

    /// What the test found, as the web page says it.
    static func text(of check: OnkelBummelCheckResult) -> (String, Bool) {
        let detail = check.detail ?? ""
        switch check.state {
        case "ok":
            return (String(localized: "Gespeichert. Die Testnachricht ist bei Onkel Bummel."), false)
        case "notConfigured":
            return (String(localized: "Gespeichert. Ohne Adresse und Token gehen keine Nachrichten raus."), false)
        case "token":
            return (String(localized: "Onkel Bummel kennt den Token nicht."), true)
        case "topic":
            return (String(localized: "Der Token darf nicht an dieses Topic senden. Lege ihn mit dem Topic (oder *) neu an."), true)
        case "unreachable":
            return (String(localized: "Gespeichert, aber Onkel Bummel ist nicht erreichbar: \(detail)"), true)
        default:
            return (String(localized: "Gespeichert, aber Onkel Bummel antwortete unerwartet: \(detail)"), true)
        }
    }
}

/// `GET api/onkel-bummel`, decoded apart from the generated type so a server from before it reads as none.
struct OnkelBummelSettingsStatus: Decodable {
    let configured: Bool
    let enabled: Bool
    let url: String?
    let tokenSet: Bool
    let topic: String
    let link: String?
    let editable: Bool
}

struct OnkelBummelCheckResult: Decodable {
    let state: String
    let detail: String?
}
