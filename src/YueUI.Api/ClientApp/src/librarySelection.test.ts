import { describe, expect, test } from 'vitest'
import { deletionPlan } from './librarySelection'
import { run, song } from './test/fixtures'

describe('deletionPlan', () => {
  const runs = [
    run('a', { songs: [song('a/song1', { bytes: 10 }), song('a/song2', { bytes: 20 })] }),
    run('b', { songs: [song('b/song1', { bytes: 5 }), song('b/song2', { bytes: 7 })] }),
  ]

  test('a run with every song chosen goes whole, the others song by song', () => {
    expect(deletionPlan(runs, new Set(['a/song1', 'a/song2', 'b/song2']))).toEqual({
      runIds: ['a'],
      songIds: ['b/song2'],
      count: 3,
      bytes: 37,
    })
  })

  test('songs no longer listed are left out', () => {
    expect(deletionPlan(runs, new Set(['gone/song1']))).toEqual({ runIds: [], songIds: [], count: 0, bytes: 0 })
  })
})
