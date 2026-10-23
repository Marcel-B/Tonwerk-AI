import { beforeEach, describe, expect, it, test } from 'vitest'
import { locale } from './i18n'
import {
  heardNote,
  isVocalTrack,
  mergeTakes,
  singAlignment,
  singStage,
  sungLyrics,
  trackNotes,
  voiceRange,
  wordsFromGuide,
} from './singing'
import type { ScoreDocument } from './logic/types'
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

describe('handing a Logic track to the singing page', () => {
  const note = (start: number, end: number, pitch = 60) => ({ start, end, pitch })

  it('keeps only the sung words of lyrics', () => {
    const lyrics = '[Verse]\nRain on the roof (whispered)\nNobody knows\n\n[Chorus]\n(Screamy vocals)\nHold on\n[Outro]'
    expect(sungLyrics(lyrics)).toBe('Rain on the roof\nNobody knows\n\nHold on')
  })

  it('finds the vocal tracks', () => {
    expect(isVocalTrack({ id: 'Vocal', kind: 'Melody' })).toBe(true)
    expect(isVocalTrack({ id: 'Vocal 8vb', kind: 'Doubling' })).toBe(true)
    expect(isVocalTrack({ id: 'Harmony 3rd down', kind: 'Harmony' })).toBe(true)
    expect(isVocalTrack({ id: 'Ins', kind: 'Melody' })).toBe(false)
    expect(isVocalTrack({ id: 'Bass', kind: 'Bass' })).toBe(false)
  })

  it('reads a track in seconds after the count-in', () => {
    const score = {
      ticksPerQuarterNote: 480,
      tempoBpm: 120,
      countInTicks: 1920,
      voices: [
        {
          id: 'Vocal',
          displayName: 'Vocal',
          kind: 'Melody',
          notes: [
            { startTicks: 2400, durationTicks: 480, noteNumber: 64, velocity: null },
            { startTicks: 1920, durationTicks: 480, noteNumber: 62, velocity: null },
          ],
        },
      ],
    } as unknown as ScoreDocument
    expect(trackNotes(score, 'Vocal')).toEqual([note(0, 0.5, 62), note(0.5, 1, 64)])
  })

  it('carries the melody’s words over to a backing part', () => {
    // "Hold" on the first note, held over the second, "on" on the third, "me" on the fourth.
    const guide = [note(0, 1), note(1, 2), note(2, 3), note(3, 4)]
    const answer = {
      leftover: 0,
      notes: [
        { word: 'hold', type: 2, phonemes: '' },
        { word: null, type: 3, phonemes: '' },
        { word: 'on', type: 2, phonemes: '' },
        { word: 'me', type: 2, phonemes: '' },
      ],
    }
    // A part joining on the held syllable, one in the middle of "on", one outside the melody.
    const part = [note(1, 2, 57), note(2, 2.5, 57), note(2.5, 3, 55), note(3, 4, 57), note(5, 6, 57)]
    expect(wordsFromGuide(part, guide, answer).map((n) => n.lyric ?? '')).toEqual(['hold', 'on', '-', 'me', ''])
    // The melody itself sings what the server laid on it.
    expect(wordsFromGuide(guide, guide, answer).map((n) => n.lyric ?? '')).toEqual(['hold', '-', 'on', 'me'])
  })
})
