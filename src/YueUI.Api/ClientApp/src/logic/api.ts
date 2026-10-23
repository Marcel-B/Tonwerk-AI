import type {
  Assignments,
  ConversionOptions,
  ConversionResult,
  Diagnostic,
  Instrument,
  InstrumentInput,
  LogicInstrument,
} from './types'

/**
 * The Logic page's calls, ported from yue-to-logic-pro. Kept apart from ../api.ts, which stays the only place YuE
 * UI's own features talk to /api: these routes carry YueToLogic.Core's contract (PascalCase enums, the MIDI as
 * base64) rather than YuE UI's records.
 */

const apiBase = import.meta.env.VITE_API_BASE ?? ''

/** A request the backend refused or could not answer; `status` is 0 for network errors. */
export class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
  ) {
    super(message)
  }
}

/**
 * Where the score comes from: a song of the library, which the server reads score.abc and audio.flac of itself,
 * or a score.abc the user uploads, optionally with its audio.flac.
 */
export type ScoreSource = { song: string } | { file: File; audio: File | null }

/**
 * Stem sets of the library song whose bass or drums stand in for the generated ones: the server turns the stem into
 * MIDI (Basic Pitch for the bass, its own onset detection for kick, snare and hi-hat) once and keeps it.
 */
export interface StemParts {
  bass: string | null
  drums: string | null
}

function appendParts(form: FormData, source: ScoreSource, parts: StemParts | null): void {
  if (parts && 'song' in source) {
    if (parts.bass) {
      form.append('bassStems', parts.bass)
    }
    if (parts.drums) {
      form.append('drumStems', parts.drums)
    }
  }
}

function appendSource(form: FormData, source: ScoreSource, withAudio: boolean): void {
  if ('song' in source) {
    form.append('song', source.song)
  } else {
    form.append('file', source.file)
    if (withAudio && source.audio) {
      form.append('audio', source.audio)
    }
  }
}

/**
 * Sends the score and options to the backend. Resolves with the result for both a successful (200) and an
 * unconvertible (422) score, because both carry diagnostics worth showing; rejects with an ApiError otherwise.
 */
export async function convertScore(
  source: ScoreSource,
  options: ConversionOptions,
  signal?: AbortSignal,
  parts: StemParts | null = null,
): Promise<ConversionResult> {
  const form = new FormData()
  appendSource(form, source, false)
  form.append('options', JSON.stringify(options))
  appendParts(form, source, parts)

  const response = await post('/api/logic/convert', form, signal)
  if (response.ok || response.status === 422) {
    return (await response.json()) as ConversionResult
  }
  throw await problem(response)
}

/**
 * The score as MusicXML for a notation program, converted with the same options as the preview. The drums are left
 * out and the chord symbols stand above the first staff; a score that cannot be read rejects with its diagnostics.
 */
export async function exportMusicXml(
  source: ScoreSource,
  options: ConversionOptions,
  name: string,
  parts: StemParts | null = null,
): Promise<Blob> {
  const form = new FormData()
  appendSource(form, source, false)
  form.append('options', JSON.stringify(options))
  form.append('name', name)
  appendParts(form, source, parts)

  const response = await post('/api/logic/musicxml', form)
  if (response.status === 422) {
    throw new LogicExportError(((await response.json()) as ConversionResult).diagnostics)
  }
  if (!response.ok) {
    throw await problem(response)
  }
  return response.blob()
}

/** The Logic project could not be built; the diagnostics say why (e.g. not a FLAC file). */
export class LogicExportError extends Error {
  constructor(readonly diagnostics: Diagnostic[]) {
    super(diagnostics.map((d) => d.message).join(' '))
  }
}

export interface LogicExport {
  zip: Blob
  fileName: string
  /** Warnings such as an audio file whose length does not match the score. */
  warnings: Diagnostic[]
}

