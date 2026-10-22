import { beforeEach, describe, expect, test } from 'vitest'
import { locale } from './i18n'
import { heardNote, mergeTakes, singAlignment, singStage, voiceRange } from './singing'
import type { SingTake } from './types'

function take(id: string, stage: SingTake['stage'], updatedAt: string, extra: Partial<SingTake> = {}): SingTake {
  return {
    id,
    title: id,
    stage,
    voiceId: 'en',
    voiceLabel: 'English',
    lyrics: '',
    pitchShift: 0,
    autoShift: false,
    noteCount: 4,
    createdAt: `2026-10-21T10:00:0${id}Z`,
    updatedAt,
    ...extra,
  }
}

beforeEach(() => {
  locale.value = 'en'
})

describe('singing', () => {
  test('live takes win when newer, cancelled ones leave, newest first', () => {
    const merged = mergeTakes(
      [take('1', 'queued', '2026-10-21T10:00:00Z'), take('2', 'done', '2026-10-21T10:05:00Z')],
      [take('1', 'singing', '2026-10-21T10:01:00Z'), take('2', 'cancelled', '2026-10-21T10:06:00Z')],
    )
    expect(merged.map((t) => [t.id, t.stage])).toEqual([['1', 'singing']])
  })

  test('stage, alignment and what was heard read as the app says them', () => {
    expect(singStage({ stage: 'singing', progress: 0.42 })).toBe('Singing 42 %')
    expect(singAlignment(take('1', 'done', '', { sung: 8, unsung: 1, leftover: 2 }))).toBe(
      '8 notes sung · 1 without a syllable · 2 words left over',
    )
    expect(
      heardNote({
        seconds: 3.2,
        recordedSeconds: 5,
        notes: 9,
        held: 0,
        unsung: 0,
        leftover: 0,
        text: 'twinkle',
        peak: 0.99,
        low: 55,
        high: 67,
      }),
    ).toContain('clips')
    expect(voiceRange({ id: 'x', label: 'x', language: 'English', own: true, low: 55, high: 67 })).toBe('G2–G3')
  })
})
