import { describe, expect, test } from 'vitest'
import {
  applyTemplate,
  defaultFormState,
  defaultSampling,
  exampleScore,
  fromSongRequest,
  maxSongSeconds,
  partsOf,
  planningFor,
  samplingOverrides,
  toGenerateRequest,
  toTemplateSettings,
  withScore,
} from './form'
import type { SongRequest } from './types'

describe('toGenerateRequest', () => {
  test('a fresh form asks for the worker defaults, sending nothing it would not change', () => {
    const request = toGenerateRequest({ ...defaultFormState(), title: ' Song ', style: ' pop ' })
    expect(request).toMatchObject({
      title: 'Song',
      style: 'pop',
      seed: null,
      engines: null,
      maxTokens: null,
      abc: null,
      fullSteps: null,
      abcSampling: null,
      semanticSampling: null,
      voice: null,
      lora: null,
      loraStrength: null,
    })
    expect(request.draftSteps).toBe(8)
  })

  test('the length becomes tokens below six minutes, and a garbled seed counts as random', () => {
    const form = { ...defaultFormState(), maxSeconds: 120, seed: 'abc' }
    expect(toGenerateRequest(form)).toMatchObject({ maxTokens: 3000, seed: null })
    expect(toGenerateRequest({ ...form, seed: '42' }).seed).toBe(42)
  })

  test('draft steps go only with a draft, full steps only with full quality when changed', () => {
    const full = { ...defaultFormState(), quality: 'full' as const }
    expect(toGenerateRequest(full)).toMatchObject({ draftSteps: null, fullSteps: null })
    expect(toGenerateRequest({ ...full, fullSteps: 48 }).fullSteps).toBe(48)
  })

  test('a score switched off stays in the form but is not sent', () => {
    const form = { ...defaultFormState(), abc: exampleScore, cot: 'melody' as const }
    expect(toGenerateRequest(form)).toMatchObject({ abc: exampleScore.trim(), cot: 'melody' })
    expect(toGenerateRequest({ ...form, useAbc: false })).toMatchObject({ abc: null, cot: 'melody' })
  })

  test('advanced parameters switched off go out as defaults, the seed and a score with its planning stay', () => {
    const form = {
      ...defaultFormState(),
      useAdvanced: false,
      seed: '5',
      maxSeconds: 60,
      lora: 'warm',
      abcSampling: { ...defaultSampling.abcSampling, topK: 50 },
    }
    expect(toGenerateRequest(form)).toMatchObject({
      seed: 5,
      maxTokens: null,
      lora: null,
      abcSampling: null,
      cot: 'full',
    })
    expect(toGenerateRequest({ ...form, abc: exampleScore, cot: 'melody' }).cot).toBe('melody')
  })

  test('a voice goes along with the original reverb', () => {
    const form = { ...defaultFormState(), voiceId: 'v1', voiceShift: -12 }
    expect(toGenerateRequest(form).voice).toEqual({
      voiceId: 'v1',
      semiToneShift: -12,
      strength: 0.7,
      diffusionSteps: 50,
      keepReverb: true,
    })
  })
})

describe('samplingOverrides', () => {
  test('only what differs from the model, nothing when nothing does', () => {
    expect(samplingOverrides('abcSampling', defaultSampling.abcSampling)).toBeNull()
    expect(samplingOverrides('abcSampling', { ...defaultSampling.abcSampling, topK: 50, topP: Number.NaN })).toEqual({
      topK: 50,
    })
  })
})

