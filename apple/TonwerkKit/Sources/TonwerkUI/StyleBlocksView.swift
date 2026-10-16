import SwiftUI

/// The style's building blocks by category, as the web page's `StyleBlocks.vue`: a tap puts a block into the style,
/// another takes it out. Own words in the style stay where they are.
struct StyleBlocksView: View {
    @Environment(AppModel.self) private var model
    @Binding var style: String
    let instrumental: Bool
    @State private var choice = Choice.category(.genre)
    @AppStorage("styleBlocksOpen") private var open = false

    /// A category of the fixed list, or the blocks of the best rated songs (`api/insights`), offered only once there
    /// are some.
    private enum Choice: Hashable {
        case proven
        case category(StyleBlocks.Category)
    }

    var body: some View {
        DisclosureGroup("Bausteine", isExpanded: $open) {
            VStack(alignment: .leading, spacing: 10) {
                let proven = model.provenStyleBlocks
                Picker("Gruppe", selection: $choice) {
                    if !proven.isEmpty {
                        Text("★ Bewährt").tag(Choice.proven)
                    }
                    ForEach(StyleBlocks.Category.allCases) { category in
                        let count = StyleBlocks.count(category, in: style)
                        Text(count == 0 ? category.name : "\(category.name) (\(count))").tag(Choice.category(category))
                    }
                }
                switch choice {
                case .proven:
                    Text("Bausteine deiner am besten bewerteten Songs (Werkzeuge → Auswertung).")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                    chips(proven)
                case .category(let category):
                    Text(category.hint)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                    if instrumental && (category == .voice || category == .timbre || category == .language) {
                        Text("Bei „Instrumental“ ohne Wirkung.")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    chips(category.tags)
                }
            }
            .padding(.top, 4)
        }
        .task(id: open) {
            // The proven blocks are a suggestion; without a server that knows them the group just stays away.
            if open {
                try? await model.loadInsights()
            }
        }
        .onChange(of: model.provenStyleBlocks) {
            if choice == .proven && model.provenStyleBlocks.isEmpty {
                choice = .category(.genre)
            }
        }
    }

    private func chips(_ tags: [String]) -> some View {
        FlowLayout {
            ForEach(tags, id: \.self) { tag in
                Chip(title: tag, chosen: StyleBlocks.has(tag, in: style)) {
                    style = StyleBlocks.toggle(tag, in: style)
                }
            }
        }
    }
}

/// The lyrics' section tags, as the web page's `SectionTags.vue`. A tap puts the tag on a line of its own at the end
/// of the lyrics, since the text field does not tell where its cursor is before macOS 15 and iOS 18.
struct SectionTagsView: View {
    @Binding var lyrics: String
    @AppStorage("sectionTagsOpen") private var open = false

    var body: some View {
        DisclosureGroup("Abschnitte", isExpanded: $open) {
            VStack(alignment: .leading, spacing: 8) {
                Text("Antippen hängt den Abschnitt in einer eigenen Zeile an. Die Marken bleiben englisch, auch bei deutschem Text; ein Abschnitt sind etwa 4 bis 6 Zeilen. [Intro] gelingt oft schlecht, ein Song beginnt sicherer mit [Verse].")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                FlowLayout {
                    ForEach(SectionTags.known, id: \.self) { tag in
                        Chip(title: tag) { lyrics = SectionTags.append(tag, to: lyrics) }
                    }
                }
                Text("Nicht von YuE2 dokumentiert: das Modell liest sie als Wörter, die Wirkung ist offen.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                FlowLayout {
                    ForEach(SectionTags.other, id: \.self) { tag in
                        Chip(title: tag, secondary: true) { lyrics = SectionTags.append(tag, to: lyrics) }
                    }
                }
            }
            .padding(.top, 4)
        }
    }
}
