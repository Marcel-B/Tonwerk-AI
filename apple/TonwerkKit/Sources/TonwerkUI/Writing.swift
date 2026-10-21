import Foundation
import Observation
import TonwerkKit

/// The writing page's state (`WritingPage.vue`): conversations with a model in LM Studio for ideas, rhymes,
/// variants and lyrics. The server keeps the conversations; the answer being written comes as `writing` events
/// (``AppModel/liveWriting``), the whole text so far each time.
@MainActor
@Observable
final class WritingSession {
    private(set) var overview: Components.Schemas.WritingOverview?
    private(set) var chat: Components.Schemas.WritingChat?
    private(set) var models: [Components.Schemas.LyricsModel] = []
    private(set) var sending = false
    var problem: String?
    var text = ""
    /// The chosen model, empty for the chat's own or the server's default.
    private(set) var chosenModel: String

    private static let modelKey = "writingModel"
    private unowned let model: AppModel

    init(model: AppModel) {
        self.model = model
        chosenModel = UserDefaults.standard.string(forKey: Self.modelKey) ?? ""
    }

    /// What the picker shows: the chosen model while LM Studio has it, else the chat's, else the default.
    var modelId: String {
        get {
            if models.contains(where: { $0.id == chosenModel }) { return chosenModel }
            if let own = chat?.model, models.contains(where: { $0.id == own }) { return own }
            return overview?.model ?? chosenModel
        }
        set {
            chosenModel = newValue
            UserDefaults.standard.set(newValue, forKey: Self.modelKey)
        }
    }

    /// The answer being written in the open chat.
    var reply: Components.Schemas.WritingReply? {
        guard let reply = model.liveWriting ?? overview?.reply, reply.chatId == chat?.id else { return nil }
        return reply
    }

    /// Whether any chat waits for an answer; one model, one answer at a time.
    var replying: Bool {
        guard let reply = model.liveWriting ?? overview?.reply else { return false }
        return reply.finished != true
    }

    var messages: [Components.Schemas.WritingMessage] { chat?.messages ?? [] }

    func load() async {
        // LM Studio's models only: Claude has no part on this page, and the large ones need a large Mac.
        if let listed = await model.lyricsModels() {
            models = listed.models.filter { $0.cloud != true && $0.needsGb == nil }
        }
        await refresh()
    }

    func refresh() async {
        do {
            overview = try await model.writingOverview()
            if let id = chat?.id ?? overview?.chats.first?.id {
                chat = try await model.writingChat(id)
            }
            problem = nil
        } catch {
            problem = error.localizedDescription
        }
    }

    /// A finished answer is in the chat now; the event carried only its text.
    func replyChanged() {
        guard let reply = model.liveWriting, reply.finished == true, reply.chatId == chat?.id else { return }
        Task { await refresh() }
    }

    func open(_ id: String) async {
        do {
            chat = try await model.writingChat(id)
            problem = nil
        } catch {
            problem = error.localizedDescription
        }
    }

    func newChat() async {
        do {
            chat = try await model.createWritingChat()
            overview = try await model.writingOverview()
            problem = nil
        } catch {
            problem = error.localizedDescription
        }
    }

    func send(_ message: String? = nil) async {
        let message = (message ?? text).trimmingCharacters(in: .whitespacesAndNewlines)
        guard !message.isEmpty, !sending, !replying else { return }
        sending = true
        defer { sending = false }
        do {
            if chat == nil {
                chat = try await model.createWritingChat()
            }
            guard let id = chat?.id else { return }
            let reply = try await model.sendWriting(id, text: message, model: modelId.isEmpty ? nil : modelId)
            model.liveWriting = reply
            if message == text.trimmingCharacters(in: .whitespacesAndNewlines) {
                text = ""
            }
            chat = try await model.writingChat(id)
            problem = nil
        } catch {
            problem = error.localizedDescription
        }
    }

    func retry() async {
        guard let id = chat?.id, !replying else { return }
        do {
            model.liveWriting = try await model.retryWriting(id, model: modelId.isEmpty ? nil : modelId)
            chat = try await model.writingChat(id)
        } catch {
            problem = error.localizedDescription
        }
    }

    func stop() async {
        guard let id = reply?.chatId else { return }
        try? await model.stopWriting(id)
    }

