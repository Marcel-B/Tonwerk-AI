import { ref } from 'vue'
import { keyParts } from './abcTranspose'
import { t } from './i18n'
import type { SongInfo } from './types'

/**
 * What was heard in songs (tempo, key, how clearly the lyrics are sung), for the library's labels, its filter and the
 * style blocks. The server hears them (Analysis/SongAnalyzer.cs); this only reads them.
 */

/** The library's filter by what was heard; `all` lets every song through. */
export type TraitFilter = 'all' | 'slow' | 'medium' | 'fast' | 'major' | 'minor' | 'unclear'

export const traitFilters: readonly TraitFilter[] = ['all', 'slow', 'medium', 'fast', 'major', 'minor', 'unclear']

/** Below this tempo a song counts as slow, from the next as fast; between them is medium. */
export const slowBelow = 90
export const fastFrom = 120

/** Less than half of the lyrics understood: worth hearing again, or making again. */
export const unclearBelow = 0.5

/** Whether the song passes the filter; a song not heard yet passes only `all`. */
export function matchesTrait(song: SongInfo, filter: TraitFilter): boolean {
  const traits = song.traits
  switch (filter) {
    case 'all':
      return true
    case 'slow':
      return traits?.tempo != null && traits.tempo < slowBelow
    case 'medium':
      return traits?.tempo != null && traits.tempo >= slowBelow && traits.tempo < fastFrom
    case 'fast':
      return traits?.tempo != null && traits.tempo >= fastFrom
    case 'major':
      return traits?.key != null && !keyParts(traits.key).minor
    case 'minor':
      return traits?.key != null && keyParts(traits.key).minor
    case 'unclear':
      return traits?.clarity != null && traits.clarity < unclearBelow
  }
}

/** "F#m" as "F♯-Moll" or "F♯ minor". */
export function keyLabel(key: string): string {
  const { root, minor } = keyParts(key)
  return t(minor ? 'keyMinor' : 'keyMajor', { root })
}

/** The tempo as a style block would name it: "92 BPM". */
export function tempoTag(tempo: number): string {
  return `${Math.round(tempo)} BPM`
}

/**
 * The tempo heard in the song last taken over into the form ("use everything"), offered among the tempo blocks: taken
 * into the style it would change the song, which "use everything" is meant to make again as it was.
 */
export const heardTempo = ref<number | null>(null)
