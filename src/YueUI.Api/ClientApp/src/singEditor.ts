import type { SingingAligned, SingingMelody, SungNote } from './types'

/** A note of the singing page's editor, in beats on its grid, with the syllable it sings. */
export interface SingNote {
  id: number
  start: number
  length: number
  pitch: number
  /** A word or syllable, a hold mark (`holds`: the word before goes on over this note, a melisma), or empty for silence. */
  lyric: string
}

/** The marks SoulX-Singer's own MIDI editor and other singing editors use; `yueui_sing.py` reads the same. */
export const holds = new Set(['-', '+', '~', '_'])

export const trimmed = (note: SingNote): string => note.lyric.trim()
export const isHold = (note: SingNote): boolean => holds.has(trimmed(note))
export const isSilent = (note: SingNote): boolean => trimmed(note) === ''
export const noteEnd = (note: SingNote): number => note.start + note.length

let nextId = 1

/**
 * The note editor of the singing page (the app's `SingEditor`): notes on a grid of beats, a syllable on each, kept in
 * order of time. Every change goes through `change`, which keeps an undo step and counts `revision`, so the page knows
 * when to ask the server for the pronunciation again. Plain so the tests need no Vue; the page wraps it in `reactive`.
 */
export class SingEditor {
  notes: SingNote[] = []
  tempo = 120
  selection: number | null = null
  /** Beats a new note snaps to: a sixteenth by default. */
  grid = 0.25
  /** The pronunciation of the word starting on a note, by note id. */
  phonemes: Record<number, string> = {}
  /** Syllables of the last spread text that found no note. */
  leftover = 0
  /** Counts changes of notes and tempo. */
  revision = 0
  /** The length the next drawn note gets: the last one drawn or stretched. */
  drawLength = 1
  private history: SingNote[][] = []

  get canUndo(): boolean {
    return this.history.length > 0
  }

  get selected(): SingNote | undefined {
    return this.notes.find((note) => note.id === this.selection)
  }

  /** Whether any note carries words; otherwise the page's text is spread over them on the server. */
  get hasLyrics(): boolean {
    return this.notes.some((note) => !isSilent(note))
  }

  get lengthInBeats(): number {
    return this.notes.reduce((end, note) => Math.max(end, noteEnd(note)), 0)
  }

  seconds(beats: number): number {
    return (beats * 60) / Math.max(1, this.tempo)
  }

  beats(seconds: number): number {
    return (seconds * Math.max(1, this.tempo)) / 60
  }

  setTempo(tempo: number): void {
    const next = Math.min(400, Math.max(20, Math.round(tempo)))
    if (next !== this.tempo) {
      this.tempo = next
      this.revision++
    }
  }

  change(body: (notes: SingNote[]) => SingNote[] | void): void {
    this.history.push(this.notes.map((note) => ({ ...note })))
    if (this.history.length > 100) {
      this.history.shift()
    }
    const copy = this.notes.map((note) => ({ ...note }))
    this.notes = (body(copy) ?? copy).sort((a, b) => a.start - b.start || a.pitch - b.pitch)
    this.revision++
  }

  undo(): void {
    const last = this.history.pop()
    if (!last) {
      return
    }
    this.notes = last
    if (this.selection !== null && !this.notes.some((note) => note.id === this.selection)) {
      this.selection = null
    }
    this.revision++
  }

  snap(beat: number): number {
    return Math.max(0, Math.floor(beat / this.grid + 1e-9) * this.grid)
  }

  round(beats: number): number {
    return Math.round(beats / this.grid) * this.grid
  }

  /** A note drawn at a point: on the grid, as long as the last one drawn but never into the next note, singing "la". */
  add(beat: number, pitch: number): SingNote {
    const start = this.snap(beat)
    const next = this.notes.find((note) => note.start > start)?.start ?? Infinity
    const length = Math.max(this.grid, Math.min(this.drawLength, next - start))
    const note: SingNote = { id: nextId++, start, length, pitch: clampPitch(pitch), lyric: 'la' }
    this.change((notes) => [...notes, note])
    this.selection = note.id
    return note
  }

