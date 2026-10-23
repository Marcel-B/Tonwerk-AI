import type { StemSetState } from '../src/types'
import { expect, run, song, test } from './fakeApi'

function stems(id: string, createdAt: string, names: string[], stage: StemSetState['stage'] = 'done'): StemSetState {
  return {
    id,
    songId: 'run-1/song1',
    title: 'Nachtzug',
    upload: false,
    model: id === 'old' ? 'htdemucs' : 'mel-roformer',
    dereverb: names.includes('vocals_dry'),
    stage,
    message: null,
    stems: names.map((name) => ({ name, file: `${name}.flac`, seconds: 120, peaks: null })),
    createdAt,
    updatedAt: createdAt,
    finished: stage === 'done',
  }
}

test.beforeEach(({ api }) => {
  api.runs = [run('run-1', { title: 'Nachtzug', songs: [song('run-1/song1', { hasScore: true })] })]
  api.answers.set('GET /api/voice', () => ({
    voicesConfigured: false,
    conversionConfigured: false,
    stemsConfigured: true,
  }))
  api.answers.set('GET /api/instruments', () => [])
  api.answers.set('GET /api/instruments/assignments', () => ({}))
  api.answers.set('GET /api/logic/presets', () => [])
  api.answers.set('POST /api/logic/convert', () => ({ success: false, score: null, midiBase64: null, diagnostics: [] }))
  api.answers.set('GET /api/logic/sessions', () => [])
})

test("a song opened again goes on with its session's options", async ({ page, api }) => {
  api.answers.set('GET /api/logic/synths/tracks', () => [])
  api.answers.set('GET /api/logic/synths/presets', () => [])
  api.answers.set('GET /api/logic/synths/mixer', () => ({ settings: null, updatedAt: null }))
  api.answers.set('GET /api/logic/sessions', () => [
    {
      id: 7,
      name: 'Nachtzug · Song 1',
      songId: 'run-1/song1',
      scoreFile: null,
      audioFile: null,
      settings: { form: { countIn: 2, bass: 'Walking' }, outputName: 'Nachtzug-Mix' },
      createdAt: '2026-10-23T10:00:00Z',
      updatedAt: '2026-10-23T10:00:00Z',
    },
  ])
  await page.goto('#/logic/run-1/song1')

  await expect(page.getByRole('combobox', { name: /^Nachtzug · Song 1 ·/ })).toBeVisible()
  await expect(page.getByLabel('Name der Sitzung')).toHaveValue('Nachtzug · Song 1')
  await expect.poll(() => api.callsTo('POST', '/api/logic/convert').length).toBeGreaterThan(0)
  const form = String(api.callsTo('POST', '/api/logic/convert').at(-1)!.body)
  expect(form).toContain('"countIn":{"bars":2')
  expect(form).toContain('"pattern":"Walking"')
})

test("the song's newest stems with vocals go into the Logic project", async ({ page, api }) => {
  api.answers.set('GET /api/stems', () => [
    stems('drums', '2026-10-03T10:00:00Z', ['drums', 'bass']),
    stems('new', '2026-10-02T10:00:00Z', ['vocals', 'vocals_dry', 'instrumental']),
    stems('old', '2026-10-01T10:00:00Z', ['vocals', 'instrumental']),
  ])
  await page.goto('#/logic/run-1/song1')

  await expect(page.getByText('Stems im Logic-Projekt')).toBeVisible()
  // PrimeVue's Select is named after its value, as the song's is.
  const choice = page.getByRole('combobox', { name: /^mel-roformer vom/ })
  await expect(choice).toBeVisible()
  await expect(page.getByText('Gesang und trockener Gesang kommen')).toBeVisible()
  await choice.click()
  // A set without vocals has nothing for the project's vocal tracks.
  await expect(page.getByRole('option')).toHaveText(['Ohne Stems', /^mel-roformer/, /^htdemucs/])
})

