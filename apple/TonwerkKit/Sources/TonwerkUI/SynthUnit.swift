import AudioToolbox
import CoreMIDI
import AVFoundation
import TonwerkDSP

/// Tonwerk's synthesizers as an Audio Unit inside the app, so ``LogicPlayer``'s sequencer can send a track's notes to
/// it like to Apple's sampler, and the instruments page's keyboard can play it. Registered in this process only
/// (`registerSubclass`), so it needs no app extension and shows up in no other app; its own subtype keeps it apart
/// from the Tonwerk Synth plugin (`aumu Twsy Bvlp`) when that is installed.
final class TonwerkSynthUnit: AUAudioUnit {
    static let component = AudioComponentDescription(
        componentType: kAudioUnitType_MusicDevice, componentSubType: fourCharacterCode("Twap"),
        componentManufacturer: fourCharacterCode("Bvlp"),
        // The Mac app is sandboxed, and a sandboxed process finds no component that is not marked safe for it: without
        // the flag the keyboard said "Der Synthesizer ließ sich nicht laden." and the Logic page fell back to the sampler.
        componentFlags: AudioComponentFlags.sandboxSafe.rawValue, componentFlagsMask: 0)

    private static let registration: Void = {
        AUAudioUnit.registerSubclass(
            TonwerkSynthUnit.self, as: component, name: "Bvelop: Tonwerk", version: 1)
    }()

    /// The unit made last; the node may hand back a bridge as `auAudioUnit` instead of this class.
    nonisolated(unsafe) private static var lastMade: TonwerkSynthUnit?

    /// A new instrument node with the synthesizer, playing `sound`; made asynchronously, as Apple asks for an
    /// `AUAudioUnit` subclass, and in this process, the only one the unit is registered in.
    @MainActor
    static func loadInstrument(playing sound: SynthSound) async throws -> (AVAudioUnitMIDIInstrument, TonwerkSynthUnit) {
        _ = registration
        lastMade = nil
        // iOS has no out-of-process option to rule out; a unit registered with `registerSubclass` is in-process there.
        #if os(macOS)
        let options: AudioComponentInstantiationOptions = .loadInProcess
        #else
        let options: AudioComponentInstantiationOptions = []
        #endif
        let made = try await AVAudioUnit.instantiate(with: component, options: options)
        guard let node = made as? AVAudioUnitMIDIInstrument,
            let unit = node.auAudioUnit as? TonwerkSynthUnit ?? lastMade
        else { throw LoadError.unexpectedUnit(String(describing: type(of: made.auAudioUnit))) }
        lastMade = nil
        unit.play(sound)
        return (node, unit)
    }

    enum LoadError: LocalizedError {
        case unexpectedUnit(String)

        var errorDescription: String? {
            switch self {
            case .unexpectedUnit(let name): "Statt des Synthesizers kam \(name)."
            }
        }
    }

    private let state: RenderState
    private var busArray: AUAudioUnitBusArray!

    override init(componentDescription: AudioComponentDescription, options: AudioComponentInstantiationOptions = []) throws {
        state = RenderState()
        try super.init(componentDescription: componentDescription, options: options)
        let format = AVAudioFormat(standardFormatWithSampleRate: 48000, channels: 2)!
        let bus = try AUAudioUnitBus(format: format)
        bus.maximumChannelCount = 2
        busArray = AUAudioUnitBusArray(audioUnit: self, busType: .output, busses: [bus])
        maximumFramesToRender = RenderState.capacity
        Self.lastMade = self
    }

    override var outputBusses: AUAudioUnitBusArray { busArray }

    /// MIDI 1.0, so a host that sends event lists sends them in the protocol ``RenderState`` reads.
    override var audioUnitMIDIProtocol: MIDIProtocolID { ._1_0 }

    override func allocateRenderResources() throws {
        try super.allocateRenderResources()
        tw_synth_prepare(state.synth, busArray[0].format.sampleRate)
    }

    /// Takes effect at the next render, sounding notes included.
    func play(_ sound: SynthSound) {
        var patch = sound.patch
        tw_synth_set_patch(state.synth, &patch)
    }

    override var internalRenderBlock: AUInternalRenderBlock {
        let state = self.state
        return { _, timestamp, frameCount, _, outputData, events, _ in
            state.render(timestamp: timestamp, frames: frameCount, into: outputData, events: events)
        }
    }
}

/// What the render block needs, apart from the unit, so the block keeps no reference to it: the synthesizer and
/// buffers of its own for a host that hands over none.
private final class RenderState {
    static let capacity: AUAudioFrameCount = 4096

    let synth: OpaquePointer
    private let left = UnsafeMutablePointer<Float>.allocate(capacity: Int(capacity))
    private let right = UnsafeMutablePointer<Float>.allocate(capacity: Int(capacity))

    init() {
        synth = tw_synth_create()
    }

    deinit {
        tw_synth_destroy(synth)
        left.deallocate()
        right.deallocate()
    }

