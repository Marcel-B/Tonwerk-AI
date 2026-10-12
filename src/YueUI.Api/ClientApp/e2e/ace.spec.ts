import type { AceJob } from '../src/types'
import { expect, run, song, test } from './fakeApi'

function job(fields: Partial<AceJob> = {}): AceJob {
  return {
    id: 'a1',
    title: 'Wüste Nacht',
    request: { caption: 'dark synthwave', task: 'text2music' },
    seed: 7,
    stage: 'queued',
    fraction: 0,
    message: null,
    run: null,
    seconds: null,
    createdAt: '2026-10-12T12:00:00Z',
    updatedAt: '2026-10-12T12:00:00Z',
    finished: false,
    ...fields,
  }
}

test.beforeEach(({ api }) => {
  api.runs = [run('run-1', { title: 'Mein Hund' })]
  api.answers.set('GET /api/ace', () => ({
    installed: true,
    ready: true,
    binaries: '/ace/build',
    models: '/ace/models',
    lmModels: ['acestep-5Hz-lm-4B-Q8_0.gguf'],
    ditModels: ['acestep-v15-turbo-Q8_0.gguf', 'acestep-v15-sft-Q8_0.gguf'],
    adapters: [],
    jobs: [],
  }))
  api.answers.set('POST /api/ace', () => job())
})

test('a new song sends the description and what was fixed, the rest left to the model', async ({ page, api }) => {
  await page.goto('#/ace')
  await page.getByRole('textbox', { name: 'Beschreibung' }).fill('dark synthwave, female vocals')
  await page.getByRole('textbox', { name: 'Titel' }).fill('Wüste Nacht')
  await page.getByRole('button', { name: 'Komponieren' }).click()

  await expect(page.getByText('Wüste Nacht').last()).toBeVisible()
  const body = api.callsTo('POST', '/api/ace')[0]!.body as Record<string, unknown>
  expect(body).toMatchObject({
    title: 'Wüste Nacht',
    caption: 'dark synthwave, female vocals',
    lyrics: null,
    duration: null,
    bpm: null,
    task: 'text2music',
    batch: 1,
  })

  // Progress arrives as events; a finished job links to its songs.
  api.runs = [
    run('20261012-120000-Wueste-Nacht', {
      songs: [song('20261012-120000-Wueste-Nacht/song1', { engine: 'ace', seed: 7 })],
    }),
    ...api.runs,
  ]
  await api.emit('ace', job({ stage: 'composing', fraction: 0.4, updatedAt: '2026-10-12T12:01:00Z' }))
  await expect(page.getByRole('listitem').getByText('Komponiert 40 %')).toBeVisible()
  await api.emit('library', null)
  await api.emit(
    'ace',
    job({
      stage: 'done',
      fraction: 1,
      finished: true,
      run: '20261012-120000-Wueste-Nacht',
      updatedAt: '2026-10-12T12:02:00Z',
    }),
  )
  await expect(page.getByRole('link', { name: 'Song 1 · Seed 7' })).toBeVisible()
})

test('a cover picks a library song and drops what the source decides', async ({ page, api }) => {
  await page.goto('#/ace')
  await page.getByRole('button', { name: 'Cover' }).click()
  await expect(page.getByText('Länge, Tempo und Tonart kommen vom Ausgangssong.')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Komponieren' })).toBeDisabled()

  await page.getByRole('combobox', { name: 'Ausgangssong' }).click()
  await page.getByRole('option', { name: 'Mein Hund · Song 1' }).click()
  await page.getByRole('textbox', { name: 'Beschreibung' }).fill('jazz piano')
  await page.getByRole('button', { name: 'Komponieren' }).click()

  await expect.poll(() => api.callsTo('POST', '/api/ace').length).toBe(1)
  expect(api.callsTo('POST', '/api/ace')[0]!.body).toMatchObject({
    task: 'cover',
    source: 'run-1/song1',
    caption: 'jazz piano',
    lmModel: null,
    bpm: null,
  })
})
