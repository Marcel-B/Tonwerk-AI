import type { LyricsTiming } from './types'

/**
 * The lyrics as NowPlaying shows them, line by line, with the index of the timed line each sung line is: the server
 * times only lines with a letter or digit, outside the section tags, in order (Timing/LyricsAligner.cs `SungLines`).
 */
export interface ShownLine {
  text: string
  section: boolean
  /** Index into the timing's lines, or -1 for a tag or an empty line. */
  timed: number
}

export function shownLines(lyrics: string): ShownLine[] {
  let next = 0
  return lyrics
    .split('\n')
    .map((line) => line.trim())
    .map((text) => {
      const section = /^\[[^\]]+\]$/.test(text)
      const sung = !section && /[\p{L}\p{N}]/u.test(text)
      return { text, section, timed: sung ? next++ : -1 }
    })
}

/** The timed line sung at `seconds`: the last one started, until the next starts; -1 before the first. */
export function lineAt(timing: LyricsTiming | null, seconds: number): number {
  if (!timing) {
    return -1
  }
  let found = -1
  for (let i = 0; i < timing.lines.length; i++) {
    if (timing.lines[i]!.start <= seconds) {
      found = i
    } else {
      break
    }
  }
  // After a line's end with a long pause before the next (an instrumental break), nothing is lit.
  const line = found >= 0 ? timing.lines[found]! : null
  return line && seconds <= line.end + 4 ? found : -1
}
