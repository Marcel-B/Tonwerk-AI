import Foundation
import TonwerkDSP

/// A sound of Tonwerk's synthesizers, as the web page's `synth.ts`, `fm.ts` and `effects.ts` describe it and the
/// server keeps it (opaque JSON there). Both engines' settings are held, so switching in the editor loses nothing;
/// the JSON carries the one `engine` names, as the web page writes it. ``TonwerkDSP`` plays it.
struct SynthSound: Equatable {
    enum Engine: String, CaseIterable { case analog, fm }

    var engine: Engine = .analog
    var analog = AnalogSound.lead
    var fm = FmSound.brass
    var fx = SoundEffects()

    /// The starting sound for a kind of track (`defaultPatch`, `defaultFmPatch`), in the given engine.
    static func defaults(for kind: String?, engine: Engine = .analog) -> SynthSound {
        SynthSound(engine: engine, analog: .defaults(for: kind), fm: .defaults(for: kind))
    }

    /// Whatever the server kept, made playable as `normalizePatch` does: missing fields take the kind's default,
    /// numbers are clamped into their ranges.
    init(json: Any?, kind: String? = nil) {
        let value = json as? [String: Any] ?? [:]
        engine = value["engine"] as? String == "fm" ? .fm : .analog
        analog = engine == .analog ? AnalogSound(json: value, fallback: .defaults(for: kind)) : .defaults(for: kind)
        fm = engine == .fm ? FmSound(json: value, fallback: .defaults(for: kind)) : .defaults(for: kind)
        fx = SoundEffects(json: value["fx"])
    }

    init(engine: Engine = .analog, analog: AnalogSound = .lead, fm: FmSound = .brass, fx: SoundEffects = SoundEffects()) {
        self.engine = engine
        self.analog = analog
        self.fm = fm
        self.fx = fx
    }

    var json: [String: Any] {
        var result = engine == .fm ? fm.json : analog.json
        result["engine"] = engine.rawValue
        result["fx"] = fx.json
        return result
    }

    /// For the synthesizer.
    var patch: TWPatch {
        var patch = TWPatch()
        patch.engine = engine == .fm ? 1 : 0
        analog.write(into: &patch)
        fm.write(into: &patch)
        fx.write(into: &patch)
        return patch
    }
}

// MARK: Ranges

/// The ranges of the web page's `RANGES`, `FM_RANGES` and `EFFECT_RANGES`, shared by the editor and normalising.
enum SynthRange {
    static let octave: ClosedRange<Float> = -2...2
    static let detune: ClosedRange<Float> = -50...50
    static let level: ClosedRange<Float> = 0...1
    static let width: ClosedRange<Float> = 0.05...0.95
    static let pwmEnv: ClosedRange<Float> = -1...1
    static let cutoff: ClosedRange<Float> = 20...18000
    static let resonance: ClosedRange<Float> = 0...20
    static let envAmount: ClosedRange<Float> = -4...6
    static let attack: ClosedRange<Float> = 0...4
    static let decay: ClosedRange<Float> = 0.01...4
    static let release: ClosedRange<Float> = 0.01...6
    static let rate: ClosedRange<Float> = 0.1...20
    static let algorithm: ClosedRange<Float> = 1...8
    static let ratio: ClosedRange<Float> = 0.5...16
    static let delayTime: ClosedRange<Float> = 0.02...1.5
    static let delayFeedback: ClosedRange<Float> = 0...0.9
    static let tone: ClosedRange<Float> = 500...12000
    static let reverbDecay: ClosedRange<Float> = 0.3...8
}

/// A number from stored JSON, clamped, or the fallback when it is missing or not a finite number (`clamp`).
func clamped(_ value: Any?, _ range: ClosedRange<Float>, _ fallback: Float) -> Float {
    // JSON's true and false are NSNumbers too; `is Bool` would also take 0 and 1, so ask Core Foundation.
    guard let number = value as? NSNumber, CFGetTypeID(number) != CFBooleanGetTypeID() else { return fallback }
    let float = number.floatValue
    guard float.isFinite else { return fallback }
    return min(range.upperBound, max(range.lowerBound, float))
}

private func object(_ value: Any?) -> [String: Any] { value as? [String: Any] ?? [:] }

private func choice<T: RawRepresentable>(_ value: Any?, _ fallback: T) -> T where T.RawValue == String {
    (value as? String).flatMap(T.init(rawValue:)) ?? fallback
}

// MARK: Shared parts

enum Wave: String, CaseIterable {
    case sine, triangle, sawtooth, square

