import type { RunInfo } from './types'

/** What deleting the selected songs takes: whole runs where every song is chosen, single songs otherwise. */
export interface DeletionPlan {
  runIds: string[]
  songIds: string[]
  /** Songs in all, those of whole runs included. */
  count: number
  bytes: number
}

/**
 * A run whose songs are all chosen goes in one request, which also takes the run's own files with it; ids of songs no
 * longer listed (deleted elsewhere meanwhile) are left out.
 */
export function deletionPlan(runs: RunInfo[], selected: ReadonlySet<string>): DeletionPlan {
  const plan: DeletionPlan = { runIds: [], songIds: [], count: 0, bytes: 0 }
  for (const run of runs) {
    const chosen = run.songs.filter((song) => selected.has(song.id))
    if (chosen.length === 0) {
      continue
    }
    if (chosen.length === run.songs.length) {
      plan.runIds.push(run.id)
    } else {
      plan.songIds.push(...chosen.map((song) => song.id))
    }
    plan.count += chosen.length
    plan.bytes += chosen.reduce((sum, song) => sum + song.bytes, 0)
  }
  return plan
}
