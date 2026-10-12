import type { StorageCategory, StorageOverview } from '../src/types'
import { expect, run, song, test } from './fakeApi'

const GB = 1e9
const MB = 1e6

function part(
  key: string,
  group: StorageCategory['group'],
  bytes: number,
  fields: Partial<StorageCategory> = {},
): StorageCategory {
  return { key, group, bytes, count: 0, volume: '/', orphaned: 0, largest: [], ...fields }
}

const overview: StorageOverview = {
  failed: 85 * MB,
  volumes: [
    { mount: '/', free: 61 * GB, total: 494 * GB },
    { mount: '/Volumes/SSD', free: 1650 * GB, total: 2000 * GB },
  ],
  categories: [
    part('songs', 'work', 18.4 * GB, {
      count: 412,
      largest: [
        {
          kind: 'song',
          id: 'run-1/song1',
          songId: 'run-1/song1',
          title: 'Mein Hund · 1',
          detail: null,
          bytes: 182 * MB,
          createdAt: '2026-10-01T10:00:00Z',
        },
      ],
    }),
    part('videos', 'work', 3.2 * GB, {
      count: 7,
      orphaned: 420 * MB,
      largest: [
        {
          kind: 'video',
          id: 'v1',
          songId: 'run-1/song1',
          title: 'Mein Hund',
          detail: 'landscape',
          bytes: 1.1 * GB,
          createdAt: '2026-10-12T12:00:00Z',
        },
        {
          kind: 'video',
          id: 'v2',
          songId: 'run-1/song1',
          title: 'Mein Hund',
          detail: 'portrait',
          bytes: 640 * MB,
          createdAt: '2026-10-12T12:30:00Z',
        },
      ],
    }),
    part('stems', 'work', 2.1 * GB, { count: 12 }),
    part('versions', 'work', 940 * MB, { count: 9 }),
    part('images', 'work', 46 * MB, { count: 23 }),
    part('covers', 'work', 12 * MB, { count: 31 }),
    part('stream', 'app', 1.4 * GB, { count: 380 }),
    part('logs', 'app', 22 * MB, { count: 14 }),
    part('database', 'app', 3 * MB),
    part('yue2', 'models', 38 * GB, { volume: '/Volumes/SSD' }),
    part('lmstudio', 'models', 31 * GB, { volume: '/Volumes/SSD' }),
    part('imageModels', 'models', 15 * GB),
    part('speechModels', 'models', 12 * GB),
    part('programs', 'models', 9.8 * GB),
  ],
}

test.beforeEach(({ api }) => {
  api.runs = [
    run('run-1', { title: 'Mein Hund' }),
    run('run-2', { title: 'Fehlversuch', songs: [song('run-2/song1', { rating: 1, bytes: 30 * MB })] }),
  ]
  api.answers.set('GET /api/storage/overview', () => overview)
  api.answers.set('POST /api/storage/cleanup/stream', () => ({ bytes: 1.4 * GB, removed: 380 }))
  api.answers.set('DELETE /api/videos/v1', () => 204)
})

test('the storage page shows the parts and cleans up what can be made again', async ({ page, api }) => {
  await page.goto('#/storage')

  await expect(page.getByText('Deine Werke')).toBeVisible()
  await expect(page.getByText('Abgebrochenes mit Resten')).toBeVisible()
  await expect(page.getByText('Songs mit einem Stern')).toBeVisible()
  await expect(page.getByText(/Tonwerk 69\sGB · 1\.650\sGB von 2\.000\sGB frei/)).toBeVisible()
  await expect(page.getByText('auf SSD').first()).toBeVisible()
  await page.screenshot({
    path: process.env.STORAGE_SHOTS ? `${process.env.STORAGE_SHOTS}/uebersicht-handy.png` : undefined,
    fullPage: true,
  })

  await page.getByRole('button', { name: /Musikvideos/ }).click()
  await expect(page.getByText('Shorts/Reels 9:16')).toBeVisible()
  if (process.env.STORAGE_SHOTS) {
    await page.screenshot({ path: `${process.env.STORAGE_SHOTS}/videos-handy.png`, fullPage: true })
  }
  await page.getByRole('listitem').filter({ hasText: 'YouTube 16:9' }).getByRole('button', { name: 'Löschen' }).click()
  await page.getByRole('alertdialog').getByRole('button', { name: 'Löschen' }).click()
  await expect.poll(() => api.callsTo('DELETE', '/api/videos/v1').length).toBe(1)

  await page
    .locator('.suggestion')
    .filter({ hasText: 'Streaming-Kopien' })
    .getByRole('button', { name: 'Löschen' })
    .click()
  await expect(page.getByRole('alertdialog')).toContainText(/Streaming-Kopien \(1,4\sGB\) endgültig löschen\?/)
  await page.getByRole('alertdialog').getByRole('button', { name: 'Löschen' }).click()
  await expect.poll(() => api.callsTo('POST', '/api/storage/cleanup/stream').length).toBe(1)
  await expect(page.getByText(/1,4\sGB frei geworden/)).toBeVisible()
})