    var index: Int32 { Int32(Self.allCases.firstIndex(of: self)!) }
}

struct SoundEnvelope: Equatable {
    var attack: Float
    var decay: Float
    var sustain: Float
    var release: Float

    init(_ attack: Float, _ decay: Float, _ sustain: Float, _ release: Float) {
        self.attack = attack
        self.decay = decay
        self.sustain = sustain
        self.release = release
    }

    init(json: Any?, fallback: SoundEnvelope) {
        let value = object(json)
        attack = clamped(value["attack"], SynthRange.attack, fallback.attack)
        decay = clamped(value["decay"], SynthRange.decay, fallback.decay)
        sustain = clamped(value["sustain"], SynthRange.level, fallback.sustain)
        release = clamped(value["release"], SynthRange.release, fallback.release)
    }

    var json: [String: Any] { ["attack": attack, "decay": decay, "sustain": sustain, "release": release] }
    var c: TWEnvelope { TWEnvelope(attack: attack, decay: decay, sustain: sustain, release: release) }
}

struct SoundLfo<Target: RawRepresentable & CaseIterable & Equatable>: Equatable where Target.RawValue == String {
    var wave: Wave
    var rate: Float
    var target: Target
    var depth: Float

    init(json: Any?, fallback: SoundLfo) {
        let value = object(json)
        wave = choice(value["wave"], fallback.wave)
        rate = clamped(value["rate"], SynthRange.rate, fallback.rate)
        target = choice(value["target"], fallback.target)
        depth = clamped(value["depth"], SynthRange.level, fallback.depth)
    }

    init(_ wave: Wave, _ rate: Float, _ target: Target, _ depth: Float) {
        self.wave = wave
        self.rate = rate
        self.target = target
        self.depth = depth
    }

    var json: [String: Any] { ["wave": wave.rawValue, "rate": rate, "target": target.rawValue, "depth": depth] }

    var c: TWLfo {
        TWLfo(wave: wave.index, rate: rate, target: Int32(Array(Target.allCases).firstIndex(of: target)!), depth: depth)
    }
}

// MARK: Analog

struct AnalogSound: Equatable {
    enum FilterType: String, CaseIterable { case lowpass, highpass, bandpass }
    enum LfoTarget: String, CaseIterable { case pitch, filter, amp }

    struct Oscillator: Equatable {
        var wave: Wave
        var octave: Int
        var detune: Float
        var level: Float
        var width: Float = 0.5
        var pwm: Float = 0
        var pwmLfo = true
        var pwmAmpEnv: Float = 0
        var pwmFilterEnv: Float = 0

        init(_ wave: Wave, _ octave: Int, _ detune: Float, _ level: Float) {
            self.wave = wave
            self.octave = octave
            self.detune = detune
            self.level = level
        }

        init(json: Any?, fallback: Oscillator) {
            let value = object(json)
            wave = choice(value["wave"], fallback.wave)
            octave = Int(clamped(value["octave"], SynthRange.octave, Float(fallback.octave)).rounded())
            detune = clamped(value["detune"], SynthRange.detune, fallback.detune)
            level = clamped(value["level"], SynthRange.level, fallback.level)
            width = clamped(value["width"], SynthRange.width, fallback.width)
            pwm = clamped(value["pwm"], SynthRange.level, fallback.pwm)
            // Sounds saved before the switch existed had the LFO on whenever PWM was set.
            pwmLfo = value["pwmLfo"] as? Bool ?? fallback.pwmLfo
            pwmAmpEnv = clamped(value["pwmAmpEnv"], SynthRange.pwmEnv, fallback.pwmAmpEnv)
            pwmFilterEnv = clamped(value["pwmFilterEnv"], SynthRange.pwmEnv, fallback.pwmFilterEnv)
        }

        var json: [String: Any] {
            [
                "wave": wave.rawValue, "octave": octave, "detune": detune, "level": level, "width": width, "pwm": pwm,
                "pwmLfo": pwmLfo, "pwmAmpEnv": pwmAmpEnv, "pwmFilterEnv": pwmFilterEnv,
            ]
        }

        var c: TWOscillator {
            TWOscillator(
                wave: wave.index, octave: Int32(octave), detune: detune, level: level, width: width, pwm: pwm,
                pwmLfo: pwmLfo, pwmAmpEnv: pwmAmpEnv, pwmFilterEnv: pwmFilterEnv)
        }
    }

    struct Filter: Equatable {
        var type: FilterType
        var cutoff: Float
        var resonance: Float
        var envAmount: Float
        var keyTrack: Float
    }

