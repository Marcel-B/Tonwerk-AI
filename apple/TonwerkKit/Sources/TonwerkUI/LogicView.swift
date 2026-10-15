import SwiftUI
import TonwerkKit

/// The Logic page: a song's score with generated accompaniment, shown as a piano roll, played track by track in the
/// app or on MIDI instruments, mixed, and exported as a Logic project.
struct LogicView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        Group {
            if let session = model.logic, session.song != nil {
                LogicSessionView(session: session)
            } else {
                LogicStart()
            }
        }
        .navigationTitle("Logic")
    }
}

/// No song yet: the songs with a score to choose from.
private struct LogicStart: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        let songs = model.songs.filter(\.hasScore)
        if songs.isEmpty {
            ContentUnavailableView(
                "Kein Song mit Partitur", systemImage: "pianokeys",
                description: Text("Die Logic-Seite braucht die Partitur eines Songs, YuE2 schreibt sie mit Planung."))
        } else {
            List(songs) { song in
                Button {
                    model.openInLogic(song)
                } label: {
                    HStack(spacing: 10) {
                        CoverView(song: song).frame(width: 40, height: 40)
                        VStack(alignment: .leading, spacing: 2) {
                            Text(song.displayTitle).lineLimit(1)
                            Text(song.style).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                        }
                    }
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
            }
        }
    }
}

