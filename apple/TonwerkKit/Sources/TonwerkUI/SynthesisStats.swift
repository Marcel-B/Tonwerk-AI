import Charts
import SwiftUI
import TonwerkKit

/// What YuE2's synthesis took per engine, under the queue's log: the web page's `SynthesisStats.vue`. Read when
/// opened and whenever a song finishes, since each finished synthesis adds a row. Two charts first (work per second
/// of audio, Neural Engine compiles), the numbers behind "Zahlen".
struct SynthesisStatsView: View {
    @Environment(AppModel.self) private var model
    @State private var open = false
    @State private var numbersOpen = false
    @State private var overview: Components.Schemas.SynthesisOverview?
    @State private var failed = false
    // Where the finger or pointer is along a chart; the nearest mark is shown with its numbers.
    @State private var rateAt: Double?
    @State private var compileAt: Double?

    private var pickedRate: Int? { rateAt.map { Int($0.rounded()) } }
    private var pickedCompile: Int? { compileAt.map { Int($0.rounded()) } }

    private var finished: Int { model.finishedSongs.count }

    var body: some View {
        DisclosureGroup(isExpanded: $open) {
            VStack(alignment: .leading, spacing: 14) {
                if failed {
                    Text("Die Statistik ließ sich nicht laden.")
                        .font(.caption)
                        .foregroundStyle(.red)
                } else if let overview {
                    if overview.recent.isEmpty {
                        Text("Noch keine fertige Synthese gezählt.")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    } else {
                        rateChart(Array(overview.recent.reversed()))
                    }
                    if !overview.aneRecent.isEmpty {
                        compileChart(Array(overview.aneRecent.reversed()), overview: overview)
                    }
                    if !overview.engines.isEmpty {
                        DisclosureGroup("Zahlen", isExpanded: $numbersOpen) {
                            numbers(overview)
                        }
                        .font(.caption)
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

    // MARK: - Charts

    /// Oldest on the left; a draft (fewer than 32 steps) is a ring, since it is quicker per second of audio.
    private func rateChart(_ entries: [Components.Schemas.SynthesisRecord]) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text("Rechenzeit pro Sekunde Audio").font(.caption.weight(.medium))
            HStack(spacing: 12) {
                ForEach(Self.enginesShown(entries), id: \.self) { engine in
                    Self.legend(Self.colour(engine), engine, hollow: false)
                }
                if entries.contains(where: { $0.steps < 32 }) {
                    Self.legend(.secondary, "weniger als 32 Schritte", hollow: true)
                }
            }
            Chart(Array(entries.enumerated()), id: \.offset) { index, entry in
                PointMark(x: .value("Synthese", Double(index)), y: .value("s pro s Audio", entry.rate))
                    .symbol {
                        Self.dot(Self.colour(entry.engine), hollow: entry.steps < 32, size: pickedRate == index ? 12 : 8)
                    }
            }
            .chartXAxis(.hidden)
            .chartXScale(domain: -0.5...Double(max(entries.count, 1)) - 0.5)
            .chartYScale(domain: 0...Self.niceMax(entries.map(\.rate).max() ?? 0))
            .chartXSelection(value: $rateAt)
            .frame(height: 150)
            Group {
                if let index = pickedRate, entries.indices.contains(index) {
                    let entry = entries[index]
                    Text(
                        "\(entry.at.formatted(date: .abbreviated, time: .shortened)) · \(entry.engine) · \(entry.steps) Schritte · \(Song.clock(entry.seconds)) · \(Self.rate(entry.rate)) s pro s Audio · \(entry.song)"
                    )
                } else {
                    Text("Aus dem Protokoll des Workers, älteste links. Kleiner ist schneller. Tippe auf einen Punkt für seine Zahlen.")
                }
            }
            .font(.caption.monospacedDigit())
            .foregroundStyle(.secondary)
        }
    }

    /// Filled: the song waited for its program; outlined: compiled in the background while another song solved.
    private func compileChart(_ entries: [Components.Schemas.AneProgramsRecord], overview: Components.Schemas.SynthesisOverview)
        -> some View
    {
        let ane = Self.colour("ane")
        let waited = entries.filter(\.waited).count
        let syntheses = overview.engines.filter { $0.engine == "ane" }.reduce(0) { $0 + $1.count }
        return VStack(alignment: .leading, spacing: 4) {
            Text("Neural-Engine-Programme kompiliert (Minuten)").font(.caption.weight(.medium))
            HStack(spacing: 12) {
                Self.legend(ane, "Song hat gewartet", hollow: false)
                Self.legend(ane, "im Hintergrund", hollow: true)
            }
            Chart(Array(entries.enumerated()), id: \.offset) { index, entry in
                BarMark(x: .value("Kompilieren", Double(index)), y: .value("Minuten", entry.seconds / 60), width: .ratio(0.7))
                    .foregroundStyle(entry.waited ? ane : ane.opacity(0.15))
                    .cornerRadius(2)
                    .annotation(position: .overlay) {
                        if !entry.waited || pickedCompile == index {
                            Rectangle().strokeBorder(pickedCompile == index ? Color.primary : ane, lineWidth: 1.5)
                        }
                    }
            }
            .chartXAxis(.hidden)
            .chartXScale(domain: -0.5...Double(max(entries.count, 1)) - 0.5)
            .chartYScale(domain: 0...Self.niceMax((entries.map(\.seconds).max() ?? 0) / 60))
            .chartXSelection(value: $compileAt)
            .frame(height: 150)
            Group {
                if let index = pickedCompile, entries.indices.contains(index) {
                    let entry = entries[index]
                    Text(
                        "\(entry.at.formatted(date: .abbreviated, time: .shortened)) · \(Song.clock(entry.seconds)) · \(entry.waited ? "Song hat gewartet" : "im Hintergrund") · \(entry.song)"
                    )
                } else {
                    Text(
                        "\(overview.anePrograms.count)× kompiliert, davon \(waited)× während ein Song wartete, bei \(syntheses) Synthesen auf der Neural Engine. Der Worker behält kompilierte Programme nur, solange er läuft und das Modell geladen hat. Tippe auf einen Balken für seine Zahlen."
                    )
                }
            }
            .font(.caption.monospacedDigit())
            .foregroundStyle(.secondary)
        }
    }

    private func numbers(_ overview: Components.Schemas.SynthesisOverview) -> some View {
        VStack(alignment: .leading, spacing: 8) {
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
            if !overview.aneRecent.isEmpty {
                Text("Neural-Engine-Programme kompiliert").font(.caption.weight(.medium))
                ForEach(Array(overview.aneRecent.enumerated()), id: \.offset) { _, entry in
                    HStack(spacing: 10) {
                        Text(entry.at.formatted(date: .abbreviated, time: .shortened)).foregroundStyle(.secondary)
                        Text(Song.clock(entry.seconds))
                        Text(entry.waited ? "Song hat gewartet" : "im Hintergrund").foregroundStyle(.secondary)
                        Text(entry.song).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
                    }
                    .font(.caption.monospacedDigit())
                }
            }
        }
        .padding(.top, 4)
    }

    // MARK: - Helpers

    /// Engines in the web chart's fixed colour order (`synthesisChart.ts`), so an engine keeps its colour.
    private static let engineOrder = ["mlx", "ane", "remote"]

    private static func colour(_ engine: String) -> Color {
        switch engine {
        case "mlx": Color(red: 0.2, green: 0.5, blue: 0.87)
        case "ane": Color(red: 0.89, green: 0.38, blue: 0.18)
        case "remote": Color(red: 0.1, green: 0.65, blue: 0.46)
        default: .gray
        }
    }

    private static func enginesShown(_ entries: [Components.Schemas.SynthesisRecord]) -> [String] {
        let seen = Set(entries.map(\.engine))
        return engineOrder.filter(seen.contains) + seen.subtracting(engineOrder).sorted()
    }

    private static func dot(_ colour: Color, hollow: Bool, size: CGFloat) -> some View {
        Circle()
            .fill(hollow ? Color.clear : colour)
            .overlay(Circle().strokeBorder(colour, lineWidth: hollow ? 2 : 0))
            .frame(width: size, height: size)
    }

    private static func legend(_ colour: Color, _ label: String, hollow: Bool) -> some View {
        HStack(spacing: 4) {
            Circle()
                .fill(hollow ? Color.clear : colour)
                .overlay(Circle().strokeBorder(colour, lineWidth: 2))
                .frame(width: 9, height: 9)
            Text(label)
        }
        .font(.caption)
        .foregroundStyle(.secondary)
    }

    /// 1, 2 or 5 times a power of ten, at least the value: the web chart's `niceMax`.
    private static func niceMax(_ value: Double) -> Double {
        guard value > 0 else { return 1 }
        let power = pow(10, floor(log10(value)))
        return [1.0, 2, 5, 10].map { $0 * power }.first { $0 >= value - 1e-9 } ?? 10 * power
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
