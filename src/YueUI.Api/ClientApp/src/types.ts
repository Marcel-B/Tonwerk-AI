// Mirrors the API's JSON (src/YueUI.Api: Worker/WorkerModels.cs, Library/SongLibrary.cs, GenerateRequest.cs) by hand.

export type WorkerStatus = 'stopped' | 'starting' | 'ready'

/** The worker's stages, in order; the last three end a song. */
export type Stage = 'queued' | 'planning' | 'tokens' | 'synth' | 'decode' | 'ready' | 'failed' | 'cancelled'

export interface WorkerInfo {
  status: WorkerStatus
  /** Some song is queued or running. */
  busy: boolean
  /** The YuE Studio app is open, with a worker (and a model) of its own. */
  studioRunning: boolean
  lastError: string | null
  /**
   * The last worker that started took YueUI's extra fields (sampling, full-quality steps, songs over six minutes);
   * null until one has started, false when a YuE Studio update left them without effect.
   */
  extensions: boolean | null
}

export interface SongState {
  /** `run/songN`, as in the library. */
  id: string
  run: string
  title: string
  index: number
  seed: number | null
  stage: Stage
  detail: string
  /** Progress of the current stage, 0–1. */
  fraction: number
  engine: string | null
  seconds: number | null
  quality: Quality | null
  message: string | null
  updatedAt: string
  /** The stages so far, each with when it began, oldest first; the last one is `stage`. */
  stages: StageTime[]
  /** A render from saved tokens: no planning or composing ahead of it. */
  render: boolean
  /** The voice chosen in the form: a version with it is queued once the song is ready. */
  voice: SongVoice | null
  finished: boolean
}

export interface StageTime {
  stage: Stage
  startedAt: string
}

export interface LogEntry {
  time: string
  level: 'info' | 'warning' | 'error' | 'stderr'
  message: string
}

/** A source of the collected log (`GET /api/logs`); the server's `LogSources.All`. */
export const logSources = [
  'server',
  'worker',
  'queue',
  'lyrics',
  'voices',
  'speech',
  'images',
  'ace',
  'video',
  'export',
  'logic',
  'backup',
  'push',
  'update',
] as const
export type LogSource = (typeof logSources)[number]
export type LogLevel = 'debug' | 'info' | 'warning' | 'error'

/** A line of the collected log. */
export interface LogLine {
  time: string
  source: LogSource
  level: LogLevel
  message: string
  exception?: string | null
}

/** Lines newest first; `more` when older ones match too. */
export interface LogPage {
  entries: LogLine[]
  more: boolean
}

export interface StatusSnapshot {
  worker: WorkerInfo
  songs: SongState[]
  log: LogEntry[]
  transcriptions: TranscriptionState[]
  lyrics: LyricsState | null
  /** Songs being sung with another voice, and the last one that finished. */
  versions: VersionState[]
  /** Songs, renders, lyrics drafts and transcriptions waiting for the memory, in the order they start. */
  queue: QueuedJob[]
  /** Queue:BundleWindow in seconds: how long songs may pass a waiting lyrics draft or voice version. */
  bundleWindowSeconds: number | null
  /** Songs being split into stems, and the last set that finished. */
  stems: StemSetState[] | null
  /** The speech lab's takes in the works, in the order they are spoken, and the last one that finished. */
  speech: SpeechTake[] | null
  /** Uploaded recordings being sung with another voice, and the last one that finished. */
  swaps: SwapState[] | null
  /** Music videos being rendered or waiting for it, and the last one that finished. */
  videos: VideoState[] | null
  /** Painted covers in the works (and the last finished one); null from a server before them. */
  images?: ImageState[] | null
  /** Which large model besides YuE2 holds the memory; the server decides it (Memory/ModelMemory.cs). */
  memory?: MemoryInfo | null
  /** ACE-Step's songs in the works (and the last finished one); null from a server before them. */
  ace?: AceJob[] | null
}

/** The large models besides YuE2 that take turns in the memory. */
export type LargeModel = 'lyrics' | 'voices' | 'speech' | 'images' | 'ace'

export interface MemoryInfo {
  holder: LargeModel | null
}

export type JobKind = 'song' | 'render' | 'lyrics' | 'transcription'