private struct LogicSessionView: View {
    @Environment(AppModel.self) private var model
    @Bindable var session: LogicSession
    @State private var choosing = false
    @State private var bluetooth = false
    @State private var exporting = false
    @State private var exportFile: URL?
    @State private var exportWarnings: [LogicDiagnostic] = []
    @State private var exportProblem: String?
    @State private var saving = false

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                header
                if let problem = session.problem {
                    Label(problem, systemImage: "exclamationmark.triangle")
                        .foregroundStyle(.red)
                }
                if let score = session.score {
                    transport(score)
                    PianoRoll(score: score, voices: session.voices, player: session.player)
                        .frame(height: 240)
                        .card(padding: 0)
                    tracks
                } else if session.converting {
                    ProgressView("Wandelt um …").frame(maxWidth: .infinity).padding(40)
                }
                options
                exportCard
                warnings
            }
            .padding(16)
            .frame(maxWidth: 900)
            .frame(maxWidth: .infinity)
        }
        .toolbar {
            ToolbarItem {
                Button {
                    choosing = true
                } label: {
                    Label("Anderer Song", systemImage: "music.note.list")
                }
            }
        }
        .sheet(isPresented: $choosing) {
            NavigationStack {
                LogicStart()
                    .navigationTitle("Song wählen")
                    .toolbar {
                        ToolbarItem(placement: .cancellationAction) {
                            Button("Abbrechen") { choosing = false }
                        }
                    }
            }
            #if os(macOS)
            .frame(minWidth: 420, minHeight: 480)
            #endif
            .onChange(of: session.song?.id) { choosing = false }
        }
        #if os(iOS)
        .sheet(isPresented: $bluetooth) {
            BluetoothMidiView().ignoresSafeArea()
        }
        #endif
        .fileMover(isPresented: $saving, file: exportFile) { _ in }
        .onChange(of: session.outputs.outputs) { session.reroute() }
        .task(id: session.form) {
            // Options change by taps and steppers; one conversion after the last of them.
            guard session.conversion != nil, session.form != session.convertedForm else { return }
            try? await Task.sleep(for: .milliseconds(400))
            guard !Task.isCancelled else { return }
            exportFile = nil
            await session.convert()
        }
    }

    // MARK: Header and transport

    private var header: some View {
        HStack(spacing: 12) {
            if let song = session.song {
                CoverView(song: song).frame(width: 56, height: 56)
                VStack(alignment: .leading, spacing: 3) {
                    Text(song.displayTitle).font(.title3.weight(.semibold)).lineLimit(1)
                    if let score = session.score {
                        Text("\(Int(score.tempoBpm.rounded())) BPM · \(Song.clock(score.durationSeconds)) · \(score.voices.count) Spuren")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                }
            }
            Spacer()
            if session.converting {
                ProgressView().controlSize(.small)
            }
        }
    }

    private func transport(_ score: LogicScore) -> some View {
        LogicTransport(player: session.player, recording: $session.playsRecording)
    }

    // MARK: Tracks

    private var tracks: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                CardHeading("Spuren", systemImage: "slider.vertical.3")
                Spacer()
                midiMenu
            }
            ForEach(Array(session.voices.enumerated()), id: \.element.id) { index, voice in
                TrackRow(session: session, voice: voice, colour: PianoRoll.colour(index))
                if index < session.voices.count - 1 {
                    Divider()
                }
            }
            Divider()
            HStack {
                Text("Master").font(.callout.weight(.medium)).frame(width: 110, alignment: .leading)
                Slider(value: Binding(
                    get: { session.mixer.master },
                    set: { session.setMix(nil, volume: $0) }), in: 0...LogicMixer.maxVolume)
            }
        }
        .card()
    }

    private var midiMenu: some View {
        Menu {
            Section("MIDI-Ausgänge") {
                if session.outputs.outputs.isEmpty {
                    Text("Keine")
                }
                ForEach(session.outputs.outputs) { output in
                    Text(output.displayName)
                }
            }
            Button {
                session.outputs.refresh()
            } label: {
                Label("Neu suchen", systemImage: "arrow.clockwise")
            }
            Button {
                #if os(iOS)
                bluetooth = true
                #else
                BluetoothMidi.show()
                #endif
            } label: {
                Label("Bluetooth-MIDI …", systemImage: "dot.radiowaves.left.and.right")
            }
        } label: {
            Label("\(session.outputs.outputs.count) MIDI", systemImage: "cable.connector")
                .font(.caption)
        }
        .fixedSize()
    }

    // MARK: Options

    private var options: some View {
        VStack(alignment: .leading, spacing: 10) {
            CardHeading("Arrangement", systemImage: "music.quarternote.3")
            Toggle("Akkorde", isOn: $session.form.includeChords)
            if session.form.includeChords {
                Picker("Akkordmuster", selection: $session.form.chordPattern) {
                    Text("Wie notiert").tag("")
                    ForEach(LogicForm.chordPatterns, id: \.self) { Text(Self.name($0)).tag($0) }
                }
            }
            Picker("Bass", selection: $session.form.bass) {
                Text("Aus").tag("")
                ForEach(LogicForm.bassPatterns, id: \.self) { Text(Self.name($0)).tag($0) }
            }
            Picker("Schlagzeug", selection: $session.form.drums) {
                Text("Aus").tag("")
                ForEach(LogicForm.drumPatterns, id: \.self) { Text(Self.name($0)).tag($0) }
            }
            if !session.form.drums.isEmpty {
                Toggle("Becken an Abschnitten", isOn: $session.form.crash)
                Toggle("Eine Spur pro Trommel", isOn: $session.form.splitDrums)
            }
            Stepper("Einzähler: \(session.form.countIn) Takte", value: $session.form.countIn, in: 0...4)
        }
        .card()
    }

    static func name(_ pattern: String) -> String {
        let names = [
            "Eighths": "Achtel", "Quarters": "Viertel", "RootFifth": "Grundton und Quinte", "Octaves": "Oktaven",
            "Offbeat": "Offbeat", "Sustained": "Gehalten", "Walking": "Walking",
            "FourOnTheFloor": "Four on the Floor", "Backbeat": "Backbeat", "HalfTime": "Half-Time", "Disco": "Disco",
            "SixteenthHats": "Sechzehntel-Hi-Hat", "Shuffle": "Shuffle", "Block": "Blockakkorde",
            "Sixteenths": "Sechzehntel", "ArpeggioUp": "Arpeggio auf", "ArpeggioUpDown": "Arpeggio auf und ab",
        ]
        return names[pattern].map { String(localized: String.LocalizationValue($0)) } ?? pattern
    }

    // MARK: Export

    private var exportCard: some View {
        VStack(alignment: .leading, spacing: 10) {
            CardHeading("Logic-Projekt", systemImage: "square.and.arrow.down.on.square")
            Toggle("Eine Region pro Abschnitt", isOn: $session.form.splitSections)
            if let exportFile {
                HStack {
                    ShareLink(item: exportFile) {
                        Label("Teilen", systemImage: "square.and.arrow.up")
                    }
                    Button {
                        saving = true
                    } label: {
                        Label("Sichern …", systemImage: "square.and.arrow.down")
                    }
                }
                .buttonStyle(.bordered)
                Text("„\(exportFile.lastPathComponent)“ ist bereit. Entpackt öffnet es Logic Pro.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                ForEach(exportWarnings, id: \.self) { warning in
                    Label(warning.message, systemImage: "exclamationmark.circle")
                        .font(.caption)
                        .foregroundStyle(.orange)
                }
            } else {
                Button {
                    Task { await export() }
                } label: {
                    HStack(spacing: 8) {
                        if exporting { ProgressView().controlSize(.small) }
                        Text(exporting ? "Wird erstellt …" : "Projekt erstellen")
                    }
                    .frame(maxWidth: .infinity)
                }
                .buttonStyle(.borderedProminent)
                .disabled(exporting || session.score == nil)
            }
            if let exportProblem {
                Label(exportProblem, systemImage: "exclamationmark.triangle").foregroundStyle(.red)
            }
        }
        .card()
        .onChange(of: session.song?.id) { exportFile = nil }
        .onChange(of: session.assignments) { exportFile = nil }
    }

    private func export() async {
        exporting = true
        exportProblem = nil
        defer { exporting = false }
        do {
            let (file, found) = try await session.export()
            exportFile = file
            exportWarnings = found
        } catch {
            exportProblem = error.localizedDescription
        }
    }

    @ViewBuilder
    private var warnings: some View {
        if !session.warnings.isEmpty {
            DisclosureGroup("\(session.warnings.count) Hinweise zur Umwandlung") {
                VStack(alignment: .leading, spacing: 6) {
                    ForEach(session.warnings, id: \.self) { warning in
                        Text(warning.line.map { "Zeile \($0): \(warning.message)" } ?? warning.message)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            .frame(maxWidth: .infinity, alignment: .leading)
                    }
                }
                .padding(.top, 6)
            }
            .card()
        }
    }
}

