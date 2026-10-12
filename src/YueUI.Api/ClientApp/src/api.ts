import type {
  AudioMidiInfo,
  AudioMidiSettings,
  BackupStatus,
  GenerateRequest,
  LogEntry,
  LogLevel,
  LogPage,
  LogSource,
  LoraList,
  LyricsLanguage,
  LyricsModels,
  LyricsState,
  MidiScore,
  PlaylistInfo,
  PromptTemplate,
  QueuedJob,
  RunInfo,
  SongRequest,
  SongState,
  SpeechInfo,
  SpeechTake,
  SpeechVoice,
  StatusSnapshot,
  StorageInfo,
  TranscriptionList,
  TranscriptionState,
  TranscriptionTask,
  ReferenceVoice,
  StemModel,
  StemSetState,
  SwapState,
  VersionRequest,
  VersionState,
  VideoEffect,
  VideoFormat,
  VideoMotion,
  VideoState,
  MediaInfo,
  ImageInfo,
  ImageState,
  MemoryInfo,
  VoiceInfo,
  WorkerInfo,
} from './types'

const apiBase = import.meta.env.VITE_API_BASE ?? ''

/** A request the backend refused or could not answer; `status` is 0 for network errors. */
export class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
    /** Field errors of a validation problem, keyed by the request's property names. */
    readonly errors: Record<string, string[]> = {},
  ) {
    super(message)
  }
}

/** Queues a run; its songs then arrive as `song` events. */
/** Resolves to the waiting job while other work holds the memory, else to null: the song went to the worker. */
export async function generate(request: GenerateRequest): Promise<QueuedJob | null> {
  return queuedJob(await send('/api/generate', json('POST', request)))
}

/**
 * Starts English or German lyrics in YuE2's format from a few keywords or a photo (a data URL, see `photo.ts`), written by
 * the language model in LM Studio; the draft arrives as a `lyrics` event. While YuE2 generates or another draft is being
 * written it waits in the queue (stage `queued`).
 */
export async function draftLyrics(
  keywords: string,
  style: string,
  language: LyricsLanguage,
  model: string | null,
  image: string | null = null,
): Promise<LyricsState> {
  return (
    await send('/api/lyrics', json('POST', { keywords, style, language, model, image }))
  ).json() as Promise<LyricsState>
}

/**
 * Has the language model change lyrics as instructed ("make the chorus catchier") instead of drafting new ones; the
 * result arrives as a `lyrics` event like a draft. `keywords` and `style` say what the song is about, for context.
 */
export async function reviseLyrics(
  lyrics: string,
  instruction: string,
  keywords: string,
  style: string,
  language: LyricsLanguage,
  model: string | null,
): Promise<LyricsState> {
  return (
    await send('/api/lyrics', json('POST', { lyrics, instruction, keywords, style, language, model }))
  ).json() as Promise<LyricsState>
}

/** The models LM Studio has downloaded, for the picker; starts LM Studio's server if needed (503 if it cannot). */
export async function getLyricsModels(): Promise<LyricsModels> {
  return (await send('/api/lyrics/models')).json() as Promise<LyricsModels>
}

/** The LoRAs a song can be made with. */
export async function listLoras(): Promise<LoraList> {
  return (await send('/api/loras')).json() as Promise<LoraList>
}

/** Synthesizes a finished song again from its saved tokens, normally a draft at full quality. */
export async function render(songId: string, quality: 'full' | 'draft' = 'full'): Promise<QueuedJob | null> {
  return queuedJob(await send(`/api/songs/${songId}/render`, json('POST', { quality })))
}

/** A 202 carries the job only when it has to wait. */
async function queuedJob(response: Response): Promise<QueuedJob | null> {
  const text = await response.text()
  return text ? (JSON.parse(text) as QueuedJob) : null
}

/** Takes a job out of the queue before it starts. */
export async function cancelQueued(id: string): Promise<void> {
  await send(`/api/queue/${encodeURIComponent(id)}`, { method: 'DELETE' })
}

