import SwiftUI
import TonwerkKit

/// What the server works on and what waits, from the event stream, like the web page's queue page.
struct QueueView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        Group {
            if model.inProgress.isEmpty && model.queue.isEmpty {
                ContentUnavailableView(
                    "Nichts in Arbeit", systemImage: "hourglass",
                    description: Text("Songs, Songtexte und Transkriptionen erscheinen hier, sobald sie bestellt sind."))
            } else {
                List {
                    if !model.inProgress.isEmpty {
                        Section("In Arbeit") {
                            ForEach(model.inProgress, id: \.id) { state in
                                VStack(alignment: .leading, spacing: 4) {
                                    HStack {
                                        Text(state.title ?? state.run)
                                            .lineLimit(1)
                                        Spacer()
                                        Text(state.stage ?? "")
                                            .font(.caption)
                                            .foregroundStyle(.secondary)
                                    }
                                    ProgressView(value: min(max(state.fraction ?? 0, 0), 1))
                                    if let detail = state.detail, !detail.isEmpty {
                                        Text(detail)
                                            .font(.caption)
                                            .foregroundStyle(.secondary)
                                            .lineLimit(1)
                                    }
                                }
                                .padding(.vertical, 2)
                            }
                        }
                    }
                    if !model.queue.isEmpty {
                        Section("Wartet") {
                            ForEach(model.queue, id: \.id) { job in
                                HStack {
                                    Text(job.title)
                                        .lineLimit(1)
                                    Spacer()
                                    Text(job.createdAt, style: .relative)
                                        .font(.caption)
                                        .foregroundStyle(.secondary)
                                }
                            }
                        }
                    }
                }
            }
        }
        .navigationTitle("Warteschlange")
    }
}
