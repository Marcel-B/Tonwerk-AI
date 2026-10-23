import { expect, run, song, test } from './fakeApi'

function lora(name: string, model: string | null, trigger: string | null) {
  return { name, label: name, model, trigger, strength: 1, sizeBytes: 80 << 20, modifiedAt: '2026-10-22T12:00:00Z' }
}

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
        loras: true,
        references: 4,
      },
    ],
  }))
  api.answers.set('GET /api/image-loras', () => ({
    folder: '/data/image-loras',
    loras: [
      lora('MetroRose', 'flux2-klein-4b', 'metrorose'),
      lora('RealSkin', 'flux2-klein-4b', null),
      lora('mystery', null, null),
    ],
    suggestions: [],
    fetching: [],
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

test('LoRAs of the chosen model go with the picture at their strength', async ({ page, api }) => {
  await page.goto('#/songs')
  await page.getByRole('button', { name: 'Weitere Aktionen' }).first().click()
  await page.getByRole('menuitem', { name: 'Cover malen' }).click()

  const dialog = page.getByRole('dialog')
  await dialog.getByRole('button', { name: 'MetroRose' }).click()
  await expect(dialog.getByRole('slider', { name: 'Stärke von MetroRose' })).toBeVisible()
  await dialog.getByRole('slider', { name: 'Stärke von MetroRose' }).press('ArrowLeft')
  // The one without a known model is only counted until someone assigns it.
  await expect(dialog.getByRole('button', { name: 'mystery' })).toHaveCount(0)
  await expect(dialog.getByText('1 LoRA(s) ohne bekanntes Modell')).toBeVisible()

  await dialog.getByRole('button', { name: 'Malen' }).click()
  await expect.poll(() => api.callsTo('POST', '/api/songs/run-1/song1/images').length).toBe(1)
  const body = api.callsTo('POST', '/api/songs/run-1/song1/images')[0]!.body as {
    loras: { name: string; strength: number }[]
  }
  expect(body.loras).toEqual([{ name: 'MetroRose', strength: 0.95 }])
})

test('a cover style brings its sentence and pictures, which go with the picture as references', async ({
  page,
  api,
}) => {
  api.answers.set('GET /api/cover-styles', () => [
    {
      id: 7,
      name: 'Nachtzug-Look',
      prompt: 'Neon rain on wet asphalt.',
      model: null,
      loras: [],
      images: [{ id: 'ab'.repeat(16), role: 'figure' }],
      createdAt: '2026-10-22T12:00:00Z',
      updatedAt: '2026-10-22T12:00:00Z',
    },
  ])
  await page.goto('#/songs')
  await page.getByRole('button', { name: 'Weitere Aktionen' }).first().click()
  await page.getByRole('menuitem', { name: 'Cover malen' }).click()

  const dialog = page.getByRole('dialog')
  await expect(dialog.getByText('FLUX.2 Klein 4B schaut sich bis zu 4 Bilder an')).toBeVisible()
  await dialog.getByRole('combobox', { name: 'Cover-Stil' }).click()
  await page.getByRole('option', { name: 'Nachtzug-Look' }).click()
  await expect(dialog.getByRole('textbox')).toHaveValue(/Neon rain on wet asphalt\.$/)

  await dialog.getByRole('button', { name: 'Malen' }).click()
  await expect.poll(() => api.callsTo('POST', '/api/songs/run-1/song1/images').length).toBe(1)
  const body = api.callsTo('POST', '/api/songs/run-1/song1/images')[0]!.body as { references: unknown[] }
  expect(body.references).toEqual([{ source: 'style', id: `7/${'ab'.repeat(16)}`, role: 'figure', strength: null }])
})

test('a candidate opens large with the text it was sent with, to copy and download', async ({ page, api }) => {
  api.answers.set('GET /api/songs/run-1/song1/images', () => [
    {
      id: 'i9',
      songId: 'run-1/song1',
      title: 'Nachtzug',
      modelId: 'flux2-klein-4b',
      modelLabel: 'FLUX.2 Klein 4B',
      prompt: 'Ein Zug bei Nacht',
      paintedPrompt: 'A train at night',
      sentPrompt: 'A train at night. metrorose.',
      seed: 42,
      stage: 'done',
      fraction: 1,
      createdAt: '2026-10-22T12:00:00Z',
      updatedAt: '2026-10-22T12:00:00Z',
      finished: true,
    },
  ])
  await page.goto('#/songs')
  await page.getByRole('button', { name: 'Weitere Aktionen' }).first().click()
  await page.getByRole('menuitem', { name: 'Cover malen' }).click()
  await page.getByRole('button', { name: 'Groß ansehen' }).click()

  const viewer = page.getByRole('dialog', { name: 'Nachtzug' })
  await expect(viewer.getByText('A train at night. metrorose.')).toBeVisible()
  await expect(viewer.getByText('Ein Zug bei Nacht')).toBeVisible()
  await expect(viewer.getByRole('link', { name: 'Herunterladen' })).toHaveAttribute('download', 'Nachtzug 42.jpg')
})

test('a painted cover shows its text from the database', async ({ page, api }) => {
  api.runs = [
    run('run-1', {
      title: 'Nachtzug',
      songs: [song('run-1/song1', { coverUpdatedAt: '2026-10-22T12:00:00Z' })],
    }),
  ]
  api.answers.set('GET /api/songs/run-1/song1/cover/painting', () => ({
    imageId: 'i9',
    modelId: 'flux2-klein-4b',
    modelLabel: 'FLUX.2 Klein 4B',
    prompt: 'Ein Zug bei Nacht',
    paintedPrompt: 'A train at night',
    sentPrompt: 'A train at night',
    seed: 42,
    loras: null,
    references: null,
    createdAt: '2026-10-22T12:00:00Z',
  }))
  await page.goto('#/songs')
  await page.getByRole('button', { name: 'Groß ansehen' }).click()

  const viewer = page.getByRole('dialog', { name: 'Nachtzug' })
  await expect(viewer.getByText('A train at night', { exact: true })).toBeVisible()
  // The sent text equals the English here, so it is shown once.
  await expect(viewer.getByText('Vollständig an das Modell geschickt')).toHaveCount(0)
  await expect(viewer.getByText(/FLUX\.2 Klein 4B · Seed 42/)).toBeVisible()
})
