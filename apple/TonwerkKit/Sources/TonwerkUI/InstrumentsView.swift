import SwiftUI
import TonwerkKit

/// The instruments page (`InstrumentsPage.vue`): the named sounds with their editor, the MIDI instrument library,
/// and a keyboard at the bottom that plays the sound being edited or a chosen instrument.
struct InstrumentsView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        Group {
            if let session = model.instrumentsPage {
                InstrumentsSessionView(session: session)
            } else {
                ProgressView().onAppear { model.openInstruments() }
            }
        }
        .navigationTitle("Instrumente")
    }
}

private struct InstrumentsSessionView: View {
    @Bindable var session: InstrumentsSession
    @State private var editing: InstrumentsSession.Draft?
    @State private var noting: Noting?
    @State private var deleting: Components.Schemas.Instrument?
    @State private var confirmSoundDelete = false
    @State private var bluetooth = false

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                if let problem = session.problem ?? session.library.problem {
                    Label(problem, systemImage: "exclamationmark.triangle").foregroundStyle(.red)
                }
                sounds
                instruments
            }
            .padding()
            .frame(maxWidth: 1100, alignment: .leading)
            .frame(maxWidth: .infinity)
        }
        .safeAreaInset(edge: .bottom) { keyboard }
        .sheet(item: $editing) { draft in
            InstrumentEditor(draft: draft, outputs: session.outputs) { saved in
                await session.save(saved)
            }
        }
        .sheet(item: $noting) { noting in
            NoteEditor(title: noting.instrument.name, note: noting.instrument.note ?? "") { note in
                await session.setNote(noting.instrument, note)
            }
        }
        #if os(iOS)
        .sheet(isPresented: $bluetooth) { BluetoothMidiView() }
        #endif
        .confirmationDialog(
            "„\(deleting?.name ?? "")“ löschen?", isPresented: Binding(
                get: { deleting != nil }, set: { if !$0 { deleting = nil } }),
            titleVisibility: .visible
        ) {
            Button("Löschen", role: .destructive) {
                if let instrument = deleting {
                    Task { await session.delete(instrument) }
                }
            }
        } message: {
            Text("Spuren, die es spielen, spielen danach in der App.")
        }
        .confirmationDialog(
            "„\(session.editing ?? "")“ löschen?", isPresented: $confirmSoundDelete, titleVisibility: .visible
        ) {
            Button("Löschen", role: .destructive) {
                Task { await session.deleteSound() }
            }
        } message: {
            Text("Spuren mit diesem Klang behalten ihre Kopie davon.")
        }
    }

    // MARK: Sounds

    private var sounds: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                CardHeading("Klänge", systemImage: "waveform")
                Spacer()
                Menu {
                    Button("Analog") { session.startNew(.analog) }
                    Button("FM") { session.startNew(.fm) }
                } label: {
                    Label("Neu", systemImage: "plus")
                }
                .fixedSize()
            }
            if session.library.named.isEmpty {
                Text("Noch keine gespeicherten Klänge. Stell einen ein und speichere ihn unter einem Namen.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
            } else {
                ScrollView(.horizontal, showsIndicators: false) {
                    HStack(spacing: 6) {
                        ForEach(session.library.named) { sound in
                            Button {
                                session.open(sound)
                            } label: {
                                HStack(spacing: 4) {
                                    Text(sound.name)
                                    Text(sound.engine == .fm ? "FM" : "Analog").font(.caption2).opacity(0.7)
                                }
                            }
                            .buttonStyle(.bordered)
                            .tint(sound.id == session.editing?.uppercased() ? Theme.accent : nil)
                        }
                    }
                }
            }
            HStack(spacing: 8) {
                TextField("Name des Klangs", text: $session.name)
                    .textFieldStyle(.roundedBorder)
                    .frame(maxWidth: 260)
                Button("Speichern") {
                    Task { await session.save() }
                }
                .buttonStyle(.borderedProminent)
                .disabled(!session.canSave)
                if session.editing != nil {
                    Button(role: .destructive) {
                        confirmSoundDelete = true
                    } label: {
                        Image(systemName: "trash")
                    }
                    .help("Klang löschen")
                }
            }
            SoundEditor(sound: $session.draft)
        }
        .card()
    }

    // MARK: Instruments

    private var instruments: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                CardHeading("MIDI-Instrumente", systemImage: "cable.connector")
                Spacer()
                midiMenu
                Button {
                    editing = InstrumentsSession.Draft()
                } label: {
                    Label("Hinzufügen", systemImage: "plus")
                }
            }
            Text("Ein Name für einen MIDI-Ausgang und Kanal. Die Logic-Seite legt Spuren darauf, das Logic-Projekt routet sie dorthin.")
                .font(.callout)
                .foregroundStyle(.secondary)
            if session.instruments.isEmpty {
                Text("Noch keine Instrumente.").font(.callout).foregroundStyle(.secondary)
            }
            ForEach(session.instruments, id: \.id) { instrument in
                InstrumentRow(
                    instrument: instrument, connected: session.midiTarget(instrument.id) != nil,
                    playing: session.target == .instrument(instrument.id),
                    play: {
                        session.target = session.target == .instrument(instrument.id) ? .sound : .instrument(instrument.id)
                    },
                    edit: { editing = InstrumentsSession.Draft(instrument) },
                    note: { noting = Noting(instrument: instrument) },
                    delete: { deleting = instrument })
                Divider()
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
            Label("\(session.outputs.outputs.count) MIDI", systemImage: "cable.connector").font(.caption)
        }
        .fixedSize()
    }

    // MARK: Keyboard

    private var keyboard: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Image(systemName: "pianokeys").foregroundStyle(Theme.accent)
                Picker("Spielt", selection: $session.target) {
                    Text(session.name.isEmpty ? String(localized: "Bearbeiteter Klang") : session.name)
                        .tag(InstrumentsSession.Target.sound)
                    ForEach(session.instruments, id: \.id) { instrument in
                        Text(instrument.name).tag(InstrumentsSession.Target.instrument(instrument.id))
                    }
                }
                .fixedSize()
                if case .instrument(let id) = session.target, session.midiTarget(id) == nil {
                    Text("nicht verbunden").font(.caption).foregroundStyle(.orange)
                }
                if let problem = session.keyboard.problem {
                    Text(problem).font(.caption).foregroundStyle(.red)
                }
            }
            TestKeyboard(noteOn: session.noteOn, noteOff: session.noteOff)
        }
        .padding(.horizontal)
        .padding(.vertical, 8)
        .frame(maxWidth: 1100)
        .frame(maxWidth: .infinity)
        .background(.bar)
    }
}