/** A job waiting in the server's queue (Queue/QueuedJob.cs) until no other model holds the memory. */
export interface QueuedJob {
  id: string
  kind: JobKind
  /**
   * The run's title (empty when the worker is to name it), the song's for a render, the keywords for lyrics, the
   * recording's file name for a transcription.
   */
  title: string
  createdAt: string
  /** The song a render is for. */
  songId: string | null
  batch: number | null
  quality: 'draft' | 'full' | null
  /** A lyrics job that revises lyrics rather than drafting new ones. */
  revision: boolean
  /** The voice a new run's songs are sung with once they are ready. */
  voiceLabel: string | null
  /** What a transcription transcribes; null for other jobs and a server from before transcriptions were queued. */
  transcriptionTask?: TranscriptionTask | null
}

/** melody-full: the Vocal and Ins melodies; melody-vocal: only the sung one. */
export type TranscriptionTask = 'melody-full' | 'melody-vocal'

/** A recording going through SheetSage2 (Worker/WorkerModels.cs). */
export interface TranscriptionState {
  id: string
  fileName: string
  task: TranscriptionTask
  /** queued: only in the answer to an upload that waits in the queue. */
  stage: 'queued' | 'starting' | 'progress' | 'done' | 'failed' | 'cancelled'
  /** 0–1, null while SheetSage2 only says it is still busy. */
  fraction: number | null
  detail: string
  abc: string | null
  warnings: string[]
  /** The folder in the transcription list, once done. */
  result: string | null
  message: string | null
  /** busy, no_env, afconvert, abc_error or crash */
  code: string | null
  updatedAt: string
  finished: boolean
}

/** A finished transcription on disk (Library/TranscriptionLibrary.cs). */
export interface TranscriptionInfo {
  id: string
  sourceName: string
  task: TranscriptionTask | null
  createdAt: string | null
  warnings: string[]
  /** What the folder takes on disk. */
  bytes: number
}

export interface TranscriptionList {
  /** SheetSage2's environment exists (YuE Studio installs it). */
  installed: boolean
  items: TranscriptionInfo[]
}

export type Quality = 'draft' | 'full'

export type Cot = 'full' | 'melody' | 'off'

export type Engines = 'gpu' | 'gpu+ane'

export interface GenerateRequest {
  style: string
  lyrics: string
  title: string
  batch: number
  quality: Quality
  cot: Cot
  /** Null for a random seed. */
  seed: number | null
  instrumental: boolean
  /** Null leaves the choice to the worker (Neural Engine for full quality). */
  engines: Engines | null
  draftSteps: number | null
  /** Upper limit of song tokens (25 per second of audio, 200–9000); null for the worker's 9000. */
  maxTokens: number | null
  /** A score in ABC notation instead of the model's own plan; needs `cot` "full" or "melody". */
  abc: string | null
  /** Synthesis steps at full quality, 1–64; null for the model's 32. */
  fullSteps: number | null
  abcSampling: SamplingOverrides | null
  semanticSampling: SamplingOverrides | null
  /** Sings each finished song again with this voice. */
  voice: VersionRequest | null
  /** A LoRA from the server's `loras/` folder that changes the sound; null for none. */
  lora: string | null
  /** How strongly the LoRA applies, 0–3; null for 1 (as trained). */
  loraStrength: number | null
}

/** A LoRA for YuE2's acoustic path (Loras/LoraLibrary.cs). */
export interface LoraInfo {
  /** The file name without `.safetensors`. */
  name: string
  sizeBytes: number
  modifiedAt: string
  /** The word its trainer put in every caption; it belongs in the style. */
  triggerWord: string | null
  steps: number | null
  songs: number | null
  minutes: number | null
}

export interface LoraList {
  /** Where the server looks for them, so a file can be put there by hand. */
  folder: string
  loras: LoraInfo[]
}

/** Changes to one of the model's sampling settings; a missing value keeps the model's. */
export interface SamplingOverrides {
  temperature?: number
  topP?: number
  topK?: number
  repetitionPenalty?: number
  penaltyWindow?: number
}

