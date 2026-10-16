#pragma once

#include "Patch.h"

#include <algorithm>
#include <cmath>

namespace tonwerk
{
/**
 * The envelope of Tonwerk's envelope.ts, run sample by sample instead of scheduled: a linear attack, then an
 * exponential fall to the sustain level, where `decay` is the time to get about 98 % of the way there (a time constant
 * of decay / 4), and on release the same kind of fall to zero with release / 4. Values from 0 to 1; the caller scales.
 *
 * The browser stops a voice `release` seconds after the key is let go, when the level is down to about 2 %. This one
 * does the same, but fades that last bit out over a few milliseconds instead of cutting it, which would click.
 */
class EnvelopeGenerator
{
public:
    void prepare(double newSampleRate) { sampleRate = newSampleRate; }

    void setParameters(const Envelope& env)
    {
        // The same floors as the browser, which keep a zero time from dividing by zero.
        attackStep = 1.0 / (std::max(0.002, (double) env.attack) * sampleRate);
        decayCoefficient = std::exp(-1.0 / (std::max(0.002, (double) env.decay) / 4.0 * sampleRate));
        sustain = env.sustain;
        const double release = std::max(0.005, (double) env.release);
        releaseCoefficient = std::exp(-1.0 / (release / 4.0 * sampleRate));
        releaseSamples = (int) (release * sampleRate);
    }

    void noteOn()
    {
        level = 0.0;
        stage = Stage::attack;
    }

    void noteOff()
    {
        if (stage == Stage::idle || stage == Stage::release || stage == Stage::fade)
            return;
        stage = Stage::release;
        releaseLeft = releaseSamples;
    }

    /** Ends at once, for a voice taken over by another note; a few milliseconds still, so it does not click. */
    void quickRelease()
    {
        if (stage != Stage::idle)
            startFade();
    }

    float next()
    {
        switch (stage)
        {
            case Stage::idle:
                return 0.0f;
            case Stage::attack:
                level += attackStep;
                if (level >= 1.0)
                {
                    level = 1.0;
                    stage = Stage::decay;
                }
                break;
            case Stage::decay:
                level = sustain + (level - sustain) * decayCoefficient;
                break;
            case Stage::release:
                level *= releaseCoefficient;
                if (--releaseLeft <= 0)
                    startFade();
                break;
            case Stage::fade:
                level -= fadeStep;
                if (level <= 0.0)
                {
                    level = 0.0;
                    stage = Stage::idle;
                }
                break;
        }
        return (float) level;
    }

    bool isActive() const { return stage != Stage::idle; }
    bool isReleasing() const { return stage == Stage::release || stage == Stage::fade; }
    float current() const { return (float) level; }

private:
    enum class Stage { idle, attack, decay, release, fade };

    void startFade()
    {
        stage = Stage::fade;
        fadeStep = std::max(level, 1.0e-6) / std::max(1.0, 0.003 * sampleRate);
    }

    double sampleRate = 48000.0;
    Stage stage = Stage::idle;
    double level = 0.0;
    double attackStep = 0.0;
    double decayCoefficient = 0.0;
    double sustain = 0.0;
    double releaseCoefficient = 0.0;
    int releaseSamples = 0;
    int releaseLeft = 0;
    double fadeStep = 0.0;
};
} // namespace tonwerk
