import AudioToolbox
import AVFoundation
import TonwerkDSP

/// Tonwerk's synthesizers as an Audio Unit inside the app, so ``LogicPlayer``'s sequencer can send a track's notes to
/// it like to Apple's sampler, and the instruments page's keyboard can play it. Registered in this process only
/// (`registerSubclass`), so it needs no app extension and shows up in no other app; its own subtype keeps it apart
/// from the Tonwerk Synth plugin (`aumu Twsy Bvlp`) when that is installed.
final class TonwerkSynthUnit: AUAudioUnit {
    static let component = AudioComponentDescription(
        componentType: kAudioUnitType_MusicDevice, componentSubType: fourCharacterCode("Twap"),
        componentManufacturer: fourCharacterCode("Bvlp"), componentFlags: 0, componentFlagsMask: 0)

    private static let registration: Void = {
        AUAudioUnit.registerSubclass(
            TonwerkSynthUnit.self, as: component, name: "Bvelop: Tonwerk", version: 1)
    }()

    /// The unit made last on this thread; the synchronous `AVAudioUnitMIDIInstrument` initialiser may hand back a
    /// bridge as `auAudioUnit` instead of this class.
    nonisolated(unsafe) private static var lastMade: TonwerkSynthUnit?

    /// A new instrument node with the synthesizer, playing `sound`.
    @MainActor
    static func instrument(playing sound: SynthSound) -> (AVAudioUnitMIDIInstrument, TonwerkSynthUnit)? {
        _ = registration
        lastMade = nil
        let node = AVAudioUnitMIDIInstrument(audioComponentDescription: component)
        guard let unit = node.auAudioUnit as? TonwerkSynthUnit ?? lastMade else { return nil }
        lastMade = nil
        unit.play(sound)
        return (node, unit)
    }

    /// The same, made asynchronously as Apple asks for an `AUAudioUnit` subclass; for the instruments page's keyboard,
    /// which must not make it inside a gesture.
    @MainActor
    static func loadInstrument(playing sound: SynthSound) async -> (AVAudioUnitMIDIInstrument, TonwerkSynthUnit)? {
        _ = registration
        lastMade = nil
        guard let made = try? await AVAudioUnit.instantiate(with: component, options: .loadInProcess),
            let node = made as? AVAudioUnitMIDIInstrument,
            let unit = node.auAudioUnit as? TonwerkSynthUnit ?? lastMade
        else { return nil }
        lastMade = nil
        unit.play(sound)
        return (node, unit)
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
        guard frames <= Self.capacity else { return kAudioUnitErr_TooManyFramesToProcess }
        let buffers = UnsafeMutableAudioBufferListPointer(output)
        let own = [left, right]
        for index in 0..<buffers.count {
            if buffers[index].mData == nil {
                buffers[index].mData = UnsafeMutableRawPointer(own[min(index, 1)])
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
        guard event.pointee.head.eventType == .MIDI else { return }
        let midi = event.pointee.MIDI
        tw_synth_midi(synth, midi.data.0, midi.data.1, midi.data.2)
    }
}

func fourCharacterCode(_ text: String) -> FourCharCode {
    text.utf8.reduce(0) { $0 << 8 | FourCharCode($1) }
}