export interface SongInfo {
  id: string
  index: number
  seed: number | null
  quality: Quality | null
  seconds: number | null
  hasAudio: boolean
  hasScore: boolean
  /** The tokens are saved, so the song can be synthesized again (a draft at full quality). */
  canRender: boolean
  /** What the song folder takes on disk: audio, tokens and the worker's intermediate files. */
  bytes: number
  /** One to five stars given in this app, null while not rated. */
  rating: number | null
  /** The song sung with other voices, oldest first. */
  versions: VersionState[]
  /** When the song's own cover was chosen, null while it has the drawn one. */
  coverUpdatedAt: string | null
  /** A note written to the song in this app, null while there is none. */
  note: string | null
  /** The formats the song has a finished music video in; empty while it has none. */
  videoFormats: VideoFormat[]
  /** `ace` for a song ACE-Step made; absent or null for YuE2's. */
  engine?: 'ace' | null
}

/**
 * What a song was generated with, from its request.json (Library/SongLibrary.cs); null where the file does not say,
 * e.g. for a song YuE Studio made.
 */
export interface SongRequest {
  title: string
  style: string
  lyrics: string
  instrumental: boolean | null
  quality: Quality | null
  cot: Cot | null
  seed: number | null
  engines: Engines | null
  draftSteps: number | null
  maxTokens: number | null
  abc: string | null
  fullSteps: number | null
  abcSampling: SamplingOverrides | null
  semanticSampling: SamplingOverrides | null
  /** The LoRA the song was made with, as the worker noted it beside the song. */
  lora: string | null
  loraStrength: number | null
}

export interface RunInfo {
  /** The run folder's name; stays when the run is renamed. */
  id: string
  /** The title given in this app if there is one, else the worker's. */
  title: string
  /** The worker's title, which the folder is named after; an emptied title returns to it. */
  originalTitle: string
  createdAt: string | null
  style: string
  lyrics: string
  songs: SongInfo[]
  /** What the whole run folder takes on disk. */
  bytes: number
}

/** A playlist (PlaylistEndpoints.cs): song ids (`run/songN`) in the order they play. */
export interface PlaylistInfo {
  id: number
  name: string
  songIds: string[]
}

/**
 * A prompt template (TemplateEndpoints.cs): what a good song was made with, under a name. The settings are the form's
 * own (form.ts `TemplateSettings`), which the server keeps without reading; they are checked when applied.
 */
export interface PromptTemplate {
  id: number
  name: string
  settings: Record<string, unknown>
  createdAt: string
  updatedAt: string
}

/** What a lyrics draft is written in (Lyrics/LyricsLanguage.cs). */
export type LyricsLanguage = 'english' | 'german'

/** A language model LM Studio offers for drafts (Lyrics/LyricsModels.cs). */
export interface LyricsModel {
  id: string
  name: string
  /** Size on disk, roughly what it takes in memory; null when the server does not say. */
  sizeBytes: number | null
  /** Already loaded in LM Studio, so a draft uses it without loading. */
  loaded: boolean
  /** Can read a photo, as LM Studio says; null when the server does not say. */
  vision: boolean | null
}

/** What the lyrics model picker offers; `default` is the configured model, used when a draft names none. */
export interface LyricsModels {
  default: string
  models: LyricsModel[]
}

/** The last lyrics draft from the local language model (Worker/WorkerModels.cs); it arrives as `lyrics` events. */
export interface LyricsState {
  id: string
  /** queued only in the answer to the request, while the draft waits for the memory. */
  stage: 'queued' | 'writing' | 'done' | 'failed'
  lyrics: string | null
  /** Why it failed, e.g. LM Studio's own message. */
  message: string | null
  updatedAt: string
  finished: boolean
}

/** The volume the songs are written to (LibraryEndpoints.cs). */
/** A MIDI file read back into a score (MidiScore in LogicEndpoints.cs). */
export interface MidiScore {
  abc: string
  /** What could not be carried over, e.g. a track it ignored. */
  warnings: string[]
}

/** How the last Nextcloud backup went (Backup/CloudBackup.cs). */
export interface BackupRun {
  startedAt: string
  finishedAt: string
  success: boolean
  archive: string | null
  files: number
  bytes: number
  error: string | null
  scheduled: boolean
}

/** The Nextcloud backup; `done` of `total` files while it runs. */
export interface BackupStatus {
  configured: boolean
  running: boolean
  done: number
  total: number
  last: BackupRun | null
  nextRun: string | null
  files: boolean
}

export interface StorageInfo {
  freeBytes: number
  totalBytes: number
}

/** Where the disk space goes; Storage/StorageScanner.cs. */
export interface StorageOverview {
  volumes: StorageVolume[]
  categories: StorageCategory[]
  /** What failed or cancelled versions, stems, swaps and videos still take; `cleanupStorage('failed')` removes them. */
  failed: number
}