/// Play, pause, position and the recording switch; its own view, so the position's updates redraw only it.
private struct LogicTransport: View {
    let player: LogicPlayer
    @Binding var recording: Bool
    @State private var scrubbing: Double?

    var body: some View {
        HStack(spacing: 12) {
            Button {
                player.toggle()
            } label: {
                Image(systemName: player.isPlaying ? "pause.fill" : "play.fill")
                    .contentTransition(.symbolEffect(.replace))
            }
            .buttonStyle(PlayButtonStyle(size: 44))
            .keyboardShortcut(.space, modifiers: [])
            Text(Song.clock(scrubbing ?? player.position))
                .font(.caption.monospacedDigit())
                .foregroundStyle(.secondary)
            Slider(
                value: Binding(get: { scrubbing ?? player.position }, set: { scrubbing = $0 }),
                in: 0...max(1, player.duration)
            ) { editing in
                if !editing, let target = scrubbing {
                    player.seek(to: target)
                    scrubbing = nil
                }
            }
            Text(Song.clock(player.duration))
                .font(.caption.monospacedDigit())
                .foregroundStyle(.secondary)
            Toggle(isOn: $recording) {
                Image(systemName: "waveform")
            }
            .toggleStyle(.button)
            .help("Aufnahme mitspielen")
            .accessibilityLabel("Aufnahme mitspielen")
        }
        .overlay(alignment: .bottom) {
            if let problem = player.problem {
                Text(problem).font(.caption).foregroundStyle(.red).offset(y: 18)
            }
        }
    }
}

/// One track: its sound or instrument, mute and solo, volume and pan.
private struct TrackRow: View {
    let session: LogicSession
    let voice: LogicScore.Voice
    let colour: Color

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 8) {
                RoundedRectangle(cornerRadius: 2).fill(colour).frame(width: 4, height: 18)
                Text(voice.displayName).font(.callout.weight(.medium)).lineLimit(1)
                Spacer()
                route
                toggle("M", on: session.muted.contains(voice.id), tint: .orange) { session.toggleMute(voice.id) }
                toggle("S", on: session.soloed.contains(voice.id), tint: .yellow) { session.toggleSolo(voice.id) }
            }
            HStack(spacing: 10) {
                Image(systemName: "speaker.wave.2").font(.caption).foregroundStyle(.secondary)
                Slider(value: Binding(
                    get: { session.mixer[voice.id].volume },
                    set: { session.setMix(voice.id, volume: $0) }), in: 0...LogicMixer.maxVolume)
                Text("L").font(.caption2).foregroundStyle(.secondary)
                Slider(value: Binding(
                    get: { session.mixer[voice.id].pan },
                    set: { session.setMix(voice.id, pan: $0) }), in: -1...1)
                    .frame(maxWidth: 110)
                Text("R").font(.caption2).foregroundStyle(.secondary)
            }
            .controlSize(.small)
        }
    }

    private var route: some View {
        let instrument = session.instrument(of: voice.id)
        let connected = session.output(of: voice.id) != nil
        return Menu {
            Button {
                Task { await session.assign(voice.id, to: nil) }
            } label: {
                chosen("Klang der App", instrument == nil)
            }
            if !session.instruments.isEmpty {
                Section("MIDI-Instrumente") {
                    ForEach(session.instruments, id: \.id) { candidate in
                        Button {
                            Task { await session.assign(voice.id, to: candidate.id) }
                        } label: {
                            chosen("\(candidate.name) · Kanal \(candidate.channel)", candidate.id == instrument?.id)
                        }
                    }
                }
            }
        } label: {
            HStack(spacing: 4) {
                if let instrument {
                    Image(systemName: connected ? "cable.connector" : "cable.connector.slash")
                        .foregroundStyle(connected ? Theme.accent : .orange)
                    Text(instrument.name)
                } else {
                    Image(systemName: "speaker.wave.2")
                    Text("App")
                }
            }
            .font(.caption)
            .lineLimit(1)
        }
        .fixedSize()
        .help(instrument != nil && !connected
            ? String(localized: "Nicht verbunden, die Spur spielt mit dem Klang der App.") : "")
    }

    @ViewBuilder
    private func chosen(_ title: String, _ selected: Bool) -> some View {
        if selected {
            Label(title, systemImage: "checkmark")
        } else {
            Text(title)
        }
    }

    private func toggle(_ letter: String, on: Bool, tint: Color, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Text(letter)
                .font(.caption.weight(.bold))
                .frame(width: 24, height: 22)
                .background(on ? tint : Color.secondary.opacity(0.15), in: RoundedRectangle(cornerRadius: 5))
                .foregroundStyle(on ? Color.black : Color.primary)
        }
        .buttonStyle(.plain)
    }
}