    func render(
        timestamp: UnsafePointer<AudioTimeStamp>, frames: AUAudioFrameCount,
        into output: UnsafeMutablePointer<AudioBufferList>, events: UnsafePointer<AURenderEvent>?
    ) -> AUAudioUnitStatus {
        let buffers = UnsafeMutableAudioBufferListPointer(output)
        for index in 0..<buffers.count {
            if buffers[index].mData == nil {
                // Only the buffers of our own are limited; the iPhone's engine may ask for more frames than the
                // capacity (a song at another sample rate, the screen locked), which a host's buffers then hold.
                guard frames <= Self.capacity else { return kAudioUnitErr_TooManyFramesToProcess }
                buffers[index].mData = UnsafeMutableRawPointer(index == 0 ? left : right)
            }
            buffers[index].mDataByteSize = frames * UInt32(MemoryLayout<Float>.size)
        }
        guard buffers.count > 0, let first = buffers[0].mData?.assumingMemoryBound(to: Float.self) else {
            return noErr
        }
        let second = buffers.count > 1 ? buffers[1].mData?.assumingMemoryBound(to: Float.self) : nil

        // Notes are played at their sample within the buffer: render up to the next event, take it, go on.
        let now = AUEventSampleTime(timestamp.pointee.mSampleTime)
        var event = events
        var done: AUAudioFrameCount = 0
        while done < frames {
            while let current = event, current.pointee.head.eventSampleTime - now <= AUEventSampleTime(done) {
                handle(current)
                event = current.pointee.head.next.map { UnsafePointer($0) }
            }
            var until = frames
            if let next = event {
                until = AUAudioFrameCount(min(AUEventSampleTime(frames), next.pointee.head.eventSampleTime - now))
            }
            tw_synth_render(synth, first + Int(done), second.map { $0 + Int(done) }, Int32(until - done))
            done = until
        }
        while let current = event {
            handle(current)
            event = current.pointee.head.next.map { UnsafePointer($0) }
        }
        return noErr
    }

    private func handle(_ event: UnsafePointer<AURenderEvent>) {
        switch event.pointee.head.eventType {
        case .MIDI:
            let midi = event.pointee.MIDI
            tw_synth_midi(synth, midi.data.0, midi.data.1, midi.data.2)
        case .midiEventList:
            // Newer hosts send Universal MIDI Packets instead; the packets follow each other in the event's memory,
            // so they are read where they lie, not from a copy of the struct, which holds only the first.
            let raw = UnsafeRawPointer(event)
            guard let listOffset = MemoryLayout<AUMIDIEventList>.offset(of: \.eventList),
                  let packetOffset = MemoryLayout<MIDIEventList>.offset(of: \.packet),
                  let wordsOffset = MemoryLayout<MIDIEventPacket>.offset(of: \.words)
            else { return }
            let list = (raw + listOffset).assumingMemoryBound(to: MIDIEventList.self)
            var packet = (raw + listOffset + packetOffset).assumingMemoryBound(to: MIDIEventPacket.self)
            for number in 0..<list.pointee.numPackets {
                let words = (UnsafeRawPointer(packet) + wordsOffset).assumingMemoryBound(to: UInt32.self)
                handle(words: words, count: Int(packet.pointee.wordCount))
                if number + 1 < list.pointee.numPackets {
                    packet = UnsafePointer(MIDIEventPacketNext(packet))
                }
            }
        default:
            break
        }
    }

    /// Channel voice messages of MIDI 1.0 (type 2) and MIDI 2.0 (type 4, scaled down to seven bits); the rest is
    /// skipped by its length.
    private func handle(words: UnsafePointer<UInt32>, count: Int) {
        var index = 0
        while index < count {
            let word = words[index]
            let type = word >> 28
            let length: Int
            switch type {
            case 0, 1, 2, 6, 7: length = 1
            case 3, 4, 8, 9, 10: length = 2
            case 11, 12: length = 3
            default: length = 4
            }
            let status = UInt8((word >> 16) & 0xFF)
            let first = UInt8((word >> 8) & 0x7F)
            if type == 2 {
                tw_synth_midi(synth, status, first, UInt8(word & 0x7F))
            } else if type == 4, index + 1 < count {
                let second = words[index + 1]
                switch status >> 4 {
                case 0x9:
                    // A MIDI 2.0 note on may have velocity 0, which MIDI 1.0 reads as note off.
                    tw_synth_midi(synth, status, first, max(1, UInt8(second >> 25)))
                case 0x8, 0xA, 0xB:
                    tw_synth_midi(synth, status, first, UInt8(second >> 25))
                case 0xE:
                    let bend = second >> 18
                    tw_synth_midi(synth, status, UInt8(bend & 0x7F), UInt8((bend >> 7) & 0x7F))
                default:
                    break
                }
            }
            index += length
        }
    }
}

func fourCharacterCode(_ text: String) -> FourCharCode {
    text.utf8.reduce(0) { $0 << 8 | FourCharCode($1) }
}