private struct InstrumentRow: View {
    let instrument: Components.Schemas.Instrument
    let connected: Bool
    let playing: Bool
    let play: () -> Void
    let edit: () -> Void
    let note: () -> Void
    let delete: () -> Void

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: connected ? "cable.connector" : "cable.connector.slash")
                .foregroundStyle(connected ? Theme.accent : .secondary)
            VStack(alignment: .leading, spacing: 2) {
                Text(instrument.name).font(.callout.weight(.medium))
                Text(details).font(.caption).foregroundStyle(.secondary).lineLimit(2)
                if let text = instrument.note, !text.isEmpty {
                    Text(text).font(.caption).foregroundStyle(.secondary).lineLimit(2)
                }
            }
            Spacer()
            Button(action: play) {
                Image(systemName: playing ? "pianokeys.inverse" : "pianokeys")
            }
            .help("Mit der Tastatur spielen")
            Button(action: note) {
                Image(systemName: instrument.note?.isEmpty == false ? "text.bubble.fill" : "text.bubble")
            }
            .help("Notiz")
            Menu {
                Button("Bearbeiten …", action: edit)
                Button("Löschen", role: .destructive, action: delete)
            } label: {
                Image(systemName: "ellipsis.circle")
            }
            .fixedSize()
        }
        .buttonStyle(.borderless)
    }

    private var details: String {
        var text = "\(instrument.port) · Kanal \(instrument.channel)"
        if instrument.kind == .drumMachine {
            text += " · " + String(localized: "Drumcomputer")
            if let drums = instrument.drums {
                let notes = [("Kick", drums.kick), ("Snare", drums.snare), ("HH", drums.closedHiHat), ("Crash", drums.crash)]
                text += " (" + notes.compactMap { name, note in note.map { "\(name) \($0)" } }.joined(separator: ", ") + ")"
            }
        }
        return text
    }
}

