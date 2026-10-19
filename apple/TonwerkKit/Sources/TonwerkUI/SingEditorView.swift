import SwiftUI

/// The singing page's piano roll: keys on the left, notes on a grid of beats with their syllables on them and the
/// pronunciation above, held notes joined to the note they go on from. A tap on a free place sets a note, a drag
/// moves one, its right edge stretches it; the chosen note's syllable is typed in the bar below.
struct SingEditorView: View {
    @Bindable var editor: SingEditor
    let play: () -> Void
    @FocusState private var typing: Bool
    @State private var typed = ""

    private let rowHeight: CGFloat = 22
    private let keysWidth: CGFloat = 46
    private let height: CGFloat = 330

    private var pointsPerBeat: CGFloat { 56 * editor.zoom }

    /// Two octaves around the notes, at least C1 to C6 (Logic's names: C3 is note 60).
    private var range: ClosedRange<Int> {
        let pitches = editor.notes.map(\.pitch)
        let low = min(36, (pitches.min() ?? 60) - 4)
        let high = max(84, (pitches.max() ?? 60) + 4)
        return max(0, low)...min(127, high)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            toolbar
            roll
                .frame(height: height)
                .background(.quaternary.opacity(0.35), in: RoundedRectangle(cornerRadius: 8, style: .continuous))
                .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
            inspector
        }
        .onChange(of: editor.selection) {
            typed = editor.selected?.lyric ?? ""
        }
    }

    // MARK: Toolbar

    private var toolbar: some View {
        ViewThatFits(in: .horizontal) {
            HStack(spacing: 12) { toolbarItems }
            VStack(alignment: .leading, spacing: 8) { toolbarItems }
        }
    }

    @ViewBuilder
    private var toolbarItems: some View {
        HStack(spacing: 10) {
            Button(action: play) {
                Label(editor.player.isPlaying ? "Stopp" : "Vorhören", systemImage: editor.player.isPlaying ? "stop.fill" : "play.fill")
            }
            .disabled(editor.notes.isEmpty)
            Button {
                editor.undo()
            } label: {
                Label("Rückgängig", systemImage: "arrow.uturn.backward")
            }
            .disabled(!editor.canUndo)
        }
        HStack(spacing: 10) {
            Stepper(value: $editor.tempo, in: 30...300, step: 1) {
                Text("\(Int(editor.tempo.rounded())) BPM").monospacedDigit()
            }
            .fixedSize()
            Picker("Raster", selection: $editor.grid) {
                Text("1/4").tag(1.0)
                Text("1/8").tag(0.5)
                Text("1/16").tag(0.25)
                Text("1/32").tag(0.125)
            }
            .fixedSize()
            Button {
                editor.zoom = max(0.4, editor.zoom / 1.4)
            } label: {
                Image(systemName: "minus.magnifyingglass")
            }
            .help("Kleiner")
            Button {
                editor.zoom = min(4, editor.zoom * 1.4)
            } label: {
                Image(systemName: "plus.magnifyingglass")
            }
            .help("Größer")
        }
        .buttonStyle(.borderless)
    }

    // MARK: Roll

    private var roll: some View {
        ScrollViewReader { proxy in
            ScrollView(.vertical) {
                HStack(alignment: .top, spacing: 0) {
                    keys
                    ScrollView(.horizontal) {
                        grid
                    }
                }
            }
            .onAppear { proxy.scrollTo(centre, anchor: .center) }
            .onChange(of: editor.notes.isEmpty) { proxy.scrollTo(centre, anchor: .center) }
        }
    }

    /// The row the notes sit around, scrolled to when the editor appears or gets a melody.
    private var centre: Int {
        let pitches = editor.notes.map(\.pitch).sorted()
        return pitches.isEmpty ? 64 : pitches[pitches.count / 2]
    }

    private var keys: some View {
        VStack(spacing: 0) {
            ForEach(Array(range.reversed()), id: \.self) { pitch in
                let black = [1, 3, 6, 8, 10].contains(pitch % 12)
                ZStack(alignment: .trailing) {
                    Rectangle().fill(black ? Color.primary.opacity(0.55) : Color.primary.opacity(0.06))
                    if pitch % 12 == 0 {
                        Text(Self.name(pitch)).font(.caption2.weight(.semibold)).padding(.trailing, 4)
                            .foregroundStyle(.secondary)
                    }
                }
                .frame(width: keysWidth, height: rowHeight)
                .overlay(alignment: .bottom) { Rectangle().fill(Color.secondary.opacity(0.2)).frame(height: 0.5) }
                .id(pitch)
            }
        }
    }

    private var gridWidth: CGFloat {
        CGFloat(max(32, editor.lengthInBeats + 8)) * pointsPerBeat
    }

    private var gridHeight: CGFloat { CGFloat(range.count) * rowHeight }

    private func y(_ pitch: Int) -> CGFloat { CGFloat(range.upperBound - pitch) * rowHeight }

    private func x(_ beat: Double) -> CGFloat { CGFloat(beat) * pointsPerBeat }

    private var grid: some View {
        ZStack(alignment: .topLeading) {
            background
                .gesture(SpatialTapGesture().onEnded { tap in
                    let beat = Double(tap.location.x / pointsPerBeat)
                    let pitch = range.upperBound - Int(tap.location.y / rowHeight)
                    editor.add(at: beat, pitch: pitch)
                    typing = true
                })
            ForEach(editor.notes) { note in
                SingNoteView(
                    note: note, selected: editor.selection == note.id, phonemes: editor.phonemes[note.id],
                    pointsPerBeat: pointsPerBeat, rowHeight: rowHeight, grid: editor.grid,
                    select: { editor.selection = note.id },
                    move: { beats, semitones in
                        editor.selection = note.id
                        editor.move(note.id, by: beats, semitones: semitones)
                    },
                    resize: { beats in editor.resize(note.id, by: beats) })
                    .offset(x: x(note.start), y: y(note.pitch))
            }
            if editor.player.isPlaying {
                Rectangle()
                    .fill(Color.primary)
                    .frame(width: 1.5, height: gridHeight)
                    .offset(x: x(editor.beats(editor.player.position)))
                    .allowsHitTesting(false)
            }
        }
        .frame(width: gridWidth, height: gridHeight, alignment: .topLeading)
    }

    /// Rows, beat and bar lines, and the lines joining a held note to the one it goes on from.
    private var background: some View {
        let notes = editor.notes
        let range = range
        return Canvas { context, size in
            for pitch in range where [1, 3, 6, 8, 10].contains(pitch % 12) {
                context.fill(Path(CGRect(x: 0, y: y(pitch), width: size.width, height: rowHeight)), with: .color(.primary.opacity(0.05)))
            }
            for pitch in range where pitch % 12 == 0 {
                context.fill(Path(CGRect(x: 0, y: y(pitch) + rowHeight - 0.5, width: size.width, height: 0.5)), with: .color(.secondary.opacity(0.35)))
            }
            let beats = Int(size.width / pointsPerBeat) + 1
            for beat in 0...beats {
                let bar = beat % 4 == 0
                context.fill(
                    Path(CGRect(x: x(Double(beat)), y: 0, width: bar ? 1 : 0.5, height: size.height)),
                    with: .color(.secondary.opacity(bar ? 0.4 : 0.15)))
                if bar {
                    context.draw(
                        Text("\(beat / 4 + 1)").font(.caption2).foregroundStyle(.secondary),
                        at: CGPoint(x: x(Double(beat)) + 3, y: 2), anchor: .topLeading)
                }
            }
            for (before, note) in zip(notes, notes.dropFirst()) where note.isHold {
                var line = Path()
                line.move(to: CGPoint(x: x(before.end), y: y(before.pitch) + rowHeight / 2))
                line.addLine(to: CGPoint(x: x(note.start), y: y(note.pitch) + rowHeight / 2))
                context.stroke(line, with: .color(Theme.accent.opacity(0.8)), lineWidth: 1.5)
            }
        }
        .frame(width: gridWidth, height: gridHeight)
        .contentShape(Rectangle())
    }

    // MARK: Inspector

    @ViewBuilder
    private var inspector: some View {
        if let note = editor.selected {
            HStack(spacing: 8) {
                Button {
                    editor.selection = editor.neighbour(of: note.id, step: -1) ?? note.id
                } label: {
                    Image(systemName: "chevron.left")
                }
                .help("Note davor")
                Text(Self.name(note.pitch)).font(.callout.monospaced()).frame(minWidth: 34)
                TextField("Silbe", text: $typed)
                    .textFieldStyle(.roundedBorder)
                    .focused($typing)
                    .autocorrectionDisabled()
                    #if os(iOS)
                    .textInputAutocapitalization(.never)
                    #endif
                    .frame(maxWidth: 260)
                    .onSubmit {
                        // On to the next note, to type a line syllable by syllable.
                        let next = editor.type(typed, on: note.id)
                        if let next {
                            editor.selection = next
                            typing = true
                        }
                    }
                Button {
                    editor.toggleHold(note.id)
                    typed = editor.selected?.lyric ?? ""
                } label: {
                    Label("Gezogen", systemImage: note.isHold ? "link.circle.fill" : "link")
                }
                .help("Die Silbe davor auf dieser Note weitersingen")
                if let phonemes = editor.phonemes[note.id] {
                    Text(phonemes).font(.caption.monospaced()).foregroundStyle(.secondary).lineLimit(1)
                }
                Spacer(minLength: 4)
                Button(role: .destructive) {
                    editor.delete(note.id)
                } label: {
                    Image(systemName: "trash")
                }
                .help("Note löschen")
                .keyboardShortcut(.delete, modifiers: .command)
                Button {
                    editor.selection = editor.neighbour(of: note.id, step: 1) ?? note.id
                } label: {
                    Image(systemName: "chevron.right")
                }
                .help("Nächste Note")
            }
            .buttonStyle(.borderless)
        } else {
            Text(editor.notes.isEmpty
                ? String(localized: "Noch keine Noten: tippe in das Raster oder lade eine Melodie.")
                : String(localized: "\(editor.notes.count) Noten. Tippe eine an, um ihre Silbe zu ändern."))
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }

    /// Note names as Logic writes them, C3 being note 60.
    static func name(_ pitch: Int) -> String {
        let names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"]
        return "\(names[((pitch % 12) + 12) % 12])\(pitch / 12 - 2)"
    }
}

