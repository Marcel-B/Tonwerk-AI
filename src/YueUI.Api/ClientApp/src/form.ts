import type {
  Cot,
  Engines,
  GenerateRequest,
  LyricsLanguage,
  LyricsReasoning,
  Quality,
  SamplingOverrides,
  SongRequest,
} from './types'

/** The web form as the browser keeps it; `seed` stays text so an empty field means "random". */
export interface FormState {
  title: string
  style: string
  lyrics: string
  /** Keywords for a lyrics draft; not sent with the song. */
  lyricsIdea: string
  /** The language of the next lyrics draft; not sent with the song, the style's language tag is. */
  lyricsLanguage: LyricsLanguage
  /** The model for lyrics drafts; empty for the server's configured one, so a changed default reaches the form. */
  lyricsModel: string
  /** How much a thinking model thinks before a draft; empty leaves it to LM Studio's setting for the model. */
  lyricsReasoning: LyricsReasoning | ''
  /** The draft this browser asked for and still waits for; kept so that it lands even after a reload. */
  lyricsDraftId: string
  instrumental: boolean
  quality: Quality
  batch: number
  cot: Cot
  seed: string
  /**
   * Whether the score goes out with the song. Off keeps it in the form unsent, so "use everything" can bring a song's
   * own score along and still make that very song again.
   */
  useAbc: boolean
  /** Whether the advanced parameters go out; off sends the worker's defaults and keeps the values in the form. */
  useAdvanced: boolean
  draftSteps: number
  engines: Engines | ''
  /** Longest song in seconds; the API wants tokens. */
  maxSeconds: number
  abc: string
  fullSteps: number
  /** How the score is sampled. */
  abcSampling: Sampling
  /** How the song tokens are sampled. */
  semanticSampling: Sampling
  /** A LoRA from the server's folder; empty for none. */
  lora: string
  loraStrength: number
  /** The reference voice each finished song is sung with afterwards; empty for none. */
  voiceId: string
  /** Whole octaves, as in the library's voice dialog. */
  voiceShift: number
  voiceStrength: number
  /** Seed-VC's diffusion steps: fast, normal or fine, as in the library's voice dialog. */
  voiceSteps: number
}

/** One of the model's sampling settings, every value filled in. */
export interface Sampling {
  temperature: number
  topP: number
  topK: number
  repetitionPenalty: number
  penaltyWindow: number
}

export type SamplingPhase = 'abcSampling' | 'semanticSampling'

/** YuE2's GenerationConfig (src/yue2/protocol.py): the checkpoint's own values. */
export const defaultSampling: Record<SamplingPhase, Sampling> = {
  abcSampling: { temperature: 0.7, topP: 0.9, topK: 30, repetitionPenalty: 1.005, penaltyWindow: 100 },
  semanticSampling: { temperature: 1.0, topP: 0.95, topK: 100, repetitionPenalty: 1.2, penaltyWindow: 50 },
}

export const tokensPerSecond = 25
/** The model's default of 9000 tokens; longer songs need the worker extension. */
export const maxSongSeconds = 360
export const lengthChoices = [30, 60, 90, 120, 150, 180, 240, 300, maxSongSeconds, 420, 480, 540, 600]
export const defaultFullSteps = 32
export const maxLoraStrength = 3

const storageKey = 'yue-ui.form'

/** The advanced section's fields; the seed sits beside the song count, outside it. */
type AdvancedKey =
  | 'cot'
  | 'draftSteps'
  | 'fullSteps'
  | 'engines'
  | 'maxSeconds'
  | 'abc'
  | 'abcSampling'
  | 'semanticSampling'
  | 'lora'
  | 'loraStrength'

/** The advanced section, set to what the worker would do without them. */
export function defaultAdvanced(): Pick<FormState, AdvancedKey> {
  return {
    cot: 'full',
    draftSteps: 8,
    engines: '',
    maxSeconds: maxSongSeconds,
    abc: '',
    fullSteps: defaultFullSteps,
    abcSampling: { ...defaultSampling.abcSampling },
    semanticSampling: { ...defaultSampling.semanticSampling },
    lora: '',
    loraStrength: 1,
  }
}

