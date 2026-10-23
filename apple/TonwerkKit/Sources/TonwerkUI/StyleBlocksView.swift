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
                .blockHelp(StyleBlocks.explanation(of: tag))
            }
        }
    }
}

/// Where the cursor last was in the lyrics, as character offsets with the text they belong to: tapping a tag ends
/// the editing first, and a draft or "use everything" replacing the text makes the position meaningless, so a tag
/// then goes to the end. Only `LyricsTextEditor` fills it, from macOS 15 and iOS 18 on, which tell the selection.
@Observable
final class LyricsCaret {
    private(set) var text = ""
    private(set) var start = 0
    private(set) var end = 0
    private(set) var known = false
    /// Counts the changes made by a tap, so the editor selects what the tap left (the tag's next line, the stretch
    /// in capitals).
    private(set) var revision = 0

    /// The selection, when it belongs to these lyrics.
    func selection(in lyrics: String) -> (start: Int, end: Int)? {
        known && text == lyrics ? (start, end) : nil
    }

    func remember(_ text: String, start: Int, end: Int) {
        self.text = text
        self.start = start
        self.end = end
        known = true
    }

    func set(_ text: String, start: Int, end: Int) {
        remember(text, start: start, end: end)
        revision += 1
    }
}

/// The lyrics' text editor; from macOS 15 and iOS 18 on it tells `caret` where the cursor is.
struct LyricsTextEditor: View {
    @Binding var text: String
    let caret: LyricsCaret

    var body: some View {
        if #available(iOS 18, macOS 15, *) {
            SelectingTextEditor(text: $text, caret: caret)
        } else {
            TextEditor(text: $text)
        }
    }
}

@available(iOS 18, macOS 15, *)
private struct SelectingTextEditor: View {
    @Binding var text: String
    let caret: LyricsCaret
    @State private var selection: TextSelection?

    var body: some View {
        TextEditor(text: $text, selection: $selection)
            .onChange(of: selection) { remember() }
            .onChange(of: caret.revision) { select() }
    }

    /// A cleared selection (the editor lost focus to a tag's button) keeps the last one.
    private func remember() {
        guard let selection, case .selection(let range) = selection.indices,
              range.upperBound <= text.endIndex else { return }
        let start = text.distance(from: text.startIndex, to: range.lowerBound)
        let end = text.distance(from: text.startIndex, to: range.upperBound)
        caret.remember(text, start: start, end: end)
    }

    private func select() {
        guard let range = caret.selection(in: text) else { return }
        let start = text.index(text.startIndex, offsetBy: min(range.start, text.count))
        let end = text.index(text.startIndex, offsetBy: min(range.end, text.count))
        selection = TextSelection(range: start..<end)
    }
}

/// The lyrics' section tags, directions and capitals, as the web page's `SectionTags.vue`. A tap puts the tag on a
/// line of its own where the cursor last was in the lyrics, else at the end (always before macOS 15 and iOS 18,
/// whose text field does not tell where its cursor is).
struct SectionTagsView: View {
    @Binding var lyrics: String
    let caret: LyricsCaret
    @AppStorage("sectionTagsOpen") private var open = false

    var body: some View {
        DisclosureGroup("Abschnitte", isExpanded: $open) {
            VStack(alignment: .leading, spacing: 8) {
                Text("Antippen setzt den Abschnitt in eine eigene Zeile an die Stelle, an der zuletzt der Cursor im Text stand, sonst ans Ende. Die Marken bleiben englisch, auch bei deutschem Text; ein Abschnitt sind etwa 4 bis 6 Zeilen. [Intro] gelingt oft schlecht, ein Song beginnt sicherer mit [Verse].")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                FlowLayout {
                    ForEach(SectionTags.known, id: \.self) { tag in
                        Chip(title: tag) { insert(tag) }
                    }
                }
                Text("Nicht von YuE2 dokumentiert: das Modell liest sie als Wörter, die Wirkung ist offen.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                FlowLayout {
                    ForEach(SectionTags.other, id: \.self) { tag in
                        Chip(title: tag, secondary: true) { insert(tag) }
                    }
                }
                Text("Anweisungen in Klammern: eine eigene Zeile im Abschnitt, mehrere hintereinander werden eine Klammer. Nicht von YuE2 dokumentiert; in einem Test wurden sie nicht gesungen und Schreien wie ruhiger Gesang kamen dort, wo sie es verlangten.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                ForEach([SectionTags.voiceDirections, SectionTags.musicDirections], id: \.self) { group in
                    FlowLayout {
                        ForEach(group, id: \.self) { direction in
                            Chip(title: direction, secondary: true) { insert(direction) }
                        }
                    }
                }
                if #available(iOS 18, macOS 15, *) {
                    Button("SCHREIEN", action: shout)
                        .buttonStyle(.bordered)
                        .controlSize(.small)
                        .tint(.red)
                    Text("Schreibt die markierten Zeilen groß (ohne Markierung die Zeile am Cursor), ein zweites Antippen wieder klein; Marken in [ ] und Anweisungen in ( ) bleiben. YuE2 singt Großgeschriebenes oft kräftiger, dokumentiert ist das nicht, und einmal blieb ein großgeschriebener Teil ungesungen.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }
            .padding(.top, 4)
        }
    }

    private func insert(_ tag: String) {
        let result = SectionTags.insert(tag, into: lyrics, at: caret.selection(in: lyrics)?.end)
        lyrics = result.text
        caret.set(result.text, start: result.caret, end: result.caret)
    }

    private func shout() {
        guard let selection = caret.selection(in: lyrics) else { return }
        let result = SectionTags.shout(lyrics, from: selection.start, to: selection.end)
        lyrics = result.text
        caret.set(result.text, start: result.start, end: result.end)
    }
}

extension View {
    /// The block's explanation on hover. On the Mac `.help` showed nothing inside the grouped form (Marcel,
    /// 2026-10-23), so there an AppKit view carries the tooltip over the chip; it takes no clicks. On an iPad with a
    /// pointer `.help` is enough, the iPhone has no hover.
    @ViewBuilder
    func blockHelp(_ text: String?) -> some View {
        if let text {
            #if os(macOS)
            overlay(ToolTipArea(text: text))
            #else
            help(text)
            #endif
        } else {
            self
        }
    }
}

#if os(macOS)

private struct ToolTipArea: NSViewRepresentable {
    let text: String

    func makeNSView(context: Context) -> NSView {
        let view = PassThroughView()
        view.toolTip = text
        return view
    }

    func updateNSView(_ view: NSView, context: Context) {
        view.toolTip = text
    }

    /// Hit testing skips it, so clicks reach the chip below; AppKit's tooltip rectangles do not depend on it.
    private final class PassThroughView: NSView {
        override func hitTest(_ point: NSPoint) -> NSView? { nil }
    }
}
#endif