/**
 * Where an export stands: the upload of a score and recording the user picked, the build on the server, the ZIP on
 * its way back. `total` is null when the size is unknown; building has no measurable progress.
 */
export type LogicProgress = { phase: 'upload' | 'download'; loaded: number; total: number | null } | { phase: 'build' }

/**
 * Builds a zipped Logic Pro project (.logicx) from the score, the options and, if there is one, its audio.flac.
 * Through XMLHttpRequest rather than fetch, since only it reports upload and download progress: the ZIP is as large
 * as the FLAC and takes a while to reach a phone, which a bare spinner made look stuck.
 */
export async function exportLogicProject(
  source: ScoreSource,
  options: ConversionOptions,
  name: string,
  /** One region per song section instead of one per track; a property of the project, not of the score. */
  splitSections: boolean,
  /** The instrument each track plays; the track then sits on its channel and is named after it. */
  instruments: Record<string, LogicInstrument> = {},
  /** A finished stem set of the library song, whose vocals go onto the project's vocal tracks. */
  stems: string | null = null,
  onProgress: (progress: LogicProgress) => void = () => {},
  parts: StemParts | null = null,
): Promise<LogicExport> {
  const form = new FormData()
  appendSource(form, source, true)
  form.append('options', JSON.stringify(options))
  form.append('name', name)
  form.append('splitSections', String(splitSections))
  if (Object.keys(instruments).length > 0) {
    form.append('instruments', JSON.stringify(instruments))
  }
  if (stems && 'song' in source) {
    form.append('stems', stems)
  }
  appendParts(form, source, parts)

  const xhr = await new Promise<XMLHttpRequest>((resolve, reject) => {
    const request = new XMLHttpRequest()
    request.open('POST', `${apiBase}/api/logic/export`)
    request.responseType = 'blob'
    // A library song sends only a few fields; its FLAC is on the server already, so there is nothing to show.
    const uploads = !('song' in source) && source.audio !== null
    if (uploads) {
      request.upload.onprogress = (event) =>
        onProgress({ phase: 'upload', loaded: event.loaded, total: event.lengthComputable ? event.total : null })
    }
    request.upload.onload = () => onProgress({ phase: 'build' })
    request.onprogress = (event) => {
      if (request.status === 200) {
        onProgress({ phase: 'download', loaded: event.loaded, total: event.lengthComputable ? event.total : null })
      }
    }
    request.onload = () => resolve(request)
    request.onerror = () => reject(new ApiError('network', 0))
    onProgress(uploads ? { phase: 'upload', loaded: 0, total: null } : { phase: 'build' })
    request.send(form)
  })

  const body = xhr.response as Blob
  if (xhr.status === 422) {
    throw new LogicExportError((JSON.parse(await body.text()) as ConversionResult).diagnostics)
  }
  if (xhr.status < 200 || xhr.status >= 300) {
    const problem = (await body
      .text()
      .then((text) => JSON.parse(text) as { title?: string; detail?: string })
      .catch(() => null)) as { title?: string; detail?: string } | null
    throw new ApiError(problem?.detail ?? problem?.title ?? `HTTP ${xhr.status}`, xhr.status)
  }

  const header = xhr.getResponseHeader('X-YueToLogic-Diagnostics')
  return {
    zip: body,
    fileName: `${name}.logicx.zip`,
    warnings: header ? (JSON.parse(header) as Diagnostic[]) : [],
  }
}

// ---- Presets -----------------------------------------------------------------------------------

/** A preset as the server keeps it; the form is whatever the interface saved, checked on the way in. */
export interface StoredPreset {
  id: number
  name: string
  form: unknown
  updatedAt: string
}

export async function listPresets(): Promise<StoredPreset[]> {
  return (await request('/api/logic/presets')).json() as Promise<StoredPreset[]>
}

/** Saves the form under the name, replacing a preset of that name. */
export async function putPreset(name: string, form: unknown): Promise<StoredPreset> {
  return (
    await request(`/api/logic/presets/${encodeURIComponent(name)}`, json('PUT', { form }))
  ).json() as Promise<StoredPreset>
}

