import { locale, t } from './i18n'
import type { ScoreDocument, VoiceTrack } from './logic/types'
import type { SingingAligned, SingingMelody, SingingVoiceInfo, SingingVoiceMade, SingTake, SungNote } from './types'
import { noteName } from './singEditor'
import { shownLines } from './timing'

/** Public domain, one syllable per note on steady notes, which is what the server reads best from a recording. */
export const suggestedPhrase = 'Twinkle, twinkle, little star, how I wonder what you are'

export function singFinished(take: Pick<SingTake, 'stage'>): boolean {
  return take.stage === 'done' || take.stage === 'failed' || take.stage === 'cancelled'
}

/** What was loaded, with the live changes laid over it (newer `updatedAt` wins); a deleted take arrives as cancelled. */
export function mergeTakes(loaded: SingTake[], live: SingTake[]): SingTake[] {
  const byId = new Map(loaded.map((take) => [take.id, take]))
  for (const take of live) {
    const known = byId.get(take.id)
    if (!known || take.updatedAt >= known.updatedAt) {
      byId.set(take.id, take)
    }
  }
  return [...byId.values()]
    .filter((take) => take.stage !== 'cancelled')
    .sort((a, b) => b.createdAt.localeCompare(a.createdAt))
}

export function singStage(take: Pick<SingTake, 'stage' | 'progress'>): string {
  if (take.stage === 'singing' && take.progress !== null && take.progress !== undefined) {
    return t('singStage_singingPercent', { percent: Math.round(take.progress * 100) })
  }
  return t(`singStage_${take.stage}`)
}

/** How the words fell on the notes: notes that got a syllable, notes left without one, words left over. */
export function singAlignment(take: SingTake): string | null {
  if (take.sung === null || take.sung === undefined) {
    return null
  }
  const parts = [t('singSung', { n: take.sung })]
  if (take.unsung) {
    parts.push(t('singUnsung', { n: take.unsung }))
  }
  if (take.leftover) {
    parts.push(t('singLeftoverWords', { n: take.leftover }))
  }
  return parts.join(' · ')
}

function number(value: number | null | undefined): string {
  return value === null || value === undefined ? '–' : value.toLocaleString(locale.value, { maximumFractionDigits: 1 })
}

export function singStats(take: SingTake): string {
  return t('singStats', {
    load: number(take.loadSeconds),
    sing: number(take.singSeconds),
    memory: number(take.peakMemoryGb),
  })
}

/** What the server made of a recording, and what to do about it where it heard something off. */
export function heardNote(heard: SingingVoiceMade): string {
  const parts = [t('singHeard', { seconds: number(heard.seconds), text: heard.text, notes: heard.notes })]
  if (heard.leftover > 0) {
    parts.push(t('singHeardLeftover', { n: heard.leftover }))
  }
  if (heard.peak > 0.98) {
    parts.push(t('singHeardLoud'))
  } else if (heard.peak < 0.05) {
    parts.push(t('singHeardQuiet'))
  }
  if (heard.seconds < 4) {
    parts.push(t('singHeardShort'))
  }
  return parts.join(' ')
}

/** The notes the voice sang, as Logic names them (C3 = 60). */
export function voiceRange(voice: SingingVoiceInfo): string | null {
  return voice.low === null || voice.low === undefined || voice.high === null || voice.high === undefined
    ? null
    : `${noteName(voice.low)}–${noteName(voice.high)}`
}

/**
 * The words of lyrics that are sung: section tags (`[Chorus]`) and directions in round brackets left out, as the
 * full-screen player shows them (`shownLines`), paragraphs kept apart by one empty line.
 */
export function sungLyrics(lyrics: string): string {
  const lines: string[] = []
  for (const line of shownLines(lyrics)) {
    if (line.section) {
      continue
    }
    if (line.text || (lines.length > 0 && lines[lines.length - 1] !== '')) {
      lines.push(line.text)
    }
  }
  while (lines.length > 0 && lines[lines.length - 1] === '') {
    lines.pop()
  }
  return lines.join('\n')
}

