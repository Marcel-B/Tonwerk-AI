#include "TonwerkDSP.h"

#include "Effects.h"
#include "dsp/AnalogVoice.h"
#include "dsp/FmVoice.h"

#include <atomic>
#include <mutex>

namespace tonwerk
{
namespace
{
constexpr int kVoices = 16;
constexpr int kBlock = 256;
/** Semitones the pitch wheel bends either way, as the plugin's `kBendRange`. */
constexpr double kBendRange = 2.0;

template <typename T>
T pick(int32_t value, std::initializer_list<T> options)
{
    const auto index = (size_t) std::clamp<int32_t>(value, 0, (int32_t) options.size() - 1);
    return *(options.begin() + index);
}

Wave waveOf(int32_t value) { return pick(value, { Wave::sine, Wave::triangle, Wave::sawtooth, Wave::square }); }

tonwerk::Envelope envelopeOf(const TWEnvelope& env) { return { env.attack, env.decay, env.sustain, env.release }; }

Oscillator oscillatorOf(const TWOscillator& osc)
{
    Oscillator result;
    result.wave = waveOf(osc.wave);
    result.octave = std::clamp<int32_t>(osc.octave, -2, 2);
    result.detune = osc.detune;
    result.level = osc.level;
    result.width = osc.width;
    result.pwm = osc.pwm;
    result.pwmLfo = osc.pwmLfo;
    result.pwmAmpEnv = osc.pwmAmpEnv;
    result.pwmFilterEnv = osc.pwmFilterEnv;
    return result;
}

Operator operatorOf(const TWOperator& op) { return { op.ratio, op.detune, op.level, op.velocity, envelopeOf(op.env) }; }

/** The app's sound in the plugin's types; the plugin's own extras (folder, sample and hold, sync) stay off. */
Patch patchOf(const TWPatch& in)
{
    Patch patch;
    patch.engine = in.engine == 1 ? Engine::fm : Engine::analog;
    auto& analog = patch.analog;
    analog.osc1 = oscillatorOf(in.osc1);
    analog.osc2 = oscillatorOf(in.osc2);
    analog.noise = in.noise;
    analog.filter.type = pick(in.filterType, { FilterType::lowpass, FilterType::highpass, FilterType::bandpass });
    analog.filter.cutoff = in.cutoff;
    analog.filter.resonance = in.resonance;
    analog.filter.envAmount = in.envAmount;
    analog.filter.keyTrack = in.keyTrack;
    analog.filterEnv = envelopeOf(in.filterEnv);
    analog.ampEnv = envelopeOf(in.ampEnv);
    analog.lfo.wave = waveOf(in.lfo.wave);
    analog.lfo.rate = in.lfo.rate;
    analog.lfo.target = pick(in.lfo.target, { AnalogLfoTarget::pitch, AnalogLfoTarget::filter, AnalogLfoTarget::amp });
    analog.lfo.depth = in.lfo.depth;
    analog.volume = in.volume;

    auto& fm = patch.fm;
    fm.algorithm = std::clamp<int32_t>(in.algorithm, 1, 8);
    fm.feedback = in.feedback;
    fm.ops = { operatorOf(in.op1), operatorOf(in.op2), operatorOf(in.op3), operatorOf(in.op4) };
    fm.lfo.wave = waveOf(in.fmLfo.wave);
    fm.lfo.rate = in.fmLfo.rate;
    fm.lfo.target = pick(in.fmLfo.target, { FmLfoTarget::pitch, FmLfoTarget::index, FmLfoTarget::amp });
    fm.lfo.depth = in.fmLfo.depth;
    fm.volume = in.fmVolume;

    patch.fx.delay.mix = in.delayMix;
    patch.fx.delay.time = std::clamp(in.delayTime, 0.02f, (float) EffectsChain::kMaxDelaySeconds - 0.05f);
    patch.fx.delay.feedback = std::clamp(in.delayFeedback, 0.0f, 0.9f);
    patch.fx.delay.tone = in.delayTone;
    patch.fx.reverb.mix = in.reverbMix;
    patch.fx.reverb.decay = std::clamp(in.reverbDecay, 0.3f, 8.0f);
    return patch;
}

/** One of the sixteen: whichever engine the patch named when its note started, as the plugin's `SynthVoice`. */
struct Voice
{
    AnalogVoice analog;
    FmVoice fm;
    Engine engine = Engine::analog;
    int key = -1;
    bool held = false;
    uint64_t started = 0;

    bool active() const { return engine == Engine::fm ? fm.isActive() : analog.isActive(); }
};
} // namespace
} // namespace tonwerk

using namespace tonwerk;

