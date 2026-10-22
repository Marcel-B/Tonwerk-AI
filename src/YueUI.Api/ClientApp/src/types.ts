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
  'singing',
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
  /** Melodies being sung by SoulX-Singer (and the last finished one); null from a server before them. */
  singing?: SingTake[] | null
}

/** The large models besides YuE2 that take turns in the memory. */
export type LargeModel = 'lyrics' | 'voices' | 'speech' | 'singing' | 'images' | 'ace'

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
  /** Tempo, key and how clearly the lyrics are sung, as far as heard yet; null before anything was. */
  traits?: SongTraits | null
}

/** What was heard in a song (Analysis/SongAnalyzer.cs, Timing/LyricsTimer.cs). */
export interface SongTraits {
  /** Beats per minute. */
  tempo: number | null
  /** As the key picker spells it: `C`, `F#m`, `Bb`. */
  key: string | null
  /** `audio` when heard in the song, `score` when only what its score asked for. */
  from: 'audio' | 'score' | null
  /** The share of the lyrics Whisper understood, 0 to 1. */
  clarity: number | null
  /** `vocals` when Whisper heard a vocal stem, `mix` when the whole song (which scores lower). */
  clarityFrom: 'vocals' | 'mix' | null
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

/** A lyrics line with when it is sung (Timing/LyricsAligner.cs). */
export interface TimedLine {
  text: string
  section: string | null
  start: number
  end: number
  /** How many of its words Whisper heard; 0 where the time is only guessed. */
  heard: number
}

export interface LyricsTiming {
  lines: TimedLine[]
  source: 'vocals' | 'mix'
  /** The share of the lyrics' words Whisper heard, 0 to 1. */
  coverage: number
  createdAt: string
}

/** `GET /api/songs/{run}/{song}/timing`: the timing, or how far a song without one is. */
export interface TimingAnswer {
  timing: LyricsTiming | null
  info: { installed: boolean; state: 'none' | 'queued' | 'listening' | 'failed'; message: string | null }
}

/** One style block or setting against the stars of the songs made with it (Library/Insights.cs). */
export interface InsightEntry {
  value: string
  count: number
  average: number
  /** The average pulled towards the overall one, so a value on one song does not lead; the lists are sorted by it. */
  score: number
}

export type InsightKey = 'style' | 'engine' | 'quality' | 'lora' | 'steps' | 'length' | 'instrumental'

export interface InsightGroup {
  key: InsightKey
  entries: InsightEntry[]
}

/** `GET /api/insights`: what the rated songs were made with. */
export interface InsightReport {
  rated: number
  average: number
  groups: InsightGroup[]
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

/** How much a thinking model thinks before a draft (Lyrics/LyricsReasoning.cs); left out, as set in LM Studio. */
export type LyricsReasoning = 'off' | 'low' | 'medium' | 'high' | 'on'

/** A language model LM Studio, or Claude in the cloud, offers for drafts (Lyrics/LyricsModels.cs). */
export interface LyricsModel {
  id: string
  name: string
  /** Size on disk, roughly what it takes in memory; null when the server does not say. */
  sizeBytes: number | null
  /** Already loaded in LM Studio, so a draft uses it without loading. */
  loaded: boolean
  /** Can read a photo, as LM Studio says; null when the server does not say. */
  vision: boolean | null
  /** Runs at Anthropic: the draft neither waits for nor stops a song, and the text leaves the Mac. */
  cloud: boolean
  /** What a local model is suggested for (Lyrics:Roles): final lyrics or fast ideas. */
  role?: 'quality' | 'ideas' | null
  /** The memory it needs and the Mac that lists it lacks, in GB. */
  needsGb?: number | null
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
  /** The Mac it was asked for: `local` or a helper's id; null for whichever is free. */
  machine?: string | null
  /** The name of the Mac that runs it, once one does. */
  ranOn?: string | null
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
  | 'song'
  | 'version'
  | 'stems'
  | 'swap'
  | 'video'
  | 'cover'
  | 'image'
  | 'transcription'
  | 'take'
  | 'singtake'
  | 'singvoice'

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
  /** The Mac it was asked for: `local` or a helper's id; null for whichever is free. */
  machine?: string | null
  /** The name of the Mac that runs it, once one does. */
  ranOn?: string | null
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
  /** The Mac it was asked for: `local` or a helper's id; null for whichever is free. */
  machine?: string | null
  /** The name of the Mac that runs it, once one does. */
  ranOn?: string | null
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
  /** The Mac it was asked for: `local` or a helper's id; null for whichever is free. */
  machine?: string | null
  /** The name of the Mac that runs it, once one does. */
  ranOn?: string | null
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
  /** The Mac to convert on: `local` or a helper's id; null for whichever is free. */
  machine?: string | null
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

export type SpeechStage = 'queued' | 'loading' | 'speaking' | 'cleaning' | 'done' | 'failed' | 'cancelled'

/** One text spoken by one model, or one recording cleaned by one; live changes arrive as `speech` events. */
export interface SpeechTake {
  id: string
  modelId: string
  modelLabel: string
  /** Empty for a cleaned recording. */
  text: string
  /** The uploaded recording's name when the take cleans it (Sprache säubern); null for a spoken take. */
  source: string | null
  /** How much of the recording is cleaned, 0 to 1, while it is. */
  progress: number | null
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
  /** The Mac it was asked for: `local` or a helper's id; null for whichever is free. */
  machine?: string | null
  /** The name of the Mac that runs it, once one does. */
  ranOn?: string | null
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
  /** The models that clean a speech recording of noise. */
  cleanModels: SpeechModelInfo[]
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
  /** The English Klein was given, where Claude translated the prompt. */
  paintedPrompt: string | null
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
  /** The Mac it was asked for: `local` or a helper's id; null for whichever is free. */
  machine?: string | null
  /** The name of the Mac that runs it, once one does. */
  ranOn?: string | null
  /** The LoRAs it was painted with. */
  loras?: ImageLoraUsed[] | null
  /** The reference pictures it was painted with, in the order the prompt names them. */
  references?: ImageReferenceUsed[] | null
}

/** What a reference picture stands for: looked at for its look, its figure, its place, or painted over. */
export type ReferenceRole = 'style' | 'figure' | 'place' | 'start'

/** A reference a new picture asks for (Images/ImageModels.cs `ImageReferenceChoice`). */
export interface ImageReferenceChoice {
  /** `style` (id `<styleId>/<pictureId>`), `image` (a candidate), `cover` (the song's own) or `upload`. */
  source: 'style' | 'image' | 'cover' | 'upload'
  id?: string | null
  role: ReferenceRole
  /** Only for a start picture: how much of it stays, 0.1 to 0.9. */
  strength?: number | null
}

export interface ImageReferenceUsed {
  role: ReferenceRole
  label: string
  strength?: number | null
}

export interface CoverStyleImage {
  id: string
  role: ReferenceRole
}

/** A look kept under a name (Images/CoverStyles.cs): text, model, LoRAs and up to four reference pictures. */
export interface CoverStyle {
  id: number
  name: string
  prompt: string | null
  model: string | null
  loras: ImageLoraChoice[]
  images: CoverStyleImage[]
  createdAt: string
  updatedAt: string
}

/** A LoRA for the image models (Images/ImageLoras.cs `ImageLoraInfo`). */
export interface ImageLora {
  /** The file name without `.safetensors`; what a picture asks for. */
  name: string
  label: string
  /** The image model it was trained for; null until someone says. */
  model: string | null
  /** Words added to the end of the description when it paints. */
  trigger: string | null
  strength: number
  sizeBytes: number
  modifiedAt: string
  /** The Hugging Face repository it came from. */
  source?: string | null
}

/** One of Krea's LoRAs, offered to fetch from Hugging Face. */
export interface ImageLoraSuggestion {
  name: string
  label: string
  model: string
  trigger: string
  strength: number
  repo: string
  file: string
  license: string
}

export interface ImageLoraList {
  /** Where the files lie on the Mac; files put there by hand are listed too. */
  folder: string
  loras: ImageLora[]
  suggestions: ImageLoraSuggestion[]
  fetching: string[]
}

export interface ImageLoraChoice {
  name: string
  strength: number
}

export interface ImageLoraUsed {
  name: string
  label: string
  strength: number
  trigger: string | null
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
  /** Some Mac here can paint with it: this one, or a helper that says so. */
  available?: boolean
  /** Why this Mac cannot (runner missing, too little memory), even where a helper can. */
  unavailable?: string | null
  /** It can write a short title into the picture. */
  lettering?: boolean
  /** The least memory a Mac needs to be offered it, in GB. */
  minSystemGb?: number | null
  /** Its runner applies LoRAs (FLUX 3 Klein's does not). */
  loras?: boolean
  /** How many pictures it looks at (FLUX.2 Klein, FLUX 3); 0 for none. */
  references?: number
  /** It paints over one start picture instead (Z-Image, Krea 2). */
  startImage?: boolean
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
  /** Whether Claude translates a description into English before painting. */
  translates: boolean
  /** flux3-mlx's Python, for FLUX 3 Klein. */
  flux3Python?: string
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
  /** The Mac to compose on: `local` or a helper's id; null for whichever is free. */
  machine?: string | null
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
  /** The Mac it was asked for: `local` or a helper's id; null for whichever is free. */
  machine?: string | null
  /** The name of the Mac that runs it, once one does. */
  ranOn?: string | null
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

/** A written song sketch planned by Claude (Sketches/SketchModels.cs `SketchPlan`). */
export interface SketchPlan {
  title: string
  style: string
  tempo: number
  beatsPerBar: number
  key: string
  sections: SketchSection[]
  notes: string[]
}

export interface SketchSection {
  name: string
  tag: string
  parts: string[]
  energy: number
  lyrics: string[]
  bars: SketchBar[]
}

export interface SketchBar {
  chords: string
  vocal: string | null
  lyric: string | null
  ins: string | null
  parts: string[] | null
}

export type SketchStage = 'queued' | 'waiting' | 'writing' | 'done' | 'failed'

export interface SketchJob {
  id: string
  title: string
  stage: SketchStage
  message: string | null
  source: string
  instruction: string | null
  model: string | null
  plan: SketchPlan | null
  inputTokens: number | null
  outputTokens: number | null
  createdAt: string
  updatedAt: string
  finished: boolean
}

export interface SketchSummary {
  id: string
  title: string
  stage: SketchStage
  message: string | null
  createdAt: string
  updatedAt: string
  bars: number | null
  seconds: number | null
}

/** Something the server repaired in a plan or doubts about it; `code` picks the words (i18n `sketchWarning_*`). */
export interface SketchWarning {
  code: 'barLength' | 'note' | 'chord' | 'chordSimplified' | 'syllables' | 'key' | 'tooLong'
  bar: number | null
  detail: string | null
}

/** A finished sketch with what was made of its plan; one still in the works has only `job`. */
export interface SketchResult {
  job: SketchJob
  abc?: string
  lyrics?: string
  request?: SongRequest
  /** What ACE-Step's page can take of the plan: lyrics, style, tempo, key, metre and length, no melody. */
  ace?: AceSongRequest
  warnings?: SketchWarning[]
  bars?: number
  seconds?: number
}

export interface SketchInfo {
  configured: boolean
  /** The default model. */
  model: string
  /** The Claude models to choose from, the default first. */
  models: ClaudeModelChoice[]
  /** LM Studio's models, which plan on the Mac; empty while LM Studio does not answer. */
  local?: LyricsModel[] | null
}

/** A Claude model a picker offers (Sketches/SketchModels.cs). */
export interface ClaudeModelChoice {
  id: string
  name: string
}

/** A Mac a job can run on (`GET /api/machines`): this one and the helpers it hands jobs to. */
export interface MachineInfo {
  id: string
  name: string
  local: boolean
  reachable: boolean
  holder: string | null
  busy: boolean
  engines: string[]
  message?: string | null
}

/** A message on the writing page (Writing/WritingModels.cs). */
export interface WritingMessage {
  role: 'user' | 'assistant'
  text: string
  at: string
  /** The model that wrote an answer. */
  model?: string | null
  /** An answer stopped before its end; what came is kept. */
  stopped?: boolean
}

export type WritingStage = 'waiting' | 'loading' | 'writing' | 'done' | 'stopped' | 'failed'

/** The answer being written; it arrives as `writing` events, the whole text so far each time. */
export interface WritingReply {
  id: string
  chatId: string
  stage: WritingStage
  model: string
  reasoning?: LyricsReasoning | null
  text: string
  /** The model is thinking before (or between) its words. */
  thinking: boolean
  message?: string | null
  updatedAt: string
  finished: boolean
}

export interface WritingChat {
  id: string
  title: string
  model?: string | null
  reasoning?: LyricsReasoning | null
  messages: WritingMessage[]
  createdAt: string
  updatedAt: string
  reply?: WritingReply | null
}

export interface WritingChatSummary {
  id: string
  title: string
  model?: string | null
  messages: number
  updatedAt: string
}

export interface WritingOverview {
  /** Newest first. */
  chats: WritingChatSummary[]
  reply?: WritingReply | null
  /** The model a chat writes with when none is chosen. */
  model: string
  /** The model kept in memory between answers. */
  loaded?: string | null
}

/** A note to sing, in seconds (SungNote in Singing/SingingModels.cs); `lyric` a word, a syllable or a hold mark. */
export interface SungNote {
  start: number
  end: number
  pitch: number
  lyric?: string | null
}

/** A voice SoulX-Singer sings with: a bundled one or one made from a recording on the singing page (`own`). */
export interface SingingVoiceInfo {
  id: string
  label: string
  language: string
  own: boolean
  /** What the voice sings in its recording. */
  text?: string | null
  seconds?: number | null
  /** The lowest and highest note heard (MIDI). */
  low?: number | null
  high?: number | null
  createdAt?: string | null
}

/** What the server made of a recording turned into a voice. */
export interface SingingVoiceMade {
  seconds: number
  recordedSeconds: number
  notes: number
  held: number
  unsung: number
  leftover: number
  text: string
  /** 0 to 1; near 1 the recording clipped. */
  peak: number
  low: number
  high: number
}

export interface SingingVoiceCreated {
  voice: SingingVoiceInfo
  heard: SingingVoiceMade
}

export type SingStage = 'queued' | 'loading' | 'singing' | 'done' | 'failed' | 'cancelled'

/** A melody with words sung by SoulX-Singer; live changes arrive as `singing` events. */
export interface SingTake {
  id: string
  title: string
  stage: SingStage
  message?: string | null
  progress?: number | null
  voiceId: string
  voiceLabel: string
  songId?: string | null
  fileName?: string | null
  lyrics: string
  pitchShift: number
  autoShift: boolean
  noteCount: number
  tempoBpm?: number | null
  /** Notes that got a syllable, notes left without one, words left over. */
  sung?: number | null
  unsung?: number | null
  leftover?: number | null
  seconds?: number | null
  loadSeconds?: number | null
  singSeconds?: number | null
  peakMemoryGb?: number | null
  /** The Mac it was asked for: `local` or a helper's id; null for whichever is free. */
  machine?: string | null
  /** The name of the Mac that runs it, once one does. */
  ranOn?: string | null
  createdAt: string
  updatedAt: string
}

/** The singing page: whether SoulX-Singer is there, its voices and takes (`GET /api/singing`). */
export interface SingingInfo {
  installed: boolean
  modelDownloaded: boolean
  python: string
  source: string
  model: string
  nltkData: string
  voices: SingingVoiceInfo[]
  takes: SingTake[]
}

/** A file's, song's or take's melody for the note editor. */
export interface SingingMelody {
  notes: SungNote[]
  tempoBpm: number
  lyrics: string
  title: string
  part?: string | null
}

/** What a note sings: SoulX-Singer's note type 2 a word's first note, 3 held over, 1 none. */
export interface AlignedNote {
  word?: string | null
  type: number
  phonemes: string
}

export interface SingingAligned {
  notes: AlignedNote[]
  leftover: number
}
