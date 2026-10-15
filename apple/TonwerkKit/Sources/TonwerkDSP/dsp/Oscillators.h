#pragma once

#include "Patch.h"

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdint>

namespace tonwerk
{
inline constexpr double kTwoPi = 6.283185307179586;

/** Hz of a MIDI note, as the browser computes it. */
inline double noteFrequency(double pitch) { return 440.0 * std::pow(2.0, (pitch - 69.0) / 12.0); }

inline double centsToRatio(double cents) { return std::pow(2.0, cents / 1200.0); }

/**
 * The smoothing a band-limited step needs around a jump (polyBLEP), for a phase `t` in [0, 1) advancing `dt` per
 * sample. Web Audio's oscillators are band-limited; naive saws and pulses here would alias audibly in the high notes.
 */
inline double polyBlep(double t, double dt)
{
    if (t < dt)
    {
        t /= dt;
        return t + t - t * t - 1.0;
    }
    if (t > 1.0 - dt)
    {
        t = (t - 1.0) / dt;
        return t * t + t + t + 1.0;
    }
    return 0.0;
}

/** One oscillator of the analog engine, in the browser's shapes and levels. */
class BlepOscillator
{
public:
    void reset() { phase = 0.0; }

    /**
     * The next sample at `frequency` Hz. A pulse width other than none means the pulse of the browser's PWM path,
     * which builds it from a sawtooth minus its delayed copy: a step of 1 from -width to 1 - width, half as loud as the
     * plain square and free of DC. The plain square stays ±1, as Web Audio's is, so a patch sounds as it did there.
     */
    double next(Wave wave, double frequency, double sampleRate, double pulseWidth = -1.0)
    {
        const double dt = std::min(0.5, frequency / sampleRate);
        double value = 0.0;
        switch (wave)
        {
            case Wave::sine:
                value = std::sin(kTwoPi * phase);
                break;
            case Wave::triangle:
                // Phase 0 at the zero crossing going up, like Web Audio's; its soft corners alias little.
                value = phase < 0.25 ? 4.0 * phase : phase < 0.75 ? 2.0 - 4.0 * phase : 4.0 * phase - 4.0;
                break;
            case Wave::sawtooth:
            {
                // Web Audio's sawtooth starts at 0 and rises, so the jump is half a cycle in.
                double t = phase + 0.5;
                if (t >= 1.0)
                    t -= 1.0;
                value = 2.0 * t - 1.0 - polyBlep(t, dt);
                break;
            }
            case Wave::square:
                if (pulseWidth > 0.0)
                {
                    value = (phase < pulseWidth ? 1.0 : 0.0) - pulseWidth;
                    double fall = phase - pulseWidth;
                    if (fall < 0.0)
                        fall += 1.0;
                    value += 0.5 * polyBlep(phase, dt) - 0.5 * polyBlep(fall, dt);
                }
                else
                {
                    value = phase < 0.5 ? 1.0 : -1.0;
                    double fall = phase + 0.5;
                    if (fall >= 1.0)
                        fall -= 1.0;
                    value += polyBlep(phase, dt) - polyBlep(fall, dt);
                }
                break;
        }
        phase += frequency / sampleRate;
        phase -= std::floor(phase);
        return value;
    }

private:
    double phase = 0.0;
};

/** The LFO: the same four shapes without band-limiting, at rates where it does not matter. */
class Lfo
{
public:
    void reset() { phase = 0.0; }

    /** Advances by `samples` and returns the value at the start of that stretch, from -1 to 1. */
    double advance(Wave wave, double rate, double sampleRate, int samples)
    {
        double value = 0.0;
        switch (wave)
        {
            case Wave::sine:
                value = std::sin(kTwoPi * phase);
                break;
            case Wave::triangle:
                value = phase < 0.25 ? 4.0 * phase : phase < 0.75 ? 2.0 - 4.0 * phase : 4.0 * phase - 4.0;
                break;
            case Wave::sawtooth:
                value = phase < 0.5 ? 2.0 * phase : 2.0 * phase - 2.0;
                break;
            case Wave::square:
                value = phase < 0.5 ? 1.0 : -1.0;
                break;
        }
        phase += rate * samples / sampleRate;
        phase -= std::floor(phase);
        return value;
    }

private:
    double phase = 0.0;
};

/** White noise from a small fast generator, so voices need no shared buffer. */
class Noise
{
public:
    explicit Noise(uint32_t seed = 0x9e3779b9u) : state(seed) {}

    double next()
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return (double) state / 2147483648.0 - 1.0;
    }

private:
    uint32_t state;
};

