import Foundation
import Observation
import TonwerkKit
#if canImport(UIKit)
import UIKit
#elseif canImport(AppKit)
import AppKit
#endif

/// One line of the collected log. Decoded by hand rather than as the generated `LogLine`, so the time stays the
/// server's own text: the next page asks for lines `before` it, and a date rounded to milliseconds would repeat or
/// skip lines of the same millisecond.
struct LogLine: Decodable, Equatable, Identifiable {
    let time: String
    let source: String
    let level: String
    let message: String
    let exception: String?

    /// The time and the place in the page together, since lines of one moment share a time.
    var id: String { "\(time)|\(source)|\(message.prefix(80))" }

    var date: Date? { try? TonwerkDates().decode(time) }

    /// A message over several lines, or with an exception, shows its first line until opened.
    var hasDetails: Bool { message.contains("\n") || exception != nil }

    var firstLine: String { message.split(separator: "\n", maxSplits: 1).first.map(String.init) ?? "" }

    var fullText: String { exception.map { "\(message)\n\($0)" } ?? message }
}

struct LogPage: Decodable {
    let entries: [LogLine]
    let more: Bool
}

/// The log page's state (`LogPage.vue`): the server's messages, the worker's output and the updater's log, filtered
/// by source, level and text, newest first; and the report to paste into a chat with Claude.
@MainActor
@Observable
final class LogSession {
    /// The sources as `LogSources.All` lists them, with the web page's names.
    nonisolated static let sources: [(id: String, name: String)] = [
        ("server", String(localized: "Server")), ("worker", String(localized: "YuE2-Worker")),
        ("queue", String(localized: "Warteschlange")), ("lyrics", String(localized: "Songtexte")),
        ("voices", String(localized: "Stimmen und Stems")), ("speech", String(localized: "Sprachlabor")),
        ("images", String(localized: "Cover")), ("ace", "ACE-Step"), ("video", String(localized: "Videos")),
        ("export", String(localized: "Export und Streaming")), ("logic", "Logic"), ("backup", "Backup"),
        ("push", String(localized: "Benachrichtigungen")), ("update", String(localized: "Updates")),
    ]

    enum Level: String, CaseIterable, Identifiable {
        case all
        case warning
        case error

        var id: String { rawValue }

        var name: String {
            switch self {
            case .all: String(localized: "Alles")
            case .warning: String(localized: "Warnungen und Fehler")
            case .error: String(localized: "Nur Fehler")
            }
        }
    }

    nonisolated static func sourceName(_ id: String) -> String {
        sources.first { $0.id == id }?.name ?? id
    }

    /// Nil for every source.
    var source: String? {
        didSet { if source != oldValue { reload() } }
    }
    /// Warnings and errors first: what is looked for here is nearly always a problem.
    var level = Level.warning {
        didSet { if level != oldValue { reload() } }
    }
    var text = "" {
        didSet {
            guard text != oldValue else { return }
            typing?.cancel()
            typing = Task { [weak self] in
                try? await Task.sleep(for: .milliseconds(300))
                guard !Task.isCancelled else { return }
                await self?.load()
            }
        }
    }

    private(set) var lines: [LogLine] = []
    private(set) var more = false
    private(set) var loading = false
    private(set) var loaded = false
    var problem: String?
    var expanded: Set<String> = []

    private(set) var copying = false
    private(set) var copied = false
    /// The report, shown to copy by hand when the clipboard refused it.
    var report: String?

    private unowned let model: AppModel
    @ObservationIgnored private var typing: Task<Void, Never>?
    /// Counts loads, so an answer to an older filter does not overwrite a newer one.
    @ObservationIgnored private var generation = 0

    init(model: AppModel) {
        self.model = model
    }

    private func reload() {
        Task { await load() }
    }

    func load(older: Bool = false) async {
        generation += 1
        let mine = generation
        loading = true
        defer { if mine == generation { loading = false } }
        do {
            let page = try await model.logs(
                source: source, level: level == .all ? nil : level.rawValue, text: text,
                before: older ? lines.last?.time : nil)
            guard mine == generation else { return }
            lines = older ? lines + page.entries : page.entries
            more = page.more
            problem = nil
            if !older {
                expanded = []
            }
        } catch {
            if mine == generation {
                problem = error.localizedDescription
            }
        }
        loaded = true
    }

    func toggle(_ line: LogLine) {
        if expanded.remove(line.id) == nil {
            expanded.insert(line.id)
        }
    }

    /// Version, engines and the last day's warnings and errors, for a chat; the clipboard takes it at once, since
    /// a native app (unlike Safari) may write it after the request.
    func copyReport() async {
        copying = true
        copied = false
        report = nil
        defer { copying = false }
        do {
            let text = try await model.logReport()
            if Clipboard.copy(text) {
                copied = true
                Task { [weak self] in
                    try? await Task.sleep(for: .seconds(6))
                    self?.copied = false
                }
            } else {
                report = text
            }
        } catch {
            problem = error.localizedDescription
        }
    }
}

enum Clipboard {
    @MainActor
    static func copy(_ text: String) -> Bool {
        #if canImport(UIKit)
        UIPasteboard.general.string = text
        return true
        #elseif canImport(AppKit)
        NSPasteboard.general.clearContents()
        return NSPasteboard.general.setString(text, forType: .string)
        #else
        return false
        #endif
    }
}

extension AppModel {
    func openLog() {
        if logPage == nil {
            logPage = LogSession(model: self)
        }
        page = .log
    }

    /// The query as `GET api/logs` takes it; empty filters are left out.
    nonisolated static func logQuery(source: String?, level: String?, text: String, before: String?) -> [URLQueryItem] {
        var items: [URLQueryItem] = []
        if let source { items.append(URLQueryItem(name: "source", value: source)) }
        if let level { items.append(URLQueryItem(name: "level", value: level)) }
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        if !trimmed.isEmpty { items.append(URLQueryItem(name: "q", value: trimmed)) }
        if let before { items.append(URLQueryItem(name: "before", value: before)) }
        return items
    }

    func logs(source: String?, level: String?, text: String, before: String?) async throws -> LogPage {
        let url = try address("api/logs")
            .appending(queryItems: Self.logQuery(source: source, level: level, text: text, before: before))
        let (data, _) = try await call("GET", url)
        return try JSONDecoder().decode(LogPage.self, from: data)
    }

    func logReport() async throws -> String {
        let (data, _) = try await call("GET", address("api/logs/report"))
        return String(decoding: data, as: UTF8.self)
    }
}