describe('fromSongRequest', () => {
  const request: SongRequest = {
    title: 'Again',
    style: 'rock',
    lyrics: 'la',
    instrumental: null,
    quality: 'full',
    cot: null,
    seed: 7,
    engines: null,
    draftSteps: null,
    maxTokens: 2000,
    abc: null,
    fullSteps: null,
    abcSampling: { topK: 10, temperature: null as unknown as number },
    semanticSampling: null,
    lora: null,
    loraStrength: null,
  }

  test('fills the form with the song, one song with its seed, the rest at defaults', () => {
    const form = fromSongRequest({ ...defaultFormState(), batch: 4, lyricsIdea: 'keep me' }, request)
    expect(form).toMatchObject({
      title: 'Again',
      quality: 'full',
      batch: 1,
      seed: '7',
      cot: 'full',
      lyricsIdea: 'keep me',
    })
    // 80 s is no choice of the form; the next one up still fits the song.
    expect(form.maxSeconds).toBe(90)
    expect(form.abcSampling).toEqual({ ...defaultSampling.abcSampling, topK: 10 })
  })

  test("without a score of its own the song's written one comes along switched off", () => {
    const own = 'X:1\nK:C\n"C" C2 D2 |'
    expect(fromSongRequest(defaultFormState(), request, own)).toMatchObject({ abc: own, useAbc: false, cot: 'full' })
    const asked = fromSongRequest({ ...defaultFormState(), useAdvanced: false }, { ...request, abc: own }, 'other')
    expect(asked).toMatchObject({ abc: own, useAbc: true, useAdvanced: true })
  })

  test('a song without a token limit gets the model default', () => {
    expect(fromSongRequest(defaultFormState(), { ...request, maxTokens: null }).maxSeconds).toBe(maxSongSeconds)
  })
})

describe('withScore', () => {
  test('only the score and its planning change, switched on', () => {
    const form = { ...defaultFormState(), style: 'jazz', seed: '3', useAbc: false }
    expect(withScore(form, exampleScore)).toMatchObject({
      style: 'jazz',
      seed: '3',
      abc: exampleScore,
      cot: 'full',
      useAbc: true,
    })
    expect(withScore(form, 'X:1\nK:C\nC2 D2 |').cot).toBe('melody')
  })
})

describe('planningFor', () => {
  test('a score with chords keeps them, one without plans them', () => {
    expect(planningFor(exampleScore)).toBe('full')
    expect(planningFor('X:1\nV: Vocal name="Vocal Melody"\nK:C\nC D E F|')).toBe('melody')
  })
})

describe('templates', () => {
  const form = { ...defaultFormState(), title: 'T', style: 'jazz', seed: '9', quality: 'full' as const, voiceId: 'v' }

  test('hold the chosen groups and never title, lyrics or score', () => {
    const settings = toTemplateSettings(form, ['style', 'seed'])
    expect(settings).toEqual({ style: 'jazz', instrumental: false, seed: '9' })
    expect(partsOf(settings)).toEqual(['style', 'seed'])
    expect(partsOf(toTemplateSettings(form, ['parameters', 'voice']))).toEqual(['parameters', 'voice'])
  })

  test('keep their sampling as a copy', () => {
    const settings = toTemplateSettings(form, ['parameters'])
    expect(settings.abcSampling).toEqual(form.abcSampling)
    expect(settings.abcSampling).not.toBe(form.abcSampling)
  })

  test('applied, change only what they hold and skip values of the wrong type', () => {
    const applied = applyTemplate(form, { style: 'blues', seed: 3, batch: 2, title: 'never' })
    expect(applied).toMatchObject({ style: 'blues', seed: '9', batch: 2, title: 'T', quality: 'full' })
  })

  test('fill sampling values an older template lacks', () => {
    const applied = applyTemplate(form, { semanticSampling: { topK: 5 } })
    expect(applied.semanticSampling).toEqual({ ...defaultSampling.semanticSampling, topK: 5 })
  })

  test('leave the planning of a score in the form alone', () => {
    const scored = { ...form, abc: exampleScore, cot: 'melody' as const }
    expect(applyTemplate(scored, { cot: 'full' }).cot).toBe('melody')
    expect(applyTemplate(form, { cot: 'off' }).cot).toBe('off')
  })
  test('with parameters switch the advanced ones back on', () => {
    const off = { ...form, useAdvanced: false }
    expect(applyTemplate(off, { draftSteps: 12 }).useAdvanced).toBe(true)
    expect(applyTemplate(off, { style: 'blues' }).useAdvanced).toBe(false)
  })
})