    var osc1: Oscillator
    var osc2: Oscillator
    var noise: Float = 0
    var filter: Filter
    var filterEnv: SoundEnvelope
    var ampEnv: SoundEnvelope
    var lfo: SoundLfo<LfoTarget>
    var volume: Float

    init(
        osc1: Oscillator, osc2: Oscillator, filter: Filter, filterEnv: SoundEnvelope, ampEnv: SoundEnvelope,
        lfo: SoundLfo<LfoTarget>, volume: Float
    ) {
        self.osc1 = osc1
        self.osc2 = osc2
        self.filter = filter
        self.filterEnv = filterEnv
        self.ampEnv = ampEnv
        self.lfo = lfo
        self.volume = volume
    }

    init(json: Any?, fallback: AnalogSound) {
        let value = object(json)
        let filter = object(value["filter"])
        osc1 = Oscillator(json: value["osc1"], fallback: fallback.osc1)
        osc2 = Oscillator(json: value["osc2"], fallback: fallback.osc2)
        noise = clamped(value["noise"], SynthRange.level, fallback.noise)
        self.filter = Filter(
            type: choice(filter["type"], fallback.filter.type),
            cutoff: clamped(filter["cutoff"], SynthRange.cutoff, fallback.filter.cutoff),
            resonance: clamped(filter["resonance"], SynthRange.resonance, fallback.filter.resonance),
            envAmount: clamped(filter["envAmount"], SynthRange.envAmount, fallback.filter.envAmount),
            keyTrack: clamped(filter["keyTrack"], SynthRange.level, fallback.filter.keyTrack))
        filterEnv = SoundEnvelope(json: value["filterEnv"], fallback: fallback.filterEnv)
        ampEnv = SoundEnvelope(json: value["ampEnv"], fallback: fallback.ampEnv)
        lfo = SoundLfo(json: value["lfo"], fallback: fallback.lfo)
        volume = clamped(value["volume"], SynthRange.level, fallback.volume)
    }

    var json: [String: Any] {
        [
            "osc1": osc1.json, "osc2": osc2.json, "noise": noise,
            "filter": [
                "type": filter.type.rawValue, "cutoff": filter.cutoff, "resonance": filter.resonance,
                "envAmount": filter.envAmount, "keyTrack": filter.keyTrack,
            ] as [String: Any],
            "filterEnv": filterEnv.json, "ampEnv": ampEnv.json, "lfo": lfo.json, "volume": volume,
        ]
    }

    func write(into patch: inout TWPatch) {
        patch.osc1 = osc1.c
        patch.osc2 = osc2.c
        patch.noise = noise
        patch.filterType = Int32(FilterType.allCases.firstIndex(of: filter.type)!)
        patch.cutoff = filter.cutoff
        patch.resonance = filter.resonance
        patch.envAmount = filter.envAmount
        patch.keyTrack = filter.keyTrack
        patch.filterEnv = filterEnv.c
        patch.ampEnv = ampEnv.c
        patch.lfo = lfo.c
        patch.volume = volume
    }

    // The starting sounds of `synth.ts`, value for value.
    static let lead = AnalogSound(
        osc1: Oscillator(.sawtooth, 0, 0, 0.7), osc2: Oscillator(.square, 0, 7, 0.35),
        filter: Filter(type: .lowpass, cutoff: 1800, resonance: 2, envAmount: 1.5, keyTrack: 0.5),
        filterEnv: SoundEnvelope(0.01, 0.4, 0.3, 0.2), ampEnv: SoundEnvelope(0.01, 0.3, 0.75, 0.15),
        lfo: SoundLfo(.sine, 5.5, .pitch, 0.1), volume: 0.8)

