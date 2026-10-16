#pragma once

#include "Envelope.h"
#include "Oscillators.h"
#include "Patch.h"

namespace tonwerk
{
/**
 * One note of the analog engine, after Tonwerk's synth.ts: two oscillators and noise into a filter, an envelope each
 * for loudness and cutoff, an LFO. The browser builds a graph of nodes per note; this computes the same thing sample by
 * sample. Pitch, cutoff and pulse widths follow their modulation every `kControlSamples` samples, the sound itself runs
 * at the full rate. Beyond the browser it has a wavefolder before the filter and a sample and hold for cutoff and
 * pitch, both off in every sound from Tonwerk.
 *
 * The patch is read on every block rather than kept from the note's start, so automation and the editor's knobs are
 * heard while a note sounds; in the browser an edit only reached the next note.
 */
class AnalogVoice
{
public:
    static constexpr int kControlSamples = 16;

    void prepare(double newSampleRate)
    {
        sampleRate = newSampleRate;
        ampEnv.prepare(sampleRate);
        filterEnv.prepare(sampleRate);
        folder.prepare(sampleRate);
    }

    void start(double newPitch, float newVelocity, const AnalogPatch& patch)
    {
        pitch = newPitch;
        velocity = newVelocity;
        ampEnv.setParameters(patch.ampEnv);
        filterEnv.setParameters(patch.filterEnv);
        ampEnv.noteOn();
        filterEnv.noteOn();
        osc1.reset();
        osc2.reset();
        lfo.reset();
        sampleHold.reset();
        folder.reset();
        filter.reset();
    }

    void release()
    {
        ampEnv.noteOff();
        filterEnv.noteOff();
    }

    void kill() { ampEnv.quickRelease(); }

    bool isActive() const { return ampEnv.isActive(); }

