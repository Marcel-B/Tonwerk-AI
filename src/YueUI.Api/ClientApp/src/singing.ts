import { locale, t } from './i18n'
import type { SingingVoiceInfo, SingingVoiceMade, SingTake } from './types'
import { noteName } from './singEditor'

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
