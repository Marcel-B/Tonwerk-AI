import { expect, run, test } from './fakeApi'

test.beforeEach(({ api }) => {
  api.runs = [run('run-1', { title: 'Nachtzug', style: 'German, dark synthwave, 110 BPM' })]
  api.answers.set('GET /api/images', () => ({
    installed: true,
    python: '/env/bin/python',
    translates: false,
    models: [
      {
        id: 'flux2-klein-4b',
        label: 'FLUX.2 Klein 4B',
        downloadGb: 8,
        memoryGb: 9,
        license: 'Apache 2.0',
        commercial: true,
        gated: false,
        downloaded: true,
        tokenFound: true,
        available: true,
        lettering: false,
      },
    ],
  }))
  api.answers.set('GET /api/songs/run-1/song1/images', () => [])
  api.answers.set('POST /api/songs/run-1/song1/images', () => ({
    id: 'i1',
    songId: 'run-1/song1',
    prompt: '',
    model: 'flux2-klein-4b',
    modelLabel: 'FLUX.2 Klein 4B',
    seed: 1,
    stage: 'queued',
    fraction: 0,
    createdAt: '2026-10-22T12:00:00Z',
    updatedAt: '2026-10-22T12:00:00Z',
    finished: false,
  }))
})

test('cover blocks go into the description, a medium replaces the painting', async ({ page, api }) => {
  await page.goto('#/songs')
  await page.getByRole('button', { name: 'Weitere Aktionen' }).first().click()
  await page.getByRole('menuitem', { name: 'Cover malen' }).click()

  const dialog = page.getByRole('dialog')
  const description = dialog.getByRole('textbox')
  await expect(description).toHaveValue(/^A square painting/)
  // The song's style suggests blocks first.
  await expect(dialog.getByRole('tab', { name: 'Zum Song' })).toBeVisible()
  await dialog.getByRole('button', { name: '80er Synthwave' }).click()
  await dialog.getByRole('tab', { name: 'Coverart' }).click()
  await dialog.getByRole('button', { name: 'Bleistiftskizze' }).click()
  await expect(description).toHaveValue(/^A square picture, .*graphite pencil sketch.*1980s synthwave look/)
  await expect(dialog.getByRole('button', { name: 'Bleistiftskizze' })).toHaveAttribute('aria-pressed', 'true')

  await dialog.getByRole('button', { name: 'Fotografie' }).click()
  await expect(description).not.toHaveValue(/pencil/)
  await expect(description).toHaveValue(/Shot as a photograph/)

  await dialog.getByRole('button', { name: 'Malen' }).click()
  await expect.poll(() => api.callsTo('POST', '/api/songs/run-1/song1/images').length).toBe(1)
  const body = api.callsTo('POST', '/api/songs/run-1/song1/images')[0]!.body as { prompt: string }
  expect(body.prompt).toContain('Shot as a photograph')
})
