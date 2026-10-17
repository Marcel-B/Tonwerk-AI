import Accelerate
import AVFoundation
import MediaToolbox
import SwiftUI
import os

/// What the analyzers and the full-screen player's effects listen to, the native counterpart of `spectrum.ts`: the
/// audio thread hands over what it just played, and the screen reads it as Web Audio's analyser reads its input
/// (8192-point Blackman FFT, smoothing 0.75, -90 to -20 dB), so the bars move as they do in the browser.
final class SpectrumAnalyzer: @unchecked Sendable {
    static let size = 8192
    private static let log2Size = vDSP_Length(13)
    private static let smoothing: Float = 0.75
    private static let lowestHz = 30.0
    private static let highestHz = 16000.0

    private struct Ring {
        var samples = [Float](repeating: 0, count: SpectrumAnalyzer.size)
        var next = 0
        var sampleRate = 48000.0
        /// When the audio thread last handed something over; a paused player hands over nothing.
        var fed: UInt64 = 0
    }

    private let ring = OSAllocatedUnfairLock(initialState: Ring())

    // Read on the main thread only.
    private var input = [Float](repeating: 0, count: size)
    private var windowed = [Float](repeating: 0, count: size)
    private let window = vDSP.window(ofType: Float.self, usingSequence: .blackman, count: size, isHalfWindow: false)
    private var real = [Float](repeating: 0, count: size / 2)
    private var imaginary = [Float](repeating: 0, count: size / 2)
    private var smoothed = [Float](repeating: 0, count: size / 2)
    private var sampleRate = 48000.0
    private var computedAt: UInt64 = 0
    private let setup = vDSP_create_fftsetup(log2Size, FFTRadix(kFFTRadix2))!

    deinit {
        vDSP_destroy_fftsetup(setup)
    }

    /// From the audio thread: the frames just played, both channels mixed to one as Web Audio's analyser does. Left
    /// out while the screen reads, so the audio never waits for it.
    func feed(left: UnsafePointer<Float>, right: UnsafePointer<Float>?, stride: Int = 1, frames: Int, sampleRate: Double) {
        let now = DispatchTime.now().uptimeNanoseconds
        _ = ring.withLockIfAvailableUnchecked { ring in
            var next = ring.next
            ring.samples.withUnsafeMutableBufferPointer { samples in
                for frame in 0..<frames {
                    let at = frame * stride
                    samples[next] = right.map { (left[at] + $0[at]) * 0.5 } ?? left[at]
                    next = (next + 1) % SpectrumAnalyzer.size
                }
            }
            ring.next = next
            ring.sampleRate = sampleRate
            ring.fed = now
        }
    }

    /// `count` log-spaced bands from 30 Hz to 16 kHz, each 0 to 1 (`readBands` in `spectrum.ts`). Several views may
    /// ask within one frame; the spectrum is computed once per frame, so the smoothing runs at one pace.
    @MainActor
    func bands(_ count: Int) -> [Float] {
        let now = DispatchTime.now().uptimeNanoseconds
        if now - computedAt > 8_000_000 {
            computedAt = now
            compute(now: now)
        }
        let hzPerBin = sampleRate / Double(Self.size)
        let top = min(Self.highestHz, sampleRate / 2)
        let ratio = log(top / Self.lowestHz)
        var bands = [Float](repeating: 0, count: count)
        for band in 0..<count {
            let from = Self.lowestHz * exp(ratio * Double(band) / Double(count))
            let to = Self.lowestHz * exp(ratio * Double(band + 1) / Double(count))
            // The lowest bands are narrower than one bin; they take the bin they fall into.
            let first = Int(from / hzPerBin)
            let last = max(first, Int((to / hzPerBin).rounded(.up)) - 1)
            var loudest: Float = 0
            for bin in min(first, smoothed.count - 1)...min(last, smoothed.count - 1) {
                loudest = max(loudest, smoothed[bin])
            }
            let decibels = 20 * log10(max(loudest, 1e-12))
            bands[band] = min(1, max(0, (decibels + 90) / 70))
        }
        return bands
    }

