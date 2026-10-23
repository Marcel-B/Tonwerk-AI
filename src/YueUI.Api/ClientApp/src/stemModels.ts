import { t } from './i18n'

/** Stems that only part the voice from the rest; any other stem means the model splits instruments too. */
const voiceParts = new Set(['vocals', 'instrumental', 'no_vocals', 'karaoke'])
const drumParts = new Set(['kick', 'snare', 'toms', 'hh', 'ride', 'crash'])

/**
 * What a model splits a song into, short enough for the model menu, so one can tell a model that only takes the voice
 * out from one that also parts the instruments. Mirrors `VoicesSession.splits` in the app.
 */
export function stemModelSplits(stems: string[] | null | undefined): string {
  const names = (stems ?? []).map((s) => s.toLowerCase())
  if (names.length === 0) {
    return ''
  }
  if (names.every((n) => voiceParts.has(n))) {
    return t(names.includes('karaoke') ? 'stemsSplitsLead' : 'stemsSplitsVocals')
  }
  if (names.every((n) => drumParts.has(n))) {
    return t('stemsSplitsDrums', { count: names.length })
  }
  return t('stemsSplitsInstruments', { count: names.filter((n) => !voiceParts.has(n)).length })
}

/** The model's name with what it splits, for the menu. */
export function stemModelLabel(model: { name: string; stems?: string[] | null }): string {
  const splits = stemModelSplits(model.stems)
  return splits ? `${model.name} · ${splits}` : model.name
}
