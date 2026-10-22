import type { SingingInfo, SingTake } from '../src/types'
import { expect, run, song, test } from './fakeApi'

function take(fields: Partial<SingTake> = {}): SingTake {
  return {
    id: 't1',
    title: 'Twinkle',
    stage: 'queued',
    voiceId: 'en',
    voiceLabel: 'Beispielstimme Englisch',
    lyrics: '',
    pitchShift: 0,
    autoShift: false,
    noteCount: 3,
    createdAt: '2026-10-21T12:00:00Z',
    updatedAt: '2026-10-21T12:00:00Z',
    ...fields,
  }
}

const info: SingingInfo = {
  installed: true,
  modelDownloaded: true,
  python: '/singing/env/bin/python',
  source: '/singing/src',
  model: '/singing/models',
  nltkData: '/singing/nltk',
  voices: [
    { id: 'en', label: 'Beispielstimme Englisch', language: 'English', own: false },
    {
      id: 'mine',
      label: 'Meine Stimme',
      language: 'English',
      own: true,
      text: 'twinkle twinkle',
      seconds: 6.5,
      low: 55,
      high: 67,
    },
  ],
  takes: [],
}

test.beforeEach(({ api }) => {
  api.runs = [run('run-1', { title: 'Mein Hund', songs: [song('run-1/song1', { hasScore: true })] })]
  api.answers.set('GET /api/singing', () => info)
  api.answers.set('GET /api/machines', () => [
    { id: 'local', name: 'Mac mini', local: true, reachable: true, holder: null, busy: false, engines: ['singing'] },
    { id: 'oi', name: 'oi-o-mat', local: false, reachable: true, holder: null, busy: false, engines: ['singing'] },
  ])
  api.answers.set('POST /api/singing/align', () => ({ notes: [], leftover: 0 }))
  api.answers.set('POST /api/singing', () => take())
})

test('notes drawn in the editor get syllables and are sung on the chosen Mac', async ({ page, api }) => {
  await page.goto('#/sing')
  await expect(page.getByText('Meine Stimme')).toBeVisible()

  // Three taps on the grid, a beat apart, each note then named by typing.
  const roll = page.getByTestId('sing-roll')
  const grid = roll.locator('.grid-area')
  await roll.scrollIntoViewIfNeeded()
  const box = (await roll.boundingBox())!
  for (const beat of [0, 1, 2]) {
    // Past the keys (46 px), a beat (56 px) apart, a few rows down from the visible top.
    await page.mouse.click(box.x + 46 + beat * 56 + 10, box.y + 100)
  }
  await expect(grid.locator('.note')).toHaveCount(3)
  const syllable = page.getByRole('textbox', { name: 'Silbe' })
  await grid.locator('.note').first().click()
  await syllable.fill('twin kle star')
  await syllable.press('Enter')
  await expect(grid.locator('.note').first()).toContainText('twin')
  await expect(grid.locator('.note').last()).toContainText('star')

  await page.getByRole('combobox', { name: 'Rechner' }).click()
  await page.getByRole('option', { name: 'oi-o-mat' }).click()
  await page.getByRole('button', { name: 'Singen', exact: true }).click()

  await expect(page.getByText('Twinkle', { exact: true })).toBeVisible()
  const form = String(api.callsTo('POST', '/api/singing')[0]!.body)
  expect(form).toContain('"lyric":"twin"')
  expect(form).toContain('"lyric":"star"')
  expect(form).toMatch(/name="machine"\r\n\r\noi\r\n/)
  expect(form).toMatch(/name="voice"\r\n\r\nen\r\n/)

  // Progress arrives as events, and the queue lists the take with its wait.
  await api.emit(
    'singing',
    take({ stage: 'singing', progress: 0.5, updatedAt: '2026-10-21T12:00:05Z', ranOn: 'oi-o-mat' }),
  )
  const listed = page.locator('.take')
  await expect(listed.getByText('Singt 50 %')).toBeVisible()
  await expect(listed.getByText('Beispielstimme Englisch · auf oi-o-mat')).toBeVisible()
})

test('a library song brings its melody and lyrics into the editor', async ({ page, api }) => {
  api.answers.set('POST /api/singing/melody', () => ({
    notes: [
      { start: 0, end: 0.5, pitch: 60, lyric: 'Hel' },
      { start: 0.5, end: 1, pitch: 62, lyric: 'lo' },
    ],
    tempoBpm: 120,
    lyrics: 'Hello',
    title: 'Mein Hund',
    part: 'Vocal',
  }))
  await page.goto('#/sing')
  await page.getByRole('combobox', { name: 'Aus der Bibliothek' }).click()
  await page.getByRole('option', { name: /Mein Hund/ }).click()
  await expect(page.getByTestId('sing-roll').locator('.note')).toHaveCount(2)
  await expect(page.getByRole('textbox', { name: 'Titel' })).toHaveValue('Mein Hund')
  expect(String(api.callsTo('POST', '/api/singing/melody')[0]!.body)).toContain('run-1/song1')
})