/** Moves a waiting job by `offset` places, towards the front when negative. */
export async function moveQueued(id: string, offset: number): Promise<void> {
  await send(`/api/queue/${encodeURIComponent(id)}/move`, json('POST', { offset }))
}

export async function cancel(songId: string): Promise<void> {
  await send(`/api/songs/${songId}/cancel`, { method: 'POST' })
}

export async function stopAll(): Promise<void> {
  await send('/api/stop', { method: 'POST' })
}

/** Ends the worker process, which frees the memory the model holds. */
export async function shutdownWorker(): Promise<void> {
  await send('/api/worker/shutdown', { method: 'POST' })
}

export async function listLibrary(): Promise<RunInfo[]> {
  return (await send('/api/library')).json() as Promise<RunInfo[]>
}

/** Deletes the song's folder, and the run's with the last song; refused while the worker is on it. */
export async function deleteSong(songId: string): Promise<void> {
  await send(`/api/songs/${songId}`, { method: 'DELETE' })
}

/** Deletes the run's folder with all its songs; refused while the worker is on one of them. */
export async function deleteRun(runId: string): Promise<void> {
  await send(`/api/runs/${runId}`, { method: 'DELETE' })
}

/** Gives the run a title of its own (the folder and song ids stay); an empty one returns it to the worker's. */
export async function renameRun(runId: string, title: string): Promise<void> {
  await send(`/api/runs/${runId}/title`, json('PUT', { title }))
}

/** One to five stars for the song; null takes its rating away. */
export async function rateSong(songId: string, rating: number | null): Promise<void> {
  await send(`/api/songs/${songId}/rating`, json('PUT', { rating }))
}

/** Writes the song's note; an empty one (or only blank lines) takes it away. */
export async function saveSongNote(songId: string, note: string): Promise<void> {
  // keepalive: a note typed just before the page goes away (the app closed, a reload) still arrives.
  await send(`/api/songs/${songId}/note`, { ...json('PUT', { note }), keepalive: true })
}

/** Every playlist, oldest first, with its song ids in order; songs deleted since are left out. */
export async function getPlaylists(): Promise<PlaylistInfo[]> {
  return (await send('/api/playlists')).json() as Promise<PlaylistInfo[]>
}

export async function createPlaylist(name: string): Promise<PlaylistInfo> {
  return (await send('/api/playlists', json('POST', { name }))).json() as Promise<PlaylistInfo>
}

export async function renamePlaylist(id: number, name: string): Promise<PlaylistInfo> {
  return (await send(`/api/playlists/${id}/name`, json('PUT', { name }))).json() as Promise<PlaylistInfo>
}

/** Replaces the playlist's songs; answers with what was stored (each song once). */
export async function putPlaylistSongs(id: number, songIds: string[]): Promise<PlaylistInfo> {
  return (await send(`/api/playlists/${id}/songs`, json('PUT', { songIds }))).json() as Promise<PlaylistInfo>
}

/** The server keeps the last playlist (409), so "add to playlist" always has somewhere to go. */
export async function deletePlaylist(id: number): Promise<void> {
  await send(`/api/playlists/${id}`, { method: 'DELETE' })
}

/** Every prompt template, by name. */
export async function getTemplates(): Promise<PromptTemplate[]> {
  return (await send('/api/templates')).json() as Promise<PromptTemplate[]>
}

/** A name already taken (in any case) answers 409. */
export async function createTemplate(name: string, settings: object): Promise<PromptTemplate> {
  return (await send('/api/templates', json('POST', { name, settings }))).json() as Promise<PromptTemplate>
}

/** Renames the template, replaces its settings, or both. */
export async function updateTemplate(
  id: number,
  change: { name?: string; settings?: object },
): Promise<PromptTemplate> {
  return (await send(`/api/templates/${id}`, json('PUT', change))).json() as Promise<PromptTemplate>
}

export async function deleteTemplate(id: number): Promise<void> {
  await send(`/api/templates/${id}`, { method: 'DELETE' })
}

