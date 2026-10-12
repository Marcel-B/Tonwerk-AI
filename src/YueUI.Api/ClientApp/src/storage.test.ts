import { describe, expect, test } from 'vitest'
import { deleteAll, sectionOf, songNumber } from './storage'
import type { StorageGroup, StorageItem } from './types'

function item(id: string, songId: string | null = null): StorageItem {
  return { kind: 'video', id, songId, title: 'Neon Night', detail: null, bytes: 100, createdAt: null }
}

function group(key: StorageGroup['key'], section: StorageGroup['section'], bytes: number): StorageGroup {
  return { key, section, bytes, count: null, volume: '/', moved: null, missing: false, largest: [] }
}

describe('songNumber', () => {
  test('reads the song from its id', () => {
    expect(songNumber(item('v', '20260921-165850-Neon/song12'))).toBe(12)
    expect(songNumber(item('t'))).toBeNull()
  })
})

describe('sectionOf', () => {
  test('lists one section biggest first, each bar measured against the biggest', () => {
    const groups = [group('videos', 'data', 50), group('yue2', 'models', 900), group('songs', 'data', 200)]

    const data = sectionOf(groups, 'data')

    expect(data.map((p) => p.group.key)).toEqual(['songs', 'videos'])
    expect(data.map((p) => p.share)).toEqual([1, 0.25])
  })

  test('an empty part has no bar', () => {
    expect(sectionOf([group('logs', 'data', 0)], 'data')[0]?.share).toBe(0)
  })
})

describe('deleteAll', () => {
  test('goes on after one is refused', async () => {
    const seen: string[] = []
    const result = await deleteAll([item('a'), item('b'), item('c')], async (i) => {
      seen.push(i.id)
      if (i.id === 'b') {
        throw new Error('busy')
      }
    })

    expect(seen).toEqual(['a', 'b', 'c'])
    expect(result.deleted.map((i) => i.id)).toEqual(['a', 'c'])
    expect(result.failed.map((f) => f.item.id)).toEqual(['b'])
  })
})
