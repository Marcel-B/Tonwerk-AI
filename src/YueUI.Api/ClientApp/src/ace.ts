import { ref, watch } from 'vue'
import type { AceHeard, AceSongRequest, AceTask } from './types'

/**
 * The ACE-Step page's form, kept per browser like the create page's. Null and empty strings leave a value to the
 * model, as the server's `AceSongRequest` does; the page shows them as "auto".
 */
export interface AceForm {
  title: string
  caption: string
  lyrics: string
  instrumental: boolean
  /** ACE-Step's language code; empty lets the LM tell. */
  language: string
  duration: number | null
  bpm: number | null
  key: string
  timeSignature: number | null
  /** Typed as text like the create page's seed; empty for a new one. */
  seed: string
  batch: number
  /** GGUF file names; empty for the server's default (the largest LM, a turbo DiT). */
  lmModel: string
  ditModel: string
  steps: number | null
  guidance: number | null
  shift: number | null
  /** Empty for euler, acestep.cpp's default. */
  solver: string
  lmTemperature: number | null
  lmCfgScale: number | null
  lmTopP: number | null
  lmTopK: number | null
  negativePrompt: string
  enrichCaption: boolean
  adapter: string
  adapterScale: number | null
  task: AceTask
  source: string
  coverStrength: number | null
  repaintStart: number | null
  repaintEnd: number | null
  /** The Mac that composes; empty lets the server choose. */
  machine: string
  /** The highs the VAE loses given back; on for new songs, since without it they sound dull. */
  brighten: boolean
}

export function defaultAceForm(): AceForm {
  return {
    title: '',
    caption: '',
    lyrics: '',
    instrumental: false,
    language: '',
    duration: null,
    bpm: null,
    key: '',
    timeSignature: null,
    seed: '',
    batch: 1,
    lmModel: '',
    ditModel: '',
    steps: null,
    guidance: null,
    shift: null,
    solver: '',
    lmTemperature: null,
    lmCfgScale: null,
    lmTopP: null,
    lmTopK: null,
    negativePrompt: '',
    enrichCaption: true,
    adapter: '',
    adapterScale: null,
    task: 'text2music',
    source: '',
    coverStrength: null,
    repaintStart: null,
    repaintEnd: null,
    machine: '',
    brighten: true,
  }
}

const storageKey = 'yue-ui.ace'

/** What was typed last, laid over the defaults so a field added later starts at its default. */
export function loadAceForm(): AceForm {
  const form = defaultAceForm()
  try {
    const stored = JSON.parse(localStorage.getItem(storageKey) ?? 'null') as Partial<AceForm> | null
    if (stored && typeof stored === 'object') {
      for (const key of Object.keys(form) as (keyof AceForm)[]) {
        const value = stored[key]
        const fallback = form[key]
        // A stored value of another type (an older version's) is left out.
        if (value !== undefined && (typeof value === typeof fallback || (value === null && isNullable(key)))) {
          ;(form as unknown as Record<string, unknown>)[key] = value
        }
      }
    }
  } catch {
    // Private mode or a broken entry: the defaults.
  }
  return form
}

export function saveAceForm(form: AceForm): void {
  try {
    localStorage.setItem(storageKey, JSON.stringify(form))
  } catch {
    // Storage full or blocked; the form still works.
  }
}

const nullable = new Set<keyof AceForm>([
  'duration',
  'bpm',
  'timeSignature',
  'steps',
  'guidance',
  'shift',
  'lmTemperature',
  'lmCfgScale',
  'lmTopP',
  'lmTopK',
  'adapterScale',
  'coverStrength',
  'repaintStart',
  'repaintEnd',
])

function isNullable(key: keyof AceForm): boolean {
  return nullable.has(key)
}

/** Whether the LM runs: only a new song is composed; a cover or repaint goes to the DiT directly. */
export function usesLm(form: AceForm): boolean {
  return form.task === 'text2music'
}

