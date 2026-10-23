import { beforeEach, describe, expect, it } from 'vitest'
import { locale } from './i18n'
import { stemModelLabel, stemModelSplits } from './stemModels'

describe('stemModelSplits', () => {
  beforeEach(() => {
    locale.value = 'de'
  })

  it('tells a voice-only model from one that splits instruments', () => {
    expect(stemModelSplits(['vocals', 'instrumental'])).toBe('Gesang / Instrumental')
    expect(stemModelSplits(['Instrumental', 'Vocals'])).toBe('Gesang / Instrumental')
    expect(stemModelSplits(['vocals', 'karaoke'])).toBe('Leadgesang / Rest')
    expect(stemModelSplits(['vocals', 'drums', 'bass', 'other'])).toBe('Gesang + 3 Instrumente')
    expect(stemModelSplits(['vocals', 'drums', 'bass', 'guitar', 'piano', 'other'])).toBe('Gesang + 5 Instrumente')
    expect(stemModelSplits(['kick', 'snare', 'toms', 'hh', 'ride', 'crash'])).toBe('Schlagzeug in 6 Teile')
  })

  it('leaves a model without stems as it is', () => {
    expect(stemModelSplits(null)).toBe('')
    expect(stemModelLabel({ name: 'X', stems: null })).toBe('X')
    expect(stemModelLabel({ name: 'Mel', stems: ['vocals', 'instrumental'] })).toBe('Mel · Gesang / Instrumental')
  })
})
