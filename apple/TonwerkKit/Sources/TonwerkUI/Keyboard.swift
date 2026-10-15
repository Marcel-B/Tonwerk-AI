import AVFoundation
import SwiftUI

/// The synthesizer the instruments page's keyboard plays, on an engine of its own (`keyboardVoice.ts`). A key sounds
/// as long as it is held: the voices know note on and off, so nothing has to be scheduled long and cut.
@MainActor
final class KeyboardPlayer {
    private let engine = AVAudioEngine()
    private var node: AVAudioUnitMIDIInstrument?
    private var unit: TonwerkSynthUnit?
    private var sound = SynthSound()
    private var held: Set<Int> = []
    private(set) var problem: String?

    func play(_ sound: SynthSound) {
        self.sound = sound
        unit?.play(sound)
    }

    func noteOn(_ key: Int, velocity: Int = 100) {
        guard start() else { return }
        held.insert(key)
        send([0x90, UInt8(key), UInt8(velocity)])
    }

    func noteOff(_ key: Int) {
        guard held.remove(key) != nil else { return }
        send([0x80, UInt8(key), 0])
    }

    /// Every key let go, as when the keyboard is switched to another instrument.
    func release() {
        for key in held {
            send([0x80, UInt8(key), 0])
        }
        held = []
    }

    private func send(_ bytes: [UInt8]) {
        guard let unit, let node else { return }
        if let schedule = unit.scheduleMIDIEventBlock {
            bytes.withUnsafeBufferPointer { schedule(AUEventSampleTimeImmediate, 0, bytes.count, $0.baseAddress!) }
        } else if bytes[0] & 0xF0 == 0x90 {
            node.startNote(bytes[1], withVelocity: bytes[2], onChannel: 0)
        } else {
            node.stopNote(bytes[1], onChannel: 0)
        }
    }

    /// The engine starts with the first key, not with the page, so the page costs nothing until it is played.
    private func start() -> Bool {
        if node == nil {
            guard case let (node, unit)? = TonwerkSynthUnit.instrument(playing: sound) else {
                problem = String(localized: "Der Synthesizer ließ sich nicht laden.")
                return false
            }
            engine.attach(node)
            engine.connect(node, to: engine.mainMixerNode, format: nil)
            self.node = node
            self.unit = unit
        }
        guard !engine.isRunning else { return true }
        do {
            #if os(iOS)
            try AVAudioSession.sharedInstance().setCategory(.playback)
            try AVAudioSession.sharedInstance().setActive(true)
            #endif
            try engine.start()
            problem = nil
            return true
        } catch {
            problem = error.localizedDescription
            return false
        }
    }
}

/// Two octaves to play a sound or an instrument, the octave movable (`TestKeyboard.vue`). Every key follows its own
/// finger, so chords can be held.
struct TestKeyboard: View {
    let noteOn: (Int) -> Void
    let noteOff: (Int) -> Void
    @AppStorage("keyboardOctave") private var octave = 4

    private static let whites = [0, 2, 4, 5, 7, 9, 11]
    private static let blacks: [(offset: Int, after: Int)] = [(1, 0), (3, 1), (6, 3), (8, 4), (10, 5)]

    var body: some View {
        VStack(spacing: 8) {
            HStack {
                Button {
                    octave = max(1, octave - 1)
                } label: {
                    Image(systemName: "chevron.left")
                }
                .disabled(octave <= 1)
                Text("C\(octave) – C\(octave + 2)").font(.caption.monospacedDigit()).foregroundStyle(.secondary)
                Button {
                    octave = min(6, octave + 1)
                } label: {
                    Image(systemName: "chevron.right")
                }
                .disabled(octave >= 6)
            }
            .buttonStyle(.borderless)
            keys.frame(height: 130)
        }
    }

    /// MIDI note of C in the shown octave, C4 being middle C (60).
    private var base: Int { (octave + 1) * 12 }

    private var keys: some View {
        GeometryReader { geometry in
            let count = Self.whites.count * 2 + 1
            let width = geometry.size.width / CGFloat(count)
            ZStack(alignment: .topLeading) {
                HStack(spacing: 1) {
                    ForEach(0..<count, id: \.self) { index in
                        let note = base + 12 * (index / 7) + Self.whites[index % 7]
                        PianoKey(note: note, black: false, noteOn: noteOn, noteOff: noteOff)
                    }
                }
                ForEach(0..<10, id: \.self) { index in
                    let black = Self.blacks[index % 5]
                    let note = base + 12 * (index / 5) + black.offset
                    let white = 7 * (index / 5) + black.after
                    PianoKey(note: note, black: true, noteOn: noteOn, noteOff: noteOff)
                        .frame(width: width * 0.6, height: geometry.size.height * 0.6)
                        .offset(x: width * CGFloat(white + 1) - width * 0.3)
                }
            }
        }
    }
}

private struct PianoKey: View {
    let note: Int
    let black: Bool
    let noteOn: (Int) -> Void
    let noteOff: (Int) -> Void
    @State private var down = false

    var body: some View {
        RoundedRectangle(cornerRadius: 4)
            .fill(fill)
            .overlay(RoundedRectangle(cornerRadius: 4).stroke(Color.secondary.opacity(0.4)))
            .overlay(alignment: .bottom) {
                if !black, note % 12 == 0 {
                    Text("C\(note / 12 - 1)").font(.caption2).foregroundStyle(.secondary).padding(.bottom, 4)
                }
            }
            .contentShape(Rectangle())
            .gesture(
                DragGesture(minimumDistance: 0)
                    .onChanged { _ in
                        guard !down else { return }
                        down = true
                        noteOn(note)
                    }
                    .onEnded { _ in
                        down = false
                        noteOff(note)
                    })
            .onDisappear {
                if down {
                    down = false
                    noteOff(note)
                }
            }
            .accessibilityLabel(Text("Taste \(note)"))
    }

    private var fill: Color {
        if down { return Theme.accent }
        return black ? Color.primary.opacity(0.85) : Color.white
    }
}
