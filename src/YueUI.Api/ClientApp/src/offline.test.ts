import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { offlineBytes, offlineSongs, setOffline, syncOffline } from './offline'
import type { PlaylistInfo, RunInfo, SongInfo } from './types'

/** Cache Storage as far as the sync uses it, keyed by URL. */
class FakeCache {
  readonly entries = new Map<string, Response>()
  async match(key: string | Request): Promise<Response | undefined> {
    return this.entries.get(typeof key === 'string' ? key : key.url)?.clone()
  }
  async put(key: string, response: Response): Promise<void> {
    this.entries.set(key, response)
  }
  async delete(key: Request): Promise<boolean> {
    return this.entries.delete(key.url)
  }
  async keys(): Promise<Request[]> {
    return [...this.entries.keys()].map((url) => new Request(url))
  }
}

const song = (id: string, extra: Partial<SongInfo> = {}) =>
  ({ id, hasAudio: true, coverUpdatedAt: null, ...extra }) as SongInfo
const runs = [
  {
    songs: [
      song('run-1/song1'),
      song('run-1/song2', { coverUpdatedAt: '2026-10-13T12:00:00Z' }),
      song('run-1/song3', { hasAudio: false }),
    ],
  },
] as RunInfo[]
const playlists: PlaylistInfo[] = [
  { id: 1, name: 'Unterwegs', songIds: ['run-1/song2', 'run-1/song3'] } as PlaylistInfo,
  { id: 2, name: 'Alles', songIds: ['run-1/song1'] } as PlaylistInfo,
]

let cache: FakeCache
let fetched: { url: string; etag: string | null }[]

beforeEach(() => {
  cache = new FakeCache()
  fetched = []
  vi.stubGlobal('location', { href: 'https://mac.example/ui/' })
  vi.stubGlobal('caches', { open: async () => cache })
  vi.stubGlobal('fetch', async (url: string, init?: RequestInit) => {
    const etag = new Headers(init?.headers).get('If-None-Match')
    fetched.push({ url, etag })
    if (etag === '"1"') {
      return new Response(null, { status: 304 })
    }
    return new Response(new Uint8Array(1000), { headers: { ETag: '"1"', 'Content-Length': '1000' } })
  })
  setOffline(2, false)
  setOffline(1, true)
})

afterEach(() => vi.unstubAllGlobals())

describe('offline playlists', () => {
  it('keeps the songs with audio and their covers, and asks again only by ETag', async () => {
    await syncOffline(playlists, runs)

    expect([...cache.entries.keys()]).toEqual([
      'https://mac.example/api/songs/run-1/song2/stream',
      'https://mac.example/api/songs/run-1/song2/cover?v=2026-10-13T12%3A00%3A00Z',
    ])
    expect([...offlineSongs.value]).toEqual(['run-1/song2'])
    expect(offlineBytes.value).toBe(2000)

    fetched = []
    await syncOffline(playlists, runs)
    expect(fetched.every((call) => call.etag === '"1"')).toBe(true)
  })

  it('drops what no offline playlist wants, but not while the library is unknown', async () => {
    await syncOffline(playlists, runs)
    setOffline(1, false)
    setOffline(2, true)

    await syncOffline(playlists, [])
    expect(cache.entries.has('https://mac.example/api/songs/run-1/song2/stream')).toBe(true)

    await syncOffline(playlists, runs)
    expect([...cache.entries.keys()]).toEqual(['https://mac.example/api/songs/run-1/song1/stream'])
    expect([...offlineSongs.value]).toEqual(['run-1/song1'])
  })
})
