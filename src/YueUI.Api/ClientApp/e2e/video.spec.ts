import { expect, run, test } from './fakeApi'

test.beforeEach(({ api }) => {
  api.runs = [run('run-1', { title: 'Mein Hund' })]
  api.answers.set('GET /api/songs/run-1/song1/videos', () => [])
  api.answers.set('GET /api/videos/media', () => ({
    installed: true,
    root: '/Users/marcel/Pictures/Tonwerk',
    folders: [
      { name: 'Hund', photos: 42, videos: 6 },
      { name: 'Leer', photos: 0, videos: 0 },
    ],
  }))
  api.answers.set('POST /api/songs/run-1/song1/videos', () => ({
    id: 'v1',
    songId: 'run-1/song1',
    title: 'Mein Hund',
    format: 'landscape',
    effect: 'bars',
    color: null,
    motion: 'none',
    showCover: false,
    showTitle: true,
    media: 'Hund',
    motif: 'Hund am Strand',
    stage: 'queued',
    fraction: 0,
    message: null,
    bytes: null,
    createdAt: '2026-10-12T12:00:00Z',
    updatedAt: '2026-10-12T12:00:00Z',
    finished: false,
  }))
})

test("a photo video takes a folder of the Mac's pictures and a motif instead of the cover", async ({ page, api }) => {
  await page.goto('#/songs')
  await page.getByRole('button', { name: 'Weitere Aktionen' }).first().click()
  await page.getByRole('menuitem', { name: 'Musikvideo' }).click()

  const dialog = page.getByRole('dialog')
  await dialog.getByRole('button', { name: 'Fotos & Videos' }).click()
  // The first folder with pictures is chosen; the photos move themselves, so there is no motion to pick.
  await expect(dialog.getByText('Hund · 42 Fotos, 6 Videos')).toBeVisible()
  await expect(dialog.getByText('Bewegung')).toBeHidden()
  await dialog.getByRole('textbox', { name: 'Motiv' }).fill('Hund am Strand')
  await dialog.getByRole('button', { name: 'Video erstellen' }).click()

  await expect(dialog.getByText('Fotos: Hund')).toBeVisible()
  const form = String(api.callsTo('POST', '/api/songs/run-1/song1/videos')[0]!.body)
  expect(form).toMatch(/name="media"\r\n\r\nHund\r\n/)
  expect(form).toMatch(/name="motif"\r\n\r\nHund am Strand\r\n/)
  expect(form).toMatch(/name="motion"\r\n\r\nnone\r\n/)
  expect(form).toContain('name="shade"')
  expect(form).not.toContain('name="background"')
})