/** Free and total space of the volume the songs are written to. */
/** The Nextcloud backup: whether it is configured, running, and how the last one went. */
export async function getBackup(): Promise<BackupStatus> {
  return (await send('/api/backup')).json() as Promise<BackupStatus>
}

/** Starts a backup now; 409 while one runs. */
export async function startBackup(): Promise<BackupStatus> {
  return (await send('/api/backup', { method: 'POST' })).json() as Promise<BackupStatus>
}

export async function getStorage(): Promise<StorageInfo> {
  return (await send('/api/storage')).json() as Promise<StorageInfo>
}

/** The song's own cover; the time it was chosen makes a changed cover a new address, past every cache. */
export function coverUrl(songId: string, updatedAt: string): string {
  return `${apiBase}/api/songs/${songId}/cover?v=${encodeURIComponent(updatedAt)}`
}

/** Keeps the image as the song's cover, for the library, the player and every later export. */
export async function putCover(songId: string, cover: Blob): Promise<void> {
  const form = new FormData()
  form.append('cover', cover, 'cover.jpg')
  await send(`/api/songs/${songId}/cover`, { method: 'PUT', body: form })
}

/** Back to the cover the browser draws. */
export async function deleteCover(songId: string): Promise<void> {
  await send(`/api/songs/${songId}/cover`, { method: 'DELETE' })
}

export function audioUrl(songId: string, download = false): string {
  return `${apiBase}/api/songs/${songId}/audio${download ? '?download=true' : ''}`
}

/**
 * What the player plays: a small AAC copy, since the Mac's home upload is too slow for the FLAC on the road. The
 * FLAC (audioUrl) stays for downloads and the Logic page, whose waveform lines up with the notes.
 */
export function streamUrl(songId: string): string {
  return `${apiBase}/api/songs/${songId}/stream`
}

export function scoreUrl(songId: string): string {
  return `${apiBase}/api/songs/${songId}/score`
}

/** The song's score.abc as text, to show it or to use it for the next song. */
export async function songScore(songId: string): Promise<string> {
  return (await send(`/api/songs/${songId}/score`)).text()
}

/** What the song was generated with, to make it again with changes. */
export async function songRequest(songId: string): Promise<SongRequest> {
  return (await send(`/api/songs/${songId}/request`)).json() as Promise<SongRequest>
}

/**
 * The song as a small AAC (`.m4a`, about a tenth of the FLAC) for the share sheet, named after its title. The
 * server encodes it first, which takes a few seconds.
 */
/**
 * The song as MP3, M4A or FLAC with its tags and cover, named after its title. The server encodes it first, which
 * takes a few seconds (not for FLAC, which only gets its tags).
 */
export async function exportSongFile(
  songId: string,
  format: 'small' | 'mp3' | 'm4a' | 'flac',
  artist: string,
  genre: string,
  cover: Blob | null,
): Promise<File> {
  const form = new FormData()
  form.append('format', format)
  form.append('artist', artist)
  form.append('genre', genre)
  if (cover) {
    form.append('cover', cover, 'cover.jpg')
  }
  const response = await send(`/api/songs/${songId}/export`, { method: 'POST', body: form })
  const name = fileName(response.headers.get('Content-Disposition')) ?? `Tonwerk.${format === 'small' ? 'm4a' : format}`
  const blob = await response.blob()
  return new File([blob], name, { type: blob.type })
}

/** Saves a fetched file the way a download link would. */
export function saveBlob(blob: Blob, name: string): void {
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = name
  document.body.append(link)
  link.click()
  link.remove()
  // Safari starts the download after click() returns; revoking at once can cancel it.
  setTimeout(() => URL.revokeObjectURL(url), 60_000)
}

/** The file name of a Content-Disposition header, preferring the UTF-8 form ASP.NET Core sends alongside. */
function fileName(disposition: string | null): string | null {
  const utf8 = disposition?.match(/filename\*=UTF-8''([^;]+)/i)
  if (utf8) {
    return decodeURIComponent(utf8[1]!)
  }
  return disposition?.match(/filename="?([^";]+)"?/i)?.[1] ?? null
}

