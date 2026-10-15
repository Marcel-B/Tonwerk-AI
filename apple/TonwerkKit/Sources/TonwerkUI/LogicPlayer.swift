import AVFoundation
import CoreMIDI
import Observation

/// Plays the converted score, the counterpart of the web page's `midiPlayer.ts` and `recording.ts`: every track on
/// a sequencer track of its own, which sends it to its instrument's MIDI output or to a sampler in the app (one per
/// track, so volume and pan apply per track), and the song's recording beside it from the count-in on.
///
/// The app's own sound is Apple's sampler for now (General MIDI on the Mac, a plain tone on the iPhone) until
/// Tonwerk's synthesizers come into the app.
@MainActor
@Observable
final class LogicPlayer {
    enum Route: Hashable {
        /// The sampler in the app.
        case app
        case midi(MIDIEndpointRef, channel: UInt8)
    }

    struct Track {
        let voice: LogicScore.Voice
        var route: Route
        var muted = false
        var soloed = false
    }

    private(set) var isPlaying = false
    /// Seconds from the start of the song, the count-in included.
    private(set) var position: Double = 0
    private(set) var duration: Double = 0
    private(set) var problem: String?
    var recordingVolume: Float = 0.8 {
        didSet { recordingNode.volume = recordingVolume }
    }

    private let engine = AVAudioEngine()
    private var sequencer: AVAudioSequencer?
    private var samplers: [String: AVAudioUnitSampler] = [:]
    private var tracks: [Track] = []
    private var sequenced: [String: AVMusicTrack] = [:]
    private let recordingNode = AVAudioPlayerNode()
    private var recording: AVAudioFile?
    private var countIn: Double = 0
    private var score: LogicScore?
    private var clock: Task<Void, Never>?

    init() {
        engine.attach(recordingNode)
        recordingNode.volume = recordingVolume
    }

    // MARK: Loading

    /// A new score: the sequencer is built anew, every voice on its track, samplers made for the voices that need
    /// one. Keeps playing from where it was if it was playing.
    func load(_ score: LogicScore, tracks: [Track], mixer: LogicMixer) {
        let wasPlaying = isPlaying
        let from = position
        stop()
        self.score = score
        self.tracks = tracks
        duration = score.durationSeconds
        countIn = score.countInSeconds
        problem = nil

        // Samplers stay attached for the tracks that keep their name, so a change of options does not reload sounds.
        for (name, sampler) in samplers where !tracks.contains(where: { $0.voice.id == name }) {
            engine.detach(sampler)
            samplers[name] = nil
        }
        for track in tracks where samplers[track.voice.id] == nil {
            let sampler = AVAudioUnitSampler()
            engine.attach(sampler)
            engine.connect(sampler, to: engine.mainMixerNode, format: nil)
            Self.loadSound(into: sampler, drums: track.voice.isDrums, kind: track.voice.kind)
            samplers[track.voice.id] = sampler
        }
        if recording != nil {
            engine.connect(recordingNode, to: engine.mainMixerNode, format: recording?.processingFormat)
        }
        apply(mixer)

        let sequencer = AVAudioSequencer(audioEngine: engine)
        let ppq = Double(score.ticksPerQuarterNote)
        sequencer.tempoTrack.addEvent(AVExtendedTempoEvent(tempo: score.tempoBpm), at: 0)
        sequenced = [:]
        for track in tracks {
            let musicTrack = sequencer.createAndAppendTrack()
            let channel: UInt32 = {
                if case .midi(_, let channel) = track.route { return UInt32(channel) }
                return track.voice.isDrums ? 9 : 0
            }()
            for note in track.voice.notes {
                let event = AVMIDINoteEvent(
                    channel: channel, key: UInt32(min(127, max(0, note.noteNumber))),
                    velocity: UInt32(min(127, max(1, note.velocity ?? 96))),
                    duration: AVMusicTimeStamp(Double(note.durationTicks) / ppq))
                musicTrack.addEvent(event, at: AVMusicTimeStamp(Double(note.startTicks) / ppq))
            }
            sequenced[track.voice.id] = musicTrack
        }
        self.sequencer = sequencer
        route()
        sequencer.prepareToPlay()
        if wasPlaying {
            play(from: from)
        } else {
            seek(to: from)
        }
    }

    /// The song's audio, to hear the score against it; nil drops it.
    func setRecording(_ url: URL?) {
        recording = url.flatMap { try? AVAudioFile(forReading: $0) }
        if let recording {
            engine.connect(recordingNode, to: engine.mainMixerNode, format: recording.processingFormat)
        }
    }

    private static func loadSound(into sampler: AVAudioUnitSampler, drums: Bool, kind: String) {
        #if os(macOS)
        let bank = URL(fileURLWithPath: "/System/Library/Components/CoreAudio.component/Contents/Resources/gs_instruments.dls")
        guard FileManager.default.fileExists(atPath: bank.path) else { return }
        // General MIDI: drums on the percussion bank, the rest by what the track is.
        let programs: [String: UInt8] = ["Bass": 33, "Chords": 4, "GuideTones": 11, "Melody": 80, "Doubling": 80, "Harmony": 80]
        let program = drums ? 0 : programs[kind] ?? 0
        try? sampler.loadSoundBankInstrument(
            at: bank, program: program,
            bankMSB: UInt8(drums ? kAUSampler_DefaultPercussionBankMSB : kAUSampler_DefaultMelodicBankMSB),
            bankLSB: UInt8(kAUSampler_DefaultBankLSB))
        #endif
    }