/** `/` is the Mac's own disk, `/Volumes/…` another one (an external SSD after deploy/move-to-volume.sh). */
export interface StorageVolume {
  mount: string
  free: number
  total: number
}

export type StorageGroup = 'work' | 'app' | 'models'

export type StorageItemKind =
  'song' | 'version' | 'stems' | 'swap' | 'video' | 'cover' | 'image' | 'transcription' | 'take'

export interface StorageCategory {
  key: string
  group: StorageGroup
  bytes: number
  /** How many entries it has, where it has entries. */
  count: number
  volume: string | null
  /** Files without a database entry, older than a day; `cleanupStorage('orphans')` removes them. */
  orphaned: number
  /** Its largest entries, at most 30. */
  largest: StorageItem[]
}

export interface StorageItem {
  kind: StorageItemKind
  /** The entry's own id; for a song and a cover the song id. */
  id: string
  songId: string | null
  title: string
  /** Untranslated: a voice, a video format, a model, a file name. */
  detail: string | null
  bytes: number
  createdAt: string | null
}

export type StorageCleanupTarget = 'stream' | 'images' | 'videos' | 'orphans' | 'failed'

export interface StorageCleanup {
  bytes: number
  removed: number
}

/** Whether voices can be managed (Seed-VC or ChangeMyVoice) and songs sung with them (the separator as well); VoiceEndpoints.cs. */
export interface VoiceInfo {
  voicesConfigured: boolean
  conversionConfigured: boolean
  /** The separator alone: songs can be split into stems, with or without voices. */
  stemsConfigured: boolean
}

/** A separation model (Voices/StemModels.cs). */
export interface StemModel {
  id: string
  name: string
  /** What it separates: vocals, instrumental, karaoke, 4stem, 6stem or drums. */
  task: string | null
  /** The stems it makes. */
  stems: string[] | null
  /** Audio length divided by computing time: 0.3 means a four-minute song takes about thirteen. */
  realtimeFactor: number | null
  /** The model this server separates with when none is chosen. */
  isDefault: boolean
}

/** One stem of a separation (Voices/StemModels.cs). */
export interface StemFile {
  /** What the model called it: vocals, instrumental, drums, vocals_dry, … */
  name: string
  file: string
  seconds: number
  /** The loudness outline, 0–1 on one scale for the whole set; null when the server could not read it. */
  peaks: number[] | null
}

export type StemStage = 'queued' | 'separating' | 'done' | 'failed' | 'cancelled'

/** A song split into stems (Voices/StemModels.cs); live changes arrive as `stems` events. */
export interface StemSetState {
  id: string
  /** `run/songN`; empty for an uploaded file. */
  songId: string
  /** The run's title, or an upload's file name without its ending. */
  title: string
  /** Separated from an uploaded file rather than a song of the library. */
  upload: boolean
  model: string
  dereverb: boolean
  stage: StemStage
  message: string | null
  stems: StemFile[]
  createdAt: string
  updatedAt: string
  finished: boolean
}

/** A reference voice (Voices/VoiceModels.cs). */
export interface ReferenceVoice {
  id: string
  label: string
  /** How long the stored recording is; the service keeps at most 25 seconds. */
  seconds: number
  createdAt: string | null
}

/** 1920×1080 for YouTube, or 1080×1920 for Shorts, Reels and TikTok. */
export type VideoFormat = 'landscape' | 'portrait'

/** A spectrum analyzer, the waveform, or only the still picture. */
export type VideoEffect = 'bars' | 'wave' | 'none'

/** What moves behind the cover: drifting particles, a plasma ball's lightning, or nothing. */
export type VideoMotion = 'none' | 'particles' | 'plasma'

/** A music video of a song (Video/VideoModels.cs); live changes arrive as `video` events. */
export interface VideoState {
  id: string
  /** `run/songN` */
  songId: string
  title: string
  format: VideoFormat
  effect: VideoEffect
  /** The analyzer's colour (`#rrggbb`), null for Tonwerk's emerald. */
  color: string | null
  motion: VideoMotion
  showCover: boolean
  showTitle: boolean
  /** A photo video's folder of pictures (under the server's `Montage:MediaRoot`); null for a cover video. */
  media: string | null
  /** What the pictures should show; CLIP picks the ones that fit best. */
  motif: string | null
  stage: 'queued' | 'rendering' | 'done' | 'failed' | 'cancelled'
  /** How much is rendered, 0 to 1. */
  fraction: number
  message: string | null
  bytes: number | null
  createdAt: string
  updatedAt: string
  finished: boolean
}

