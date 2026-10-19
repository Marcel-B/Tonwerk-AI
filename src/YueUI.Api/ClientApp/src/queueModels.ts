import { formatDuration, t } from './i18n'
import type { LargeModel, QueuedJob, TranscriptionState, WorkerInfo } from './types'

/**
 * The large models that take turns in the memory (Queue/JobQueue.cs): YuE2 for songs and renders, the lyrics model in
 * LM Studio, separation plus Seed-VC for voice versions and stems, and the speech lab's text-to-speech models. A
 * transcription is small and runs beside YuE2, but inside the worker, so the other three wait for it. FLUX.2 Klein
 * paints covers in the same memory (Images/ImageMaker.cs), and so does ACE-Step, the second song model
 * (Ace/AceComposer.cs); a music video is ffmpeg on the CPU and runs beside them all.
 */
export type Model = 'yue' | 'lyrics' | 'voice' | 'speech' | 'singing' | 'image' | 'ace' | 'transcription' | 'video'

export function modelOf(job: QueuedJob): Model {
  return job.kind === 'lyrics' ? 'lyrics' : job.kind === 'transcription' ? 'transcription' : 'yue'
}

/** SheetSage2 transcribes one recording at a time, in the worker. */
export function transcribing(transcriptions: TranscriptionState[]): boolean {
  return transcriptions.some((tr) => !tr.finished)
}

/**
 * A voice version or a stem separation: both wait in VoiceConverter's one queue and hold the memory as the "voice"
 * model while they run.
 */
export interface VoiceWork {
  stage: string
  createdAt: string
  finished: boolean
}

/** The server's name for each large model besides YuE2 (Memory/ModelMemory.cs), as the queue's tiles name them. */
const largeModels: Record<LargeModel, Model> = {
  lyrics: 'lyrics',
  voices: 'voice',
  speech: 'speech',
  singing: 'singing',
  images: 'image',
  ace: 'ace',
}

/**
 * Which model holds the memory now, or null while none works. The server decides it for the large models besides
 * YuE2 (ModelMemory), so nothing here guesses it from their stages; YuE2 and a transcription come from the worker.
 */
export function holderOf(
  worker: WorkerInfo,
  memory: LargeModel | null,
  transcriptions: TranscriptionState[] = [],
): Model | null {
  if (memory !== null) {
    return largeModels[memory]
  }
  if (worker.busy) {
    return 'yue'
  }
  return transcribing(transcriptions) ? 'transcription' : null
}

/**
 * Why a job still waits, in the words of the queue's own rules (JobQueue.TryStartAsync, MayPassLocked,
 * VersionOverdue, TranscriptionMayGoLocked): the model that holds the memory, the bundling window, or the job before
 * it. Transcriptions keep a lane of their own, so songs and drafts count their places without them.
 * @param windowSeconds Queue:BundleWindow; null for a server from before it was sent.
 * @param transcriptionRuns A transcription is in the worker, which holds back drafts and the next transcription.
 */
export function waitReason(
  jobs: QueuedJob[],
  index: number,
  holder: Model | null,
  versions: VoiceWork[],
  windowSeconds: number | null,
  now: number,
  transcriptionRuns = false,
): string {
  const job = jobs[index]!
  if (holder === 'speech') {
    // While a take speaks, JobQueue starts nothing (ModelMemory).
    return t('waitSpeech')
  }
  if (holder === 'image') {
    // Likewise while a cover is painted.
    return t('waitImage')
  }
  if (holder === 'ace') {
    return t('waitAce')
  }
  if (holder === 'singing') {
    return t('waitSinging')
  }
  const left = (since: string) => (windowSeconds ?? 0) - (now - new Date(since).getTime()) / 1000
  const oldest = versions.filter((v) => v.stage === 'queued').sort((a, b) => a.createdAt.localeCompare(b.createdAt))[0]
  const versionFirst = oldest !== undefined && windowSeconds !== null && left(oldest.createdAt) <= 0

  if (job.kind === 'transcription') {
    if (holder === 'lyrics') {
      return t('waitLyrics')
    }
    if (holder === 'voice') {
      return t('waitVoice')
    }
    if (versionFirst) {
      return t('waitVersionFirst')
    }
    if (transcriptionRuns) {
      return t('waitTranscription')
    }
    const ahead = jobs.slice(0, index)
    if (ahead.some((j) => j.kind === 'transcription')) {
      return t('waitTurn')
    }
    // Like songs, it passes a draft only while YuE2 is loaded anyway (within the window, which is not checked here).
    if (holder !== 'yue' && ahead.some((j) => j.kind === 'lyrics')) {
      return t('waitBehindDraft')
    }
    return t('waitStarting')
  }

  const lane = jobs.filter((j) => j.kind !== 'transcription')
  const place = lane.indexOf(job)

  if (modelOf(job) === 'lyrics') {
    if (holder === 'yue') {
      // Songs go ahead of a draft at the front while YuE2 is loaded anyway, until the draft has waited the window.
      const rest = place === 0 ? left(job.createdAt) : 0
      return rest > 0 ? t('waitYueBundle', { time: formatDuration(rest) }) : t('waitYue')
    }
    if (holder === 'voice') {
      return t('waitVoice')
    }
    if (holder === 'lyrics') {
      return t('waitDraft')
    }
    if (transcriptionRuns) {
      return t('waitTranscription')
    }
    return place === 0 ? t('waitStarting') : t('waitTurn')
  }

  if (holder === 'lyrics') {
    return t('waitLyrics')
  }
  if (holder === 'voice') {
    return t('waitVoice')
  }
  if (versionFirst) {
    return t('waitVersionFirst')
  }
  if (place > 0 && modelOf(lane[0]!) === 'lyrics') {
    return t('waitBehindDraft')
  }
  return place === 0 ? t('waitStarting') : t('waitTurn')
}

/**
 * Why a take of the speech lab still waits (SpeechLab.WaitForMemoryAsync): its takes are spoken one at a time, in the
 * order they were asked for, each once YuE2, the lyrics model and the voices have let go of the memory. ACE-Step's
 * songs wait the same way (AceComposer), as `own`.
 * @param index Its place among the takes in the works, the one being spoken included.
 */
export function speechWaitReason(index: number, holder: Model | null, own: Model = 'speech'): string {
  if (holder === own || index > 0) {
    return t('waitTurn')
  }
  switch (holder) {
    case 'yue':
      return t('waitYue')
    case 'lyrics':
      return t('waitLyrics')
    case 'voice':
      return t('waitVoice')
    case 'speech':
      return t('waitSpeech')
    case 'image':
      return t('waitImage')
    case 'ace':
      return t('waitAce')
    case 'singing':
      return t('waitSinging')
    case 'transcription':
      return t('waitTranscription')
    default:
      return t('waitStarting')
  }
}