    /** Adds `count` samples to `out`; `bend` is the pitch wheel in semitones. */
    void render(const AnalogPatch& patch, float* out, int count, double bend)
    {
        if (! ampEnv.isActive())
            return;
        ampEnv.setParameters(patch.ampEnv);
        filterEnv.setParameters(patch.filterEnv);
        const double frequency = noteFrequency(pitch);
        const double peak = velocity * patch.volume;
        const Widths widths1 = widthsOf(patch.osc1);
        const Widths widths2 = widthsOf(patch.osc2);
        // The LFO runs for the pulse widths even when it moves nothing else, as in the browser.
        const bool lfoRuns = patch.lfo.depth > 0.0f || widths1.lfo > 0.0 || widths2.lfo > 0.0;
        const double tracked =
            patch.filter.cutoff * std::pow(2.0, patch.filter.keyTrack * (pitch - 60.0) / 12.0);
        const auto& sh = patch.sampleHold;
        const bool sampling = sh.filter > 0.0f || sh.pitch > 0.0f;
        const auto& fold = patch.fold;
        // Off exactly when untouched, so every sound from Tonwerk passes as before.
        const bool folding = fold.amount > 0.0f || ! isZero(fold.env) || ! isZero(fold.symmetry);

        for (int done = 0; done < count;)
        {
            const int chunk = std::min(kControlSamples, count - done);
            const double lfoValue = lfoRuns ? lfo.advance(patch.lfo.wave, patch.lfo.rate, sampleRate, chunk) : 0.0;
            const double depth = patch.lfo.depth;

            double pitchCents = bend * 100.0;
            double filterCents = filterEnv.current() * patch.filter.envAmount * 1200.0;
            double tremolo = 1.0;
            if (depth > 0.0)
            {
                switch (patch.lfo.target)
                {
                    case AnalogLfoTarget::pitch: pitchCents += depth * 100.0 * lfoValue; break;
                    case AnalogLfoTarget::filter: filterCents += depth * 2400.0 * lfoValue; break;
                    case AnalogLfoTarget::amp: tremolo = 1.0 - depth / 2.0 + depth / 2.0 * lfoValue; break;
                }
            }
            if (sampling)
            {
                const double step = sampleHold.advance(sh.rate, sampleRate, chunk);
                filterCents += sh.filter * 2400.0 * step;
                pitchCents += sh.pitch * 1200.0 * step;
            }
            filter.set(patch.filter.type,
                       std::max(20.0, tracked) * centsToRatio(filterCents),
                       patch.filter.resonance,
                       sampleRate);

            const double ampShape = ampEnv.current();
            const double filterShape = filterEnv.current();
            const double width1 = widths1.at(lfoValue, ampShape, filterShape);
            const double width2 = widths2.at(lfoValue, ampShape, filterShape);
            const double pitchRatio = centsToRatio(pitchCents);
            const double foldGain = 1.0 + 7.0 * std::clamp(fold.amount + fold.env * filterShape, 0.0, 1.0);
            const double frequency1 = frequency * std::pow(2.0, patch.osc1.octave) * centsToRatio(patch.osc1.detune) * pitchRatio;
            const double frequency2 = frequency * std::pow(2.0, patch.osc2.octave) * centsToRatio(patch.osc2.detune) * pitchRatio;

            for (int i = 0; i < chunk; ++i)
            {
                double sum = 0.0;
                if (patch.osc1.level > 0.0f)
                    sum += patch.osc1.level * osc1.next(patch.osc1.wave, frequency1, sampleRate, width1);
                if (patch.osc2.level > 0.0f)
                    sum += patch.osc2.level * osc2.next(patch.osc2.wave, frequency2, sampleRate, width2);
                if (patch.noise > 0.0f)
                    sum += patch.noise * noise.next();
                if (folding)
                    sum = folder.process(sum, foldGain, fold.symmetry);
                filterEnv.next();
                const double level = ampEnv.next();
                out[done + i] += (float) (filter.process(sum) * level * peak * tremolo);
            }
            done += chunk;
        }
    }

private:
    /** How a pulse oscillator's width moves: by the LFO and the two envelopes, each with its share of the room. */
    struct Widths
    {
        /** Below 0: not the pulse path, the plain wave. */
        double base = -1.0;
        double lfo = 0.0;
        double ampEnv = 0.0;
        double filterEnv = 0.0;

        double at(double lfoValue, double ampShape, double filterShape) const
        {
            if (base < 0.0)
                return -1.0;
            return base + lfo * lfoValue + ampEnv * ampShape + filterEnv * filterShape;
        }
    };

    static Widths widthsOf(const tonwerk::Oscillator& osc)
    {
        Widths widths;
        const double lfoSwing = osc.pwmLfo ? osc.pwm : 0.0;
        const bool moved = lfoSwing > 0.0 || ! isZero(osc.pwmAmpEnv) || ! isZero(osc.pwmFilterEnv);
        if (osc.wave != Wave::square || (isZero(osc.width - 0.5f) && ! moved))
            return widths;
        widths.base = osc.width;
        if (moved)
        {
            // Together the sources never reach 0 or 100 %, where the wave would fall silent; at full depth they share.
            const double room = std::min(osc.width - 0.02, 0.98 - osc.width);
            const double share =
                room / std::max(1.0, lfoSwing + std::abs(osc.pwmAmpEnv) + std::abs(osc.pwmFilterEnv));
            widths.lfo = lfoSwing * share;
            widths.ampEnv = osc.pwmAmpEnv * share;
            widths.filterEnv = osc.pwmFilterEnv * share;
        }
        return widths;
    }

    double sampleRate = 48000.0;
    double pitch = 60.0;
    float velocity = 1.0f;
    EnvelopeGenerator ampEnv;
    EnvelopeGenerator filterEnv;
    BlepOscillator osc1;
    BlepOscillator osc2;
    Lfo lfo;
    SampleHold sampleHold;
    Wavefolder folder;
    Biquad filter;
    Noise noise;
};
} // namespace tonwerk