/** Whether Basic Pitch is installed, for the pages that turn a track into MIDI. */
export async function getAudioMidi(): Promise<AudioMidiInfo> {
  return (await send('/api/audio-midi')).json() as Promise<AudioMidiInfo>
}

/** A track turned into MIDI: the file, named by the server, and how many notes were heard. */
export interface MidiTrack {
  file: File
  notes: number
}

/** A recording (a sung track, typically) to MIDI with Basic Pitch; takes seconds, answered at once. */
export async function audioToMidi(file: File, settings: AudioMidiSettings): Promise<MidiTrack> {
  const form = new FormData()
  form.append('file', file)
  form.append('mono', String(settings.mono))
  form.append('quantize', String(settings.quantize))
  form.append('bends', String(settings.bends))
  if (settings.tempo) {
    form.append('tempo', String(settings.tempo))
  }
  return midiTrack(await send('/api/audio-midi', { method: 'POST', body: form }), 'recording.mid')
}

/**
 * A separated stem to MIDI, at its song's tempo; an uploaded file has none, so `tempo` (null for 120 bpm without a
 * grid).
 */
export async function stemToMidi(
  setId: string,
  stem: string,
  settings: AudioMidiSettings,
  tempo: number | null = null,
): Promise<MidiTrack> {
  const response = await send(
    `/api/stems/${setId}/${encodeURIComponent(stem)}/midi`,
    json('POST', { mono: settings.mono, quantize: settings.quantize, bends: settings.bends, tempo }),
  )
  return midiTrack(response, `${stem}.mid`)
}

async function midiTrack(response: Response, fallback: string): Promise<MidiTrack> {
  const name = fileName(response.headers.get('Content-Disposition')) ?? fallback
  const notes = Number(response.headers.get('X-Audio-Midi-Notes') ?? 0)
  return { file: new File([await response.blob()], name, { type: 'audio/midi' }), notes }
}

/** A MIDI file, typically a song edited in Logic, read back into a score. */
export async function midiToAbc(file: File): Promise<MidiScore> {
  const form = new FormData()
  form.append('file', file)
  return (await send('/api/midi/abc', { method: 'POST', body: form })).json() as Promise<MidiScore>
}

/**
 * Uploads a recording for SheetSage2; its progress then arrives as `transcription` events, or it waits in the queue
 * (stage `queued`) while another transcription runs or another model holds the memory.
 */
export async function transcribe(file: File, task: TranscriptionTask): Promise<TranscriptionState> {
  const form = new FormData()
  form.append('file', file)
  form.append('task', task)
  return (await send('/api/transcriptions', { method: 'POST', body: form })).json() as Promise<TranscriptionState>
}

export async function cancelTranscription(id: string): Promise<void> {
  await send(`/api/transcriptions/${id}/cancel`, { method: 'POST' })
}

export async function listTranscriptions(): Promise<TranscriptionList> {
  return (await send('/api/transcriptions')).json() as Promise<TranscriptionList>
}

export async function transcriptionScore(id: string): Promise<string> {
  return (await send(`/api/transcriptions/${encodeURIComponent(id)}/score`)).text()
}

export async function deleteTranscription(id: string): Promise<void> {
  await send(`/api/transcriptions/${encodeURIComponent(id)}`, { method: 'DELETE' })
}

export function transcriptionScoreUrl(id: string): string {
  return `${apiBase}/api/transcriptions/${encodeURIComponent(id)}/score?download=true`
}

/** Score, MIDI parts and annotations of a transcription. */
export function transcriptionZipUrl(id: string): string {
  return `${apiBase}/api/transcriptions/${encodeURIComponent(id)}/zip`
}

/** This server's VAPID public key (base64url), the `applicationServerKey` for subscribing to its pushes. */
export async function pushKey(): Promise<string> {
  return ((await (await send('/api/push')).json()) as { publicKey: string }).publicKey
}

