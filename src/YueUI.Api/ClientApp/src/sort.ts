import { ref, watch } from 'vue'
import type { RunInfo, SongInfo } from './types'

/**
 * How the library is ordered. The server lists runs newest first (their folder names start with the date), so
 * `newest` keeps that order and the others sort a copy.
 */
export type LibrarySort = 'newest' | 'oldest' | 'rating' | 'longest' | 'shortest' | 'fastest' | 'slowest' | 'unclear'

export const sorts: readonly LibrarySort[] = [
  'newest',
  'oldest',
  'rating',
  'longest',
  'shortest',
  'fastest',
  'slowest',
  'unclear',
]

/** Orders that put the smallest value first. */
const ascending: readonly LibrarySort[] = ['shortest', 'slowest', 'unclear']
const storageKey = 'yue-ui.librarySort'

function initialSort(): LibrarySort {
  try {
    const stored = localStorage.getItem(storageKey) as LibrarySort | null
    if (stored && sorts.includes(stored)) {
      return stored
    }
  } catch {
    // Storage may be blocked; the newest come first then.
  }
  return 'newest'
}

/** Remembered per browser: the order is a habit of whoever looks, not something the library needs to know. */
export const librarySort = ref<LibrarySort>(initialSort())

watch(librarySort, (value) => {
  try {
    localStorage.setItem(storageKey, value)
  } catch {
    // Remembering the order is a convenience only.
  }
})

/** A run as the library shows it: `songs` are the ones to list, in order; `run.songs` stays the whole run. */
export interface SortedRun {
  id: string
  run: RunInfo
  songs: SongInfo[]
}

/** A song's value for the order; null sorts last whichever way, a song without a length has nothing to compare. */
function key(song: SongInfo, sort: LibrarySort, ratings: Record<string, number>): number | null {
  switch (sort) {
    case 'rating':
      // Unrated counts as no stars, so rated songs come before them.
      return ratings[song.id] ?? 0
    case 'longest':
    case 'shortest':
      return song.seconds
    case 'fastest':
    case 'slowest':
      return song.traits?.tempo ?? null
    case 'unclear':
      // The least understood lyrics first; a song not timed (or without lyrics) has nothing to compare.
      return song.traits?.clarity ?? null
    default:
      return null
  }
}

function compare(a: number | null, b: number | null, descending: boolean): number {
  if (a === null || b === null) {
    return a === b ? 0 : a === null ? 1 : -1
  }
  return descending ? b - a : a - b
}

/**
 * Sorts the runs, and the songs inside each run, by their best song: a run with one five-star song comes before a
 * run of three-star songs, and the five-star song is its first. Runs stay whole, since a run shares its title, style
 * and lyrics. Equal runs keep the server's order (newest first); `Array.prototype.sort` is stable.
 *
 * `songsOf` gives the songs to show of a run (the rating filter may hide some); a hidden song must not decide where
 * its run goes.
 */
export function sortRuns(
  runs: RunInfo[],
  sort: LibrarySort,
  ratings: Record<string, number>,
  songsOf: (run: RunInfo) => SongInfo[],
): SortedRun[] {
  const entries = runs.map((run) => ({ id: run.id, run, songs: songsOf(run) }))
  if (sort === 'newest') {
    return entries
  }
  if (sort === 'oldest') {
    return entries.reverse()
  }
  const descending = !ascending.includes(sort)
  for (const entry of entries) {
    entry.songs = [...entry.songs].sort((a, b) => compare(key(a, sort, ratings), key(b, sort, ratings), descending))
  }
  const best = (songs: SongInfo[]) => (songs.length > 0 ? key(songs[0]!, sort, ratings) : null)
  return entries.sort((a, b) => compare(best(a.songs), best(b.songs), descending))
}
