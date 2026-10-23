import { describe, expect, it } from 'vitest'
import type { StoredSession } from './api'
import { defaultFormState, formFrom } from './options'
import { readSettings, sessionOfSong, settingsOf, type PageState } from './sessions'

function page(overrides: Partial<PageState> = {}): PageState {
  return {
    form: defaultFormState(),
    outputName: 'Neon Night',
    stems: undefined,
    stemParts: { bass: false, drums: false },
    tracks: null,
    ...overrides,
  }
}

describe('formFrom', () => {
  it('takes what fits and the defaults for the rest', () => {
    const form = formFrom({ countIn: 2, bass: 'Walking', harmonyKey: 'F#m', vocalOctave: -1, swing: 'viel', extra: 1 })
    expect(form.countIn).toBe(2)
    expect(form.bass).toBe('Walking')
    expect(form.harmonyKey).toBe('F#m')
    expect(form.vocalOctave).toBe(-1)
    expect(form.swing).toBe(0)
    expect('extra' in form).toBe(false)
  })

  it('reads the old drum switch', () => {
    expect(formFrom({ drums: true }).drums).toBe('FourOnTheFloor')
    expect(formFrom(null)).toEqual(defaultFormState())
  })
})

describe('settingsOf', () => {
  it('keeps what another client wrote and the tracks until a conversion', () => {
    const before = { form: { countIn: 4, appOnly: true }, muted: ['Bass'], fromApp: 1 }
    const settings = settingsOf(page(), before)
    expect(settings.fromApp).toBe(1)
    expect(settings.muted).toEqual(['Bass'])
    expect((settings.form as Record<string, unknown>).appOnly).toBe(true)
    expect((settings.form as Record<string, unknown>).countIn).toBe(0)
    expect('stems' in settings).toBe(false)
  })

  it('writes this song’s tracks only', () => {
    const settings = settingsOf(
      page({
        stems: null,
        tracks: {
          ids: ['Vocal', 'Bass'],
          muted: ['Bass'],
          solo: [],
          mixer: { master: 0.8, tracks: { VOCAL: { volume: 1.5, pan: 0 }, OTHER: { volume: 0.1, pan: 1 } } },
          sounds: { BASS: { patch: { engine: 'fm' }, preset: 'Sub' }, OTHER: { patch: {}, preset: null } },
          instruments: { Vocal: 3, Other: 4 },
        },
      }),
      null,
    )
    expect(settings.stems).toBeNull()
    expect(settings.mixer).toEqual({ master: 0.8, tracks: { VOCAL: { volume: 1.5, pan: 0 } } })
    expect(settings.sounds).toEqual({ Vocal: null, Bass: { patch: { engine: 'fm' }, preset: 'Sub' } })
    expect(settings.instruments).toEqual({ Vocal: 3, Bass: null })
  })
})

describe('readSettings', () => {
  it('drops what has the wrong shape', () => {
    const settings = readSettings({
      form: 'kaputt',
      stems: 5,
      muted: ['Bass', 3],
      sounds: { Bass: { patch: {}, preset: 7 }, Vocal: null, Ins: 'x' },
      instruments: { Bass: 2, Vocal: 'x', Ins: null },
      mixer: { tracks: [] },
    })
    expect(settings.form).toBeUndefined()
    expect('stems' in settings).toBe(false)
    expect(settings.muted).toEqual(['Bass'])
    expect(settings.sounds).toEqual({ Bass: { patch: {}, preset: null }, Vocal: null })
    expect(settings.instruments).toEqual({ Bass: 2, Ins: null })
    expect(settings.mixer).toEqual({ master: 1, tracks: {} })
    expect(readSettings(null)).toEqual({})
  })
})

describe('sessionOfSong', () => {
  it('takes the newest of the song', () => {
    const session = (id: number, songId: string | null, updatedAt: string): StoredSession => ({
      id,
      name: `S${id}`,
      songId,
      scoreFile: null,
      audioFile: null,
      settings: {},
      createdAt: updatedAt,
      updatedAt,
    })
    const sessions = [
      session(1, 'run/song1', '2026-10-23T10:00:00Z'),
      session(2, 'run/song1', '2026-10-23T12:00:00Z'),
      session(3, 'run/song2', '2026-10-23T13:00:00Z'),
    ]
    expect(sessionOfSong(sessions, 'run/song1')?.id).toBe(2)
    expect(sessionOfSong(sessions, 'run/song3')).toBeNull()
  })
})
