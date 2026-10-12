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