/** Asks the server to notify this browser, in the given language, when songs, transcriptions and lyrics finish. */
export async function savePushSubscription(subscription: PushSubscription, language: string): Promise<void> {
  await send('/api/push/subscriptions', json('POST', { ...subscription.toJSON(), language }))
}

export async function deletePushSubscription(endpoint: string): Promise<void> {
  await send('/api/push/subscriptions', json('DELETE', { endpoint }))
}

/** Sends a notification to this browser only, to check the whole way to the phone. */
export async function testPush(endpoint: string): Promise<void> {
  await send('/api/push/test', json('POST', { endpoint }))
}

/** Whether reference voices can be managed and songs sung with them; the interface hides both otherwise. */
export async function getVoiceInfo(): Promise<VoiceInfo> {
  return (await send('/api/voice')).json() as Promise<VoiceInfo>
}

/** The reference voices. */
export async function listVoices(): Promise<ReferenceVoice[]> {
  return (await send('/api/voices')).json() as Promise<ReferenceVoice[]>
}

/**
 * Stores a recording as a reference voice; the service keeps at most 25 seconds, from `start` if given, up to `end`.
 * Both in seconds, sent with a point whatever the language.
 */
export async function addVoice(label: string, file: File, start?: number, end?: number): Promise<ReferenceVoice> {
  const form = new FormData()
  form.append('label', label)
  form.append('file', file)
  if (start !== undefined) {
    form.append('startSeconds', String(start))
  }
  if (end !== undefined) {
    form.append('endSeconds', String(end))
  }
  return (await send('/api/voices', { method: 'POST', body: form })).json() as Promise<ReferenceVoice>
}

/** Every uploaded recording sung with another voice, newest first. */
export async function listSwaps(): Promise<SwapState[]> {
  return (await send('/api/swaps')).json() as Promise<SwapState[]>
}

export interface SwapRequest {
  voiceId: string
  semiToneShift: number
  strength: number
  diffusionSteps: number
  /** The file is a whole song: separate it first and mix the new vocals back. */
  separate: boolean
  keepReverb: boolean
}

/** Uploads the recording and queues it; its progress arrives as `swap` events. */
export async function addSwap(file: File, request: SwapRequest): Promise<SwapState> {
  const form = new FormData()
  form.append('file', file)
  for (const [key, value] of Object.entries(request)) {
    if (value !== undefined) {
      form.append(key, String(value))
    }
  }
  return (await send('/api/swaps', { method: 'POST', body: form })).json() as Promise<SwapState>
}

/** Stops a swap in the works, or removes a finished one with its upload. */
export async function deleteSwap(id: string): Promise<void> {
  await send(`/api/swaps/${id}`, { method: 'DELETE' })
}

/** The result as FLAC; with `download`, under a name made of the file's and the voice's. */
export function swapAudioUrl(id: string, download = false): string {
  return `${apiBase}/api/swaps/${id}/audio${download ? '?download=true' : ''}`
}

/** The upload as it came, to compare. */
export function swapSourceUrl(id: string): string {
  return `${apiBase}/api/swaps/${id}/source`
}

/** The folders a photo video can take its pictures from. */
export async function fetchMediaFolders(): Promise<MediaInfo> {
  return (await send('/api/videos/media')).json() as Promise<MediaInfo>
}

/** The song's music videos, newest first. */
export async function listVideos(songId: string): Promise<VideoState[]> {
  return (await send(`/api/songs/${songId}/videos`)).json() as Promise<VideoState[]>
}

export interface VideoRequest {
  format: VideoFormat
  effect: VideoEffect
  motion: VideoMotion
  showCover: boolean
  showTitle: boolean
  /** The analyzer's colour, `#rrggbb`. */
  color: string
  /** A photo video's folder; its pictures take the background's place. */
  media?: string
  motif?: string
}