  /** A drag ended: by beats and semitones, the start on the grid. */
  move(id: number, beats: number, semitones: number): void {
    const shift = this.round(beats)
    if (shift === 0 && semitones === 0) {
      return
    }
    this.change((notes) => {
      const note = notes.find((n) => n.id === id)
      if (note) {
        note.start = Math.max(0, note.start + shift)
        note.pitch = clampPitch(note.pitch + semitones)
      }
    })
  }

  resize(id: number, beats: number): void {
    const note = this.notes.find((n) => n.id === id)
    if (!note) {
      return
    }
    const length = Math.max(this.grid, this.round(note.length + beats))
    if (length === note.length) {
      return
    }
    this.drawLength = length
    this.change((notes) => {
      const changed = notes.find((n) => n.id === id)
      if (changed) {
        changed.length = length
      }
    })
  }

  delete(id: number): void {
    const next = this.neighbour(id, 1) ?? this.neighbour(id, -1)
    this.change((notes) => notes.filter((note) => note.id !== id))
    this.selection = next
  }

  clear(): void {
    this.change(() => [])
    this.selection = null
    this.phonemes = {}
    this.leftover = 0
  }

  /**
   * Typed into a note: one word stays on it; several go one per note from here on, as singing editors do, so a line
   * can be typed at once. Returns the note after the last one written, to go on typing there.
   */
  type(text: string, id: number): number | null {
    const first = this.notes.findIndex((note) => note.id === id)
    if (first < 0) {
      return null
    }
    const words = text.split(/\s+/).filter(Boolean)
    this.change((notes) => {
      if (words.length === 0) {
        notes[first]!.lyric = ''
        return
      }
      words.forEach((word, offset) => {
        if (first + offset < notes.length) {
          notes[first + offset]!.lyric = word
        }
      })
    })
    const after = first + Math.max(1, words.length)
    return after < this.notes.length ? this.notes[after]!.id : null
  }

  /** Makes a note go on with the word before (or gives it back a syllable of its own). */
  toggleHold(id: number): void {
    this.change((notes) => {
      const note = notes.find((n) => n.id === id)
      if (note) {
        note.lyric = isHold(note) ? 'la' : '-'
      }
    })
  }

  neighbour(id: number | null, step: number): number | null {
    const index = this.notes.findIndex((note) => note.id === id)
    if (index < 0) {
      return this.notes[0]?.id ?? null
    }
    return this.notes[index + step]?.id ?? null
  }

  /**
   * The words of a text laid on the notes by the server (in the order of `notes`): a word's first note gets it, the
   * notes it is held over the hold mark, the rest stay silent.
   */
  spread(answer: SingingAligned): void {
    if (answer.notes.length !== this.notes.length) {
      return
    }
    this.change((notes) => {
      answer.notes.forEach((sung, index) => {
        notes[index]!.lyric = sung.type === 2 ? (sung.word ?? '') : sung.type === 3 ? '-' : ''
      })
    })
    this.leftover = answer.leftover
    this.read(answer)
  }

  /** The pronunciation of each word, by the notes as they were sent. */
  read(answer: SingingAligned, sent: number[] = this.notes.map((note) => note.id)): void {
    if (answer.notes.length !== sent.length) {
      return
    }
    const found: Record<number, string> = {}
    answer.notes.forEach((sung, index) => {
      if (sung.type === 2 && sung.phonemes) {
        found[sent[index]!] = sung.phonemes
      }
    })
    this.phonemes = found
  }

  forgetPhonemes(): void {
    this.phonemes = {}
  }

  /**
   * A melody from a file, a song or a take, on the grid of its tempo (a 96th of a beat, so nothing is moved that a
   * sixteenth grid would not show). Undo starts afresh.
   */
  load(melody: SingingMelody): void {
    this.tempo = melody.tempoBpm > 0 ? Math.min(400, melody.tempoBpm) : 120
    const fine = (beats: number) => Math.round(beats * 96) / 96
    this.change(() =>
      melody.notes.map((note) => {
        const start = Math.max(0, fine(this.beats(note.start)))
        return {
          id: nextId++,
          start,
          length: Math.max(1 / 96, fine(this.beats(note.end)) - start),
          pitch: note.pitch,
          lyric: note.lyric ?? '',
        }
      }),
    )
    this.history = []
    this.selection = null
    this.phonemes = {}
    this.leftover = 0
  }