    static func defaults(for kind: String?) -> AnalogSound {
        switch kind {
        case "Doubling":
            var sound = lead
            sound.volume = 0.5
            return sound
        case "Harmony":
            var sound = lead
            sound.volume = 0.45
            return sound
        case "Chords":
            return AnalogSound(
                osc1: Oscillator(.sawtooth, 0, -8, 0.5), osc2: Oscillator(.sawtooth, 0, 8, 0.5),
                filter: Filter(type: .lowpass, cutoff: 1400, resonance: 1, envAmount: 1, keyTrack: 0.3),
                filterEnv: SoundEnvelope(0.2, 1, 0.5, 0.6), ampEnv: SoundEnvelope(0.08, 0.8, 0.8, 0.5),
                lfo: SoundLfo(.triangle, 0.4, .filter, 0.15), volume: 0.45)
        case "Bass":
            return AnalogSound(
                osc1: Oscillator(.sawtooth, 0, 0, 0.7), osc2: Oscillator(.square, -1, 0, 0.5),
                filter: Filter(type: .lowpass, cutoff: 380, resonance: 5, envAmount: 2, keyTrack: 0.3),
                filterEnv: SoundEnvelope(0.005, 0.25, 0.15, 0.1), ampEnv: SoundEnvelope(0.005, 0.3, 0.85, 0.08),
                lfo: SoundLfo(.sine, 5, .pitch, 0), volume: 0.9)
        case "GuideTones":
            return AnalogSound(
                osc1: Oscillator(.sine, 0, 0, 0.8), osc2: Oscillator(.triangle, 1, 0, 0.2),
                filter: Filter(type: .lowpass, cutoff: 4000, resonance: 0, envAmount: 0, keyTrack: 0),
                filterEnv: SoundEnvelope(0.01, 0.5, 1, 0.3), ampEnv: SoundEnvelope(0.03, 0.5, 0.8, 0.3),
                lfo: SoundLfo(.sine, 5, .pitch, 0), volume: 0.5)
        default:
            return lead
        }
    }
}

// MARK: FM

struct FmSound: Equatable {
    enum LfoTarget: String, CaseIterable { case pitch, index, amp }

    struct Operator: Equatable {
        var ratio: Float
        var detune: Float
        var level: Float
        var velocity: Float
        var env: SoundEnvelope

        init(_ ratio: Float, _ level: Float, _ env: SoundEnvelope, velocity: Float = 0.7, detune: Float = 0) {
            self.ratio = ratio
            self.detune = detune
            self.level = level
            self.velocity = velocity
            self.env = env
        }

        init(json: Any?, fallback: Operator) {
            let value = object(json)
            // Halves, as the editor sets them.
            ratio = (clamped(value["ratio"], SynthRange.ratio, fallback.ratio) * 2).rounded() / 2
            detune = clamped(value["detune"], SynthRange.detune, fallback.detune)
            level = clamped(value["level"], SynthRange.level, fallback.level)
            velocity = clamped(value["velocity"], SynthRange.level, fallback.velocity)
            env = SoundEnvelope(json: value["env"], fallback: fallback.env)
        }

        var json: [String: Any] {
            ["ratio": ratio, "detune": detune, "level": level, "velocity": velocity, "env": env.json]
        }

        var c: TWOperator { TWOperator(ratio: ratio, detune: detune, level: level, velocity: velocity, env: env.c) }
    }

    /// The TX81Z's algorithms as printed on the synths (`ALGORITHMS`); feedback is on operator 4.
    static let algorithms = [
        "4→3→2→1", "(3+4)→2→1", "(4 + 3→2)→1", "(4→3 + 2)→1", "2→1 · 4→3", "4→(1 · 2 · 3)", "4→3 · 2 · 1", "1 · 2 · 3 · 4",
    ]
    /// Which operators are heard per algorithm, the rest modulate.
    static let carriers: [[Int]] = [[0], [0], [0], [0], [0, 2], [0, 1, 2], [0, 1, 2], [0, 1, 2, 3]]

    var algorithm: Int
    var feedback: Float
    /// Operators 1 to 4.
    var ops: [Operator]
    var lfo: SoundLfo<LfoTarget>
    var volume: Float

    init(algorithm: Int, feedback: Float, ops: [Operator], lfo: SoundLfo<LfoTarget>, volume: Float) {
        self.algorithm = algorithm
        self.feedback = feedback
        self.ops = ops
        self.lfo = lfo
        self.volume = volume
    }

    init(json: Any?, fallback: FmSound) {
        let value = object(json)
        let ops = value["ops"] as? [Any] ?? []
        algorithm = Int(clamped(value["algorithm"], SynthRange.algorithm, Float(fallback.algorithm)).rounded())
        feedback = clamped(value["feedback"], SynthRange.level, fallback.feedback)
        self.ops = (0..<4).map { Operator(json: $0 < ops.count ? ops[$0] : nil, fallback: fallback.ops[$0]) }
        lfo = SoundLfo(json: value["lfo"], fallback: fallback.lfo)
        volume = clamped(value["volume"], SynthRange.level, fallback.volume)
    }

    var json: [String: Any] {
        ["algorithm": algorithm, "feedback": feedback, "ops": ops.map(\.json), "lfo": lfo.json, "volume": volume]
    }

    func write(into patch: inout TWPatch) {
        patch.algorithm = Int32(algorithm)
        patch.feedback = feedback
        patch.op1 = ops[0].c
        patch.op2 = ops[1].c
        patch.op3 = ops[2].c
        patch.op4 = ops[3].c
        patch.fmLfo = lfo.c
        patch.fmVolume = volume
    }