export function defaultFormState(): FormState {
  return {
    title: '',
    style: '',
    lyrics: '',
    lyricsIdea: '',
    lyricsLanguage: 'english',
    lyricsModel: '',
    lyricsReasoning: '',
    lyricsDraftId: '',
    instrumental: false,
    quality: 'draft',
    batch: 1,
    seed: '',
    useAbc: true,
    useAdvanced: true,
    voiceId: '',
    voiceShift: 0,
    voiceStrength: 0.7,
    voiceSteps: 50,
    ...defaultAdvanced(),
  }
}

/**
 * A score that goes out with the song. It and the planning chosen for it (a transcription wants "melody") belong
 * together and outlast a reset.
 */
export function usesScore(form: FormState): boolean {
  return form.useAbc && form.abc.trim() !== ''
}

/** The advanced parameters back at their defaults; the score is not a parameter and stays, with its planning. */
export function resetAdvanced(form: FormState): FormState {
  return { ...form, ...defaultAdvanced(), abc: form.abc, cot: usesScore(form) ? form.cot : 'full' }
}

/**
 * Whether an advanced parameter differs from its default. The section is collapsed and kept across visits, so a
 * forgotten value must stay visible; a score (with its planning) is shown on its own.
 */
export function advancedChanged(form: FormState): boolean {
  const defaults = defaultAdvanced()
  return (Object.keys(defaults) as AdvancedKey[]).some((key) => {
    if (key === 'abc' || (key === 'cot' && usesScore(form))) {
      return false
    }
    const value = form[key]
    const initial = defaults[key]
    if (typeof initial === 'string') {
      return String(value).trim() !== initial
    }
    return typeof initial === 'object' ? JSON.stringify(value) !== JSON.stringify(initial) : value !== initial
  })
}

/** Only what differs from the model's values, so unchanged songs still share token batches with others. */
export function samplingOverrides(phase: SamplingPhase, sampling: Sampling): SamplingOverrides | null {
  const overrides: SamplingOverrides = {}
  for (const key of Object.keys(sampling) as (keyof Sampling)[]) {
    if (Number.isFinite(sampling[key]) && sampling[key] !== defaultSampling[phase][key]) {
      overrides[key] = sampling[key]
    }
  }
  return Object.keys(overrides).length > 0 ? overrides : null
}

/** The last form, so a phone that reloads the page (or a second visit) keeps a half-written song. */
export function loadFormState(): FormState {
  try {
    const stored = localStorage.getItem(storageKey)
    if (stored) {
      const defaults = defaultFormState()
      const form = JSON.parse(stored) as Partial<FormState>
      // Nested, so a form kept before a value was added still gets it.
      return {
        ...keepValid(defaults, form),
        abcSampling: keepValid(defaults.abcSampling, form.abcSampling),
        semanticSampling: keepValid(defaults.semanticSampling, form.semanticSampling),
      }
    }
  } catch {
    // Unreadable or blocked storage: start fresh.
  }
  return defaultFormState()
}

/**
 * The stored values that still have the default's type, the defaults for the rest. PrimeVue's InputNumber, used
 * before NumberField, wrote null for an emptied field, and a missing number would reach the API as null.
 */
function keepValid<T extends object>(defaults: T, stored: Partial<T> | undefined): T {
  const result = { ...defaults }
  for (const key of Object.keys(defaults) as (keyof T)[]) {
    const value = stored?.[key]
    if (sameType(defaults[key], value)) {
      result[key] = value as T[keyof T]
    }
  }
  return result
}

function sameType(initial: unknown, value: unknown): boolean {
  return typeof value === typeof initial && value !== null && (typeof value !== 'number' || Number.isFinite(value))
}

export function saveFormState(form: FormState): void {
  try {
    localStorage.setItem(storageKey, JSON.stringify(form))
  } catch {
    // Remembering the form is a convenience only.
  }
}