/** A folder a photo video can take its pictures from (Video/MontageBuilder.cs). */
export interface MediaFolder {
  name: string
  photos: number
  videos: number
}

/** `GET /api/videos/media`: whether photo videos are installed, and the folders under the root. */
export interface MediaInfo {
  installed: boolean
  root: string
  folders: MediaFolder[]
}

export type VersionStage = 'queued' | 'separating' | 'converting' | 'mixing' | 'done' | 'failed' | 'cancelled'

/**
 * An uploaded recording sung with another voice (Voices/VoiceModels.cs): a vocal track straight through Seed-VC, or a
 * whole song (`separate`) separated first and mixed back. Live changes arrive as `swap` events.
 */
export interface SwapState {
  id: string
  fileName: string
  voiceId: string
  voiceLabel: string
  semiToneShift: number
  strength: number
  diffusionSteps: number
  separate: boolean
  keepReverb: boolean
  stemModel: string | null
  stage: VersionStage
  fraction: number
  estimatedSeconds: number
  message: string | null
  createdAt: string
  updatedAt: string
  finished: boolean
}

/** A song sung with another voice (Voices/VoiceModels.cs); live changes arrive as `version` events. */
export interface VersionState {
  id: string
  /** `run/songN` */
  songId: string
  title: string
  voiceId: string
  voiceLabel: string
  semiToneShift: number
  strength: number
  diffusionSteps: number
  keepReverb: boolean
  /** The separation model that separated the vocals; null for versions made before it was kept. */
  stemModel: string | null
  stage: VersionStage
  /** Progress of the conversion, 0–1, estimated from the time the voice service expects; 1 once that has passed. */
  fraction: number
  /** How long the voice service expects the conversion to take; 0 before it started. */
  estimatedSeconds: number
  message: string | null
  createdAt: string
  updatedAt: string
  finished: boolean
}

/** A voice chosen in the form, with its settings (SongVoice in Voices/VoiceModels.cs). */
export interface SongVoice {
  voiceId: string
  voiceLabel: string
  semiToneShift: number
  strength: number
  diffusionSteps: number
  keepReverb: boolean
}

/** What a version is made with (VersionRequest in Voices/VoiceModels.cs). */
export interface VersionRequest {
  voiceId: string
  /** −24 to 24; under the song's accompaniment only whole octaves stay in key. */
  semiToneShift: number
  /** How much of the reference's timbre comes through, 0–1. */
  strength: number
  diffusionSteps: number
  /** Mixes the original's reverb back in. */
  keepReverb: boolean
}

/** A text-to-speech model the speech lab offers (SpeechModelInfo in Speech/SpeechModels.cs). */
export interface SpeechModelInfo {
  id: string
  label: string
  /** The Hugging Face repository mlx-audio loads. */
  repo: string
  /** Roughly what the first take downloads. */
  downloadGb: number
  clones: boolean
  license: string
  note: string
  /** Already in the model cache, so the first take does not download. */
  downloaded: boolean
}

/** A recorded voice to clone (Speech/SpeechModels.cs). */
export interface SpeechVoice {
  id: string
  label: string
  /** What is said in the recording; several models need it. */
  transcript: string
  seconds: number
  createdAt: string
}

export type SpeechStage = 'queued' | 'loading' | 'speaking' | 'done' | 'failed' | 'cancelled'

/** One text spoken by one model; live changes arrive as `speech` events. */
export interface SpeechTake {
  id: string
  modelId: string
  modelLabel: string
  text: string
  voiceId: string | null
  voiceLabel: string | null
  stage: SpeechStage
  message: string | null
  /** How long the spoken audio is. */
  seconds: number | null
  /** Loading the model, including a first download. */
  loadSeconds: number | null
  speakSeconds: number | null
  peakMemoryGb: number | null
  createdAt: string
  updatedAt: string
  finished: boolean
}

