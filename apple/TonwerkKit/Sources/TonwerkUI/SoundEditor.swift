import SwiftUI

/// The synthesizers' controls (`SynthEditor.vue` with `AnalogPanel.vue`, `FmPanel.vue`, `EffectsPanel.vue`): the
/// engine switch, then the engine's modules and the effects, laid out in as many columns as fit.
struct SoundEditor: View {
    @Binding var sound: SynthSound
    @State private var op = 0

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Picker("Klangerzeugung", selection: $sound.engine) {
                Text("Analog").tag(SynthSound.Engine.analog)
                Text("FM").tag(SynthSound.Engine.fm)
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .frame(maxWidth: 260)

            LazyVGrid(columns: [GridItem(.adaptive(minimum: 250), spacing: 12, alignment: .top)], spacing: 12) {
                if sound.engine == .analog {
                    analogModules
                } else {
                    fmModules
                }
                Module("Effekte", systemImage: "dot.radiowaves.right") {
                    ParamSlider("Delay", value: $sound.fx.delayMix, in: SynthRange.level, format: .percent)
                    ParamSlider("Zeit", value: $sound.fx.delayTime, in: SynthRange.delayTime, format: .seconds)
                    ParamSlider("Rückkopplung", value: $sound.fx.delayFeedback, in: SynthRange.delayFeedback, format: .percent)
                    ParamSlider("Ton", value: $sound.fx.delayTone, in: SynthRange.tone, format: .hertz, logarithmic: true)
                    ParamSlider("Hall", value: $sound.fx.reverbMix, in: SynthRange.level, format: .percent)
                    ParamSlider("Nachhall", value: $sound.fx.reverbDecay, in: SynthRange.reverbDecay, format: .seconds)
                }
            }
        }
    }

    // MARK: Analog

    @ViewBuilder
    private var analogModules: some View {
        OscillatorModule(title: "Oszillator 1", osc: $sound.analog.osc1)
        OscillatorModule(title: "Oszillator 2", osc: $sound.analog.osc2)
        Module("Filter", systemImage: "line.3.horizontal.decrease") {
            Picker("Typ", selection: $sound.analog.filter.type) {
                Text("Tiefpass").tag(AnalogSound.FilterType.lowpass)
                Text("Hochpass").tag(AnalogSound.FilterType.highpass)
                Text("Bandpass").tag(AnalogSound.FilterType.bandpass)
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            ParamSlider("Grenzfrequenz", value: $sound.analog.filter.cutoff, in: SynthRange.cutoff, format: .hertz, logarithmic: true)
            ParamSlider("Resonanz", value: $sound.analog.filter.resonance, in: SynthRange.resonance, format: .number)
            ParamSlider("Hüllkurve", value: $sound.analog.filter.envAmount, in: SynthRange.envAmount, format: .octaves)
            ParamSlider("Tastaturfolge", value: $sound.analog.filter.keyTrack, in: SynthRange.level, format: .percent)
            ParamSlider("Rauschen", value: $sound.analog.noise, in: SynthRange.level, format: .percent)
        }
        EnvelopeModule(title: "Filterhüllkurve", env: $sound.analog.filterEnv)
        EnvelopeModule(title: "Lautstärke", env: $sound.analog.ampEnv) {
            ParamSlider("Pegel", value: $sound.analog.volume, in: SynthRange.level, format: .percent)
        }
        LfoModule(lfo: $sound.analog.lfo, targets: [(.pitch, "Tonhöhe"), (.filter, "Filter"), (.amp, "Lautstärke")])
    }

    // MARK: FM

    @ViewBuilder
    private var fmModules: some View {
        Module("Algorithmus", systemImage: "point.3.connected.trianglepath.dotted") {
            Picker("Algorithmus", selection: $sound.fm.algorithm) {
                ForEach(1...8, id: \.self) { number in
                    Text("\(number): \(FmSound.algorithms[number - 1])").tag(number)
                }
            }
            ParamSlider("Rückkopplung (Op 4)", value: $sound.fm.feedback, in: SynthRange.level, format: .percent)
            ParamSlider("Pegel", value: $sound.fm.volume, in: SynthRange.level, format: .percent)
            Text("Hörbar: \(carriers)").font(.caption).foregroundStyle(.secondary)
        }
        Module("Operator", systemImage: "waveform.path") {
            Picker("Operator", selection: $op) {
                ForEach(0..<4, id: \.self) { index in
                    Text("\(index + 1)").tag(index)
                }
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            ParamSlider("Verhältnis", value: $sound.fm.ops[op].ratio, in: SynthRange.ratio, step: 0.5, format: .ratio)
            ParamSlider("Verstimmung", value: $sound.fm.ops[op].detune, in: SynthRange.detune, format: .cents)
            ParamSlider(
                FmSound.carriers[sound.fm.algorithm - 1].contains(op) ? "Pegel" : "Modulation",
                value: $sound.fm.ops[op].level, in: SynthRange.level, format: .percent)
            ParamSlider("Anschlag", value: $sound.fm.ops[op].velocity, in: SynthRange.level, format: .percent)
        }
        EnvelopeModule(title: "Hüllkurve Operator \(op + 1)", env: $sound.fm.ops[op].env)
        LfoModule(lfo: $sound.fm.lfo, targets: [(.pitch, "Tonhöhe"), (.index, "Modulation"), (.amp, "Lautstärke")])
    }

    private var carriers: String {
        FmSound.carriers[sound.fm.algorithm - 1].map { "Op \($0 + 1)" }.joined(separator: ", ")
    }
}

// MARK: Modules

private struct Module<Content: View>: View {
    let title: String
    let systemImage: String
    @ViewBuilder let content: Content

    init(_ title: String, systemImage: String, @ViewBuilder content: () -> Content) {
        self.title = title
        self.systemImage = systemImage
        self.content = content()
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Label {
                Text(title)
            } icon: {
                Image(systemName: systemImage).foregroundStyle(Theme.accent)
            }
            .font(.subheadline.weight(.semibold))
            content
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .card(padding: 12)
    }
}

private struct OscillatorModule: View {
    let title: String
    @Binding var osc: AnalogSound.Oscillator

    var body: some View {
        Module(title, systemImage: "waveform") {
            WavePicker(wave: $osc.wave)
            Stepper(value: $osc.octave, in: -2...2) {
                Text("Oktave \(osc.octave > 0 ? "+" : "")\(osc.octave)").font(.callout)
            }
            ParamSlider("Verstimmung", value: $osc.detune, in: SynthRange.detune, format: .cents)
            ParamSlider("Pegel", value: $osc.level, in: SynthRange.level, format: .percent)
            if osc.wave == .square {
                ParamSlider("Pulsbreite", value: $osc.width, in: SynthRange.width, format: .percent)
                ParamSlider("PWM", value: $osc.pwm, in: SynthRange.level, format: .percent)
                Toggle("PWM durch LFO", isOn: $osc.pwmLfo).font(.callout)
                ParamSlider("PWM Lautstärke", value: $osc.pwmAmpEnv, in: SynthRange.pwmEnv, format: .percent)
                ParamSlider("PWM Filter", value: $osc.pwmFilterEnv, in: SynthRange.pwmEnv, format: .percent)
            }
        }
    }
}

private struct EnvelopeModule<Extra: View>: View {
    let title: String
    @Binding var env: SoundEnvelope
    @ViewBuilder let extra: Extra

    init(title: String, env: Binding<SoundEnvelope>, @ViewBuilder extra: () -> Extra) {
        self.title = title
        _env = env
        self.extra = extra()
    }

    var body: some View {
        Module(title, systemImage: "chart.line.uptrend.xyaxis") {
            EnvelopeShape(env: env)
                .stroke(Theme.accent, lineWidth: 2)
                .frame(height: 44)
            ParamSlider("Attack", value: $env.attack, in: SynthRange.attack, format: .seconds)
            ParamSlider("Decay", value: $env.decay, in: SynthRange.decay, format: .seconds)
            ParamSlider("Sustain", value: $env.sustain, in: SynthRange.level, format: .percent)
            ParamSlider("Release", value: $env.release, in: SynthRange.release, format: .seconds)
            extra
        }
    }
}

extension EnvelopeModule where Extra == EmptyView {
    init(title: String, env: Binding<SoundEnvelope>) {
        self.init(title: title, env: env) { EmptyView() }
    }
}

private struct LfoModule<Target: RawRepresentable & CaseIterable & Hashable>: View where Target.RawValue == String {
    @Binding var lfo: SoundLfo<Target>
    let targets: [(Target, String)]

    var body: some View {
        Module("LFO", systemImage: "waveform.path.ecg") {
            WavePicker(wave: $lfo.wave)
            Picker("Ziel", selection: $lfo.target) {
                ForEach(targets.indices, id: \.self) { index in
                    Text(targets[index].1).tag(targets[index].0)
                }
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            ParamSlider("Rate", value: $lfo.rate, in: SynthRange.rate, format: .hertz, logarithmic: true)
            ParamSlider("Tiefe", value: $lfo.depth, in: SynthRange.level, format: .percent)
        }
    }
}

private struct WavePicker: View {
    @Binding var wave: Wave

    var body: some View {
        Picker("Wellenform", selection: $wave) {
            Image(systemName: "wave.3.right").accessibilityLabel("Sinus").tag(Wave.sine)
            Text("△").accessibilityLabel("Dreieck").tag(Wave.triangle)
            Text("⩘").accessibilityLabel("Sägezahn").tag(Wave.sawtooth)
            Text("⊓").accessibilityLabel("Puls").tag(Wave.square)
        }
        .pickerStyle(.segmented)
        .labelsHidden()
    }
}

/// The envelope's shape as `EnvelopeCurve.vue` draws it: attack, decay to sustain, a held stretch, release.
private struct EnvelopeShape: Shape {
    let env: SoundEnvelope

    func path(in rect: CGRect) -> Path {
        let hold: Float = 0.5
        let total = max(0.01, env.attack + env.decay + hold + env.release)
        func x(_ t: Float) -> CGFloat { rect.minX + rect.width * CGFloat(t / total) }
        func y(_ level: Float) -> CGFloat { rect.maxY - rect.height * CGFloat(level) }
        var path = Path()
        path.move(to: CGPoint(x: rect.minX, y: rect.maxY))
        path.addLine(to: CGPoint(x: x(env.attack), y: rect.minY))
        path.addQuadCurve(
            to: CGPoint(x: x(env.attack + env.decay), y: y(env.sustain)),
            control: CGPoint(x: x(env.attack + env.decay * 0.15), y: y(env.sustain)))
        path.addLine(to: CGPoint(x: x(env.attack + env.decay + hold), y: y(env.sustain)))
        path.addQuadCurve(
            to: CGPoint(x: rect.maxX, y: rect.maxY),
            control: CGPoint(x: x(env.attack + env.decay + hold + env.release * 0.15), y: rect.maxY))
        return path
    }
}

// MARK: Slider

/// How a parameter's value reads beside its slider.
enum ParamFormat {
    case percent, seconds, hertz, cents, octaves, ratio, number

    func text(_ value: Float) -> String {
        switch self {
        case .percent: "\(Int((value * 100).rounded())) %"
        case .seconds: value < 1 ? "\(Int((value * 1000).rounded())) ms" : String(format: "%.2f s", value)
        case .hertz: value < 1000 ? String(format: value < 10 ? "%.1f Hz" : "%.0f Hz", value) : String(format: "%.1f kHz", value / 1000)
        case .cents: "\(Int(value.rounded())) ct"
        case .octaves: String(format: "%+.1f Okt.", value)
        case .ratio: String(format: "× %.1f", value)
        case .number: String(format: "%.1f", value)
        }
    }
}

/// A labelled slider with its value; logarithmic for frequencies, so the low end gets room.
struct ParamSlider: View {
    let title: String
    @Binding var value: Float
    let range: ClosedRange<Float>
    var step: Float?
    let format: ParamFormat
    var logarithmic = false

    init(
        _ title: String, value: Binding<Float>, in range: ClosedRange<Float>, step: Float? = nil,
        format: ParamFormat, logarithmic: Bool = false
    ) {
        self.title = title
        _value = value
        self.range = range
        self.step = step
        self.format = format
        self.logarithmic = logarithmic
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack {
                Text(title).font(.callout)
                Spacer()
                Text(format.text(value)).font(.caption.monospacedDigit()).foregroundStyle(.secondary)
            }
            Slider(value: position, in: 0...1)
                .controlSize(.small)
                .accessibilityLabel(Text(title))
                .accessibilityValue(Text(format.text(value)))
        }
    }

    /// The slider's 0 to 1 against the value.
    private var position: Binding<Float> {
        Binding(
            get: {
                let clamped = min(range.upperBound, max(range.lowerBound, value))
                guard logarithmic, range.lowerBound > 0 else {
                    return (clamped - range.lowerBound) / (range.upperBound - range.lowerBound)
                }
                return log(clamped / range.lowerBound) / log(range.upperBound / range.lowerBound)
            },
            set: { position in
                var next = logarithmic && range.lowerBound > 0
                    ? range.lowerBound * pow(range.upperBound / range.lowerBound, position)
                    : range.lowerBound + position * (range.upperBound - range.lowerBound)
                if let step {
                    next = (next / step).rounded() * step
                }
                value = min(range.upperBound, max(range.lowerBound, next))
            })
    }
}
