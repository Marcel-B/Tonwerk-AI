import CoreMIDI
import Foundation
import Observation
import TonwerkKit

/// The instruments page's state, the native counterpart of `InstrumentsPage.vue`: the MIDI instrument library, the
/// named sounds with the sound being edited, and the keyboard that plays either. Kept by ``AppModel`` once opened,
/// so a page change keeps an unsaved sound.
@MainActor
@Observable
final class InstrumentsSession {
    /// What the keyboard plays.
    enum Target: Hashable {
        case sound
        case instrument(Int64)
    }

    private(set) var instruments: [Components.Schemas.Instrument] = []
    private(set) var problem: String?

    /// The sound in the editor, heard on the keyboard with every change.
    var draft = SynthSound() {
        didSet { keyboard.play(draft) }
    }
    /// The named sound the draft came from, nil for a new one.
    private(set) var editing: String?
    var name = ""
    /// The draft as last loaded or saved, to tell whether it has changes.
    private(set) var saved = SynthSound()
    var target = Target.sound {
        didSet { keyboard.release() }
    }

    let keyboard = KeyboardPlayer()
    let outputs = MidiOutputs()

    private unowned let model: AppModel

    init(model: AppModel) {
        self.model = model
        keyboard.play(draft)
        Task { await load() }
    }

    var library: SoundLibrary { model.soundLibrary }

    func load() async {
        instruments = await model.logicInstruments().sorted {
            $0.name.localizedStandardCompare($1.name) == .orderedAscending
        }
        await library.load()
    }

    // MARK: Sounds

    var changed: Bool {
        draft != saved || name.trimmingCharacters(in: .whitespaces) != (editing ?? "")
    }

    var canSave: Bool {
        let trimmed = name.trimmingCharacters(in: .whitespaces)
        return !trimmed.isEmpty && trimmed.count <= 64 && changed
    }

    func open(_ sound: SoundLibrary.Named) {
        load(SynthSound(json: sound.patch), from: sound.name)
    }

    func startNew(_ engine: SynthSound.Engine) {
        load(.defaults(for: "Melody", engine: engine), from: nil)
    }

    private func load(_ sound: SynthSound, from name: String?) {
        draft = sound
        saved = sound
        editing = name
        self.name = name ?? ""
    }

    func save() async {
        let trimmed = name.trimmingCharacters(in: .whitespaces)
        guard await library.save(draft, as: trimmed) else { return }
        saved = draft
        // The server keeps a name's spelling as first saved; show the entry that was written.
        editing = library.preset(named: trimmed)?.name ?? trimmed
        name = editing ?? trimmed
        // Tracks that follow this sound hear the change.
        model.logic?.reroute()
    }

    func deleteSound() async {
        guard let editing else { return }
        await library.delete(editing)
        model.logic?.reroute()
        startNew(draft.engine)
    }

    // MARK: Instruments

    struct Draft: Equatable {
        var id: Int64?
        var name = ""
        var port = ""
        var channel = 1
        var drumMachine = false
        var drums = LogicForm.generalMidiDrums

        init() {}

        init(_ instrument: Components.Schemas.Instrument) {
            id = instrument.id
            name = instrument.name
            port = instrument.port
            channel = Int(instrument.channel)
            drumMachine = instrument.kind == .drumMachine
            if let notes = instrument.drums {
                let given: [String: Int32?] = [
                    "kick": notes.kick, "snare": notes.snare, "closedHiHat": notes.closedHiHat,
                    "openHiHat": notes.openHiHat, "crash": notes.crash, "clap": notes.clap,
                ]
                drums.merge(given.compactMapValues { $0.map(Int.init) }) { _, own in own }
            }
        }

        var input: Components.Schemas.InstrumentInput {
            Components.Schemas.InstrumentInput(
                name: name.trimmingCharacters(in: .whitespaces), port: port.trimmingCharacters(in: .whitespaces),
                channel: Int32(channel), kind: drumMachine ? .drumMachine : .synth,
                drums: drumMachine ? drumNotes : nil)
        }

        private var drumNotes: Components.Schemas.DrumNotes {
            func note(_ key: String) -> Int32? { drums[key].map(Int32.init) }
            return Components.Schemas.DrumNotes(
                kick: note("kick"), snare: note("snare"), closedHiHat: note("closedHiHat"),
                openHiHat: note("openHiHat"), crash: note("crash"), clap: note("clap"))
        }

