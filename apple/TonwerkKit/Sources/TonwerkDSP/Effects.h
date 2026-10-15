#pragma once

#include "dsp/Oscillators.h"
#include "dsp/Patch.h"

#include <algorithm>
#include <array>
#include <cmath>
#include <vector>

namespace tonwerk
{
/**
 * Delay and reverb after the voices, as Tonwerk's effects.ts has them: sends, the dry sound passes unchanged. The delay
 * is the plugin's (EffectsChain.cpp) without JUCE: a fractional read so a gliding time bends the repeats, a lowpass in
 * the loop, the browser's glide times.
 *
 * The reverb is not the browser's convolution with a noise impulse, which needs an FFT engine: a feedback delay
 * network of eight lines mixed by a Hadamard matrix, each line's gain set so the tail falls by 60 dB over `decay`,
 * damped a little above 6 kHz. A different room, the same length; levels set so a full mix is about as loud as the dry
 * sound, as there.
 */
class EffectsChain
{
public:
    static constexpr double kMaxDelaySeconds = 1.6;

    void prepare(double newSampleRate)
    {
        sampleRate = newSampleRate;
        delayLine.assign((size_t) (kMaxDelaySeconds * sampleRate) + 4, 0.0f);
        writeIndex = 0;
        tone.reset();
        glideFast = 1.0 - std::exp(-1.0 / (0.02 * sampleRate));
        glideSlow = 1.0 - std::exp(-1.0 / (0.05 * sampleRate));
        damping = std::exp(-kTwoPi * 6000.0 / sampleRate);
        for (size_t i = 0; i < kLines; ++i)
        {
            lengths[i] = std::max(1, (int) std::lround(kLineMilliseconds[i] / 1000.0 * sampleRate));
            lines[i].assign((size_t) lengths[i], 0.0f);
            positions[i] = 0;
            lowpass[i] = 0.0;
        }
        appliedDecay = -1.0;
    }

    /** Takes `count` mono samples and writes dry plus effects into `left` and, if given, `right`. */
    void process(const Effects& fx, const float* in, float* left, float* right, int count)
    {
        if (delayLine.empty())
        {
            // Not prepared yet: the dry sound alone rather than whatever the buffers held.
            std::copy(in, in + count, left);
            if (right != nullptr)
                std::copy(in, in + count, right);
            return;
        }
        if (std::abs(fx.reverb.decay - appliedDecay) > 1.0e-4)
            setDecay(fx.reverb.decay);
        const int size = (int) delayLine.size();
        for (int done = 0; done < count;)
        {
            const int chunk = std::min(16, count - done);
            tone.set(FilterType::lowpass, smoothedTone, 1.0, sampleRate);
            for (int i = 0; i < chunk; ++i)
            {
                smoothedDelayMix += (fx.delay.mix - smoothedDelayMix) * glideFast;
                smoothedFeedback += (fx.delay.feedback - smoothedFeedback) * glideFast;
                smoothedTone += (fx.delay.tone - smoothedTone) * glideFast;
                smoothedReverbMix += (fx.reverb.mix - smoothedReverbMix) * glideFast;
                smoothedTime += (fx.delay.time - smoothedTime) * glideSlow;

                const float dry = in[done + i];
                const double delaySamples = std::clamp(smoothedTime * sampleRate, 1.0, (double) size - 2.0);
                double readPosition = writeIndex - delaySamples;
                if (readPosition < 0.0)
                    readPosition += size;
                const int index = (int) readPosition;
                const double fraction = readPosition - index;
                const float a = delayLine[(size_t) index];
                const float b = delayLine[(size_t) ((index + 1) % size)];
                const double toned = tone.process(a + (b - a) * fraction);
                delayLine[(size_t) writeIndex] = (float) (dry * smoothedDelayMix + toned * smoothedFeedback);
                writeIndex = (writeIndex + 1) % size;

                double wetLeft = toned;
                double wetRight = toned;
                // The room is computed while it may still ring, and left alone once it has died away.
                const double send = dry * smoothedReverbMix;
                if (send != 0.0 || tail > 0)
                {
                    tail = send != 0.0 ? tailSamples : tail - 1;
                    const auto [roomLeft, roomRight] = room(send);
                    wetLeft += roomLeft;
                    wetRight += roomRight;
                }
                left[done + i] = (float) (dry + wetLeft);
                if (right != nullptr)
                    right[done + i] = (float) (dry + wetRight);
            }
            done += chunk;
        }
    }

private:
    static constexpr size_t kLines = 8;
    /** Mutually prime-ish lengths between 30 and 80 ms, so the echoes do not line up into a pitch. */
    static constexpr std::array<double, kLines> kLineMilliseconds { 29.7, 37.1, 41.1, 43.7, 53.1, 59.3, 67.9, 79.3 };

    void setDecay(double decay)
    {
        appliedDecay = decay;
        double energy = 0.0;
        for (size_t i = 0; i < kLines; ++i)
        {
            // -60 dB is a factor of 1000 over `decay` seconds; each pass through a line takes its length.
            gains[i] = std::pow(10.0, -3.0 * lengths[i] / (decay * sampleRate));
            energy += 1.0 / (1.0 - gains[i] * gains[i]);
        }
        // A tail's energy grows with its length, though less than 1/(1-g²) says, since the damping takes some on
        // every pass; this power, measured with noise, keeps a full send near the dry sound's level from 0.3 to 8 s.
        outputGain = 1.0 / std::pow(energy / kLines, 0.28);
        tailSamples = (int) (decay * sampleRate) + 1;
    }

    std::pair<double, double> room(double input)
    {
        std::array<double, kLines> read {};
        for (size_t i = 0; i < kLines; ++i)
            read[i] = lines[i][(size_t) positions[i]];
        // A fast Walsh-Hadamard transform: every line feeds every other, energy kept (scaled by 1/sqrt(8)).
        std::array<double, kLines> mixed = read;
        for (size_t width = 1; width < kLines; width *= 2)
            for (size_t start = 0; start < kLines; start += 2 * width)
                for (size_t j = start; j < start + width; ++j)
                {
                    const double x = mixed[j];
                    const double y = mixed[j + width];
                    mixed[j] = x + y;
                    mixed[j + width] = x - y;
                }
        constexpr double norm = 0.35355339059327373; // 1 / sqrt(8)
        double outLeft = 0.0;
        double outRight = 0.0;
        for (size_t i = 0; i < kLines; ++i)
        {
            lowpass[i] = mixed[i] * norm * (1.0 - damping) + lowpass[i] * damping;
            lines[i][(size_t) positions[i]] = (float) (input + lowpass[i] * gains[i]);
            positions[i] = (positions[i] + 1) % lengths[i];
            (i % 2 == 0 ? outLeft : outRight) += read[i];
        }
        return { outLeft * outputGain / 2.0, outRight * outputGain / 2.0 };
    }

    double sampleRate = 48000.0;
    std::vector<float> delayLine;
    int writeIndex = 0;
    Biquad tone;
    double smoothedTime = 0.35;
    double smoothedDelayMix = 0.0;
    double smoothedFeedback = 0.35;
    double smoothedTone = 4000.0;
    double smoothedReverbMix = 0.0;
    double glideFast = 0.0;
    double glideSlow = 0.0;

    std::array<std::vector<float>, kLines> lines;
    std::array<int, kLines> lengths {};
    std::array<int, kLines> positions {};
    std::array<double, kLines> gains {};
    std::array<double, kLines> lowpass {};
    double damping = 0.0;
    double outputGain = 0.0;
    double appliedDecay = -1.0;
    int tailSamples = 0;
    int tail = 0;
};
} // namespace tonwerk