struct TWSynth
{
    std::array<Voice, kVoices> voices;
    EffectsChain effects;
    Patch patch;
    double bend = 0.0;
    uint64_t counter = 0;
    std::array<float, kBlock> mono {};

    // The patch the main thread set last; the audio thread takes it when it can get the lock without waiting.
    std::mutex lock;
    Patch pending;
    std::atomic<bool> changed { false };

    void takePatch()
    {
        if (! changed.load(std::memory_order_acquire) || ! lock.try_lock())
            return;
        patch = pending;
        changed.store(false, std::memory_order_relaxed);
        lock.unlock();
    }

    void noteOn(int key, int velocity)
    {
        // The same key again takes its own voice; otherwise a free one, else the oldest let go, else the oldest.
        Voice* chosen = nullptr;
        for (auto& voice : voices)
            if (voice.key == key && voice.active())
                chosen = &voice;
        if (chosen == nullptr)
            for (auto& voice : voices)
                if (! voice.active())
                {
                    chosen = &voice;
                    break;
                }
        if (chosen == nullptr)
            for (auto& voice : voices)
                if (! voice.held && (chosen == nullptr || voice.started < chosen->started))
                    chosen = &voice;
        if (chosen == nullptr)
            for (auto& voice : voices)
                if (chosen == nullptr || voice.started < chosen->started)
                    chosen = &voice;

        chosen->engine = patch.engine;
        chosen->key = key;
        chosen->held = true;
        chosen->started = ++counter;
        const float level = (float) velocity / 127.0f;
        if (patch.engine == Engine::fm)
            chosen->fm.start(key, level, patch.fm);
        else
            chosen->analog.start(key, level, patch.analog);
    }

    void noteOff(int key)
    {
        for (auto& voice : voices)
            if (voice.key == key && voice.held)
            {
                voice.held = false;
                voice.analog.release();
                voice.fm.release();
            }
    }

    void allOff(bool now)
    {
        for (auto& voice : voices)
        {
            voice.held = false;
            if (now)
            {
                voice.analog.kill();
                voice.fm.kill();
            }
            else
            {
                voice.analog.release();
                voice.fm.release();
            }
        }
    }
};

extern "C" {

TWSynth* tw_synth_create(void) { return new TWSynth(); }

void tw_synth_destroy(TWSynth* synth) { delete synth; }

void tw_synth_prepare(TWSynth* synth, double sampleRate)
{
    if (sampleRate <= 0.0)
        return;
    for (auto& voice : synth->voices)
    {
        voice.analog.prepare(sampleRate);
        voice.fm.prepare(sampleRate);
    }
    synth->effects.prepare(sampleRate);
}

void tw_synth_set_patch(TWSynth* synth, const TWPatch* patch)
{
    const Patch converted = patchOf(*patch);
    std::lock_guard<std::mutex> guard(synth->lock);
    synth->pending = converted;
    synth->changed.store(true, std::memory_order_release);
}

void tw_synth_midi(TWSynth* synth, uint8_t status, uint8_t data1, uint8_t data2)
{
    synth->takePatch();
    switch (status & 0xF0)
    {
        case 0x90:
            if (data2 > 0)
                synth->noteOn(data1 & 0x7F, data2 & 0x7F);
            else
                synth->noteOff(data1 & 0x7F);
            break;
        case 0x80:
            synth->noteOff(data1 & 0x7F);
            break;
        case 0xE0:
            synth->bend = (((data2 & 0x7F) << 7 | (data1 & 0x7F)) - 8192) / 8192.0 * kBendRange;
            break;
        case 0xB0:
            if (data1 == 123)
                synth->allOff(false);
            else if (data1 == 120)
                synth->allOff(true);
            break;
        default:
            break;
    }
}

void tw_synth_render(TWSynth* synth, float* left, float* right, int32_t frames)
{
    synth->takePatch();
    for (int32_t done = 0; done < frames;)
    {
        const int count = std::min<int>(kBlock, frames - done);
        float* mono = synth->mono.data();
        std::fill(mono, mono + count, 0.0f);
        for (auto& voice : synth->voices)
        {
            if (! voice.active())
                continue;
            if (voice.engine == Engine::fm)
                voice.fm.render(synth->patch.fm, mono, count, synth->bend);
            else
                voice.analog.render(synth->patch.analog, mono, count, synth->bend);
            if (! voice.active())
                voice.key = -1;
        }
        synth->effects.process(synth->patch.fx, mono, left + done, right != nullptr ? right + done : nullptr, count);
        done += count;
    }
}

int32_t tw_synth_active_voices(const TWSynth* synth)
{
    int32_t count = 0;
    for (const auto& voice : synth->voices)
        if (voice.active())
            ++count;
    return count;
}
}