/**
 * The still layers drawn in `video.ts`, as PNGs of the frame's size (the plasma ball's frames of its area); all but
 * the background only when asked for. Each goes under its name, the moving one under the motion's.
 */
export interface VideoLayerImages {
  /** A photo video has its pictures instead, darkened by its `shade`. */
  background: Blob | null
  shade?: Blob
  cover: Blob | null
  title: Blob | null
  particles?: Blob
  /** The plasma ball's frames, sent as `plasma0`, `plasma1`, … */
  plasma?: Blob[]
}

/** Sends the layers and queues the video; its progress arrives as `video` events. */
export async function addVideo(songId: string, request: VideoRequest, layers: VideoLayerImages): Promise<VideoState> {
  const form = new FormData()
  for (const [key, value] of Object.entries(request)) {
    form.append(key, String(value))
  }
  for (const [name, layer] of Object.entries(layers) as [string, Blob | Blob[] | null | undefined][]) {
    if (Array.isArray(layer)) {
      layer.forEach((blob, index) => form.append(`${name}${index}`, blob, `${name}${index}.png`))
    } else if (layer) {
      form.append(name, layer, `${name}.png`)
    }
  }
  return (await send(`/api/songs/${songId}/videos`, { method: 'POST', body: form })).json() as Promise<VideoState>
}

/** Stops a video in the works, or removes a finished one. */
export async function deleteVideo(id: string): Promise<void> {
  await send(`/api/videos/${id}`, { method: 'DELETE' })
}

/** The MP4; with `download`, under a name made of the title, the song and where it is meant to go. */
export function videoUrl(id: string, download = false): string {
  return `${apiBase}/api/videos/${id}/file${download ? '?download=true' : ''}`
}

/** The MP4 as a file for the share sheet, fetched first since Safari wants the file within the tap. */
export async function fetchVideoFile(id: string): Promise<File> {
  const response = await send(`/api/videos/${id}/file?download=true`)
  const name = fileName(response.headers.get('Content-Disposition')) ?? 'Tonwerk.mp4'
  return new File([await response.blob()], name, { type: 'video/mp4' })
}

/** Refused (409) while a job of the service still waits for the voice. */
export async function deleteVoice(id: string): Promise<void> {
  await send(`/api/voices/${encodeURIComponent(id)}`, { method: 'DELETE' })
}

export function voiceAudioUrl(id: string): string {
  return `${apiBase}/api/voices/${encodeURIComponent(id)}/audio`
}

/** Queues the song to be sung with a reference voice; its progress arrives as `version` events. */
export async function addVersion(songId: string, request: VersionRequest): Promise<VersionState> {
  return (await send(`/api/songs/${songId}/versions`, json('POST', request))).json() as Promise<VersionState>
}

/** Stops a version in the works, or removes a finished one with its audio. */
export async function deleteVersion(songId: string, versionId: string): Promise<void> {
  await send(`/api/songs/${songId}/versions/${versionId}`, { method: 'DELETE' })
}

export function versionAudioUrl(songId: string, versionId: string, download = false): string {
  return `${apiBase}/api/songs/${songId}/versions/${versionId}/audio${download ? '?download=true' : ''}`
}

/** The version's AAC copy for the player, as streamUrl for the song. */
export function versionStreamUrl(songId: string, versionId: string): string {
  return `${apiBase}/api/songs/${songId}/versions/${versionId}/stream`
}

/** The separation models offered, this server's default marked; at least that one. */
export async function listStemModels(): Promise<StemModel[]> {
  return (await send('/api/stems/models')).json() as Promise<StemModel[]>
}

/** Every song split into stems, newest first. */
export async function listStems(): Promise<StemSetState[]> {
  return (await send('/api/stems')).json() as Promise<StemSetState[]>
}

/** Queues the song to be split into stems; its progress arrives as `stems` events. */
export async function separateSong(songId: string, model: string | null, dereverb: boolean): Promise<StemSetState> {
  return (await send(`/api/songs/${songId}/stems`, json('POST', { model, dereverb }))).json() as Promise<StemSetState>
}

