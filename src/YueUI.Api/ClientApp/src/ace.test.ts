import { describe, expect, test } from 'vitest'
import { aceKeyOf, aceKeys, defaultAceForm, fromAceRequest, toAceRequest, withAceTemplate, withHeard } from './ace'

describe('toAceRequest', () => {
  test('a new song sends only what was set, the rest left to the model', () => {
    const request = toAceRequest({
      ...defaultAceForm(),
      title: '  ',
      caption: ' dark synthwave ',
      lyrics: '[Verse]\nLa la',
      language: 'de',
      bpm: 96,
      seed: ' 42 ',
      source: 'kept/song0',
      coverStrength: 0.4,
    })

    expect(request).toMatchObject({
      title: null,
      caption: 'dark synthwave',
      lyrics: '[Verse]\nLa la',
      language: 'de',
      bpm: 96,
      duration: null,
      key: null,
      seed: 42,
      batch: 1,
      lmModel: null,
      enrichCaption: true,
      task: 'text2music',
      // Only a cover or repaint starts from a song.
      source: null,
      coverStrength: null,
    })
  })

  test('an instrumental has neither lyrics nor language', () => {
    const request = toAceRequest({
      ...defaultAceForm(),
      caption: 'ambient',
      lyrics: 'kept',
      language: 'de',
      instrumental: true,
    })
    expect([request.lyrics, request.language, request.instrumental]).toEqual([null, null, true])
  })

  test('a cover leaves out what the LM would decide, the source decides it', () => {
    const request = toAceRequest({
      ...defaultAceForm(),
      task: 'cover',
      caption: 'jazz',
      source: 'run/song1',
      coverStrength: 0.5,
      bpm: 120,
      lmModel: 'lm.gguf',
      lmTemperature: 0.9,
      negativePrompt: 'noise',
      repaintStart: 10,
    })
    expect(request).toMatchObject({
      source: 'run/song1',
      coverStrength: 0.5,
      bpm: null,
      lmModel: null,
      lmTemperature: null,
      negativePrompt: null,
      enrichCaption: null,
      repaintStart: null,
    })
  })

  test('a seed that is not a whole number is left to the server', () => {
    expect(toAceRequest({ ...defaultAceForm(), caption: 'x', seed: '1.5' }).seed).toBeNull()
  })
})

describe('fromAceRequest', () => {
  test('a song comes back with its own seed, the rest at the defaults', () => {
    const form = fromAceRequest({ caption: 'pop', seed: 8, lyrics: 'Written by the LM', batch: null })
    // Made before the highs could be given back, so made again without.
    expect(form).toEqual({
      ...defaultAceForm(),
      caption: 'pop',
      seed: '8',
      lyrics: 'Written by the LM',
      brighten: false,
    })
    expect(toAceRequest(form).seed).toBe(8)
    expect(fromAceRequest({ caption: 'pop', brighten: true }).brighten).toBe(true)
  })
})

describe('withHeard', () => {
  const heard = {
    caption: 'dreamy synth-pop, hummed melody',
    lyrics: '[Verse]\nLa la',
    bpm: 96,
    key: 'D# Minor',
    timeSignature: 3,
    language: 'en',
    duration: 4,
    instrumental: false,
  }

  test('what was heard replaces the song, the way it is made stays', () => {
    const form = {
      ...defaultAceForm(),
      title: 'Idee',
      ditModel: 'xl.gguf',
      seed: '5',
      task: 'cover' as const,
      bpm: 120,
    }
    expect(withHeard(form, heard)).toEqual({
      ...form,
      task: 'text2music',
      caption: 'dreamy synth-pop, hummed melody',
      lyrics: '[Verse]\nLa la',
      language: 'en',
      bpm: 96,
      key: 'Eb minor',
      timeSignature: 3,
      // A hummed idea of four seconds still asks for the shortest song.
      duration: 10,
    })
  })

  test('what it could not tell goes back to the model', () => {
    const form = { ...defaultAceForm(), caption: 'mine', lyrics: 'old', language: 'de', key: 'C major', bpm: 100 }
    const blank = {
      caption: null,
      lyrics: null,
      bpm: null,
      key: 'H major',
      timeSignature: null,
      language: null,
      duration: null,
      instrumental: true,
    }
    expect(withHeard(form, blank)).toMatchObject({
      caption: 'mine',
      lyrics: '',
      instrumental: true,
      language: '',
      key: '',
      bpm: null,
      duration: null,
    })
  })

  test("keys as the LM writes them become the picker's", () => {
    expect(aceKeyOf('Bb major')).toBe('Bb major')
    expect(aceKeyOf(' g♯ minor ')).toBe('Ab minor')
    expect(aceKeyOf('F#  Major')).toBe('F# major')
    expect(aceKeyOf('C')).toBe('')
    expect(aceKeyOf(null)).toBe('')
  })
})

test('keys read as acestep.cpp writes them', () => {
  expect(aceKeys).toHaveLength(28)
  expect(aceKeys.slice(0, 2)).toEqual(['C major', 'C minor'])
  expect(aceKeys).toContain('F# minor')
})

describe('withAceTemplate', () => {
  test('a library song gives title, style and lyrics, the rest of the form stays', () => {
    const form = {
      ...defaultAceForm(),
      title: 'Alt',
      caption: 'old caption',
      lyrics: 'old',
      language: 'en',
      duration: 180,
      seed: '7',
      lmModel: 'lm.gguf',
      machine: 'helper',
      task: 'cover' as const,
      source: 'run/song1',
    }
    const taken = withAceTemplate(form, {
      title: 'Neonregen',
      style: 'synthwave, German, female vocal',
      lyrics: '[verse]\nRegen',
      instrumental: false,
    })

    expect(taken).toMatchObject({
      title: 'Neonregen',
      caption: 'synthwave, German, female vocal',
      lyrics: '[verse]\nRegen',
      instrumental: false,
      language: 'de',
      task: 'text2music',
      duration: 180,
      seed: '7',
      lmModel: 'lm.gguf',
      machine: 'helper',
    })
  })

  test('without a language block the LM tells, an instrumental takes no lyrics', () => {
    const taken = withAceTemplate(
      { ...defaultAceForm(), language: 'fr' },
      { title: 'Drift', style: 'ambient, slow', lyrics: '[verse]\nNo', instrumental: true },
    )

    expect(taken.language).toBe('')
    expect(taken.lyrics).toBe('')
    expect(taken.instrumental).toBe(true)
  })
})