/** The form as the API takes it: only what was set, the rest left to the model. */
export function toAceRequest(form: AceForm): AceSongRequest {
  const text = (value: string) => (value.trim() === '' ? null : value.trim())
  const lm = usesLm(form)
  const seed = form.seed.trim() === '' ? null : Number(form.seed.trim())
  return {
    title: text(form.title),
    caption: form.caption.trim(),
    lyrics: form.instrumental ? null : text(form.lyrics),
    instrumental: form.instrumental,
    language: form.instrumental ? null : text(form.language),
    // The source decides a cover's or repaint's length, tempo and key.
    duration: lm ? form.duration : null,
    bpm: lm ? form.bpm : null,
    key: lm ? text(form.key) : null,
    timeSignature: lm ? form.timeSignature : null,
    seed: seed !== null && Number.isInteger(seed) ? seed : null,
    batch: form.batch,
    lmModel: lm ? text(form.lmModel) : null,
    ditModel: text(form.ditModel),
    steps: form.steps,
    guidance: form.guidance,
    shift: form.shift,
    solver: text(form.solver),
    lmTemperature: lm ? form.lmTemperature : null,
    lmCfgScale: lm ? form.lmCfgScale : null,
    lmTopP: lm ? form.lmTopP : null,
    lmTopK: lm ? form.lmTopK : null,
    negativePrompt: lm ? text(form.negativePrompt) : null,
    enrichCaption: lm ? form.enrichCaption : null,
    adapter: text(form.adapter),
    adapterScale: text(form.adapter) ? form.adapterScale : null,
    task: form.task,
    source: lm ? null : text(form.source),
    coverStrength: form.task === 'cover' ? form.coverStrength : null,
    repaintStart: form.task === 'repaint' ? form.repaintStart : null,
    repaintEnd: form.task === 'repaint' ? form.repaintEnd : null,
    machine: text(form.machine),
    brighten: form.brighten,
  }
}

/**
 * A song's request laid over the form, to make it again: what it does not say goes back to the default. The Mac chosen
 * is the page's, not the song's, so the caller hands it over.
 */
export function fromAceRequest(request: AceSongRequest, machine = ''): AceForm {
  const form = defaultAceForm()
  return {
    ...form,
    machine,
    title: request.title ?? '',
    caption: request.caption ?? '',
    lyrics: request.lyrics ?? '',
    instrumental: request.instrumental ?? false,
    language: request.language ?? '',
    duration: request.duration ?? null,
    bpm: request.bpm ?? null,
    key: request.key ?? '',
    timeSignature: request.timeSignature ?? null,
    seed: request.seed === null || request.seed === undefined ? '' : String(request.seed),
    batch: request.batch ?? 1,
    lmModel: request.lmModel ?? '',
    ditModel: request.ditModel ?? '',
    steps: request.steps ?? null,
    guidance: request.guidance ?? null,
    shift: request.shift ?? null,
    solver: request.solver ?? '',
    lmTemperature: request.lmTemperature ?? null,
    lmCfgScale: request.lmCfgScale ?? null,
    lmTopP: request.lmTopP ?? null,
    lmTopK: request.lmTopK ?? null,
    negativePrompt: request.negativePrompt ?? '',
    enrichCaption: request.enrichCaption ?? true,
    adapter: request.adapter ?? '',
    adapterScale: request.adapterScale ?? null,
    task: request.task ?? 'text2music',
    source: request.source ?? '',
    coverStrength: request.coverStrength ?? null,
    repaintStart: request.repaintStart ?? null,
    repaintEnd: request.repaintEnd ?? null,
    // A song from before the switch was made without it, and taking it over should make it again.
    brighten: request.brighten ?? false,
  }
}

/**
 * What ACE-Step heard in a recording laid over the form, as a new song: its description, words, tempo, key, metre,
 * language and length replace the form's where it could tell, and leave the field to the model where it could not;
 * title, models, seed and the rest stay. A recording without words makes no instrumental unless ACE-Step heard one.
 */