    func rename(_ title: String) async {
        guard let id = chat?.id, !title.trimmingCharacters(in: .whitespaces).isEmpty else { return }
        do {
            chat = try await model.renameWriting(id, title: title)
            overview = try await model.writingOverview()
        } catch {
            problem = error.localizedDescription
        }
    }

    func delete() async {
        guard let id = chat?.id else { return }
        do {
            try await model.deleteWriting(id)
            chat = nil
            await refresh()
        } catch {
            problem = error.localizedDescription
        }
    }

    /// Unloads the kept model, so a song need not wait for it.
    func release() async {
        try? await model.releaseWriting()
        overview = try? await model.writingOverview()
    }

    /// A start for a message, for the chips under the field.
    struct Starter: Identifiable {
        let label: String
        let text: String
        var id: String { label }
    }

    static let starters: [Starter] = [
        Starter(label: String(localized: "Titelideen"), text: String(localized: "Zehn Titelideen für einen Song über ")),
        Starter(label: String(localized: "Reime"), text: String(localized: "Reime und Fast-Reime auf „“, gern mehrsilbig")),
        Starter(label: String(localized: "Varianten"), text: String(localized: "Drei Varianten dieser Zeile: ")),
        Starter(label: String(localized: "Bilder"), text: String(localized: "Konkrete Bilder und Szenen für einen Song über ")),
        Starter(label: String(localized: "Songtext"), text: String(localized: "Schreib daraus einen deutschen Songtext mit [Verse] und [Chorus]: ")),
    ]

    nonisolated static func stageName(_ reply: Components.Schemas.WritingReply) -> String {
        switch reply.stage {
        case "waiting": String(localized: "wartet auf den Speicher")
        case "loading": String(localized: "lädt \(reply.model ?? "das Modell") …")
        case "writing": reply.thinking == true ? String(localized: "denkt nach …") : String(localized: "schreibt …")
        case "stopped": String(localized: "angehalten")
        case "failed": reply.message ?? String(localized: "fehlgeschlagen")
        default: ""
        }
    }
}

extension AppModel {
    func openWriting() {
        if writingPage == nil {
            writingPage = WritingSession(model: self)
        }
        page = .write
    }

    /// Puts a written answer into the create form's lyrics and shows the form.
    func useAsLyrics(_ text: String) {
        form.lyrics = text
        saveForm()
        page = .create
    }

    func writingOverview() async throws -> Components.Schemas.WritingOverview {
        let (data, _) = try await call("GET", address("api/writing"))
        return try decode(Components.Schemas.WritingOverview.self, data)
    }

    func writingChat(_ id: String) async throws -> Components.Schemas.WritingChat {
        let (data, _) = try await call("GET", address("api/writing", id))
        return try decode(Components.Schemas.WritingChat.self, data)
    }

    func createWritingChat() async throws -> Components.Schemas.WritingChat {
        let (data, _) = try await call("POST", address("api/writing"), json: [:], accept: [201])
        return try decode(Components.Schemas.WritingChat.self, data)
    }

    func sendWriting(_ id: String, text: String, model: String?) async throws -> Components.Schemas.WritingReply {
        let body: [String: Any] = ["text": text, "model": model.map { $0 as Any } ?? NSNull()]
        let (data, _) = try await call("POST", address("api/writing", id, "messages"), json: body, accept: [202])
        return try decode(Components.Schemas.WritingReply.self, data)
    }

    func retryWriting(_ id: String, model: String?) async throws -> Components.Schemas.WritingReply {
        let body: [String: Any] = ["model": model.map { $0 as Any } ?? NSNull()]
        let (data, _) = try await call("POST", address("api/writing", id, "retry"), json: body, accept: [202])
        return try decode(Components.Schemas.WritingReply.self, data)
    }

    func stopWriting(_ id: String) async throws {
        _ = try await call("POST", address("api/writing", id, "stop"), accept: [204, 404])
    }

    func renameWriting(_ id: String, title: String) async throws -> Components.Schemas.WritingChat {
        let (data, _) = try await call("PUT", address("api/writing", id), json: ["title": title])
        return try decode(Components.Schemas.WritingChat.self, data)
    }

    func deleteWriting(_ id: String) async throws {
        _ = try await call("DELETE", address("api/writing", id), accept: [204, 404])
    }

    func releaseWriting() async throws {
        _ = try await call("POST", address("api/writing", "release"), accept: [204])
    }
}