export function toGenerateRequest(sent: FormState): GenerateRequest {
  // Switched off, the advanced parameters go out as their defaults; the score's planning stays with a score.
  const form = sent.useAdvanced ? sent : resetAdvanced(sent)
  const seed = form.seed.trim() === '' ? null : Number.parseInt(form.seed, 10)
  return {
    title: form.title.trim(),
    style: form.style.trim(),
    lyrics: form.lyrics.trim(),
    instrumental: form.instrumental,
    quality: form.quality,
    batch: form.batch,
    cot: form.cot,
    seed: seed !== null && Number.isFinite(seed) ? seed : null,
    engines: form.engines === '' ? null : form.engines,
    draftSteps: form.quality === 'draft' ? form.draftSteps : null,
    maxTokens: form.maxSeconds < maxSongSeconds ? form.maxSeconds * tokensPerSecond : null,
    abc: usesScore(form) ? form.abc.trim() : null,
    fullSteps: form.quality === 'full' && form.fullSteps !== defaultFullSteps ? form.fullSteps : null,
    abcSampling: samplingOverrides('abcSampling', form.abcSampling),
    semanticSampling: samplingOverrides('semanticSampling', form.semanticSampling),
    // The original's reverb as the library's dialog starts it; the form stays short.
    voice:
      form.voiceId === ''
        ? null
        : {
            voiceId: form.voiceId,
            semiToneShift: form.voiceShift,
            strength: form.voiceStrength,
            diffusionSteps: form.voiceSteps,
            keepReverb: true,
          },
    lora: form.lora === '' ? null : form.lora,
    loraStrength: form.lora === '' || form.loraStrength === 1 ? null : form.loraStrength,
  }
}

/**
 * The form set to what a song was made with, to make it again with changes: its seed included, one song (the seed is
 * that song's). What the song's request does not say goes back to its default; the lyrics draft's settings are the
 * form's own and stay. The score is the one the song was asked with; without one, the score YuE2 wrote for it
 * (`ownScore`) comes along switched off, so "generate" still makes the same song and switching it on (to change the
 * key, say) builds on it.
 */
export function fromSongRequest(form: FormState, request: SongRequest, ownScore: string | null = null): FormState {
  const defaults = defaultFormState()
  const seconds = request.maxTokens === null ? maxSongSeconds : request.maxTokens / tokensPerSecond
  return {
    ...form,
    title: request.title,
    style: request.style,
    lyrics: request.lyrics,
    instrumental: request.instrumental ?? defaults.instrumental,
    quality: request.quality ?? defaults.quality,
    batch: 1,
    cot: request.cot ?? defaults.cot,
    seed: request.seed === null ? '' : String(request.seed),
    draftSteps: request.draftSteps ?? defaults.draftSteps,
    engines: request.engines ?? '',
    // The form offers fixed lengths; the next one up still fits the song.
    maxSeconds: lengthChoices.find((choice) => choice >= seconds) ?? lengthChoices[lengthChoices.length - 1]!,
    abc: request.abc ?? ownScore ?? '',
    useAbc: request.abc !== null,
    useAdvanced: true,
    fullSteps: request.fullSteps ?? defaults.fullSteps,
    abcSampling: { ...defaultSampling.abcSampling, ...definedOnly(request.abcSampling) },
    semanticSampling: { ...defaultSampling.semanticSampling, ...definedOnly(request.semanticSampling) },
    lora: request.lora ?? '',
    loraStrength: request.loraStrength ?? defaults.loraStrength,
  }
}

/**
 * A song's score (its score.abc, or one from a MIDI file) as the form's score and nothing else: the rest of the form
 * stays. Planning follows the score: chords are kept when it has them.
 */
export function withScore(form: FormState, abc: string): FormState {
  return { ...form, abc, cot: planningFor(abc), useAbc: true }
}

/** A null in the JSON must not replace a default. */
function definedOnly(overrides: SamplingOverrides | null): SamplingOverrides {
  return Object.fromEntries(Object.entries(overrides ?? {}).filter(([, value]) => typeof value === 'number'))
}