test('without stems the button leads to the voices page with the song picked', async ({ page }) => {
  await page.goto('#/logic/run-1/song1')

  await expect(page.getByText('Für diesen Song gibt es noch keine Stems mit Gesang.')).toBeVisible()
  await expect(page.getByRole('combobox', { name: 'Ohne Stems' })).toHaveCount(0)
  await page.getByRole('button', { name: 'Stems erzeugen' }).click()
  await expect(page).toHaveURL(/#\/voices\/run-1\/song1$/)
})

test("bass and drums come from the song's stems once asked for", async ({ page, api }) => {
  api.answers.set('GET /api/audio-midi', () => ({ installed: true, python: '' }))
  api.answers.set('GET /api/stems', () => [
    stems('new', '2026-10-02T10:00:00Z', ['vocals', 'vocals_dry', 'instrumental']),
    stems('old', '2026-10-01T10:00:00Z', ['vocals', 'drums', 'bass', 'other']),
  ])
  await page.goto('#/logic/run-1/song1')

  await expect(page.getByText('Aus den Stems als MIDI')).toBeVisible()
  await page.getByLabel('Bass aus dem Bass-Stem statt aus den Akkorden').check()
  await page.getByRole('button', { name: 'Konvertieren' }).click()

  // The set picked for the vocals has no bass, so the one that has it goes along.
  await expect.poll(() => api.callsTo('POST', '/api/logic/convert').length).toBeGreaterThan(0)
  const form = String(api.callsTo('POST', '/api/logic/convert').at(-1)!.body)
  expect(form).toMatch(/name="bassStems"\r\n\r\nold/)
  expect(form).not.toContain('drumStems')
})

test('without bass or drum stems the page says which models make them', async ({ page, api }) => {
  api.answers.set('GET /api/audio-midi', () => ({ installed: true, python: '' }))
  api.answers.set('GET /api/stems', () => [stems('new', '2026-10-02T10:00:00Z', ['vocals', 'instrumental'])])
  await page.goto('#/logic/run-1/song1')

  await expect(page.getByText(/z\. B\. BS-RoFormer-SW/)).toBeVisible()
  await expect(page.getByText('Aus den Stems als MIDI')).toHaveCount(0)
})

test('a converted song is saved as a session, and a muted track with it', async ({ page, api }) => {
  api.answers.set('GET /api/logic/synths/tracks', () => [])
  api.answers.set('GET /api/logic/synths/presets', () => [])
  api.answers.set('GET /api/logic/synths/mixer', () => ({ settings: null, updatedAt: null }))
  const voice = (id: string, kind: string, pitch: number) => ({
    id,
    displayName: id,
    kind,
    notes: [{ startTicks: 0, durationTicks: 480, noteNumber: pitch, velocity: 90 }],
  })
  api.answers.set('POST /api/logic/convert', () => ({
    success: true,
    midi: null,
    diagnostics: [],
    score: {
      ticksPerQuarterNote: 480,
      title: 'Nachtzug',
      tempoBpm: 120,
      timeSignatures: [{ startTicks: 0, numerator: 4, denominator: 4 }],
      keySignatures: [],
      sections: [],
      voices: [voice('Vocal', 'Melody', 64), voice('Bass', 'Bass', 40)],
      chords: [],
      lengthTicks: 1920,
      countInTicks: 0,
      durationSeconds: 2,
    },
  }))
  const stored = {
    id: 9,
    name: 'Nachtzug · Song 1',
    songId: 'run-1/song1',
    scoreFile: null,
    audioFile: null,
    settings: {},
    createdAt: '2026-10-23T10:00:00Z',
    updatedAt: '2026-10-23T10:00:00Z',
  }
  api.answers.set('POST /api/logic/sessions', () => stored)
  api.answers.set('PUT /api/logic/sessions/9', () => stored)
  await page.goto('#/logic/run-1/song1')

  await expect.poll(() => api.callsTo('POST', '/api/logic/sessions').length).toBe(1)
  const created = String(api.callsTo('POST', '/api/logic/sessions')[0]!.body)
  expect(created).toMatch(/name="song"\r\n\r\nrun-1\/song1/)
  expect(created).toMatch(/name="name"\r\n\r\nNachtzug · Song 1/)
  await expect(page.getByText('Wird automatisch gespeichert')).toBeVisible()

  await page.locator('#preview-track-1').uncheck()
  await expect
    .poll(() => api.callsTo('PUT', '/api/logic/sessions/9').map((call) => JSON.stringify(call.body)))
    .toContainEqual(expect.stringContaining('"muted":["Bass"]'))
})