/** Queues an uploaded recording to be split into stems like a song. */
export async function separateFile(file: File, model: string | null, dereverb: boolean): Promise<StemSetState> {
  const form = new FormData()
  form.append('file', file)
  if (model) {
    form.append('model', model)
  }
  form.append('dereverb', String(dereverb))
  return (await send('/api/stems', { method: 'POST', body: form })).json() as Promise<StemSetState>
}

/** Stops a separation in the works, or removes a finished one with its files. */
export async function deleteStems(id: string): Promise<void> {
  await send(`/api/stems/${id}`, { method: 'DELETE' })
}

export function stemAudioUrl(id: string, name: string, download = false): string {
  return `${apiBase}/api/stems/${id}/${encodeURIComponent(name)}${download ? '?download=true' : ''}`
}

/** The speech lab's models, recorded voices and takes. */
export async function getSpeech(): Promise<SpeechInfo> {
  return (await send('/api/speech')).json() as Promise<SpeechInfo>
}

/** Stores a recording (any format the server's ffmpeg reads) as a voice to clone, with what is said in it. */
export async function addSpeechVoice(
  label: string,
  transcript: string,
  recording: Blob,
  fileName: string,
): Promise<SpeechVoice> {
  const form = new FormData()
  form.append('label', label)
  form.append('transcript', transcript)
  form.append('file', recording, fileName)
  return (await send('/api/speech/voices', { method: 'POST', body: form })).json() as Promise<SpeechVoice>
}

export async function deleteSpeechVoice(id: string): Promise<void> {
  await send(`/api/speech/voices/${encodeURIComponent(id)}`, { method: 'DELETE' })
}

export function speechVoiceAudioUrl(id: string): string {
  return `${apiBase}/api/speech/voices/${encodeURIComponent(id)}/audio`
}

/** One take per model, spoken one after the other; their progress arrives as `speech` events. */
export async function speak(text: string, voiceId: string | null, models: string[]): Promise<SpeechTake[]> {
  return (await send('/api/speech/takes', json('POST', { text, voiceId, models }))).json() as Promise<SpeechTake[]>
}

/** Stops a take that is being spoken, or removes a finished one with its audio. */
export async function deleteSpeechTake(id: string): Promise<void> {
  await send(`/api/speech/takes/${encodeURIComponent(id)}`, { method: 'DELETE' })
}

export function speechTakeAudioUrl(id: string, download = false): string {
  return `${apiBase}/api/speech/takes/${encodeURIComponent(id)}/audio${download ? '?download=true' : ''}`
}

/** Whether mflux is installed, and the Klein models with what they cost and allow. */
export async function getImageInfo(): Promise<ImageInfo> {
  return (await send('/api/images')).json() as Promise<ImageInfo>
}

/** The song's painted cover candidates, newest first. */
export async function listImages(songId: string): Promise<ImageState[]> {
  return (await send(`/api/songs/${songId}/images`)).json() as Promise<ImageState[]>
}

/** Queues a picture; its progress arrives as `image` events. Without a seed the server picks one. */
export async function paintImage(
  songId: string,
  request: { prompt: string; model: string; seed?: number },
): Promise<ImageState> {
  return (await send(`/api/songs/${songId}/images`, json('POST', request))).json() as Promise<ImageState>
}

export function imageUrl(id: string): string {
  return `${apiBase}/api/images/${encodeURIComponent(id)}`
}

/** Makes the picture the song's cover; the library event brings it into the list and the player. */
export async function takeImage(id: string): Promise<void> {
  await send(`/api/images/${encodeURIComponent(id)}/cover`, { method: 'PUT' })
}

/** Stops a picture in the works, or removes a finished one. */
export async function deleteImage(id: string): Promise<void> {
  await send(`/api/images/${encodeURIComponent(id)}`, { method: 'DELETE' })
}

