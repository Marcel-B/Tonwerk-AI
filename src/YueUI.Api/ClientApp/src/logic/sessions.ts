import type { StoredSession } from './api'
import { formFrom, type FormState } from './options'
import type { MixerState, TrackMix, TrackSound } from './synths'

/**
 * What a session keeps of the Logic page, as JSON on the server (`/api/logic/sessions`). The app writes the same
 * names (`LogicSessionSettings` in TonwerkUI), so a session moves between browser and app; each keeps the fields it
 * does not know as they were. The sounds, instruments and mixer are the page's for every song (kept per track name),
 * so the session holds this song's tracks of them and lays them back when it is opened.
 */
export interface SessionSettings {
  /** In the web form's names; the app fills only the fields it has. */
  form?: Partial<FormState>
  outputName?: string
  /** The stem set whose vocals go into the project; null for none, absent for the newest. */
  stems?: string | null
  stemParts?: { bass: boolean; drums: boolean }
  /** Track names. */
  muted?: string[]
  solo?: string[]
  /** This song's tracks, keyed in upper case as the mixer keys them. */
  mixer?: MixerState
  /** Per track name its sound, null for the default of its kind. */
  sounds?: Record<string, TrackSound | null>
  /** Per track name its MIDI instrument, null for the browser's sound. */
  instruments?: Record<string, number | null>
}

function strings(value: unknown): string[] | undefined {
  return Array.isArray(value) ? value.filter((entry): entry is string => typeof entry === 'string') : undefined
}

function record(value: unknown): Record<string, unknown> | undefined {
  return value && typeof value === 'object' && !Array.isArray(value) ? (value as Record<string, unknown>) : undefined
}

/** Whatever the server kept, with every field of the wrong shape dropped, so a broken one cannot stop the page. */
export function readSettings(raw: unknown): SessionSettings {
  const value = record(raw) ?? {}
  const settings: SessionSettings = {}
  if (record(value.form)) {
    settings.form = value.form as Partial<FormState>
  }
  if (typeof value.outputName === 'string') {
    settings.outputName = value.outputName
  }
  if (value.stems === null || typeof value.stems === 'string') {
    settings.stems = value.stems
  }
  const parts = record(value.stemParts)
  if (parts) {
    settings.stemParts = { bass: parts.bass === true, drums: parts.drums === true }
  }
  settings.muted = strings(value.muted)
  settings.solo = strings(value.solo)
  const mixer = record(value.mixer)
  if (mixer) {
    settings.mixer = {
      master: typeof mixer.master === 'number' ? mixer.master : 1,
      tracks: (record(mixer.tracks) ?? {}) as Record<string, TrackMix>,
    }
  }
  const sounds = record(value.sounds)
  if (sounds) {
    settings.sounds = Object.fromEntries(
      Object.entries(sounds).flatMap(([track, sound]): [string, TrackSound | null][] => {
        const entry = record(sound)
        if (sound === null) {
          return [[track, null]]
        }
        return entry && 'patch' in entry
          ? [[track, { patch: entry.patch, preset: typeof entry.preset === 'string' ? entry.preset : null }]]
          : []
      }),
    )
  }
  const instruments = record(value.instruments)
  if (instruments) {
    settings.instruments = Object.fromEntries(
      Object.entries(instruments).filter(
        (entry): entry is [string, number | null] => entry[1] === null || typeof entry[1] === 'number',
      ),
    )
  }
  for (const key of Object.keys(settings) as (keyof SessionSettings)[]) {
    if (settings[key] === undefined) {
      delete settings[key]
    }
  }
  return settings
}

export function sessionForm(settings: SessionSettings): FormState {
  return formFrom(settings.form)
}

/** What the page holds now for the tracks it shows; tracks are null before a conversion, which keeps the saved ones. */
export interface PageState {
  form: FormState
  outputName: string
  /** Undefined while the song has no stems to choose from, so the newest is taken once there are some. */
  stems: string | null | undefined
  stemParts: { bass: boolean; drums: boolean }
  tracks: {
    ids: string[]
    muted: string[]
    solo: string[]
    mixer: MixerState
    sounds: Record<string, TrackSound>
    instruments: Record<string, number>
  } | null
}

/**
 * The settings to save: the page's state over what the session held, so fields another client wrote stay and a
 * session opened before its conversion keeps its tracks.
 */
export function settingsOf(page: PageState, before: unknown): Record<string, unknown> {
  const kept = record(before) ?? {}
  const settings: Record<string, unknown> = {
    ...kept,
    form: { ...(record(kept.form) ?? {}), ...page.form },
    outputName: page.outputName,
    stemParts: page.stemParts,
  }
  if (page.stems === undefined) {
    delete settings.stems
  } else {
    settings.stems = page.stems
  }
  const tracks = page.tracks
  if (tracks) {
    const keys = new Set(tracks.ids.map((id) => id.toUpperCase()))
    settings.muted = tracks.muted
    settings.solo = tracks.solo
    settings.mixer = {
      master: tracks.mixer.master,
      tracks: Object.fromEntries(Object.entries(tracks.mixer.tracks).filter(([key]) => keys.has(key.toUpperCase()))),
    }
    settings.sounds = Object.fromEntries(
      tracks.ids.map((id) => {
        const sound = tracks.sounds[id.toUpperCase()]
        return [id, sound ? { patch: sound.patch, preset: sound.preset } : null]
      }),
    )
    settings.instruments = Object.fromEntries(tracks.ids.map((id) => [id, tracks.instruments[id] ?? null]))
  }
  return settings
}

/** The newest session of a library song: the one to go on with when the song is opened again. */
export function sessionOfSong(sessions: StoredSession[], songId: string): StoredSession | null {
  return (
    sessions.filter((session) => session.songId === songId).sort((a, b) => b.updatedAt.localeCompare(a.updatedAt))[0] ??
    null
  )
}

const lastKey = 'yue-ui.logic.session'

/** The session this browser last worked on, opened again after a reload when the address names no song. */
export function lastSession(): number | null {
  try {
    const stored = Number(localStorage.getItem(lastKey))
    return Number.isInteger(stored) && stored > 0 ? stored : null
  } catch {
    return null
  }
}

export function rememberSession(id: number | null): void {
  try {
    if (id === null) {
      localStorage.removeItem(lastKey)
    } else {
      localStorage.setItem(lastKey, String(id))
    }
  } catch {
    // Then a reload starts with an empty page, as before.
  }
}
