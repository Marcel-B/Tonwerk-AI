import SwiftUI
import TonwerkKit

/// The log page (`LogPage.vue`): what the server, the worker and the updater reported, newest first, and the
/// report for Claude, so a problem can be looked into without SSH on the Mac.
struct LogView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        Group {
            if let session = model.logPage {
                LogSessionView(session: session)
            } else {
                ProgressView().onAppear { model.openLog() }
            }
        }
        .navigationTitle("Protokoll")
    }
}

private struct LogSessionView: View {
    @Bindable var session: LogSession

    var body: some View {
        List {
            Section {
                header
                filters
            }
            if let problem = session.problem {
                Label(problem, systemImage: "exclamationmark.triangle").foregroundStyle(.red)
            } else if session.loaded && !session.loading && session.lines.isEmpty {
                Text("Keine passenden Zeilen.").foregroundStyle(.secondary)
            }
            Section {
                ForEach(session.lines) { line in
                    LogRow(line: line, expanded: session.expanded.contains(line.id)) {
                        session.toggle(line)
                    }
                }
                if session.more {
                    Button {
                        Task { await session.load(older: true) }
                    } label: {
                        Text("Ältere laden").frame(maxWidth: .infinity)
                    }
                    .disabled(session.loading)
                }
            }
        }
        .task { await session.load() }
        .refreshable { await session.load() }
        .toolbar {
            ToolbarItem {
                Button {
                    Task { await session.load() }
                } label: {
                    Label("Aktualisieren", systemImage: "arrow.clockwise")
                }
                .disabled(session.loading)
            }
        }
    }

    private var header: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Was Server, YuE2-Worker, Stimmen, Sprachlabor und Updater gemeldet haben, das Neueste oben. Der Server hebt es 14 Tage auf.")
                .font(.caption)
                .foregroundStyle(.secondary)
            Button {
                Task { await session.copyReport() }
            } label: {
                if session.copying {
                    ProgressView().controlSize(.small)
                } else {
                    Label("Für Claude kopieren", systemImage: "doc.on.doc")
                }
            }
            .buttonStyle(.borderedProminent)
            if session.copied {
                Label("Kopiert: Version, Engines und die Warnungen und Fehler der letzten 24 Stunden. Einfach in den Chat einfügen.", systemImage: "checkmark.circle")
                    .font(.caption)
                    .foregroundStyle(.green)
            }
            if let report = session.report {
                Text("Kopieren ging nicht. Hier ist der Bericht zum Markieren und Kopieren:").font(.caption)
                ScrollView {
                    Text(report)
                        .font(.system(.caption, design: .monospaced))
                        .textSelection(.enabled)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
                .frame(maxHeight: 220)
            }
        }
        .padding(.vertical, 4)
    }

    private var filters: some View {
        Group {
            Picker("Quelle", selection: $session.source) {
                Text("Alle Quellen").tag(String?.none)
                ForEach(LogSession.sources, id: \.id) { source in
                    Text(source.name).tag(String?.some(source.id))
                }
            }
            Picker("Stufe", selection: $session.level) {
                ForEach(LogSession.Level.allCases) { level in
                    Text(level.name).tag(level)
                }
            }
            TextField("Suchen", text: $session.text)
                .autocorrectionDisabled()
        }
    }
}

private struct LogRow: View {
    let line: LogLine
    let expanded: Bool
    let toggle: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 6) {
                Text(line.date?.formatted(date: .numeric, time: .standard) ?? line.time)
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(.secondary)
                tag(LogSession.sourceName(line.source), color: .secondary)
                if line.level != "info" {
                    tag(line.level, color: line.level == "error" ? .red : line.level == "warning" ? .orange : .secondary)
                }
            }
            Text(expanded ? line.fullText : line.firstLine)
                .font(.system(.footnote, design: .monospaced))
                .textSelection(.enabled)
                .fixedSize(horizontal: false, vertical: true)
            if line.hasDetails {
                Button(action: toggle) {
                    Label("Details", systemImage: expanded ? "chevron.up" : "chevron.down")
                        .font(.caption)
                }
                .buttonStyle(.borderless)
            }
        }
        .padding(.vertical, 2)
    }

    private func tag(_ text: String, color: Color) -> some View {
        Text(text)
            .font(.caption2.weight(.semibold))
            .foregroundStyle(color)
            .padding(.horizontal, 6)
            .padding(.vertical, 1)
            .background(color.opacity(0.12), in: Capsule())
    }
}
