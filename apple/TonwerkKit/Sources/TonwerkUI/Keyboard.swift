import AVFoundation
import SwiftUI

/// The synthesizer the instruments page's keyboard plays, on an engine of its own (`keyboardVoice.ts`). A key sounds
/// as long as it is held: the voices know note on and off, so nothing has to be scheduled long and cut.
///
/// Loading the unit and starting the engine never happen inside the key's gesture: done there, the Mac app hung on
/// the first key (the key stayed lit, the sidebar stopped switching pages while scrolling still went on), as if
/// SwiftUI's gesture handling had been left half done. They run in a task of their own; keys pressed meanwhile are
/// kept in `held` and sound once the synthesizer is ready, if they are still down.
@MainActor
@Observable
final class KeyboardPlayer {
    @ObservationIgnored private let engine = AVAudioEngine()
    @ObservationIgnored private var node: AVAudioUnitMIDIInstrument?
    @ObservationIgnored private var unit: TonwerkSynthUnit?
    @ObservationIgnored private var sound = SynthSound()
    @ObservationIgnored private var held: Set<Int> = []
    @ObservationIgnored private var starting: Task<Void, Never>?
    private(set) var problem: String?

    func play(_ sound: SynthSound) {
        self.sound = sound
        unit?.play(sound)
    }

    func noteOn(_ key: Int, velocity: Int = 100) {
        held.insert(key)
        guard ready else {
            start()
            return
        }
        send([0x90, UInt8(key), UInt8(velocity)])
    }

    func noteOff(_ key: Int) {
        guard held.remove(key) != nil, ready else { return }
        send([0x80, UInt8(key), 0])
    }

    /// Every key let go, as when the keyboard is switched to another instrument.
    func release() {
        if ready {
            for key in held {
                send([0x80, UInt8(key), 0])
            }
        }
        held = []
    }

    private var ready: Bool { unit != nil && engine.isRunning }

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

    /// The engine starts with the first key, not with the page, so the page costs nothing until it is played. The
    /// engine also stops by itself when the output device changes; the next key starts it again.
    private func start() {
        guard starting == nil else { return }
        starting = Task { [weak self] in
            await Task.yield()
            guard let self else { return }
            await self.prepare()
            self.starting = nil
            guard self.ready else { return }
            for key in self.held {
                self.send([0x90, UInt8(key), 100])
            }
        }
    }

    private func prepare() async {
        if node == nil {
            guard case let (node, unit)? = await TonwerkSynthUnit.loadInstrument(playing: sound) else {
                problem = String(localized: "Der Synthesizer ließ sich nicht laden.")
                return
            }
            engine.attach(node)
            engine.connect(node, to: engine.mainMixerNode, format: nil)
            self.node = node
            self.unit = unit
        }
        guard !engine.isRunning else { return }
        do {
            #if os(iOS)
            try AVAudioSession.sharedInstance().setCategory(.playback)
            try AVAudioSession.sharedInstance().setActive(true)
            #endif
            try engine.start()
            problem = nil
        } catch {
            problem = error.localizedDescription
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
    /// Reset by SwiftUI when the drag ends and also when it is cancelled, which `onEnded` never hears of; the note
    /// follows it in `onChange`, so a key cannot stay down.
    @GestureState private var down = false
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        RoundedRectangle(cornerRadius: 4)
            .fill(fill)
            .overlay(RoundedRectangle(cornerRadius: 4).stroke(Color.secondary.opacity(0.4)))
            .overlay(alignment: .bottom) {
                if !black, note % 12 == 0 {
                    Text("C\(note / 12 - 1)").font(.caption2).foregroundStyle(Color.black.opacity(0.5)).padding(.bottom, 4)
                }
            }
            .contentShape(Rectangle())
            .gesture(DragGesture(minimumDistance: 0).updating($down) { _, down, _ in down = true })
            .onChange(of: down) { _, down in
                if down {
                    noteOn(note)
                } else {
                    noteOff(note)
                }
            }
            .onDisappear {
                if down {
                    noteOff(note)
                }
            }
            .accessibilityLabel(Text("Taste \(note)"))
    }

    private var fill: Color {
        if down { return Theme.accent }
        // Fixed greys rather than `primary`, which turns white in dark mode and made the black keys white too. In the
        // dark the white keys dim a little so they do not glare, and stay far lighter than the black ones.
        let dark = scheme == .dark
        if black {
            return Color(white: dark ? 0.06 : 0.13)
        }
        return Color(white: dark ? 0.8 : 1)
    }
}
