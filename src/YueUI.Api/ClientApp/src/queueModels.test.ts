import { beforeAll, describe, expect, test } from 'vitest'
import { locale, t } from './i18n'
import { holderOf, speechWaitReason, waitReason, type VoiceWork } from './queueModels'
import type { QueuedJob, TranscriptionState, WorkerInfo } from './types'

const idle: WorkerInfo = { status: 'ready', busy: false, studioRunning: false, lastError: null, extensions: true }
const now = Date.parse('2026-10-06T12:00:00Z')
const ago = (minutes: number) => new Date(now - minutes * 60_000).toISOString()

function job(id: string, kind: QueuedJob['kind'], createdAt = ago(1)): QueuedJob {
  return { id, kind, title: id, createdAt, songId: null, batch: null, quality: null, revision: false, voiceLabel: null }
}

beforeAll(() => {
  locale.value = 'en'
})

describe('holderOf', () => {
  const running = { finished: false } as TranscriptionState

  test("takes the server's holder over the worker", () => {
    expect(holderOf({ ...idle, busy: true }, 'voices')).toBe('voice')
    expect(holderOf(idle, 'images')).toBe('image')
    expect(holderOf(idle, 'ace')).toBe('ace')
  })

  test('else YuE2 while busy, then a transcription, then none', () => {
    expect(holderOf({ ...idle, busy: true }, null, [running])).toBe('yue')
    expect(holderOf(idle, null, [running])).toBe('transcription')
    expect(holderOf(idle, null, [{ ...running, finished: true }])).toBeNull()
  })
})

describe('waitReason', () => {
  const window = 20 * 60

  test('a draft at the front lets songs pass while YuE2 works, counting down the window', () => {
    const jobs = [job('draft', 'lyrics', ago(5)), job('song', 'song')]
    expect(waitReason(jobs, 0, 'yue', [], window, now)).toBe(t('waitYueBundle', { time: '15:00' }))
    expect(waitReason(jobs, 0, 'yue', [], window, now + 15 * 60_000)).toBe(t('waitYue'))
    expect(waitReason(jobs, 1, null, [], window, now)).toBe(t('waitBehindDraft'))
  })

  test('a version waiting past the window holds back songs', () => {
    const versions: VoiceWork[] = [{ stage: 'queued', createdAt: ago(30), finished: false }]
    expect(waitReason([job('song', 'song')], 0, 'yue', versions, window, now)).toBe(t('waitVersionFirst'))
    expect(waitReason([job('song', 'song')], 0, 'yue', [], window, now)).toBe(t('waitStarting'))
  })

  test('nothing starts while a take speaks or a cover is painted', () => {
    expect(waitReason([job('song', 'song')], 0, 'speech', [], window, now)).toBe(t('waitSpeech'))
    expect(waitReason([job('draft', 'lyrics')], 0, 'image', [], window, now)).toBe(t('waitImage'))
    expect(waitReason([job('song', 'song')], 0, 'ace', [], window, now)).toBe(t('waitAce'))
  })

  test('transcriptions keep a lane of their own', () => {
    const jobs = [job('tr1', 'transcription'), job('song', 'song'), job('tr2', 'transcription')]
    expect(waitReason(jobs, 1, null, [], window, now)).toBe(t('waitStarting'))
    expect(waitReason(jobs, 2, null, [], window, now)).toBe(t('waitTurn'))
    expect(waitReason(jobs, 0, null, [], window, now, true)).toBe(t('waitTranscription'))
  })
})

describe('speechWaitReason', () => {
  test('the first take names the holder, the others wait their turn', () => {
    expect(speechWaitReason(0, 'voice')).toBe(t('waitVoice'))
    expect(speechWaitReason(0, null)).toBe(t('waitStarting'))
    expect(speechWaitReason(1, null)).toBe(t('waitTurn'))
  })

  test("ACE-Step's songs wait the same way, for the lab too", () => {
    expect(speechWaitReason(0, 'ace', 'ace')).toBe(t('waitTurn'))
    expect(speechWaitReason(0, 'speech', 'ace')).toBe(t('waitSpeech'))
    expect(speechWaitReason(0, 'ace')).toBe(t('waitAce'))
  })
})
