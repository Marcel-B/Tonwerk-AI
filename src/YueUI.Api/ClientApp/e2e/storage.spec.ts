import type { StorageUsage } from '../src/types'
import { expect, test } from './fakeApi'

const usage: StorageUsage = {
  dataFolder: '/Users/marcel/Library/Application Support/YuE UI',
  volumes: [
    { path: '/', freeBytes: 82e9, totalBytes: 494e9 },
    { path: '/Volumes/SSD', freeBytes: 610e9, totalBytes: 1000e9 },
  ],
  categories: [
    {
      id: 'yue2',
      group: 'models',
      bytes: 31.4e9,
      count: null,
      path: '/Volumes/SSD/Tonwerk/yue2',
      volume: '/Volumes/SSD',
    },
    { id: 'lmstudio', group: 'models', bytes: 16.2e9, count: null, path: '/x', volume: '/' },
    { id: 'songs', group: 'library', bytes: 9.8e9, count: 412, path: '/x', volume: '/' },
    { id: 'videos', group: 'library', bytes: 3.1e9, count: 7, path: '/x', volume: '/' },
    { id: 'stems', group: 'library', bytes: 2.4e9, count: 23, path: '/x', volume: '/' },
    { id: 'versions', group: 'library', bytes: 0.9e9, count: 31, path: '/x', volume: '/' },
    { id: 'stream', group: 'app', bytes: 1.7e9, count: null, path: '/x', volume: '/' },
    { id: 'covers', group: 'library', bytes: 48e6, count: 64, path: '/x', volume: '/' },
    { id: 'database', group: 'app', bytes: 12e6, count: null, path: '/x', volume: '/' },
  ],
  items: [
    {
      kind: 'video',
      id: 'v1',
      songId: 'run-1/song1',
      title: 'Nachtzug',
      detail: 'landscape',
      bytes: 1.2e9,
      createdAt: '2026-10-01T18:00:00Z',
      rating: null,
    },
    {
      kind: 'video',
      id: 'v2',
      songId: 'run-2/song1',
      title: 'Sommerregen',
      detail: 'portrait',
      bytes: 0.8e9,
      createdAt: '2026-10-03T18:00:00Z',
      rating: null,
    },
    {
      kind: 'song',
      id: 'run-1/song1',
      songId: 'run-1/song1',
      title: 'Nachtzug',
      detail: 'song1',
      bytes: 1.4e9,
      createdAt: '2026-09-20T18:00:00Z',
      rating: 4,
    },
  ],
  leftovers: { count: 3, bytes: 412e6 },
}

test.beforeEach(({ api }) => {
  api.answers.set('GET /api/storage/usage', () => usage)
})

test('the storage page lists the kinds and deletes a video after asking', async ({ page, api }) => {
  await page.goto('#/storage')
  await expect(page.getByRole('heading', { name: 'Was Tonwerk gemacht hat' })).toBeVisible()
  await expect(page.getByText('auf /Volumes/SSD')).toBeVisible()
  await page.screenshot({ path: 'test-results/storage-overview.png', fullPage: true })

  await page.getByRole('button', { name: /Videos/ }).click()
  await expect(page.getByText('Shorts/Reels 9:16')).toBeVisible()
  await page.screenshot({ path: 'test-results/storage-videos.png', fullPage: true })

  page.once('dialog', (dialog) => void dialog.accept())
  await page.getByRole('listitem').filter({ hasText: 'Nachtzug' }).getByRole('button', { name: 'Löschen' }).click()
  await expect.poll(() => api.callsTo('DELETE', '/api/videos/v1').length).toBe(1)
})

test('leftovers are cleared only when confirmed', async ({ page, api }) => {
  await page.goto('#/storage')
  page.once('dialog', (dialog) => void dialog.dismiss())
  await page.getByRole('button', { name: 'Reste löschen' }).click()
  expect(api.callsTo('POST', '/api/storage/leftovers/delete')).toHaveLength(0)

  api.answers.set('POST /api/storage/leftovers/delete', () => ({ count: 3, bytes: 412e6 }))
  page.once('dialog', (dialog) => void dialog.accept())
  await page.getByRole('button', { name: 'Reste löschen' }).click()
  await expect(page.getByText('3 Reste gelöscht')).toBeVisible()
})
