#pragma once

// Tonwerk's two synthesizers for the apps: the plain C++ DSP of the Tonwerk Synth plugin (Marcel-B/Synth, src/dsp,
// copied unchanged into dsp/), which reproduces the browser's synth.ts and fm.ts, behind a C interface Swift can call.
// Synth.cpp adds what the plugin takes from JUCE: sixteen voices, MIDI, and delay and reverb (Effects.h).
//
// Threads: create, prepare and set_patch on any thread but the audio thread; midi and render on the audio thread only.
// A new patch reaches the voices at the next render, sounding notes included, as in the plugin.

#include <stdbool.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/// Waves in the order of Tonwerk's WAVES: sine, triangle, sawtooth, square (the pulse).
typedef struct {
    float attack, decay, sustain, release;
} TWEnvelope;

typedef struct {
    int32_t wave;
    int32_t octave;
    float detune, level, width, pwm;
    bool pwmLfo;
    float pwmAmpEnv, pwmFilterEnv;
} TWOscillator;

/// Targets: analog pitch, filter, amp; FM pitch, index, amp.
typedef struct {
    int32_t wave;
    float rate;
    int32_t target;
    float depth;
} TWLfo;

typedef struct {
    float ratio, detune, level, velocity;
    TWEnvelope env;
} TWOperator;

/// One sound with both engines' settings; `engine` 0 plays the analog one, 1 the FM one.
typedef struct {
    int32_t engine;

    TWOscillator osc1, osc2;
    float noise;
    /// lowpass, highpass, bandpass.
    int32_t filterType;
    float cutoff, resonance, envAmount, keyTrack;
    TWEnvelope filterEnv, ampEnv;
    TWLfo lfo;
    float volume;

    int32_t algorithm;
    float feedback;
    TWOperator op1, op2, op3, op4;
    TWLfo fmLfo;
    float fmVolume;

    float delayMix, delayTime, delayFeedback, delayTone;
    float reverbMix, reverbDecay;
} TWPatch;

typedef struct TWSynth TWSynth;

TWSynth *tw_synth_create(void);
void tw_synth_destroy(TWSynth *synth);
/// Allocates for the sample rate; not while rendering.
void tw_synth_prepare(TWSynth *synth, double sampleRate);
void tw_synth_set_patch(TWSynth *synth, const TWPatch *patch);
/// One MIDI 1.0 channel message: note on and off, pitch bend, all notes off (123) and all sound off (120).
void tw_synth_midi(TWSynth *synth, uint8_t status, uint8_t data1, uint8_t data2);
/// Writes `frames` samples of the voices plus effects; `right` may be null for mono.
void tw_synth_render(TWSynth *synth, float *left, float *right, int32_t frames);
/// Notes still sounding, their release included.
int32_t tw_synth_active_voices(const TWSynth *synth);

#ifdef __cplusplus
}
#endif