  /**
   * The notes as the server's `SungNote`s in seconds. With words on the notes only those that sing go (a silent one
   * would hold the word before on the server); without any, all of them, for the page's text to be spread.
   */
  sung(all = false): SungNote[] {
    const chosen = all || !this.hasLyrics ? this.notes : this.notes.filter((note) => !isSilent(note))
    return chosen.map((note) => ({
      start: this.seconds(note.start),
      end: this.seconds(noteEnd(note)),
      pitch: note.pitch,
      ...(isSilent(note) ? {} : { lyric: trimmed(note) }),
    }))
  }

  /**
   * The melody as a Standard MIDI File with a lyric event on every note that sings, as singing editors and Logic read
   * it, and as this page reads it back.
   */
  midi(title: string): Uint8Array {
    const ppq = 480
    const tick = (beats: number) => Math.round(beats * ppq)
    const text = (value: string) => [...new TextEncoder().encode(value)]
    const micros = Math.round(60_000_000 / Math.max(1, this.tempo))
    const conductor = [
      ...meta(0x03, text(title)),
      ...meta(0x51, [(micros >> 16) & 0xff, (micros >> 8) & 0xff, micros & 0xff]),
      ...meta(0x58, [4, 2, 24, 8]),
    ]
    // Offs before ons at the same tick, so touching notes do not swallow each other.
    const events: { tick: number; order: number; bytes: number[] }[] = []
    for (const note of this.notes) {
      const start = tick(note.start)
      if (!isSilent(note)) {
        events.push({ tick: start, order: 1, bytes: metaBody(0x05, text(trimmed(note))) })
      }
      events.push({ tick: start, order: 2, bytes: [0x90, note.pitch, 100] })
      events.push({ tick: Math.max(start + 1, tick(noteEnd(note))), order: 0, bytes: [0x80, note.pitch, 0] })
    }
    events.sort((a, b) => a.tick - b.tick || a.order - b.order)
    const vocal = [...meta(0x03, text('Vocal'))]
    let now = 0
    for (const event of events) {
      vocal.push(...variable(event.tick - now), ...event.bytes)
      now = event.tick
    }
    const file = [...text('MThd'), ...int32(6), 0, 1, 0, 2, ppq >> 8, ppq & 0xff]
    for (const track of [conductor, vocal]) {
      const body = [...track, 0, 0xff, 0x2f, 0]
      file.push(...text('MTrk'), ...int32(body.length), ...body)
    }
    return new Uint8Array(file)
  }
}

function clampPitch(pitch: number): number {
  return Math.min(127, Math.max(0, Math.round(pitch)))
}

function meta(type: number, data: number[]): number[] {
  return [0, ...metaBody(type, data)]
}

function metaBody(type: number, data: number[]): number[] {
  return [0xff, type, ...variable(data.length), ...data]
}

export function variable(value: number): number[] {
  let rest = Math.max(0, value)
  const bytes = [rest & 0x7f]
  rest >>= 7
  while (rest > 0) {
    bytes.unshift((rest & 0x7f) | 0x80)
    rest >>= 7
  }
  return bytes
}

function int32(value: number): number[] {
  return [(value >> 24) & 0xff, (value >> 16) & 0xff, (value >> 8) & 0xff, value & 0xff]
}

/** Note names as Logic writes them, C3 being note 60. */
export function noteName(pitch: number): string {
  const names = ['C', 'C#', 'D', 'D#', 'E', 'F', 'F#', 'G', 'G#', 'A', 'A#', 'B']
  return `${names[((pitch % 12) + 12) % 12]}${Math.floor(pitch / 12) - 2}`
}
