import { describe, expect, it } from 'vitest'
import { tabAudioSupported, tabRecordingName } from './tabAudio'

class FakeRecorder {}
const capture = { getDisplayMedia: () => Promise.resolve() }

describe('tabAudioSupported', () => {
  it('is offered in desktop Chromium', () => {
    expect(tabAudioSupported({ mediaDevices: capture, userAgentData: {} }, FakeRecorder as never)).toBe(true)
  })

  it('is not offered in Safari or Firefox, which share a screen without its sound', () => {
    expect(tabAudioSupported({ mediaDevices: capture }, FakeRecorder as never)).toBe(false)
  })

  it('is not offered on a phone, which has no screen capture', () => {
    expect(tabAudioSupported({ mediaDevices: {}, userAgentData: {} }, FakeRecorder as never)).toBe(false)
    expect(tabAudioSupported({ userAgentData: {} }, FakeRecorder as never)).toBe(false)
  })

  it('is not offered without MediaRecorder', () => {
    expect(tabAudioSupported({ mediaDevices: capture, userAgentData: {} }, undefined as never)).toBe(false)
  })
})

describe('tabRecordingName', () => {
  it('names the recording by its local start time', () => {
    expect(tabRecordingName(new Date(2026, 9, 14, 9, 5, 3), 'webm')).toBe('Tab 2026-10-14 09-05-03.webm')
  })
})
