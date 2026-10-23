import { ref } from 'vue'
import {
  getMixer,
  listSynthPresets,
  listTrackSynths,
  putMixer,
  putSynthPreset,
  putTrackSynth,
  removeSynthPreset,
  removeTrackSynth,
} from './api'
import type { SynthPatch } from './synth'

/**
 * The browser synthesizer's sounds as the server keeps them: one per track that was given its own, and named ones to
 * reuse. Held here once for the page, so the preview's player and the editor read the same state. Patches stay
 * unchecked JSON until they are played or edited, where `normalizePatch` fills in what the track's kind needs.
 */

export interface TrackSound {
  patch: unknown
  /** The preset the sound was taken from; it may have been changed since, the track keeps its copy. */
  preset: string | null
}

export interface NamedSound {
  name: string
  patch: unknown
}

/** Keyed by track name in upper case, since the server matches track names without regard to case. */
export const trackSounds = ref<Record<string, TrackSound>>({})
export const namedSounds = ref<NamedSound[]>([])
export const soundsError = ref<string | null>(null)

/** Volume (linear, 1 = unchanged) and pan (-1 to 1) of one track in the preview's mixer. */
export interface TrackMix {
  volume: number
  pan: number
}

export interface MixerState {
  master: number
  /** Keyed like `trackSounds`; a track missing here sits at 1 and centre. */
  tracks: Record<string, TrackMix>
}

/** The fader's top: about +6 dB, room to lift a quiet track above the rest. */
export const MAX_VOLUME = 2

export const mixer = ref<MixerState>({ master: 1, tracks: {} })

function level(value: unknown, fallback: number, max = MAX_VOLUME): number {
  return typeof value === 'number' && Number.isFinite(value) ? Math.min(max, Math.max(0, value)) : fallback
}

/** Whatever the server kept, made safe to play: out-of-range values clamped, anything broken dropped. */
export function normalizeMixer(raw: unknown): MixerState {
  const value = raw && typeof raw === 'object' ? (raw as Record<string, unknown>) : {}
  const tracks = value.tracks && typeof value.tracks === 'object' ? (value.tracks as Record<string, unknown>) : {}
  return {
    master: level(value.master, 1),
    tracks: Object.fromEntries(
      Object.entries(tracks).map(([track, entry]) => {
        const mix = entry && typeof entry === 'object' ? (entry as Record<string, unknown>) : {}
        const pan = typeof mix.pan === 'number' && Number.isFinite(mix.pan) ? Math.min(1, Math.max(-1, mix.pan)) : 0
        return [trackKey(track), { volume: level(mix.volume, 1), pan }]
      }),
    ),
  }
}

export function trackMix(track: string): TrackMix {
  return mixer.value.tracks[trackKey(track)] ?? { volume: 1, pan: 0 }
}

let mixerTimer = 0

/** Applies at once and saves shortly after, so a fader drag sends one request. */
function saveMixer(): void {
  window.clearTimeout(mixerTimer)
  mixerTimer = window.setTimeout(() => {
    putMixer(mixer.value).then(() => (soundsError.value = null), failed)
  }, 500)
}

export function setTrackMix(track: string, mix: Partial<TrackMix>): void {
  mixer.value = {
    ...mixer.value,
    tracks: { ...mixer.value.tracks, [trackKey(track)]: { ...trackMix(track), ...mix } },
  }
  saveMixer()
}

export function setMasterVolume(volume: number): void {
  mixer.value = { ...mixer.value, master: volume }
  saveMixer()
}

/** A session's tracks laid over the mixer, the other tracks left as they are; saved like a fader move. */
export function applyMixer(stored: MixerState): void {
  const given = normalizeMixer(stored)
  mixer.value = { master: given.master, tracks: { ...mixer.value.tracks, ...given.tracks } }
  saveMixer()
}

let loading: Promise<void> | null = null

export function trackKey(track: string): string {
  return track.toUpperCase()
}

function failed(caught: unknown): void {
  soundsError.value = caught instanceof Error ? caught.message : String(caught)
}

/** Loads both lists once; the page calls it when the preview first opens. */
export function loadSounds(): Promise<void> {
  loading ??= (async () => {
    try {
      const [tracks, presets, stored] = await Promise.all([listTrackSynths(), listSynthPresets(), getMixer()])
      mixer.value = normalizeMixer(stored.settings)
      trackSounds.value = Object.fromEntries(
        tracks.map((entry) => [trackKey(entry.track), { patch: entry.patch, preset: entry.preset }]),
      )
      namedSounds.value = presets.map((entry) => ({ name: entry.name, patch: entry.patch }))
      soundsError.value = null
    } catch (caught) {
      // The preview still plays, on the default sounds.
      failed(caught)
      loading = null
    }
  })()
  return loading
}

const pending = new Map<string, number>()

/**
 * Gives the track its sound at once and saves it shortly after, so dragging a slider sends one request rather than
 * one per step.
 */
export function setTrackSound(track: string, patch: SynthPatch, preset: string | null): void {
  const key = trackKey(track)
  trackSounds.value = { ...trackSounds.value, [key]: { patch, preset } }
  window.clearTimeout(pending.get(key))
  pending.set(
    key,
    window.setTimeout(() => {
      pending.delete(key)
      putTrackSynth(track, patch, preset).then(() => (soundsError.value = null), failed)
    }, 500),
  )
}

/** Back to the default sound of the track's kind. */
export async function resetTrackSound(track: string): Promise<void> {
  const key = trackKey(track)
  window.clearTimeout(pending.get(key))
  pending.delete(key)
  const next = { ...trackSounds.value }
  delete next[key]
  trackSounds.value = next
  try {
    await removeTrackSynth(track)
    soundsError.value = null
  } catch (caught) {
    failed(caught)
  }
}

export async function saveNamedSound(name: string, patch: SynthPatch): Promise<void> {
  try {
    await putSynthPreset(name, patch)
    namedSounds.value = (await listSynthPresets()).map((entry) => ({ name: entry.name, patch: entry.patch }))
    soundsError.value = null
  } catch (caught) {
    failed(caught)
  }
}

export async function deleteNamedSound(name: string): Promise<void> {
  try {
    await removeSynthPreset(name)
    namedSounds.value = namedSounds.value.filter((entry) => entry.name.toUpperCase() !== name.toUpperCase())
    soundsError.value = null
  } catch (caught) {
    failed(caught)
  }
}
