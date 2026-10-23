import { computed, ref, shallowRef, type Ref } from 'vue'

/**
 * What the analyzers and the moving background listen to. Every place that plays audio in the browser (the player,
 * the Logic page's preview) registers its analysers here; a place with several audio contexts (the preview's synth and
 * its recording) hands over one analyser per context, since a node cannot be connected across contexts.
 */
export interface SpectrumSource {
  analysers(): AnalyserNode[]
  playing: Ref<boolean>
}

const sources = shallowRef<SpectrumSource[]>([])

/** @returns What unregisters the source again. */
export function registerSource(source: SpectrumSource): () => void {
  sources.value = [...sources.value, source]
  return () => {
    sources.value = sources.value.filter((entry) => entry !== source)
  }
}

export const allSources = computed(() => sources.value)

/** Something audible in the browser plays right now; the background only moves then. */
export const anythingPlaying = computed(() => sources.value.some((source) => source.playing.value))

/**
 * An analyser as every consumer here wants it: 8192 points, so even the lowest of 64 log-spaced bands gets bins of its
 * own (2048 left the bass bars standing in identical steps), and a little smoothing against flicker.
 */
export function createSpectrumAnalyser(context: BaseAudioContext): AnalyserNode {
  const analyser = context.createAnalyser()
  analyser.fftSize = 8192
  analyser.smoothingTimeConstant = 0.75
  analyser.minDecibels = -90
  analyser.maxDecibels = -20
  return analyser
}

const LOWEST_HZ = 30
const HIGHEST_HZ = 16000
let bins = new Uint8Array(0)

/**
 * Reads the analysers into `bands` log-spaced bands from 30 Hz to 16 kHz, each 0 to 1, the loudest analyser winning
 * per band (a mix of two contexts would need a common clock, which they do not share). Log-spaced, since that is how
 * pitch is heard: linear bins would give the top octave half the bars.
 * @returns Whether any band carries signal.
 */
export function readBands(analysers: AnalyserNode[], bands: Float32Array): boolean {
  bands.fill(0)
  let any = false
  for (const analyser of analysers) {
    if (bins.length < analyser.frequencyBinCount) {
      bins = new Uint8Array(analyser.frequencyBinCount)
    }
    analyser.getByteFrequencyData(bins)
    const hzPerBin = analyser.context.sampleRate / analyser.fftSize
    const top = Math.min(HIGHEST_HZ, analyser.context.sampleRate / 2)
    const ratio = Math.log(top / LOWEST_HZ)
    for (let band = 0; band < bands.length; band++) {
      const from = LOWEST_HZ * Math.exp((ratio * band) / bands.length)
      const to = LOWEST_HZ * Math.exp((ratio * (band + 1)) / bands.length)
      // The lowest bands are narrower than one bin; they take the bin they fall into.
      const first = Math.floor(from / hzPerBin)
      const last = Math.max(first, Math.ceil(to / hzPerBin) - 1)
      let max = 0
      for (let bin = first; bin <= last && bin < analyser.frequencyBinCount; bin++) {
        max = Math.max(max, bins[bin]!)
      }
      const value = max / 255
      if (value > bands[band]!) {
        bands[band] = value
      }
      any ||= value > 0
    }
  }
  return any
}

/**
 * Plain rgb() for a CSS variable: Aura's tokens are light-dark()/color-mix() expressions a canvas cannot parse, so a
 * probe element lets the browser resolve them for the current colour scheme (as pianoRoll.ts does).
 */
export function resolveColour(near: Element, name: string, fallback: string): string {
  const probe = document.createElement('span')
  probe.style.display = 'none'
  near.append(probe)
  probe.style.color = fallback
  probe.style.color = `var(${name}, ${fallback})`
  const colour = getComputedStyle(probe).color || fallback
  probe.remove()
  return colour
}

// ---- Settings, per browser ------------------------------------------------------------------------------

const storageKey = 'yue-ui.visuals'

interface VisualSettings {
  /** The small analyzer in the player; off routes nothing through Web Audio after the next reload. */
  player: boolean
  /** The large analyzer above the Logic page's piano roll. */
  logic: boolean
  /** The page's background moves with the music. Off by default: it is decoration and costs battery. */
  background: boolean
  /** The full-screen player's large analyzer under the cover. */
  nowPlayingAnalyzer: boolean
  /** The full-screen player's glows in the cover's colour, and the cover beating with the bass. */
  nowPlayingEffects: boolean
  /** The full-screen player shows the lyrics instead of the large cover. */
  nowPlayingLyrics: boolean
  /** The lyrics' performance directions in round brackets; off shows only what is sung, with the section tags. */
  nowPlayingDirections: boolean
}

function load(): VisualSettings {
  const defaults: VisualSettings = {
    player: true,
    logic: true,
    background: false,
    nowPlayingAnalyzer: true,
    nowPlayingEffects: true,
    nowPlayingLyrics: false,
    nowPlayingDirections: false,
  }
  try {
    const stored = JSON.parse(localStorage.getItem(storageKey) ?? '{}') as Partial<VisualSettings>
    const settings = { ...defaults }
    for (const key of Object.keys(defaults) as (keyof VisualSettings)[]) {
      if (typeof stored[key] === 'boolean') {
        settings[key] = stored[key]
      }
    }
    return settings
  } catch {
    return defaults
  }
}

export const visuals = ref<VisualSettings>(load())

export function setVisual(key: keyof VisualSettings, value: boolean): void {
  visuals.value = { ...visuals.value, [key]: value }
  try {
    localStorage.setItem(storageKey, JSON.stringify(visuals.value))
  } catch {
    // Remembering the choice is a convenience only.
  }
}

/** The system asks for less motion; nothing then moves on its own, the analyzers included. */
export const reducedMotion = ref(false)
if (typeof window !== 'undefined' && 'matchMedia' in window) {
  const query = window.matchMedia('(prefers-reduced-motion: reduce)')
  reducedMotion.value = query.matches
  query.addEventListener('change', (event) => (reducedMotion.value = event.matches))
}