    // The starting sounds of `fm.ts`, value for value.
    static let brass = FmSound(
        algorithm: 4, feedback: 0.4,
        ops: [
            Operator(1, 0.8, SoundEnvelope(0.03, 0.4, 0.8, 0.2)),
            Operator(1, 0.55, SoundEnvelope(0.08, 0.5, 0.6, 0.2), velocity: 0.6),
            Operator(1, 0.3, SoundEnvelope(0.05, 0.6, 0.5, 0.2)),
            Operator(2, 0.15, SoundEnvelope(0.05, 0.6, 0.5, 0.2)),
        ],
        lfo: SoundLfo(.sine, 5.5, .pitch, 0.1), volume: 0.7)

    static func defaults(for kind: String?) -> FmSound {
        switch kind {
        case "Doubling":
            var sound = brass
            sound.volume = 0.5
            return sound
        case "Harmony":
            var sound = brass
            sound.volume = 0.45
            return sound
        case "Chords":
            return FmSound(
                algorithm: 5, feedback: 0,
                ops: [
                    Operator(1, 0.8, SoundEnvelope(0.002, 2.5, 0, 0.6)),
                    Operator(1, 0.5, SoundEnvelope(0.002, 1.2, 0.1, 0.5), velocity: 0.8),
                    Operator(1, 0.35, SoundEnvelope(0.002, 0.8, 0, 0.4), velocity: 0.8, detune: 3),
                    Operator(14, 0.35, SoundEnvelope(0.002, 0.25, 0, 0.2), velocity: 0.9),
                ],
                lfo: SoundLfo(.sine, 5, .pitch, 0), volume: 0.6)
        case "Bass":
            return FmSound(
                algorithm: 3, feedback: 0.6,
                ops: [
                    Operator(1, 0.9, SoundEnvelope(0.002, 0.8, 0.7, 0.08)),
                    Operator(1, 0.5, SoundEnvelope(0.002, 0.25, 0.2, 0.1)),
                    Operator(3, 0.15, SoundEnvelope(0.002, 0.15, 0, 0.1)),
                    Operator(1, 0.3, SoundEnvelope(0.002, 0.4, 0.3, 0.1)),
                ],
                lfo: SoundLfo(.sine, 5, .pitch, 0), volume: 0.85)
        case "GuideTones":
            let env = SoundEnvelope(0.03, 0.5, 0.8, 0.3)
            return FmSound(
                algorithm: 7, feedback: 0,
                ops: [Operator(1, 0.8, env), Operator(2, 0.15, env), Operator(1, 0, env), Operator(1, 0, env)],
                lfo: SoundLfo(.sine, 5, .pitch, 0), volume: 0.5)
        default:
            return brass
        }
    }
}

// MARK: Effects

/// Delay and reverb (`effects.ts`): sends, 0 adds nothing.
struct SoundEffects: Equatable {
    var delayMix: Float = 0
    var delayTime: Float = 0.35
    var delayFeedback: Float = 0.35
    var delayTone: Float = 4000
    var reverbMix: Float = 0
    var reverbDecay: Float = 2

    init() {}

    init(json: Any?) {
        let value = object(json)
        let delay = object(value["delay"])
        let reverb = object(value["reverb"])
        let fallback = SoundEffects()
        delayMix = clamped(delay["mix"], SynthRange.level, fallback.delayMix)
        delayTime = clamped(delay["time"], SynthRange.delayTime, fallback.delayTime)
        delayFeedback = clamped(delay["feedback"], SynthRange.delayFeedback, fallback.delayFeedback)
        delayTone = clamped(delay["tone"], SynthRange.tone, fallback.delayTone)
        reverbMix = clamped(reverb["mix"], SynthRange.level, fallback.reverbMix)
        reverbDecay = clamped(reverb["decay"], SynthRange.reverbDecay, fallback.reverbDecay)
    }

    var json: [String: Any] {
        [
            "delay": ["mix": delayMix, "time": delayTime, "feedback": delayFeedback, "tone": delayTone],
            "reverb": ["mix": reverbMix, "decay": reverbDecay],
        ]
    }

    func write(into patch: inout TWPatch) {
        patch.delayMix = delayMix
        patch.delayTime = delayTime
        patch.delayFeedback = delayFeedback
        patch.delayTone = delayTone
        patch.reverbMix = reverbMix
        patch.reverbDecay = reverbDecay
    }
}
