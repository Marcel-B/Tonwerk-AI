import { describe, expect, it } from 'vitest'
import { lineAt, shownLines } from './timing'
import type { LyricsTiming } from './types'

const timing: LyricsTiming = {
  lines: [
    { text: 'Rain on the roof', section: 'Verse', start: 10, end: 12, heard: 4 },
    { text: 'Nobody knows', section: 'Verse', start: 13, end: 15, heard: 2 },
    { text: 'Hold on', section: 'Chorus', start: 30, end: 32, heard: 2 },
  ],
  source: 'mix',
  coverage: 1,
  createdAt: '2026-10-13T00:00:00Z',
}

describe('lyrics timing', () => {
  it('numbers only the sung lines, as the server does', () => {
    const lines = shownLines('[Verse]\nRain on the roof\nNobody knows\n\n[Chorus]\n...\nHold on')
    expect(lines.map((line) => line.timed)).toEqual([-1, 0, 1, -1, -1, -1, 2])
  })

  it('lights the line being sung and nothing in a long break', () => {
    expect(lineAt(timing, 5)).toBe(-1)
    expect(lineAt(timing, 10.5)).toBe(0)
    expect(lineAt(timing, 12.5)).toBe(0)
    expect(lineAt(timing, 14)).toBe(1)
    expect(lineAt(timing, 25)).toBe(-1)
    expect(lineAt(timing, 31)).toBe(2)
    expect(lineAt(null, 31)).toBe(-1)
  })
})
