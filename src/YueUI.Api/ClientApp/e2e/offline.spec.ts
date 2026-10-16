import { expect, run, test } from './fakeApi'

test('a playlist kept offline loads its songs onto the device', async ({ page, api }) => {
  api.runs = [run('run-1', { title: 'Unterwegs' })]
  api.playlists = [{ id: 1, name: 'Auto', songIds: ['run-1/song1'] }]
  await page.route('**/api/songs/run-1/song1/stream', (route) =>
    route.fulfill({ body: Buffer.alloc(2048), headers: { 'Content-Type': 'audio/mp4', ETag: '"a"' } }),
  )
  await page.goto('#/playlist')

  await page.getByRole('button', { name: 'Offline hören' }).click()

  await expect(page.getByText('Offline auf diesem Gerät: 1 von 1 Songs, 2 KB')).toBeVisible()
  await expect(page.getByLabel('Auf diesem Gerät gespeichert')).toBeVisible()
  expect(await page.evaluate(async () => (await (await caches.open('tonwerk-offline')).keys()).length)).toBe(1)

  // Switched off, the copies go again.
  await page.getByRole('button', { name: 'Offline hören' }).click()
  await expect(page.getByText(/Offline auf diesem Gerät/)).toBeHidden()
  await expect
    .poll(() => page.evaluate(async () => (await (await caches.open('tonwerk-offline')).keys()).length))
    .toBe(0)
})