export async function removePreset(name: string): Promise<void> {
  await request(`/api/logic/presets/${encodeURIComponent(name)}`, { method: 'DELETE' })
}

// ---- Browser synthesizer ----------------------------------------------------------------------

/** A named sound as the server keeps it; the patch is checked by `normalizePatch` before it is played. */
export interface StoredSynthPreset {
  id: number
  name: string
  patch: unknown
  updatedAt: string
}

/** The sound a track plays in the preview, and the preset it was taken from, if any. */
export interface StoredTrackSynth {
  track: string
  patch: unknown
  preset: string | null
  updatedAt: string
}

export async function listSynthPresets(): Promise<StoredSynthPreset[]> {
  return (await request('/api/logic/synths/presets')).json() as Promise<StoredSynthPreset[]>
}

/** Saves the sound under the name, replacing one of that name. */
export async function putSynthPreset(name: string, patch: unknown): Promise<StoredSynthPreset> {
  return (
    await request(`/api/logic/synths/presets/${encodeURIComponent(name)}`, json('PUT', { patch }))
  ).json() as Promise<StoredSynthPreset>
}

export async function removeSynthPreset(name: string): Promise<void> {
  await request(`/api/logic/synths/presets/${encodeURIComponent(name)}`, { method: 'DELETE' })
}

export async function listTrackSynths(): Promise<StoredTrackSynth[]> {
  return (await request('/api/logic/synths/tracks')).json() as Promise<StoredTrackSynth[]>
}

export async function putTrackSynth(track: string, patch: unknown, preset: string | null): Promise<StoredTrackSynth> {
  return (
    await request(`/api/logic/synths/tracks/${encodeURIComponent(track)}`, json('PUT', { patch, preset }))
  ).json() as Promise<StoredTrackSynth>
}

/** Gives the track back the default sound of its kind. */
export async function removeTrackSynth(track: string): Promise<void> {
  await request(`/api/logic/synths/tracks/${encodeURIComponent(track)}`, { method: 'DELETE' })
}

/** The preview's mixer as the server keeps it; `settings` is null before it was first saved. */
export interface StoredMixer {
  settings: unknown
  updatedAt: string | null
}

export async function getMixer(): Promise<StoredMixer> {
  return (await request('/api/logic/synths/mixer')).json() as Promise<StoredMixer>
}

export async function putMixer(settings: unknown): Promise<void> {
  await request('/api/logic/synths/mixer', json('PUT', { settings }))
}

// ---- Sessions ----------------------------------------------------------------------------------

/**
 * The page's state for one library song or one uploaded score, kept on the server so the work goes on later or in
 * another browser or the app. `settings` is what `sessions.ts` writes; the server never reads it.
 */
export interface StoredSession {
  id: number
  name: string
  /** The library song (`run/songN`), or null for an uploaded score. */
  songId: string | null
  /** The uploaded score's and recording's file names as they were chosen; the files lie with the session. */
  scoreFile: string | null
  audioFile: string | null
  settings: unknown
  createdAt: string
  updatedAt: string
}

export async function listSessions(): Promise<StoredSession[]> {
  return (await request('/api/logic/sessions')).json() as Promise<StoredSession[]>
}

/** A session for a library song, or for an uploaded score whose file goes along. */
export async function createSession(
  name: string,
  source: { song: string } | { file: File },
  settings: unknown,
): Promise<StoredSession> {
  const form = new FormData()
  form.append('name', name)
  if ('song' in source) {
    form.append('song', source.song)
  } else {
    form.append('file', source.file)
  }
  form.append('settings', JSON.stringify(settings))
  return (await request('/api/logic/sessions', { method: 'POST', body: form })).json() as Promise<StoredSession>
}

