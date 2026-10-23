import SwiftUI
import TonwerkKit

/// What YuE2's synthesis took per engine, under the queue's log: the web page's `SynthesisStats.vue`. Read when
/// opened and whenever a song finishes, since each finished synthesis adds a row.
struct SynthesisStatsView: View {
    @Environment(AppModel.self) private var model
    @State private var open = false
    @State private var overview: Components.Schemas.SynthesisOverview?
    @State private var failed = false

    private var finished: Int { model.finishedSongs.count }

    var body: some View {
        DisclosureGroup(isExpanded: $open) {
            VStack(alignment: .leading, spacing: 8) {
                Text("Rechenzeit der Synthese pro Sekunde Audio, aus dem Protokoll des Workers. Kleiner ist schneller.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                if failed {
                    Text("Die Statistik ließ sich nicht laden.")
                        .font(.caption)
                        .foregroundStyle(.red)
                } else if let overview {
                    if overview.engines.isEmpty {
                        Text("Noch keine fertige Synthese gezählt.")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    ForEach(overview.engines, id: \.self) { row in
                        HStack(spacing: 10) {
                            Text(row.engine).fontWeight(.medium)
                            Text("\(row.steps) Schritte").foregroundStyle(.secondary)
                            Text("\(Self.rate(row.meanRate)) s pro s Audio")
                            Text("bestes \(Self.rate(row.bestRate))").foregroundStyle(.secondary)
                            Text("\(row.count)×").foregroundStyle(.secondary)
                        }
                        .font(.caption.monospacedDigit())
                    }
                    if overview.anePrograms.count > 0 {
                        Text(
                            "Neural-Engine-Programme vorbereitet: \(overview.anePrograms.count)×, im Schnitt \(Song.clock(overview.anePrograms.meanSeconds)), längstens \(Song.clock(overview.anePrograms.longestSeconds))"
                        )
                        .font(.caption)
                        .foregroundStyle(.secondary)
                    }
                    if !overview.recent.isEmpty {
                        Text("Zuletzt").font(.caption.weight(.medium))
                        ForEach(Array(overview.recent.enumerated()), id: \.offset) { _, entry in
                            HStack(spacing: 10) {
                                Text(entry.at.formatted(date: .abbreviated, time: .shortened)).foregroundStyle(.secondary)
                                Text(entry.engine)
                                Text(Song.clock(entry.seconds))
                                Text("\(Self.rate(entry.rate)) s/s").foregroundStyle(.secondary)
                                Text(entry.song).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
                            }
                            .font(.caption.monospacedDigit())
                        }
                    }
                } else {
                    ProgressView()
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .textSelection(.enabled)
        } label: {
            Text("Statistik")
                .font(.headline)
        }
        .task(id: open ? finished : -1) {
            guard open else { return }
            await load()
        }
    }

    private static func rate(_ value: Double) -> String {
        value.formatted(.number.precision(.fractionLength(1)))
    }

    private func load() async {
        do {
            let (data, _) = try await model.call("GET", model.address("api/stats/synthesis"))
            overview = try model.decode(Components.Schemas.SynthesisOverview.self, data)
            failed = false
        } catch {
            failed = true
        }
    }
}