/// One note: its syllable inside (or a dash where it is held), the word's pronunciation above, and a drag to move
/// it or, at its right edge, to stretch it. Moves are shown while dragging and committed once on release.
private struct SingNoteView: View {
    let note: SingNote
    let selected: Bool
    let phonemes: String?
    let pointsPerBeat: CGFloat
    let rowHeight: CGFloat
    let grid: Double
    let select: () -> Void
    let move: (Double, Int) -> Void
    let resize: (Double) -> Void
    @State private var offset: CGSize = .zero
    @State private var stretch: CGFloat = 0

    private var width: CGFloat { max(8, CGFloat(note.length) * pointsPerBeat + stretch) }

    var body: some View {
        let colour = note.isSilent ? Color.secondary : Theme.accent
        RoundedRectangle(cornerRadius: 4, style: .continuous)
            .fill(colour.opacity(note.isSilent ? 0.25 : selected ? 0.95 : 0.7))
            .overlay {
                RoundedRectangle(cornerRadius: 4, style: .continuous)
                    .strokeBorder(selected ? Color.primary : Color.clear, lineWidth: 1.5)
            }
            .overlay(alignment: .leading) {
                Text(note.isHold ? "–" : note.isSilent ? "·" : note.trimmed)
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(note.isSilent ? Color.secondary : Color.black)
                    .lineLimit(1)
                    .padding(.horizontal, 4)
            }
            .overlay(alignment: .topLeading) {
                if let phonemes, !note.isHold {
                    Text(phonemes)
                        .font(.system(size: 9).monospaced())
                        .foregroundStyle(.secondary)
                        .fixedSize()
                        .offset(y: -12)
                        .allowsHitTesting(false)
                }
            }
            .overlay(alignment: .trailing) {
                Rectangle()
                    .fill(Color.white.opacity(0.001))
                    .frame(width: min(12, width / 3))
                    .gesture(DragGesture(minimumDistance: 2, coordinateSpace: .global)
                        .onChanged { drag in stretch = drag.translation.width }
                        .onEnded { drag in
                            stretch = 0
                            resize(Double(drag.translation.width / pointsPerBeat))
                        })
            }
            .frame(width: width, height: rowHeight - 2)
            .padding(.vertical, 1)
            .contentShape(Rectangle())
            .onTapGesture(perform: select)
            .gesture(DragGesture(minimumDistance: 3, coordinateSpace: .global)
                .onChanged { drag in
                    // Snapped while dragging, so the note lands where it is shown.
                    let beats = (Double(drag.translation.width / pointsPerBeat) / grid).rounded() * grid
                    offset = CGSize(
                        width: CGFloat(beats) * pointsPerBeat,
                        height: (drag.translation.height / rowHeight).rounded() * rowHeight)
                }
                .onEnded { drag in
                    offset = .zero
                    move(Double(drag.translation.width / pointsPerBeat), -Int((drag.translation.height / rowHeight).rounded()))
                })
            .offset(offset)
            .zIndex(selected ? 1 : 0)
    }
}
