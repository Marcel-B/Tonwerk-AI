import { ref } from 'vue'
import { coverUrl, streamUrl } from './api'
import type { PlaylistInfo, RunInfo } from './types'

/**
 * Playlists kept on this device for listening without the Mac: their songs' AAC copies (the player's own `stream`, so
 * nothing is encoded for it) and covers lie in the Cache Storage, where the service worker (`public/sw.js`) answers
 * the player from them, with or without a connection. Per browser, since each one has its own storage.
 */
export const offlineCache = 'tonwerk-offline'

const storageKey = 'yue-ui.offline'

function storedIds(): number[] {
  try {
    const stored = JSON.parse(localStorage.getItem(storageKey) ?? '[]') as unknown
    return Array.isArray(stored) ? stored.filter((id): id is number => Number.isInteger(id)) : []
  } catch {
    return []
  }
}

/** The playlists kept offline, by id. */
export const offlinePlaylists = ref<number[]>(storedIds())

/** The songs whose copy is on the device. */
export const offlineSongs = ref<Set<string>>(new Set())

/** A sync in the works: songs done of all wanted. */
export const offlineProgress = ref<{ done: number; total: number } | null>(null)

/** What the kept songs take on the device. */
export const offlineBytes = ref(0)

export function isOffline(playlistId: number | undefined): boolean {
  return playlistId !== undefined && offlinePlaylists.value.includes(playlistId)
}

export function setOffline(playlistId: number, keep: boolean): void {
  offlinePlaylists.value = keep
    ? [...new Set([...offlinePlaylists.value, playlistId])]
    : offlinePlaylists.value.filter((id) => id !== playlistId)
  try {
    localStorage.setItem(storageKey, JSON.stringify(offlinePlaylists.value))
  } catch {
    // private mode: kept for this visit only
  }
  if (keep) {
    // Asked once: without it, Safari may clear the storage of a site not used for a while.
    void navigator.storage?.persist?.().catch(() => false)
  }
}

const songPattern = /\/api\/songs\/([^/]+\/[^/]+)\/stream$/

function absolute(url: string): string {
  return new URL(url, location.href).href
}

/** The songs to keep: those of the offline playlists that have audio, with their cover's address where they have one. */
export function wantedSongs(
  playlists: PlaylistInfo[],
  keep: number[],
  runs: RunInfo[],
): Map<string, { cover: string | null }> {
  const songs = new Map(runs.flatMap((run) => run.songs.map((song) => [song.id, song] as const)))
  const wanted = new Map<string, { cover: string | null }>()
  for (const playlist of playlists.filter((p) => keep.includes(p.id))) {
    for (const id of playlist.songIds) {
      const song = songs.get(id)
      if (song?.hasAudio) {
        wanted.set(id, { cover: song.coverUpdatedAt ? coverUrl(id, song.coverUpdatedAt) : null })
      }
    }
  }
  return wanted
}

let syncing: Promise<void> | null = null
let again = false

/**
 * Brings the device's copies in line with the offline playlists: fetches what is missing, asks again (by ETag) for
 * what is there, since a render replaces a song's audio, and drops what no playlist wants any more. Without a
 * connection it only counts what is there. Runs one at a time; a call during a sync runs once more after it.
 */
export function syncOffline(playlists: PlaylistInfo[], runs: RunInfo[]): Promise<void> {
  if (syncing) {
    again = true
    return syncing
  }
  syncing = (async () => {
    try {
      do {
        again = false
        await syncOnce(playlists, runs)
      } while (again)
    } finally {
      syncing = null
    }
  })()
  return syncing
}

async function syncOnce(playlists: PlaylistInfo[], runs: RunInfo[]): Promise<void> {
  if (typeof caches === 'undefined') {
    return
  }
  const cache = await caches.open(offlineCache)
  const wanted = wantedSongs(playlists, offlinePlaylists.value, runs)
  const keys = await cache.keys()
  const kept = new Set<string>()
  // Nothing is dropped while the library is unknown (offline, it may come from the service worker's last copy).
  const known = runs.length > 0
  const coverUrls = new Set([...wanted.values()].flatMap((w) => (w.cover ? [absolute(w.cover)] : [])))
  for (const key of keys) {
    const song = songPattern.exec(new URL(key.url).pathname)?.[1]
    const wantedHere = song ? wanted.has(song) : coverUrls.has(key.url)
    if (!wantedHere && known) {
      await cache.delete(key)
    } else if (song) {
      kept.add(song)
    }
  }
  offlineSongs.value = new Set(kept)

  const online = typeof navigator === 'undefined' || navigator.onLine !== false
  if (online && wanted.size > 0) {
    offlineProgress.value = { done: 0, total: wanted.size }
    for (const [id, { cover }] of wanted) {
      try {
        await keep(cache, streamUrl(id))
        kept.add(id)
        offlineSongs.value = new Set(kept)
        if (cover) {
          await keep(cache, cover)
        }
      } catch {
        // Unreachable or refused: the copy there stays, the next sync tries again.
      }
      offlineProgress.value = { done: offlineProgress.value.done + 1, total: wanted.size }
    }
    offlineProgress.value = null
  }
  offlineBytes.value = await sizeOf(cache)
}

/** Fetches the file unless the copy there is current (304 to its ETag). */
async function keep(cache: Cache, url: string): Promise<void> {
  const key = absolute(url)
  const there = await cache.match(key)
  const etag = there?.headers.get('ETag')
  // Past the service worker, which would answer from the very copy being checked.
  const response = await fetch(key, { cache: 'no-store', headers: etag ? { 'If-None-Match': etag } : {} })
  if (response.status === 304) {
    return
  }
  if (!response.ok) {
    throw new Error(String(response.status))
  }
  await cache.put(key, response)
}

async function sizeOf(cache: Cache): Promise<number> {
  let total = 0
  for (const key of await cache.keys()) {
    const response = await cache.match(key)
    const length = Number(response?.headers.get('Content-Length'))
    total += Number.isFinite(length) && length > 0 ? length : ((await response?.blob())?.size ?? 0)
  }
  return total
}