    @MainActor
    private func compute(now: UInt64) {
        let fresh = ring.withLockUnchecked { ring -> Bool in
            // Nothing played for a moment (paused): the analyser hears silence, and the bars fall as in the browser.
            // The audio thread may have fed after `now` was taken, so `fed` can lie ahead of it.
            guard now <= ring.fed || now - ring.fed < 250_000_000 else { return false }
            let next = ring.next
            input.withUnsafeMutableBufferPointer { input in
                ring.samples.withUnsafeBufferPointer { samples in
                    let tail = SpectrumAnalyzer.size - next
                    input.baseAddress!.update(from: samples.baseAddress! + next, count: tail)
                    (input.baseAddress! + tail).update(from: samples.baseAddress!, count: next)
                }
            }
            sampleRate = ring.sampleRate
            return true
        }
        if !fresh {
            vDSP.fill(&input, with: 0)
        }
        vDSP.multiply(input, window, result: &windowed)
        let half = Self.size / 2
        real.withUnsafeMutableBufferPointer { real in
            imaginary.withUnsafeMutableBufferPointer { imaginary in
                var split = DSPSplitComplex(realp: real.baseAddress!, imagp: imaginary.baseAddress!)
                windowed.withUnsafeBufferPointer { windowed in
                    windowed.baseAddress!.withMemoryRebound(to: DSPComplex.self, capacity: half) {
                        vDSP_ctoz($0, 2, &split, 1, vDSP_Length(half))
                    }
                }
                vDSP_fft_zrip(setup, &split, 1, Self.log2Size, FFTDirection(FFT_FORWARD))
            }
        }
        // vDSP's real FFT is twice the DFT; Web Audio divides the DFT by the size. Bin 0 carries DC and Nyquist.
        let scale = 1 / Float(2 * Self.size)
        let keep = Self.smoothing
        for bin in 0..<half {
            let magnitude = bin == 0 ? abs(real[0]) * scale : (real[bin] * real[bin] + imaginary[bin] * imaginary[bin]).squareRoot() * scale
            smoothed[bin] = keep * smoothed[bin] + (1 - keep) * magnitude
        }
    }
}

/// An audio tap on the player's song, which feeds the analyzer with what AVPlayer plays (the stream copies are
/// plain files, not HLS, so a tap works on them).
enum SpectrumTap {
    private final class Context {
        let analyzer: SpectrumAnalyzer
        var sampleRate = 48000.0
        var usable = false
        var interleaved = false

        init(_ analyzer: SpectrumAnalyzer) {
            self.analyzer = analyzer
        }
    }

    static func make(feeding analyzer: SpectrumAnalyzer) -> MTAudioProcessingTap? {
        let info = Unmanaged.passRetained(Context(analyzer)).toOpaque()
        var callbacks = MTAudioProcessingTapCallbacks(
            version: kMTAudioProcessingTapCallbacksVersion_0,
            clientInfo: info,
            init: { _, clientInfo, storage in
                storage.pointee = clientInfo
            },
            finalize: { tap in
                Unmanaged<Context>.fromOpaque(MTAudioProcessingTapGetStorage(tap)).release()
            },
            prepare: { tap, _, format in
                let context = Unmanaged<Context>.fromOpaque(MTAudioProcessingTapGetStorage(tap)).takeUnretainedValue()
                let description = format.pointee
                context.sampleRate = description.mSampleRate
                context.usable = description.mFormatID == kAudioFormatLinearPCM
                    && description.mFormatFlags & kAudioFormatFlagIsFloat != 0 && description.mBitsPerChannel == 32
                context.interleaved = description.mFormatFlags & kAudioFormatFlagIsNonInterleaved == 0
                    && description.mChannelsPerFrame > 1
            },
            unprepare: nil,
            process: { tap, frames, _, buffers, framesOut, flagsOut in
                guard MTAudioProcessingTapGetSourceAudio(tap, frames, buffers, flagsOut, nil, framesOut) == noErr else {
                    return
                }
                let context = Unmanaged<Context>.fromOpaque(MTAudioProcessingTapGetStorage(tap)).takeUnretainedValue()
                guard context.usable else { return }
                let list = UnsafeMutableAudioBufferListPointer(buffers)
                guard let first = list.first?.mData?.assumingMemoryBound(to: Float.self) else { return }
                let count = Int(framesOut.pointee)
                if context.interleaved {
                    context.analyzer.feed(left: first, right: first + 1, stride: 2, frames: count, sampleRate: context.sampleRate)
                } else {
                    let second = list.count > 1 ? list[1].mData?.assumingMemoryBound(to: Float.self) : nil
                    context.analyzer.feed(left: first, right: second.map { UnsafePointer($0) }, frames: count, sampleRate: context.sampleRate)
                }
            })
        guard let tap = create(MTAudioProcessingTapCreate, &callbacks) else {
            Unmanaged<Context>.fromOpaque(info).release()
            return nil
        }
        return tap
    }

    /// The SDKs disagree on what `MTAudioProcessingTapCreate` hands back: CI's Xcode an `Unmanaged` tap, a newer one
    /// the tap itself. Taking the function as a value lets the compiler pick whichever this SDK declares.
    private static func create<Handle: TapHandle>(
        _ make: (CFAllocator?, UnsafePointer<MTAudioProcessingTapCallbacks>, MTAudioProcessingTapCreationFlags,
                 UnsafeMutablePointer<Handle?>) -> OSStatus,
        _ callbacks: inout MTAudioProcessingTapCallbacks
    ) -> MTAudioProcessingTap? {
        var handle: Handle?
        guard make(kCFAllocatorDefault, &callbacks, kMTAudioProcessingTapCreationFlag_PostEffects, &handle) == noErr
        else { return nil }
        return handle?.tap
    }
}