    // MARK: Routing and mixing

    /// Where each track goes and whether it sounds; solo wins over everything not soloed, as the web page's.
    func update(_ tracks: [Track]) {
        let channelsChanged = zip(tracks, self.tracks).contains { $0.route != $1.route }
        self.tracks = tracks
        if channelsChanged, let score {
            load(score, tracks: tracks, mixer: currentMixer)
        } else {
            route()
        }
    }

    private var currentMixer = LogicMixer()

    func apply(_ mixer: LogicMixer) {
        currentMixer = mixer
        engine.mainMixerNode.outputVolume = mixer.master
        for (name, sampler) in samplers {
            sampler.volume = mixer[name].volume
            sampler.pan = mixer[name].pan
        }
    }

    private func route() {
        let soloing = tracks.contains { $0.soloed }
        for track in tracks {
            guard let musicTrack = sequenced[track.voice.id] else { continue }
            switch track.route {
            case .app:
                if let sampler = samplers[track.voice.id] {
                    musicTrack.destinationAudioUnit = sampler
                }
            case .midi(let endpoint, _):
                musicTrack.destinationMIDIEndpoint = endpoint
            }
            musicTrack.isMuted = track.muted || (soloing && !track.soloed)
        }
    }

    // MARK: Transport

    func toggle() {
        isPlaying ? pause() : play(from: position >= duration ? 0 : position)
    }

    func play(from seconds: Double) {
        guard let sequencer, let score else { return }
        do {
            if !engine.isRunning {
                #if os(iOS)
                try AVAudioSession.sharedInstance().setCategory(.playback)
                try AVAudioSession.sharedInstance().setActive(true)
                #endif
                try engine.start()
            }
            sequencer.currentPositionInBeats = seconds * score.tempoBpm / 60
            try sequencer.start()
        } catch {
            problem = error.localizedDescription
            return
        }
        startRecording(at: seconds)
        isPlaying = true
        position = seconds
        clock?.cancel()
        clock = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .milliseconds(30))
                self?.tick()
            }
        }
    }

    func pause() {
        sequencer?.stop()
        recordingNode.stop()
        clock?.cancel()
        clock = nil
        isPlaying = false
        silence()
    }

    func stop() {
        pause()
        position = 0
    }

    func seek(to seconds: Double) {
        let target = min(max(0, seconds), duration)
        if isPlaying {
            pause()
            play(from: target)
        } else {
            position = target
            if let score {
                sequencer?.currentPositionInBeats = target * score.tempoBpm / 60
            }
        }
    }

    private func tick() {
        guard let sequencer, let score else { return }
        position = sequencer.currentPositionInBeats * 60 / score.tempoBpm
        if position >= duration {
            stop()
        }
    }

    private func startRecording(at seconds: Double) {
        recordingNode.stop()
        guard let recording else { return }
        let rate = recording.processingFormat.sampleRate
        let into = seconds - countIn
        let first = AVAudioFramePosition(max(0, into) * rate)
        guard first < recording.length else { return }
        let frames = AVAudioFrameCount(recording.length - first)
        // Started in the count-in, the recording waits for the end of it.
        let when: AVAudioTime? = into < 0
            ? AVAudioTime(hostTime: mach_absolute_time() + AVAudioTime.hostTime(forSeconds: -into))
            : nil
        recordingNode.scheduleSegment(recording, startingFrame: first, frameCount: frames, at: nil)
        recordingNode.play(at: when)
    }

    /// Notes cut off by a pause would otherwise hang on hardware synthesizers: all notes off on every channel used.
    private func silence() {
        for sampler in samplers.values {
            for channel: UInt8 in 0...15 {
                sampler.sendController(123, withValue: 0, onChannel: channel)
            }
        }
        var endpoints: Set<MIDIEndpointRef> = []
        for track in tracks {
            if case .midi(let endpoint, _) = track.route { endpoints.insert(endpoint) }
        }
        guard !endpoints.isEmpty else { return }
        MidiPanic.send(to: endpoints)
    }
}

/// All notes off on every channel of the given outputs, through an output port of its own.
enum MidiPanic {
    nonisolated(unsafe) private static var client = MIDIClientRef()
    nonisolated(unsafe) private static var port = MIDIPortRef()

    @MainActor
    static func send(to endpoints: Set<MIDIEndpointRef>) {
        if port == 0 {
            MIDIClientCreateWithBlock("Tonwerk Panic" as CFString, &client, nil)
            MIDIOutputPortCreate(client, "Tonwerk" as CFString, &port)
        }
        guard port != 0 else { return }
        var bytes: [UInt8] = []
        for channel: UInt8 in 0...15 {
            bytes += [0xB0 | channel, 123, 0, 0xB0 | channel, 64, 0]
        }
        var packets = MIDIPacketList()
        let packet = MIDIPacketListInit(&packets)
        _ = MIDIPacketListAdd(&packets, MemoryLayout<MIDIPacketList>.size, packet, 0, bytes.count, bytes)
        for endpoint in endpoints {
            MIDISend(port, endpoint, &packets)
        }
    }
}