/** Renames the session, replaces its settings, or both. */
export async function updateSession(
  id: number,
  change: { name?: string; settings?: unknown },
  keepalive = false,
): Promise<StoredSession> {
  // keepalive: the last change before the page goes away still arrives, as with a song's note.
  return (
    await request(`/api/logic/sessions/${id}`, { ...json('PUT', change), keepalive })
  ).json() as Promise<StoredSession>
}

export async function deleteSession(id: number): Promise<void> {
  await request(`/api/logic/sessions/${id}`, { method: 'DELETE' })
}

/** Keeps the uploaded recording with the session, or takes it away with `null`. */
export async function setSessionAudio(id: number, audio: File | null): Promise<void> {
  if (audio) {
    const form = new FormData()
    form.append('audio', audio)
    await request(`/api/logic/sessions/${id}/audio`, { method: 'PUT', body: form })
  } else {
    await request(`/api/logic/sessions/${id}/audio`, { method: 'DELETE' })
  }
}

/** The session's uploaded score or recording as a file again, under the name it was chosen with. */
export async function sessionFile(id: number, which: 'file' | 'audio', name: string): Promise<File> {
  const blob = await (await request(`/api/logic/sessions/${id}/${which}`)).blob()
  return new File([blob], name, { type: blob.type })
}

// ---- Instruments -------------------------------------------------------------------------------

export async function listInstruments(): Promise<Instrument[]> {
  return (await request('/api/instruments')).json() as Promise<Instrument[]>
}

export async function createInstrument(input: InstrumentInput): Promise<Instrument> {
  return (await request('/api/instruments', json('POST', input))).json() as Promise<Instrument>
}

export async function updateInstrument(id: number, input: InstrumentInput): Promise<Instrument> {
  return (await request(`/api/instruments/${id}`, json('PUT', input))).json() as Promise<Instrument>
}

/** Writes the instrument's note; an empty one takes it away. The form's save leaves it alone. */
export async function saveInstrumentNote(id: number, note: string): Promise<void> {
  // keepalive: a note typed just before the page goes away still arrives, as with a song's.
  await request(`/api/instruments/${id}/note`, { ...json('PUT', { note }), keepalive: true })
}

/** Removes the instrument; the server drops the assignments of tracks to it as well. */
export async function deleteInstrument(id: number): Promise<void> {
  await request(`/api/instruments/${id}`, { method: 'DELETE' })
}

export async function listAssignments(): Promise<Assignments> {
  return (await request('/api/instruments/assignments')).json() as Promise<Assignments>
}

/** Gives a track an instrument, or takes it away with `null`. */
export async function assignInstrument(track: string, instrumentId: number | null): Promise<void> {
  await request(`/api/instruments/assignments/${encodeURIComponent(track)}`, json('PUT', { instrumentId }))
}

function json(method: string, body: unknown): RequestInit {
  return { method, body: JSON.stringify(body), headers: { 'Content-Type': 'application/json' } }
}

/** A multipart POST whose answer the caller reads itself, since 422 carries a result rather than a problem. */
async function post(path: string, body: FormData, signal?: AbortSignal): Promise<Response> {
  try {
    return await fetch(`${apiBase}${path}`, { method: 'POST', body, signal })
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') {
      throw error
    }
    throw new ApiError('network', 0)
  }
}

/** A problem document's detail, or the status. */
async function problem(response: Response): Promise<ApiError> {
  const body = (await response.json().catch(() => null)) as { title?: string; detail?: string } | null
  return new ApiError(body?.detail ?? body?.title ?? `HTTP ${response.status}`, response.status)
}

/** A request whose failure is worth an ApiError. */
async function request(path: string, init: RequestInit = {}): Promise<Response> {
  let response: Response
  try {
    response = await fetch(`${apiBase}${path}`, init)
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') {
      throw error
    }
    throw new ApiError('network', 0)
  }

  if (!response.ok) {
    throw await problem(response)
  }
  return response
}