/// What `MTAudioProcessingTapCreate` writes out, whichever way the SDK declares it.
protocol TapHandle {
    var tap: MTAudioProcessingTap { get }
}

extension MTAudioProcessingTap: TapHandle {
    var tap: MTAudioProcessingTap { self }
}

extension Unmanaged: TapHandle where Instance == MTAudioProcessingTap {
    /// Created, so owned by us; the audio mix keeps its own reference. Read once.
    var tap: MTAudioProcessingTap { takeRetainedValue() }
}

extension AVAudioNode {
    /// Feeds `analyzer` with what this node puts out (the Logic page's engine, synthesizers and recording together).
    func feed(_ analyzer: SpectrumAnalyzer) {
        installTap(onBus: 0, bufferSize: 2048, format: nil) { buffer, _ in
            guard let channels = buffer.floatChannelData else { return }
            let second = buffer.format.channelCount > 1 ? UnsafePointer(channels[1]) : nil
            let stride = buffer.format.isInterleaved ? Int(buffer.format.channelCount) : 1
            analyzer.feed(
                left: channels[0], right: buffer.format.isInterleaved ? nil : second, stride: stride,
                frames: Int(buffer.frameLength), sampleRate: buffer.format.sampleRate)
        }
    }
}

/// A spectrum analyzer (`SpectrumBars.vue`): log-spaced bars that rise at once and fall back slowly, as a hardware
/// one does, with peaks that hold a moment. It draws only while `active`; silence keeps a sliver, so it reads as one.
struct SpectrumBars: View {
    let analyzer: SpectrumAnalyzer
    var bars = 40
    var peaks = false
    var colour: Color = Theme.accent
    let active: Bool
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var state = BarState()

    var body: some View {
        TimelineView(.animation(minimumInterval: 1.0 / 60, paused: !active || reduceMotion)) { timeline in
            Canvas { context, size in
                let shown = state.step(analyzer.bands(bars), live: active && !reduceMotion, at: timeline.date)
                let gap = max(1, (size.width / CGFloat(bars) / 5).rounded())
                let width = (size.width - gap * CGFloat(bars - 1)) / CGFloat(bars)
                let cap = max(1, (size.height / 40).rounded())
                for index in 0..<bars {
                    let height = max(cap, CGFloat(shown.levels[index]) * size.height)
                    let x = CGFloat(index) * (width + gap)
                    context.fill(Path(CGRect(x: x, y: size.height - height, width: width, height: height)), with: .color(colour))
                    if peaks, shown.caps[index] > 0.02 {
                        let y = max(0, size.height - CGFloat(shown.caps[index]) * size.height - cap * 2)
                        context.fill(Path(CGRect(x: x, y: y, width: width, height: cap)), with: .color(.secondary))
                    }
                }
            }
        }
        .accessibilityHidden(true)
    }
}

/// The bars' movement between frames; a class, so the canvas can step it while it draws.
private final class BarState {
    var levels: [Float] = []
    var caps: [Float] = []
    private var hold: [Int] = []
    private var date: Date?

    /// Up at once, down at a steady fall, at the browser's pace per frame of 60.
    func step(_ bands: [Float], live: Bool, at now: Date) -> (levels: [Float], caps: [Float]) {
        if levels.count != bands.count {
            levels = Array(repeating: 0, count: bands.count)
            caps = levels
            hold = Array(repeating: 0, count: bands.count)
        }
        guard date != now else { return (levels, caps) }
        let frames = Float(min(6, max(1, (now.timeIntervalSince(date ?? now) * 60).rounded())))
        date = now
        for index in bands.indices {
            let target = live ? bands[index] : 0
            levels[index] = target > levels[index] ? target : max(target, levels[index] - 0.025 * frames)
            if levels[index] >= caps[index] {
                caps[index] = levels[index]
                hold[index] = 30
            } else if hold[index] > 0 {
                hold[index] -= Int(frames)
            } else {
                caps[index] = max(0, caps[index] - 0.01 * frames)
            }
        }
        return (levels, caps)
    }
}

/// Bass, mids and highs for the full-screen player's effects, eased as the web page's `NowPlaying.vue` eases them:
/// quick up, slow down.
@MainActor
final class BeatLevels {
    private(set) var levels: [Double] = [0, 0, 0]
    private var date: Date?
    private static let ranges = [0..<6, 6..<15, 15..<24]

    /// Two views step it per frame (the glows and the cover), each at its own timeline's date; one step per frame.
    func step(_ analyzer: SpectrumAnalyzer, at now: Date) -> [Double] {
        if let date, abs(now.timeIntervalSince(date)) < 0.008 { return levels }
        date = now
        let bands = analyzer.bands(24)
        for (index, range) in Self.ranges.enumerated() {
            let level = Double(bands[range].reduce(0, +)) / Double(range.count)
            levels[index] = level > levels[index] ? levels[index] * 0.5 + level * 0.5 : levels[index] * 0.9 + level * 0.1
        }
        return levels
    }
}
