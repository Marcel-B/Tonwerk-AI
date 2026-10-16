#pragma once

#include "Envelope.h"
#include "Oscillators.h"
#include "Patch.h"

#include <array>

namespace tonwerk
{
/**
 * One note of the FM engine, after Tonwerk's fm.ts: four sine operators in the TX81Z's algorithms, an envelope each.
 *
 * Like the browser, it is frequency modulation in the literal sense: a modulator's output, in Hz, is added to its
 * target's frequency, at most `kMaxIndex` times the modulator's own frequency at full level. Feedback on operator 4
 * is computed in the loop here, as a DX does (the average of its last two outputs added to its phase); the browser had
 * to precompute that loop's wave, which comes to the same sound.
 */
class FmVoice
{
public:
    static constexpr double kMaxIndex = 8.0;
    static constexpr double kMaxFeedback = 1.5;
    static constexpr int kControlSamples = 16;

    void prepare(double newSampleRate)
    {
        sampleRate = newSampleRate;
        for (auto& env : envs)
            env.prepare(sampleRate);
    }

    void start(double newPitch, float newVelocity, const FmPatch& patch)
    {
        pitch = newPitch;
        velocity = newVelocity;
        for (size_t i = 0; i < 4; ++i)
        {
            envs[i].setParameters(patch.ops[i].env);
            envs[i].noteOn();
            phases[i] = 0.0;
        }
        feedbackLast = feedbackBefore = 0.0;
        lfo.reset();
        active = true;
    }

    void release()
    {
        for (auto& env : envs)
            env.noteOff();
    }

    void kill()
    {
        for (auto& env : envs)
            env.quickRelease();
    }

    /** Sounding as long as any envelope runs; `render` ends it once no carrier does. */
    bool isActive() const { return active; }

    void render(const FmPatch& patch, float* out, int count, double bend)
    {
        if (! active)
            return;
        const Algorithm& algorithm = kAlgorithms[(size_t) std::clamp(patch.algorithm, 1, 8) - 1];

        // Only what can be heard is computed: a silent carrier, and every modulator that only reaches silent ones.
        // Modulators always have the higher number, so one pass from operator 1 up settles it.
        std::array<bool, 4> used {};
        std::array<bool, 4> carrier {};
        int carriers = 0;
        for (int index = 0; index < 4; ++index)
        {
            carrier[(size_t) index] = isCarrier(algorithm, index);
            bool feeds = false;
            for (int m = 0; m < algorithm.modCount; ++m)
                if (algorithm.mods[(size_t) m][0] == index && used[(size_t) algorithm.mods[(size_t) m][1]])
                    feeds = true;
            used[(size_t) index] = patch.ops[(size_t) index].level > 0.0f && (carrier[(size_t) index] || feeds);
            if (used[(size_t) index] && carrier[(size_t) index])
                ++carriers;
        }
        if (carriers == 0)
        {
            active = false;
            return;
        }

        const double frequency = noteFrequency(pitch);
        std::array<double, 4> opFrequency {};
        std::array<double, 4> peak {};
        for (size_t i = 0; i < 4; ++i)
        {
            const Operator& op = patch.ops[i];
            envs[i].setParameters(op.env);
            opFrequency[i] = frequency * op.ratio;
            const double scale = 1.0 - op.velocity * (1.0 - velocity);
            peak[i] = carrier[i] ? op.level * scale : kMaxIndex * op.level * op.level * scale * opFrequency[i];
        }
        const double gain = patch.volume / std::sqrt((double) carriers);
        const double beta = patch.feedback * kMaxFeedback;
        const double depth = patch.lfo.depth;

        for (int done = 0; done < count;)
        {
            const int chunk = std::min(kControlSamples, count - done);
            const double lfoValue = depth > 0.0 ? lfo.advance(patch.lfo.wave, patch.lfo.rate, sampleRate, chunk) : 0.0;
            double pitchCents = bend * 100.0;
            double indexGain = 1.0;
            double tremolo = 1.0;
            if (depth > 0.0)
            {
                switch (patch.lfo.target)
                {
                    case FmLfoTarget::pitch: pitchCents += depth * 100.0 * lfoValue; break;
                    case FmLfoTarget::index: indexGain = 1.0 - depth / 2.0 + depth / 2.0 * lfoValue; break;
                    case FmLfoTarget::amp: tremolo = 1.0 - depth / 2.0 + depth / 2.0 * lfoValue; break;
                }
            }
            std::array<double, 4> detune {};
            for (size_t i = 0; i < 4; ++i)
                detune[i] = centsToRatio(patch.ops[i].detune + pitchCents);

            for (int s = 0; s < chunk; ++s)
            {
                std::array<double, 4> outputs {};
                for (size_t i = 0; i < 4; ++i)
                {
                    const double level = envs[i].next();
                    if (! used[i])
                        continue;
                    double value;
                    if (i == 3 && beta > 0.0)
                    {
                        value = std::sin(kTwoPi * phases[i] + beta * (feedbackLast + feedbackBefore) / 2.0);
                        feedbackBefore = feedbackLast;
                        feedbackLast = value;
                    }
                    else
                    {
                        value = std::sin(kTwoPi * phases[i]);
                    }
                    outputs[i] = value * level * peak[i];
                }

                std::array<double, 4> modulation {};
                for (int m = 0; m < algorithm.modCount; ++m)
                {
                    const auto [from, to] = algorithm.mods[(size_t) m];
                    modulation[(size_t) to] += outputs[(size_t) from] * indexGain;
                }

                double sum = 0.0;
                for (size_t i = 0; i < 4; ++i)
                {
                    if (! used[i])
                        continue;
                    if (carrier[i])
                        sum += outputs[i];
                    phases[i] += (opFrequency[i] + modulation[i]) * detune[i] / sampleRate;
                    phases[i] -= std::floor(phases[i]);
                }
                out[done + s] += (float) (sum * gain * tremolo);
            }
            done += chunk;
        }

        // The voice lasts as long as a carrier sounds; modulators are cut with it.
        bool sounding = false;
        for (size_t i = 0; i < 4; ++i)
            if (used[i] && carrier[i] && envs[i].isActive())
                sounding = true;
        active = sounding;
    }

private:
    double sampleRate = 48000.0;
    double pitch = 60.0;
    float velocity = 1.0f;
    bool active = false;
    std::array<EnvelopeGenerator, 4> envs;
    std::array<double, 4> phases {};
    double feedbackLast = 0.0;
    double feedbackBefore = 0.0;
    Lfo lfo;
};
} // namespace tonwerk