/** The Logic page's tracks that a voice sings: the melody, its doubling and the backing vocals. */
export function isVocalTrack(voice: Pick<VoiceTrack, 'id' | 'kind'>): boolean {
  return voice.id === 'Vocal' || voice.id.startsWith('Vocal ') || voice.kind === 'Harmony'
}

/** A track of a converted score as notes in seconds from where the song starts (after a count-in). */
export function trackNotes(score: ScoreDocument, voiceId: string): SungNote[] {
  const seconds = (ticks: number) =>
    ((ticks - score.countInTicks) / score.ticksPerQuarterNote) * (60 / Math.max(1, score.tempoBpm))
  return (score.voices.find((voice) => voice.id === voiceId)?.notes ?? [])
    .map((note) => ({
      start: Math.max(0, seconds(note.startTicks)),
      end: seconds(note.startTicks + note.durationTicks),
      pitch: note.noteNumber,
    }))
    .filter((note) => note.end > note.start)
    .sort((a, b) => a.start - b.start || a.pitch - b.pitch)
}

/** A track of the Logic page handed to the singing page: its notes, the melody the words are laid on, the words. */
export interface SingHandOff {
  melody: SingingMelody
  /** The vocal melody: the server lays the words on it, and the track's notes take them where they start with it. */
  guide: SungNote[]
}

export function singHandOff(score: ScoreDocument, voiceId: string, lyrics: string, title: string): SingHandOff {
  const notes = trackNotes(score, voiceId)
  const guide = score.voices.some((voice) => voice.id === 'Vocal') ? trackNotes(score, 'Vocal') : notes
  return { melody: { notes, tempoBpm: score.tempoBpm, lyrics: sungLyrics(lyrics), title }, guide }
}

/**
 * The words the server laid on the guide (in its order) carried over to a track's notes: a note starting with a guide
 * note sings what that one sings, so a backing part sings the melody's syllables even where it only joins in the
 * chorus. A note starting inside a guide note goes on with the word before; one outside the melody stays silent. A
 * part that joins in on a held syllable sings that word anew, since a hold needs a word before it.
 */
export function wordsFromGuide(notes: SungNote[], guide: SungNote[], answer: SingingAligned): SungNote[] {
  if (answer.notes.length !== guide.length) {
    return notes.map(({ lyric: _, ...note }) => note)
  }
  // The word each guide note belongs to, for a part joining on a held syllable.
  const words: (string | null)[] = []
  answer.notes.forEach((sung, index) => {
    words.push(sung.type === 2 ? (sung.word ?? null) : sung.type === 3 && index > 0 ? words[index - 1]! : null)
  })
  const sungBy = (index: number) => {
    const sung = answer.notes[index]!
    return sung.type === 2 ? (sung.word ?? '') : sung.type === 3 ? '-' : ''
  }
  // Timing humanized per note may move a part's note a little off the melody's.
  const near = (note: SungNote, other: SungNote) =>
    Math.abs(note.start - other.start) < Math.min(0.08, (other.end - other.start) / 2)
  let previous: { lyric: string; end: number } | null = null
  return notes.map(({ lyric: _, ...note }) => {
    let index = guide.findIndex((other) => near(note, other))
    let lyric = index >= 0 ? sungBy(index) : ''
    if (index < 0) {
      index = guide.findIndex((other) => other.start < note.start && note.start < other.end)
      lyric = index >= 0 && words[index] ? '-' : ''
    }
    const follows = previous !== null && previous.lyric !== '' && note.start - previous.end < 0.05
    if (lyric === '-' && !follows) {
      lyric = index >= 0 ? (words[index] ?? '') : ''
    }
    previous = { lyric, end: note.end }
    return lyric ? { ...note, lyric } : note
  })
}
