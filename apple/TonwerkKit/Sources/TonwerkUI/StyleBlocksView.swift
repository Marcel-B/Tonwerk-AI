import SwiftUI

/// The style's building blocks by category, as the web page's `StyleBlocks.vue`: a tap puts a block into the style,
/// another takes it out. Own words in the style stay where they are.
struct StyleBlocksView: View {
    @Binding var style: String
    let instrumental: Bool
    @State private var category = StyleBlocks.Category.genre
    @AppStorage("styleBlocksOpen") private var open = false

    var body: some View {
        DisclosureGroup("Bausteine", isExpanded: $open) {
            VStack(alignment: .leading, spacing: 10) {
                Picker("Gruppe", selection: $category) {
                    ForEach(StyleBlocks.Category.allCases) { category in
                        let count = StyleBlocks.count(category, in: style)
                        Text(count == 0 ? category.name : "\(category.name) (\(count))").tag(category)
                    }
                }
                Text(category.hint)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                if instrumental && (category == .voice || category == .timbre || category == .language) {
                    Text("Bei „Instrumental“ ohne Wirkung.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                FlowLayout {
                    ForEach(category.tags, id: \.self) { tag in
                        Chip(title: tag, chosen: StyleBlocks.has(tag, in: style)) {
                            style = StyleBlocks.toggle(tag, in: style)
                        }
                    }
                }
            }
            .padding(.top, 4)
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
