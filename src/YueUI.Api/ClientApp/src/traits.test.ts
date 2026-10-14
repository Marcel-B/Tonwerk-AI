import { describe, expect, test } from 'vitest'
import { sortRuns } from './sort'
import { run, song } from './test/fixtures'
import { matchesTrait, tempoTag } from './traits'
import type { SongTraits } from './types'

const heard = (traits: Partial<SongTraits>): SongTraits => ({
  tempo: null,
  key: null,
  from: 'audio',
  clarity: null,
  clarityFrom: null,
  ...traits,
})

describe('matchesTrait', () => {
  const slowMinor = song('a/song1', { traits: heard({ tempo: 72, key: 'F#m', clarity: 0.3 }) })
  const fastMajor = song('a/song2', { traits: heard({ tempo: 128, key: 'Bb', clarity: 0.8 }) })
  const medium = song('a/song3', { traits: heard({ tempo: 90 }) })
  const unheard = song('a/song4')

  test('tempo ranges meet at their bounds', () => {
    expect([slowMinor, fastMajor, medium, unheard].map((s) => matchesTrait(s, 'slow'))).toEqual([
      true,
      false,
      false,
      false,
    ])
    expect([slowMinor, fastMajor, medium, unheard].map((s) => matchesTrait(s, 'medium'))).toEqual([
      false,
      false,
      true,
      false,
    ])
    expect([slowMinor, fastMajor, medium, unheard].map((s) => matchesTrait(s, 'fast'))).toEqual([
      false,
      true,
      false,
      false,
    ])
  })

  test('mode and clarity; a song not heard yet passes only all', () => {
    expect(matchesTrait(slowMinor, 'minor')).toBe(true)
    expect(matchesTrait(fastMajor, 'major')).toBe(true)
    expect(matchesTrait(fastMajor, 'minor')).toBe(false)
    expect(matchesTrait(slowMinor, 'unclear')).toBe(true)
    expect(matchesTrait(fastMajor, 'unclear')).toBe(false)
    expect(matchesTrait(unheard, 'major')).toBe(false)
    expect(matchesTrait(unheard, 'all')).toBe(true)
  })

  test('a tempo is named as the style blocks name it', () => {
    expect(tempoTag(91.6)).toBe('92 BPM')
  })
})

describe('sorting by what was heard', () => {
  const runs = [
    run('new', {
      songs: [song('new/song1', { traits: heard({ tempo: 100, clarity: 0.9 }) }), song('new/song2')],
    }),
    run('old', { songs: [song('old/song1', { traits: heard({ tempo: 140, clarity: 0.2 }) })] }),
  ]
  const ids = (sorted: ReturnType<typeof sortRuns>) => sorted.map((e) => [e.id, ...e.songs.map((s) => s.id)])

  test('fastest and slowest; songs not heard go last', () => {
    expect(ids(sortRuns(runs, 'fastest', {}, (r) => r.songs))).toEqual([
      ['old', 'old/song1'],
      ['new', 'new/song1', 'new/song2'],
    ])
    expect(ids(sortRuns(runs, 'slowest', {}, (r) => r.songs))).toEqual([
      ['new', 'new/song1', 'new/song2'],
      ['old', 'old/song1'],
    ])
  })

  test('the least understood lyrics come first', () => {
    expect(ids(sortRuns(runs, 'unclear', {}, (r) => r.songs))[0]).toEqual(['old', 'old/song1'])
  })
})