/** Everything the lab page draws (SpeechInfo in Speech/SpeechModels.cs). */
export interface SpeechInfo {
  /** The Python environment with mlx-audio exists. */
  installed: boolean
  /** Where the server looks for it. */
  python: string
  /** Recordings need ffmpeg on the server. */
  ffmpegInstalled: boolean
  models: SpeechModelInfo[]
  voices: SpeechVoice[]
  /** Newest first. */
  takes: SpeechTake[]
}

/** A cover painted by FLUX.2 Klein (Images/ImageModels.cs `ImageState`): a candidate until it is taken. */
export interface ImageState {
  id: string
  /** `run/songN` */
  songId: string
  title: string | null
  modelId: string
  modelLabel: string
  prompt: string
  seed: number
  stage: 'queued' | 'loading' | 'painting' | 'done' | 'failed' | 'cancelled'
  message: string | null
  /** Of the painting steps. */
  fraction: number
  loadSeconds: number | null
  paintSeconds: number | null
  peakMemoryGb: number | null
  createdAt: string
  updatedAt: string
  finished: boolean
}

export interface ImageModelInfo {
  id: string
  label: string
  downloadGb: number
  memoryGb: number
  license: string
  /** Whether its pictures may go on a released song. */
  commercial: boolean
  /** Its repository needs a Hugging Face token of an account that accepted the licence. */
  gated: boolean
  downloaded: boolean
  tokenFound: boolean
}

/** `GET /api/audio-midi`: whether Basic Pitch is set up, and where the server looks for it. */
export interface AudioMidiInfo {
  installed: boolean
  python: string
}

/** How a track becomes MIDI (`AudioMidiSettings`). */
export interface AudioMidiSettings {
  /** One note at a time, as a voice sings; Basic Pitch hears overtones as notes of their own. */
  mono: boolean
  /** Starts and ends on the sixteenth grid of the tempo. */
  quantize: boolean
  /** Keeps slides and vibrato as pitch bends; only with `mono`. */
  bends: boolean
  /** An upload's tempo; a stem takes its song's. */
  tempo?: number | null
}

export interface ImageInfo {
  installed: boolean
  python: string
  models: ImageModelInfo[]
}

/** ACE-Step's tasks: a new song, a new style over a library song, or a stretch of one made again. */
export type AceTask = 'text2music' | 'cover' | 'repaint'

/**
 * A song asked of ACE-Step (Ace/AceModels.cs `AceSongRequest`); null leaves a value to the model: the LM writes lyrics
 * and picks tempo, key, metre, length and language, the DiT takes its preset's steps, guidance and shift.
 */
export interface AceSongRequest {
  title?: string | null
  caption: string
  lyrics?: string | null
  instrumental?: boolean | null
  language?: string | null
  duration?: number | null
  bpm?: number | null
  key?: string | null
  timeSignature?: number | null
  seed?: number | null
  batch?: number | null
  lmModel?: string | null
  ditModel?: string | null
  steps?: number | null
  guidance?: number | null
  shift?: number | null
  solver?: string | null
  lmTemperature?: number | null
  lmCfgScale?: number | null
  lmTopP?: number | null
  lmTopK?: number | null
  negativePrompt?: string | null
  enrichCaption?: boolean | null
  adapter?: string | null
  adapterScale?: number | null
  task?: AceTask | null
  /** `run/songN` a cover or repaint starts from. */
  source?: string | null
  coverStrength?: number | null
  repaintStart?: number | null
  repaintEnd?: number | null
}

export type AceStage =
  | 'queued'
  | 'loading'
  | 'writing'
  | 'composing'
  | 'synthesizing'
  | 'decoding'
  | 'saving'
  | 'done'
  | 'failed'
  | 'cancelled'

/** A song (or batch) being made by ACE-Step (Ace/AceModels.cs `AceJob`). */
export interface AceJob {
  id: string
  title: string
  request: AceSongRequest
  seed: number
  stage: AceStage
  /** Of the whole job, roughly. */
  fraction: number
  message: string | null
  /** The run folder the songs went into, once done. */
  run: string | null
  seconds: number | null
  createdAt: string
  updatedAt: string
  finished: boolean
}

/** `GET /api/ace`: whether acestep.cpp is built, which GGUF files are there, and the jobs, newest first. */
export interface AceInfo {
  installed: boolean
  ready: boolean
  binaries: string
  models: string
  lmModels: string[]
  ditModels: string[]
  adapters: string[]
  jobs: AceJob[]
}
