import SwiftUI
import TonwerkKit

/// The storage page (`StoragePage.vue`): the volumes, what can be cleaned up in one go, and each part with its
/// largest entries. Models are only shown, since deleting one would break a tool.
struct StorageView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        Group {
            if let session = model.storagePage {
                StorageSessionView(session: session)
            } else {
                ProgressView().onAppear { model.openStorage() }
            }
        }
        .navigationTitle("Speicherplatz")
    }
}

/// A question before something goes for good.
private struct Deletion {
    let question: String
    let run: () async -> Void
}

private struct StorageSessionView: View {
    @Environment(AppModel.self) private var model
    @Bindable var session: StorageSession
    @State private var pending: Deletion?
    @State private var shown: [String: Int] = [:]

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                Text("Was Tonwerk auf dem Mac belegt, neu gemessen bei jedem Öffnen. Gelöschtes ist endgültig weg.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                if let problem = session.problem {
                    Label(problem, systemImage: "exclamationmark.triangle").foregroundStyle(.red)
                }
                if let notice = session.notice {
                    Label(notice, systemImage: "checkmark.circle").foregroundStyle(.green)
                }
                if let overview = session.overview {
                    volumes(overview)
                    cleanupCard
                    ForEach(StorageSession.groups, id: \.id) { group in
                        groupCard(group.id, name: group.name)
                    }
                } else if session.loading {
                    ProgressView("Wird gemessen …").frame(maxWidth: .infinity).padding(.top, 40)
                }
            }
            .padding()
            .frame(maxWidth: 900, alignment: .leading)
            .frame(maxWidth: .infinity)
        }
        .task { await session.load() }
        .refreshable { await session.load() }
        .toolbar {
            ToolbarItem {
                Button {
                    Task { await session.load() }
                } label: {
                    Label("Neu messen", systemImage: "arrow.clockwise")
                }
                .disabled(session.loading)
            }
        }
        .confirmationDialog(
            pending?.question ?? "",
            isPresented: Binding(get: { pending != nil }, set: { if !$0 { pending = nil } }),
            titleVisibility: .visible
        ) {
            if let pending {
                Button("Löschen", role: .destructive) { Task { await pending.run() } }
            }
            Button("Abbrechen", role: .cancel) {}
        }
    }

    // MARK: Volumes

    private func volumes(_ overview: Components.Schemas.StorageOverview) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            ForEach(overview.volumes, id: \.mount) { volume in
                VolumeBar(
                    name: StorageSession.volumeName(volume.mount), tonwerk: session.tonwerkBytes(on: volume),
                    free: volume.free, total: volume.total)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .card()
    }

    // MARK: Cleanup

    private var cleanupCard: some View {
        VStack(alignment: .leading, spacing: 10) {
            CardHeading("Aufräumen", systemImage: "sparkles")
            let suggestions = session.suggestions
            let oneStar = session.oneStar
            let unrated = model.unrated
            if suggestions.isEmpty && oneStar.isEmpty && unrated.isEmpty {
                Text("Gerade gibt es nichts aufzuräumen.").foregroundStyle(.secondary)
            }
            ForEach(suggestions) { suggestion in
                cleanupRow(suggestion.name, hint: suggestion.hint, bytes: suggestion.bytes, busy: suggestion.target) {
                    pending = Deletion(
                        question: String(localized: "\(suggestion.name) (\(StorageSession.size(suggestion.bytes))) endgültig löschen?")
                    ) { await session.cleanUp(suggestion) }
                }
            }
            if !oneStar.isEmpty {
                let bytes = oneStar.reduce(0) { $0 + $1.bytes }
                cleanupRow(
                    String(localized: "Songs mit einem Stern"),
                    hint: oneStar.count == 1
                        ? String(localized: "Ein Song, mit allem, was dazugehört (Versionen, Stems, Cover, Videos).")
                        : String(localized: "\(oneStar.count) Songs, mit allem, was dazugehört (Versionen, Stems, Cover, Videos)."),
                    bytes: bytes, busy: "oneStar"
                ) {
                    pending = Deletion(
                        question: String(localized: "\(oneStar.count) Songs mit einem Stern (\(StorageSession.size(bytes))) samt Versionen, Stems, Cover und Videos endgültig löschen?")
                    ) { await session.deleteOneStar() }
                }
            }
            if !unrated.isEmpty {
                HStack(alignment: .firstTextBaseline) {
                    VStack(alignment: .leading, spacing: 2) {
                        HStack {
                            Text("Unbewertete Songs")
                            Spacer()
                            Text(StorageSession.size(unrated.reduce(0) { $0 + $1.bytes })).monospacedDigit()
                        }
                        Text("\(unrated.count) Songs ohne Sterne sind älter als 7 Tage. Anhören, bewerten oder löschen.")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    Button("Durchgehen") { model.page = .review }
                        .buttonStyle(.borderless)
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .card()
    }

    private func cleanupRow(
        _ name: String, hint: String, bytes: Int64, busy key: String, ask: @escaping () -> Void
    ) -> some View {
        HStack(alignment: .center, spacing: 10) {
            VStack(alignment: .leading, spacing: 2) {
                HStack {
                    Text(name)
                    Spacer()
                    Text(StorageSession.size(bytes)).monospacedDigit()
                }
                Text(hint).font(.caption).foregroundStyle(.secondary)
            }
            deleteButton(busy: key, action: ask)
        }
    }

    private func deleteButton(busy key: String, action: @escaping () -> Void) -> some View {
        Group {
            if session.busy == key {
                ProgressView().controlSize(.small)
            } else {
                Button(role: .destructive, action: action) {
                    Image(systemName: "trash")
                }
                .buttonStyle(.borderless)
                .disabled(session.busy != nil)
                .help("Löschen")
            }
        }
        .frame(width: 28)
    }

    // MARK: Parts

    private func groupCard(_ group: String, name: String) -> some View {
        let parts = session.categories(in: group)
        return VStack(alignment: .leading, spacing: 8) {
            HStack(alignment: .firstTextBaseline) {
                Text(name).font(.headline)
                Spacer()
                Text(StorageSession.size(parts.reduce(0) { $0 + $1.bytes }))
                    .monospacedDigit()
                    .foregroundStyle(.secondary)
            }
            if group == "models" {
                Text("Hier wird nichts gelöscht. deploy/move-to-volume.sh legt große Teile auf eine externe SSD.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            ForEach(parts, id: \.key) { part in
                partRow(part)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .card()
    }

    @ViewBuilder
    private func partRow(_ part: Components.Schemas.StorageCategory) -> some View {
        if part.largest.isEmpty {
            partLabel(part)
        } else {
            DisclosureGroup {
                let limit = shown[part.key] ?? 5
                ForEach(part.largest.prefix(limit), id: \.id) { item in
                    itemRow(item)
                }
                if part.largest.count > limit {
                    Button("Mehr zeigen") { shown[part.key] = limit + 10 }
                        .buttonStyle(.borderless)
                        .font(.caption)
                }
            } label: {
                partLabel(part)
            }
        }
    }

    private func partLabel(_ part: Components.Schemas.StorageCategory) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(alignment: .firstTextBaseline) {
                Text(StorageSession.categoryName(part.key))
                Text(details(part)).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                Spacer()
                Text(StorageSession.size(part.bytes)).monospacedDigit()
            }
            GeometryReader { geometry in
                Capsule()
                    .fill(Theme.accent.opacity(0.7))
                    .frame(width: geometry.size.width * CGFloat(Double(part.bytes) / Double(session.largest)))
            }
            .frame(height: 3)
        }
    }

    private func details(_ part: Components.Schemas.StorageCategory) -> String {
        var parts: [String] = []
        if part.count > 0 {
            parts.append(part.count == 1 ? String(localized: "1 Eintrag") : String(localized: "\(part.count) Einträge"))
        }
        if let volume = part.volume, volume != session.homeVolume {
            parts.append(String(localized: "auf \(StorageSession.volumeName(volume))"))
        }
        if part.orphaned > 0 {
            parts.append(String(localized: "davon \(StorageSession.size(part.orphaned)) verwaist"))
        }
        return parts.isEmpty ? "" : "· " + parts.joined(separator: " · ")
    }

    private func itemRow(_ item: Components.Schemas.StorageItem) -> some View {
        HStack(spacing: 10) {
            VStack(alignment: .leading, spacing: 2) {
                Text(item.title).lineLimit(1)
                let sub = [StorageSession.detail(item), item.createdAt?.formatted(date: .abbreviated, time: .shortened)]
                    .compactMap { $0 }
                if !sub.isEmpty {
                    Text(sub.joined(separator: " · ")).font(.caption).foregroundStyle(.secondary)
                }
            }
            Spacer()
            Text(StorageSession.size(item.bytes)).font(.callout).monospacedDigit()
            deleteButton(busy: "\(item.kind):\(item.id)") {
                let size = StorageSession.size(item.bytes)
                pending = Deletion(
                    question: item.kind == "song"
                        ? String(localized: "Song „\(item.title)“ (\(size)) endgültig löschen? Audio und Partitur sind danach weg, mit ihnen die Stimmenversionen, Stems, das Cover und die Videos des Songs.")
                        : String(localized: "„\(item.title)“ (\(size)) endgültig löschen?")
                ) { await session.delete(item) }
            }
        }
        .padding(.leading, 4)
    }
}

/// A volume's use: Tonwerk's share in the accent, the rest of what is used in grey, then what is free.
private struct VolumeBar: View {
    let name: String
    let tonwerk: Int64
    let free: Int64
    let total: Int64

    var body: some View {
        let used = max(0, total - free)
        let share = { (bytes: Int64) in min(1, Double(bytes) / Double(max(1, total))) }
        VStack(alignment: .leading, spacing: 4) {
            HStack(alignment: .firstTextBaseline) {
                Text(name).fontWeight(.medium)
                Spacer()
                Text("Tonwerk \(StorageSession.size(tonwerk)) · \(StorageSession.size(free)) von \(StorageSession.size(total)) frei")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            GeometryReader { geometry in
                HStack(spacing: 0) {
                    Rectangle().fill(Theme.accent)
                        .frame(width: geometry.size.width * share(min(tonwerk, used)))
                    Rectangle().fill(Color.secondary.opacity(0.45))
                        .frame(width: geometry.size.width * share(max(0, used - tonwerk)))
                    Spacer(minLength: 0)
                }
            }
            .frame(height: 9)
            .background(Color.secondary.opacity(0.15))
            .clipShape(Capsule())
        }
        .accessibilityElement(children: .combine)
    }
}