/**
 * Sample and hold: a random value from -1 to 1, held for a step and replaced at the next. Each voice draws its own
 * values from a seed of its own, and starts a step with its note, as the LFO does.
 */
class SampleHold
{
public:
    SampleHold() : random(nextSeed()) {}

    void reset()
    {
        phase = 0.0;
        value = random.next();
    }

    /** Advances by `samples` and returns the value at the start of that stretch. */
    double advance(double rate, double sampleRate, int samples)
    {
        const double now = value;
        phase += rate * samples / sampleRate;
        if (phase >= 1.0)
        {
            phase -= std::floor(phase);
            value = random.next();
        }
        return now;
    }

private:
    /** Voices made one after another get different seeds, so the notes of a chord do not move in step. */
    static uint32_t nextSeed()
    {
        static std::atomic<uint32_t> count { 0 };
        return 0x2545f491u * (count.fetch_add(1) * 2u + 1u);
    }

    Noise random;
    double phase = 0.0;
    double value = 0.0;
};

/**
 * A triangle wavefolder: the signal driven by `gain`, offset by `bias`, and every part beyond ±1 mirrored back, so
 * a louder input gets more folds instead of clipping. Within ±1 at gain 1 and no bias it passes unchanged.
 *
 * Folding makes harmonics far above the note, which alias at the plain sample rate. Each sample is folded twice,
 * once at the point halfway from the previous one, and the two averaged: a cheap twofold oversampling that takes the
 * worst of it off. An offset leaves DC behind, which a highpass at 10 Hz removes.
 */
class Wavefolder
{
public:
    void prepare(double sampleRate) { pole = 1.0 - kTwoPi * 10.0 / sampleRate; }

    void reset() { previous = dcIn = dcOut = 0.0; }

    double process(double x, double gain, double bias)
    {
        const double halfway = 0.5 * (previous + x);
        previous = x;
        const double folded = 0.5 * (fold(halfway * gain + bias) + fold(x * gain + bias));
        const double y = folded - dcIn + pole * dcOut;
        dcIn = folded;
        dcOut = y;
        return y;
    }

    static double fold(double v)
    {
        double t = (v + 1.0) / 4.0;
        t -= std::floor(t);
        return 1.0 - 4.0 * std::abs(t - 0.5);
    }

private:
    double pole = 0.9987;
    double previous = 0.0;
    double dcIn = 0.0;
    double dcOut = 0.0;
};

/**
 * Web Audio's BiquadFilterNode, with its coefficients from the Audio EQ Cookbook as the specification gives them.
 * Its quirk matters for the sound: for low- and highpass, Q is the resonance peak in decibels, not the cookbook's Q.
 */
class Biquad
{
public:
    void reset() { z1 = z2 = 0.0; }

    void set(FilterType type, double frequency, double q, double sampleRate)
    {
        const double nyquist = sampleRate / 2.0;
        frequency = std::clamp(frequency, 10.0, nyquist * 0.999);
        const double w0 = kTwoPi * frequency / sampleRate;
        const double cosW = std::cos(w0);
        const double sinW = std::sin(w0);
        double n0, n1, n2, d0, d1, d2;
        if (type == FilterType::bandpass)
        {
            const double alpha = sinW / (2.0 * std::max(1.0e-4, q));
            n0 = alpha;
            n1 = 0.0;
            n2 = -alpha;
            d0 = 1.0 + alpha;
            d1 = -2.0 * cosW;
            d2 = 1.0 - alpha;
        }
        else
        {
            const double alpha = sinW / (2.0 * std::pow(10.0, q / 20.0));
            if (type == FilterType::lowpass)
            {
                n0 = (1.0 - cosW) / 2.0;
                n1 = 1.0 - cosW;
                n2 = n0;
            }
            else
            {
                n0 = (1.0 + cosW) / 2.0;
                n1 = -(1.0 + cosW);
                n2 = n0;
            }
            d0 = 1.0 + alpha;
            d1 = -2.0 * cosW;
            d2 = 1.0 - alpha;
        }
        b0 = n0 / d0;
        b1 = n1 / d0;
        b2 = n2 / d0;
        a1 = d1 / d0;
        a2 = d2 / d0;
    }

    double process(double x)
    {
        // Transposed direct form II, which stays quiet when the coefficients move every few samples.
        const double y = b0 * x + z1;
        z1 = b1 * x - a1 * y + z2;
        z2 = b2 * x - a2 * y;
        return y;
    }

private:
    double b0 = 1.0, b1 = 0.0, b2 = 0.0, a1 = 0.0, a2 = 0.0;
    double z1 = 0.0, z2 = 0.0;
};
} // namespace tonwerk