export function withHeard(form: AceForm, heard: AceHeard): AceForm {
  return {
    ...form,
    task: 'text2music',
    caption: heard.caption ?? form.caption,
    lyrics: heard.instrumental ? '' : (heard.lyrics ?? ''),
    instrumental: heard.instrumental,
    language: heard.language ?? '',
    bpm: heard.bpm,
    key: aceKeyOf(heard.key),
    timeSignature: heard.timeSignature,
    duration: heard.duration === null ? null : Math.min(600, Math.max(10, heard.duration)),
  }
}

/** YuE2's language building blocks (`styleTags.ts`) as ACE-Step's codes. */
const styleLanguages: Record<string, string> = {
  english: 'en',
  german: 'de',
  mandarin: 'zh',
  cantonese: 'yue',
  japanese: 'ja',
  korean: 'ko',
  spanish: 'es',
  french: 'fr',
}

/** What a library song gives a new ACE-Step song: its words and its sound, nothing of how it was made. */
export interface AceTemplate {
  title: string
  style: string
  lyrics: string
  instrumental: boolean
}

/**
 * A library song as the template of a new ACE-Step song: title, style (as the description, building blocks and all)
 * and lyrics replace the form's, the language follows the style's language block (else the LM tells), and the rest
 * of the form (models, length, seed, the Mac) stays as it was. A cover or repaint becomes a new song, since the
 * template is the song's words, not its audio.
 */
export function withAceTemplate(form: AceForm, template: AceTemplate): AceForm {
  const language =
    template.style
      .split(',')
      .map((tag) => styleLanguages[tag.trim().toLowerCase()])
      .find((code) => code !== undefined) ?? ''
  return {
    ...form,
    title: template.title,
    caption: template.style,
    lyrics: template.instrumental ? '' : template.lyrics,
    instrumental: template.instrumental,
    language,
    task: 'text2music',
  }
}

/** The languages ACE-Step's LM knows (the server's `AceSongRequest.Languages`), German and English first. */
export const aceLanguages = [
  'de',
  'en',
  'ar',
  'az',
  'bg',
  'bn',
  'ca',
  'cs',
  'da',
  'el',
  'es',
  'fa',
  'fi',
  'fr',
  'he',
  'hi',
  'hr',
  'ht',
  'hu',
  'id',
  'is',
  'it',
  'ja',
  'ko',
  'la',
  'lt',
  'ms',
  'ne',
  'nl',
  'no',
  'pa',
  'pl',
  'pt',
  'ro',
  'ru',
  'sa',
  'sk',
  'sr',
  'sv',
  'sw',
  'ta',
  'te',
  'th',
  'tl',
  'tr',
  'uk',
  'ur',
  'vi',
  'yue',
  'zh',
] as const

/** ACE-Step reads keys as "C major", "F# minor". */
export const aceKeys = ['C', 'C#', 'Db', 'D', 'Eb', 'E', 'F', 'F#', 'Gb', 'G', 'Ab', 'A', 'Bb', 'B'].flatMap((root) =>
  ['major', 'minor'].map((mode) => `${root} ${mode}`),
)

/** Sharps the picker lists as flats, as ACE-Step's LM writes either. */
const enharmonic: Record<string, string> = { 'D#': 'Eb', 'G#': 'Ab', 'A#': 'Bb' }

/** A key as the LM wrote it ("G# Minor", "Bb major") as the picker's entry, or empty for one it does not list. */
export function aceKeyOf(text: string | null): string {
  const match = /^\s*([A-Ga-g])\s*([#b♯♭]?)\s+(major|minor)\s*$/i.exec(text ?? '')
  if (!match) {
    return ''
  }
  const accidental = match[2] === '♯' ? '#' : match[2] === '♭' ? 'b' : (match[2] ?? '')
  const root = match[1]!.toUpperCase() + accidental
  const key = `${enharmonic[root] ?? root} ${match[3]!.toLowerCase()}`
  return aceKeys.includes(key) ? key : ''
}

/**
 * The page's form, here rather than in the page: the page loads on first use, and the library's "use everything"
 * fills it before that. Last in the module, since loading needs the constants above.
 */
export const aceForm = ref<AceForm>(loadAceForm())
watch(aceForm, (form) => saveAceForm(form), { deep: true })