/**
 * The planning a score needs: "full" keeps its chords when it has them, "melody" plans them anew. Only body lines
 * count, the voice declarations quote their names as well.
 */
export function planningFor(abc: string): 'full' | 'melody' {
  return /^(?![A-Za-z]:|%).*"[^"]+"/m.test(abc) ? 'full' : 'melody'
}

/** YuE2's examples/score.abc: the melody and chords for its "City Lights" lyrics (quoted in i18n's lyricsMore). */
export const exampleScore = `X:1
T:
M:4/4
L:1/16
Q:1/4=88
V: Vocal clef=treble name="Vocal Melody" snm="Vocal"
V: Ins clef=treble name="Ins Melody" snm="Inst."
K:C
% verse
V: Vocal
"C"E2G2A2G2E2D2C4|"G"D2E2G2E2D2C2D4|"Am"E2G2A2c2B2A2G4|"F"F2E2D2E2G2E2C4|
V: Ins
Z4|
% chorus
V: Vocal
"C"G2A2c2B2A2G2E4|"F"F2A2G2E2D2E2G4|"G"A2c2B2A2G2E2D4|"C"E2G2A2G2E2D2C4|
V: Ins
Z4|`

/**
 * What a prompt template can hold, in groups that are saved or left out as a whole. Never the title, lyrics or score:
 * a template is how a song sounds, not what it says.
 */
export type TemplatePart = 'style' | 'seed' | 'parameters' | 'voice'

export const templatePartKeys: Record<TemplatePart, readonly (keyof FormState)[]> = {
  // Instrumental decides whether the style is sung, so it belongs to it.
  style: ['style', 'instrumental'],
  seed: ['seed'],
  parameters: [
    'quality',
    'batch',
    'cot',
    'draftSteps',
    'fullSteps',
    'engines',
    'maxSeconds',
    'abcSampling',
    'semanticSampling',
  ],
  voice: ['voiceId', 'voiceShift', 'voiceStrength', 'voiceSteps'],
}

export const templateParts = Object.keys(templatePartKeys) as TemplatePart[]

/** The form's values of the chosen groups, as the server keeps them. */
export type TemplateSettings = Partial<Pick<FormState, (typeof templatePartKeys)[TemplatePart][number]>>

export function toTemplateSettings(form: FormState, parts: readonly TemplatePart[]): TemplateSettings {
  const settings: Record<string, unknown> = {}
  for (const part of parts) {
    for (const key of templatePartKeys[part]) {
      const value = form[key]
      settings[key] = typeof value === 'object' ? { ...value } : value
    }
  }
  return settings as TemplateSettings
}

/** The groups a template holds; a group counts when any of its values is there. */
export function partsOf(settings: Record<string, unknown>): TemplatePart[] {
  return templateParts.filter((part) => templatePartKeys[part].some((key) => key in settings))
}

/**
 * The form with a template's values laid over it: what the template does not hold stays as it is, so a template with
 * only a style keeps the seed and parameters. Values of the wrong type (a template saved by an older version, or
 * edited elsewhere) are skipped like a stored form's. With a score in the form its planning stays, since the score
 * needs it.
 */
export function applyTemplate(form: FormState, settings: Record<string, unknown>): FormState {
  const defaults = defaultFormState()
  const result: Record<string, unknown> = { ...form }
  for (const key of templateParts.flatMap((part) => templatePartKeys[part])) {
    const value = settings[key]
    if (!sameType(defaults[key], value) || (key === 'cot' && usesScore(form))) {
      continue
    }
    // Nested, so a template saved before a sampling value was added still gets it.
    result[key] =
      key === 'abcSampling' || key === 'semanticSampling' ? keepValid(defaults[key], value as Partial<Sampling>) : value
  }
  // A template's parameters are meant to be heard.
  if (partsOf(settings).includes('parameters')) {
    result.useAdvanced = true
  }
  return result as unknown as FormState
}