export interface EventHandlers {
  snapshot(snapshot: StatusSnapshot): void
  song(song: SongState): void
  worker(worker: WorkerInfo): void
  log(entry: LogEntry): void
  /** A song was written to disk or deleted: the library has changed. */
  library(): void
  transcription(transcription: TranscriptionState): void
  lyrics(lyrics: LyricsState): void
  version(version: VersionState): void
  stems(set: StemSetState): void
  /** An uploaded recording being sung with another voice changed; a deleted one arrives as cancelled. */
  swap(swap: SwapState): void
  /** A take of the speech lab changed; a deleted one arrives as cancelled. */
  speech(take: SpeechTake): void
  /** A music video changed; a deleted one arrives as cancelled. */
  video(video: VideoState): void
  /** A painted cover candidate changed; a deleted one arrives as cancelled. */
  image(image: ImageState): void
  queue(queue: QueuedJob[]): void
  /** Another large model took the memory, or gave it back. */
  memory(memory: MemoryInfo): void
  /** False while the stream is down; the browser reconnects by itself and a new snapshot follows. */
  connection(open: boolean): void
}

/** Follows the worker live. Returns a function that closes the stream. */
export function subscribe(handlers: EventHandlers): () => void {
  const source = new EventSource(`${apiBase}/api/events`)
  const on = <T>(type: string, handle: (data: T) => void) =>
    source.addEventListener(type, (event) => handle(JSON.parse((event as MessageEvent<string>).data) as T))

  on<StatusSnapshot>('snapshot', (snapshot) => {
    handlers.connection(true)
    handlers.snapshot(snapshot)
  })
  on<SongState>('song', handlers.song)
  on<WorkerInfo>('worker', handlers.worker)
  on<LogEntry>('log', handlers.log)
  on('library', () => handlers.library())
  on<TranscriptionState>('transcription', handlers.transcription)
  on<LyricsState>('lyrics', handlers.lyrics)
  on<QueuedJob[]>('queue', handlers.queue)
  on<VersionState>('version', handlers.version)
  on<StemSetState>('stems', handlers.stems)
  on<SwapState>('swap', handlers.swap)
  on<SpeechTake>('speech', handlers.speech)
  on<VideoState>('video', handlers.video)
  on<ImageState>('image', handlers.image)
  on<MemoryInfo>('memory', handlers.memory)
  source.onerror = () => handlers.connection(false)
  return () => source.close()
}

function json(method: string, body: unknown): RequestInit {
  return { method, body: JSON.stringify(body), headers: { 'Content-Type': 'application/json' } }
}

/** A request whose failure is worth an ApiError: a problem document's detail, or the status. */
export interface LogFilter {
  source: LogSource | null
  level: LogLevel | null
  text: string
  /** The time of the last line shown, for the next older page. */
  before?: string
}

export async function getLogs(filter: LogFilter, limit = 200): Promise<LogPage> {
  const query = new URLSearchParams({ limit: String(limit) })
  if (filter.source) {
    query.set('source', filter.source)
  }
  if (filter.level) {
    query.set('level', filter.level)
  }
  if (filter.text.trim()) {
    query.set('q', filter.text.trim())
  }
  if (filter.before) {
    query.set('before', filter.before)
  }
  return (await send(`/api/logs?${query}`)).json() as Promise<LogPage>
}

/** The plain-text report for a chat: build, engines, and the last day's warnings and errors with their context. */
export async function getLogReport(): Promise<string> {
  return (await send('/api/logs/report')).text()
}

async function send(path: string, init: RequestInit = {}): Promise<Response> {
  let response: Response
  try {
    response = await fetch(`${apiBase}${path}`, init)
  } catch {
    throw new ApiError('network', 0)
  }

  if (!response.ok) {
    const problem = (await response.json().catch(() => null)) as {
      title?: string
      detail?: string
      errors?: Record<string, string[]>
    } | null
    throw new ApiError(
      problem?.detail ?? problem?.title ?? `HTTP ${response.status}`,
      response.status,
      problem?.errors ?? {},
    )
  }
  return response
}
