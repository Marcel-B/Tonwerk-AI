import type { LyricsTiming } from './types'

/**
 * The lyrics as NowPlaying shows them, line by line, with the index of the timed line each sung line is: the server
 * times only lines with a letter or digit, outside the section tags, in order (Timing/LyricsAligner.cs `SungLines`).
 * The shown lines are cut finer than that (a tag on its own line, directions apart, long paragraphs broken), so
 * several may share one timed line. Whisper's timing only knows the whole line, so its pieces divide its time by their
 * share of its words (`from`, `to`): a paragraph then lights line by line instead of all at once. The app's
 * `Lyrics.swift` does the same, keep both alike.
 */
export interface ShownLine {
  text: string
  section: boolean
  /** A performance direction in round brackets, "(Screamy vocals, 808 bass)": the model reads it, nobody sings it. */
  direction: boolean
  /** Index into the timing's lines, or -1 for a tag, a direction or an empty line. */
  timed: number
  /** Where in its timed line's time this piece starts and ends, as shares of it (0 and 1 for a whole line). */
  from: number
  to: number
}

/** Longer than this, a line is a paragraph whose line breaks were lost (as a chatbot's lyrics often come). */
const PARAGRAPH = 60

type Piece = { kind: 'tag' | 'direction' | 'text'; text: string }

/** A line cut into `[Tags]`, `(directions)` and the text between; a bracket that never closes is text. */
function pieces(line: string): Piece[] {
  const found: Piece[] = []
  let text = ''
  let i = 0
  while (i < line.length) {
    const open = line[i]
    const close = open === '[' ? ']' : open === '(' ? ')' : null
    const end = close ? line.indexOf(close, i + 1) : -1
    if (close && end > i + 1) {
      found.push({ kind: 'text', text })
      found.push({ kind: open === '[' ? 'tag' : 'direction', text: line.slice(i, end + 1) })
      text = ''
      i = end + 1
    } else {
      text += line[i]
      i++
    }
  }
  found.push({ kind: 'text', text })
  return found
    .map((piece) => ({ ...piece, text: piece.text.replace(/\s+/g, ' ').trim() }))
    .filter((piece) => piece.text)
}

/**
 * A paragraph broken where a line plausibly ended: after a sentence's end, and in English also before a capital that
 * follows a lower-case word ("carbon glass The ghosts"), but never before "I". German capitalises every noun, so there
 * only the sentence ends count. A guess: the stored lyrics stay as they are.
 */
function broken(text: string, english: boolean): string[] {
  if (text.length <= PARAGRAPH) {
    return [text]
  }
  const lines: string[][] = []
  for (const word of text.split(' ')) {
    const current = lines[lines.length - 1]
    const previous = current?.[current.length - 1]
    const ends =
      previous !== undefined &&
      (/[.!?…]["'”’]?$/.test(previous) ||
        (english && /\p{Ll}$/u.test(previous) && /^\p{Lu}/u.test(word) && !/^I(['’]|$)/.test(word)))
    if (!current || ends) {
      lines.push([word])
    } else {
      current.push(word)
    }
  }
  return lines.map((words) => words.join(' '))
}

/** German lyrics (umlauts or a few common words) keep their capitals; see `broken`. */
function looksGerman(lyrics: string): boolean {
  return /[äöüÄÖÜß]/.test(lyrics) || /\b(und|ich|nicht|ist|wir|mein|dein|auf|kein)\b/i.test(lyrics)
}

export function shownLines(lyrics: string, directions = false): ShownLine[] {
  const english = !looksGerman(lyrics)
  const shown: ShownLine[] = []
  const add = (part: Omit<ShownLine, 'from' | 'to'>) => {
    const line: ShownLine = { ...part, from: 0, to: 1 }
    const last = shown[shown.length - 1]
    // A line left empty by hidden directions must not leave a gap of two, nor one at the start.
    if (!line.text && !line.section && (!last || (!last.text && !last.section))) {
      return
    }
    shown.push(line)
  }
  let next = 0
  for (const raw of lyrics.split('\n')) {
    const line = raw.trim()
    const section = /^\[[^\]]+\]$/.test(line)
    const timed = !section && /[\p{L}\p{N}]/u.test(line) ? next++ : -1
    if (!line) {
      add({ text: '', section: false, direction: false, timed: -1 })
      continue
    }
    const parts = pieces(line)
    const hadDirection = parts.some((piece) => piece.kind === 'direction')
    const firstPiece = shown.length
    for (const piece of parts) {
      if (piece.kind === 'tag') {
        add({ text: piece.text, section: true, direction: false, timed: -1 })
      } else if (piece.kind === 'direction') {
        if (directions) {
          add({ text: piece.text, section: false, direction: true, timed: -1 })
        }
      } else if (!hadDirection || /[\p{L}\p{N}]/u.test(piece.text)) {
        // Commas and dots left over between removed directions are not worth a line.
        for (const text of broken(piece.text, english)) {
          add({ text, section: false, direction: false, timed })
        }
      }
    }
    shareTime(shown.slice(firstPiece).filter((piece) => piece.timed >= 0))
  }
  while (shown.length && !shown[shown.length - 1]!.text) {
    shown.pop()
  }
  return shown
}

/** Gives the sung pieces of one line their share of its time, by words. */
function shareTime(sung: ShownLine[]): void {
  const words = sung.map((piece) => piece.text.split(' ').length)
  const total = words.reduce((sum, count) => sum + count, 0)
  let before = 0
  sung.forEach((piece, i) => {
    piece.from = before / total
    before += words[i]!
    piece.to = before / total
  })
}

/**
 * The shown line sung at `seconds`, an index into `lines`, or -1: the timed line from `lineAt`, then the piece of it
 * whose share of its time has begun.
 */
export function shownAt(lines: ShownLine[], timing: LyricsTiming | null, seconds: number): number {
  const timed = lineAt(timing, seconds)
  if (timed < 0) {
    return -1
  }
  const line = timing!.lines[timed]!
  const share = line.end > line.start ? (seconds - line.start) / (line.end - line.start) : 0
  let found = -1
  lines.forEach((piece, i) => {
    if (piece.timed === timed && (found < 0 || piece.from <= share)) {
      found = i
    }
  })
  return found
}

/** Where a shown line starts, for a tap on it: its timed line's start plus its share. */
export function shownStart(piece: ShownLine, timing: LyricsTiming | null): number | null {
  const line = piece.timed >= 0 ? timing?.lines[piece.timed] : undefined
  return line ? line.start + piece.from * (line.end - line.start) : null
}

/** Whether the lyrics hold directions in round brackets, so the player offers to show them. */
export function hasDirections(lyrics: string): boolean {
  return lyrics.split('\n').some((line) => pieces(line).some((piece) => piece.kind === 'direction'))
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
