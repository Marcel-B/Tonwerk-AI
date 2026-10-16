import SwiftUI
import TonwerkKit

/// Which style blocks and settings the rated songs were made with, against their stars (`InsightsPage.vue`).
struct InsightsView: View {
    @Environment(AppModel.self) private var model
    @State private var problem: String?
    @State private var loading = false

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                Text("Womit die bewerteten Songs gemacht wurden, gegen ihre Sterne. Bausteine und Einstellungen über dem Schnitt sind hervorgehoben; die besten Stil-Bausteine bietet „Erstellen“ unter „Bewährt“ an.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                if let problem {
                    Label(problem, systemImage: "exclamationmark.triangle").foregroundStyle(.red)
                } else if let report = model.insights {
                    if report.rated == 0 {
                        Text("Noch keine bewerteten Songs. Sterne gibt es in der Bibliothek und im Player.")
                            .foregroundStyle(.secondary)
                    } else {
                        Text("\(report.rated) bewertete Songs, im Schnitt ★ \(Self.stars(report.average)).")
                        ForEach(report.groups, id: \.key) { group in
                            groupCard(group, average: report.average)
                        }
                        Text("Gezählt wird ab zwei Songs. Die Reihenfolge rechnet zu jedem Wert drei Songs mit Durchschnittsbewertung dazu, damit ein einzelner Glückstreffer nicht vorn landet.")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                } else if loading {
                    ProgressView().frame(maxWidth: .infinity).padding(.top, 40)
                }
            }
            .padding()
            .frame(maxWidth: 760, alignment: .leading)
            .frame(maxWidth: .infinity)
        }
        .navigationTitle("Was gut ankommt")
        // Asked again each time the page is shown: songs are rated on other pages meanwhile.
        .task { await load() }
        .refreshable { await load() }
    }

    private func load() async {
        loading = true
        defer { loading = false }
        do {
            try await model.loadInsights()
            problem = nil
        } catch {
            problem = String(localized: "Die Auswertung ließ sich nicht laden: \(error.localizedDescription)")
        }
    }

    private func groupCard(_ group: Components.Schemas.InsightGroup, average: Double) -> some View {
        let parts = Insights.split(group)
        return VStack(alignment: .leading, spacing: 6) {
            Text(Insights.groupName(group.key)).font(.headline)
            ForEach(parts.best, id: \.value) { entry in
                row(entry, group: group.key, average: average)
            }
            if !parts.weak.isEmpty {
                Text("Eher nicht")
                    .font(.subheadline.weight(.medium))
                    .foregroundStyle(.secondary)
                    .padding(.top, 6)
                ForEach(parts.weak, id: \.value) { entry in
                    row(entry, group: group.key, average: average)
                }
            }
        }
        .card(padding: 12)
    }

    private func row(_ entry: Components.Schemas.InsightEntry, group: String, average: Double) -> some View {
        let tone = Insights.tone(entry, average: average)
        return HStack(spacing: 10) {
            Text(Insights.valueName(group: group, entry.value))
                .fontWeight(tone == .good ? .semibold : .regular)
                .foregroundStyle(tone == .weak ? Color.secondary : Color.primary)
                .lineLimit(1)
                .frame(maxWidth: .infinity, alignment: .leading)
            ProgressView(value: min(max(entry.average / 5, 0), 1))
                .tint(tone == .good ? Theme.accent : .secondary)
                .frame(width: 64)
            Text("★ \(Self.stars(entry.average)) · \(entry.count) Songs")
                .font(.caption)
                .foregroundStyle(.secondary)
                .monospacedDigit()
                .frame(width: 110, alignment: .trailing)
        }
    }

    static func stars(_ value: Double) -> String {
        value.formatted(.number.precision(.fractionLength(1)))
    }
}