extension InstrumentsSession.Draft: Identifiable {}

/// The instrument whose note is being written, for the sheet.
private struct Noting: Identifiable {
    let instrument: Components.Schemas.Instrument
    var id: Int64 { instrument.id }
}

/// Adds or changes an instrument: name, port (one of the outputs or typed), channel, and a drum machine's notes.
private struct InstrumentEditor: View {
    @State var draft: InstrumentsSession.Draft
    let outputs: MidiOutputs
    let save: (InstrumentsSession.Draft) async -> Bool
    @Environment(\.dismiss) private var dismiss
    @State private var saving = false

    private static let drums: [(key: String, name: String)] = [
        ("kick", "Kick"), ("snare", "Snare"), ("closedHiHat", "Hi-Hat zu"), ("openHiHat", "Hi-Hat offen"),
        ("crash", "Crash"), ("clap", "Clap"),
    ]

    var body: some View {
        NavigationStack {
            Form {
                TextField("Name", text: $draft.name, prompt: Text("Mother32"))
                Section("MIDI") {
                    if !outputs.outputs.isEmpty {
                        Picker("Ausgang", selection: $draft.port) {
                            if !outputs.outputs.contains(where: { $0.name == draft.port }) {
                                Text(draft.port.isEmpty ? String(localized: "Wählen …") : draft.port).tag(draft.port)
                            }
                            ForEach(outputs.outputs) { output in
                                Text(output.displayName).tag(output.name)
                            }
                        }
                    }
                    TextField("Ausgang (Name)", text: $draft.port, prompt: Text("MIDI4x4 Midi Out 1"))
                    Picker("Kanal", selection: $draft.channel) {
                        ForEach(1...16, id: \.self) { Text("\($0)").tag($0) }
                    }
                    Toggle("Drumcomputer", isOn: $draft.drumMachine)
                }
                if draft.drumMachine {
                    Section("Noten der Trommeln") {
                        ForEach(Self.drums, id: \.key) { drum in
                            Stepper(value: Binding(
                                get: { draft.drums[drum.key] ?? 36 },
                                set: { draft.drums[drum.key] = $0 }), in: 0...127) {
                                HStack {
                                    Text(drum.name)
                                    Spacer()
                                    Text("\(draft.drums[drum.key] ?? 36)").monospacedDigit().foregroundStyle(.secondary)
                                }
                            }
                        }
                    }
                }
            }
            .formStyle(.grouped)
            .navigationTitle(draft.id == nil ? "Instrument hinzufügen" : "Instrument bearbeiten")
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Abbrechen") { dismiss() }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Sichern") {
                        saving = true
                        Task {
                            if await save(draft) {
                                dismiss()
                            }
                            saving = false
                        }
                    }
                    .disabled(!draft.valid || saving)
                }
            }
        }
        .frame(minWidth: 360, minHeight: 420)
    }
}

/// What the user wrote about an instrument (which mixer input it is on, …).
private struct NoteEditor: View {
    let title: String
    @State var note: String
    let save: (String) async -> Void
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            TextEditor(text: $note)
                .padding()
                .navigationTitle(title)
                .toolbar {
                    ToolbarItem(placement: .cancellationAction) {
                        Button("Abbrechen") { dismiss() }
                    }
                    ToolbarItem(placement: .confirmationAction) {
                        Button("Sichern") {
                            Task {
                                await save(note)
                                dismiss()
                            }
                        }
                    }
                }
        }
        .frame(minWidth: 360, minHeight: 260)
    }
}