        var valid: Bool {
            !name.trimmingCharacters(in: .whitespaces).isEmpty && !port.trimmingCharacters(in: .whitespaces).isEmpty
        }
    }

    /// Adds or changes an instrument; the Logic page's channels and drum notes follow.
    func save(_ draft: Draft) async -> Bool {
        do {
            try await model.saveInstrument(draft.id, draft.input)
            problem = nil
        } catch {
            problem = error.localizedDescription
            return false
        }
        await instrumentsChanged()
        return true
    }

    func delete(_ instrument: Components.Schemas.Instrument) async {
        do {
            try await model.deleteInstrument(instrument.id)
            problem = nil
        } catch {
            problem = error.localizedDescription
        }
        if target == .instrument(instrument.id) {
            target = .sound
        }
        await instrumentsChanged()
    }

    func setNote(_ instrument: Components.Schemas.Instrument, _ note: String) async {
        do {
            try await model.setInstrumentNote(instrument.id, note)
            problem = nil
            await load()
        } catch {
            problem = error.localizedDescription
        }
    }

    private func instrumentsChanged() async {
        await load()
        if let logic = model.logic {
            await logic.loadLibrary()
            await logic.convert()
        }
    }

    // MARK: Keyboard

    func noteOn(_ key: Int) {
        switch target {
        case .sound:
            keyboard.noteOn(key)
        case .instrument(let id):
            guard case let (output, channel)? = midiTarget(id) else { return }
            MidiPanic.send([0x90 | channel, UInt8(key), 100], to: [output])
        }
    }

    func noteOff(_ key: Int) {
        switch target {
        case .sound:
            keyboard.noteOff(key)
        case .instrument(let id):
            guard case let (output, channel)? = midiTarget(id) else { return }
            MidiPanic.send([0x80 | channel, UInt8(key), 0], to: [output])
        }
    }

    func instrument(_ id: Int64) -> Components.Schemas.Instrument? {
        instruments.first { $0.id == id }
    }

    /// The instrument's output and channel (0 to 15), when it is connected.
    func midiTarget(_ id: Int64) -> (MIDIEndpointRef, UInt8)? {
        guard let instrument = instrument(id), let output = outputs.output(named: instrument.port) else { return nil }
        return (output.endpoint, UInt8(min(16, max(1, instrument.channel)) - 1))
    }
}

// MARK: Server

extension AppModel {
    func saveInstrument(_ id: Int64?, _ input: Components.Schemas.InstrumentInput) async throws {
        guard let tonwerk else { throw ServerError(String(localized: "Kein Server verbunden.")) }
        if let id {
            switch try await tonwerk.api.updateInstrument(path: .init(id: id), body: .json(input)) {
            case .ok:
                return
            case .badRequest(let refused):
                throw ServerError(problem: try? refused.body.applicationProblemJson.detail)
            case .conflict(let refused):
                throw ServerError(problem: try? refused.body.applicationProblemJson.detail)
            case .notFound:
                throw ServerError(String(localized: "Das Instrument gibt es nicht mehr."))
            case .undocumented(let status, _):
                throw ServerError(String(localized: "Der Server antwortete \(status)."))
            }
        }
        switch try await tonwerk.api.createInstrument(body: .json(input)) {
        case .created:
            return
        case .badRequest(let refused):
            throw ServerError(problem: try? refused.body.applicationProblemJson.detail)
        case .conflict(let refused):
            throw ServerError(problem: try? refused.body.applicationProblemJson.detail)
        case .undocumented(let status, _):
            throw ServerError(String(localized: "Der Server antwortete \(status)."))
        }
    }

    func deleteInstrument(_ id: Int64) async throws {
        guard let tonwerk else { return }
        switch try await tonwerk.api.deleteInstrument(path: .init(id: id)) {
        case .noContent, .notFound:
            return
        case .undocumented(let status, _):
            throw ServerError(String(localized: "Der Server antwortete \(status)."))
        }
    }

    func setInstrumentNote(_ id: Int64, _ note: String) async throws {
        guard let tonwerk else { return }
        let trimmed = note.trimmingCharacters(in: .whitespacesAndNewlines)
        guard case .noContent = try await tonwerk.api.setInstrumentNote(
            path: .init(id: id), body: .json(.init(note: trimmed.isEmpty ? nil : trimmed)))
        else { throw ServerError(String(localized: "Die Notiz wurde nicht gespeichert.")) }
    }

    func openInstruments() {
        if instrumentsPage == nil {
            instrumentsPage = InstrumentsSession(model: self)
        }
        page = .instruments
    }
}