// MARK: Piano roll

/// The notes of every track over time, coloured per track, with bar lines and section names; a tap moves the
/// playhead. The notes are drawn once, only the playhead follows the player.
struct PianoRoll: View {
    let score: LogicScore
    let voices: [LogicScore.Voice]
    let player: LogicPlayer
    private let pointsPerSecond: CGFloat = 40

    static let palette: [Color] = [
        Theme.accent, .orange, .blue, .pink, .purple, .yellow, .teal, .red, .mint, .indigo, .brown, .cyan,
    ]

    static func colour(_ index: Int) -> Color { palette[index % palette.count] }

    var body: some View {
        GeometryReader { box in
            let width = max(box.size.width, CGFloat(score.durationSeconds) * pointsPerSecond)
            ScrollView(.horizontal) {
                ZStack(alignment: .topLeading) {
                    notes(width: width, height: box.size.height)
                    Playhead(player: player, pointsPerSecond: width / CGFloat(max(1, score.durationSeconds)))
                }
                .frame(width: width, height: box.size.height)
                .contentShape(Rectangle())
                .onTapGesture { point in
                    player.seek(to: Double(point.x / width) * score.durationSeconds)
                }
            }
        }
    }

    private func notes(width: CGFloat, height: CGFloat) -> some View {
        let pitches = voices.flatMap { $0.notes.map(\.noteNumber) }
        let low = (pitches.min() ?? 48) - 2
        let high = (pitches.max() ?? 72) + 2
        let top: CGFloat = 18
        let row = (height - top) / CGFloat(max(1, high - low + 1))
        let length = CGFloat(max(1, score.durationSeconds))
        return Canvas { context, _ in
            let x = { (ticks: Int) in CGFloat(score.seconds(ticks)) / length * width }
            for line in score.barLines {
                context.fill(Path(CGRect(x: x(line), y: top, width: 0.5, height: height - top)), with: .color(.secondary.opacity(0.18)))
            }
            for section in score.sections {
                context.fill(Path(CGRect(x: x(section.startTicks), y: 0, width: 1, height: height)), with: .color(.secondary.opacity(0.45)))
                context.draw(
                    Text(section.name).font(.caption2.weight(.semibold)).foregroundStyle(.secondary),
                    at: CGPoint(x: x(section.startTicks) + 4, y: 2), anchor: .topLeading)
            }
            for (index, voice) in voices.enumerated() {
                let colour = Self.colour(index)
                for note in voice.notes {
                    let rect = CGRect(
                        x: x(note.startTicks), y: top + CGFloat(high - note.noteNumber) * row,
                        width: max(1.5, x(note.startTicks + note.durationTicks) - x(note.startTicks)),
                        height: max(1.5, row - 0.5))
                    context.fill(Path(roundedRect: rect, cornerRadius: min(2, row / 3)), with: .color(colour.opacity(0.85)))
                }
            }
        }
        .frame(width: width, height: height)
    }
}

private struct Playhead: View {
    let player: LogicPlayer
    let pointsPerSecond: CGFloat

    var body: some View {
        Rectangle()
            .fill(Color.primary)
            .frame(width: 1.5)
            .offset(x: CGFloat(player.position) * pointsPerSecond)
            .allowsHitTesting(false)
    }
}
