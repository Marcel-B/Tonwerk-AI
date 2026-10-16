import { expect, run, test } from './fakeApi'

/** Ten seconds of silence as a WAV, so the element really plays. */
function silence(seconds = 10, rate = 8000): Buffer {
  const data = rate * seconds * 2
  const wav = Buffer.alloc(44 + data)
  wav.write('RIFF', 0)
  wav.writeUInt32LE(36 + data, 4)
  wav.write('WAVEfmt ', 8)
  wav.writeUInt32LE(16, 16)
  wav.writeUInt16LE(1, 20)
  wav.writeUInt16LE(1, 22)
  wav.writeUInt32LE(rate, 24)
  wav.writeUInt32LE(rate * 2, 28)
  wav.writeUInt16LE(2, 32)
  wav.writeUInt16LE(16, 34)
  wav.write('data', 36)
  wav.writeUInt32LE(data, 40)
  return wav
}

// The iPhone, where a stale graph is given up for a new element (`touchWebKit` in player.ts).
test.use({
  userAgent:
    'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1',
})

test.beforeEach(async ({ page, api }) => {
  api.runs = [run('run-1', { title: 'Nachtzug' })]
  await page.route('**/api/songs/run-1/song1/stream', (route) =>
    route.fulfill({ status: 200, contentType: 'audio/wav', body: silence() }),
  )
  // The handlers the lock screen and the AirPods call.
  await page.addInitScript(() => {
    const handlers: Record<string, ((details: object) => void) | null> = {}
    ;(window as unknown as { __actions: typeof handlers }).__actions = handlers
    navigator.mediaSession.setActionHandler = (action, handler) => {
      handlers[action] = handler as (details: object) => void
    }
  })
})

test('the AirPods resume a song paused in the pocket', async ({ page }) => {
  await page.goto('#/songs')
  await page.getByRole('group').filter({ hasText: 'Nachtzug' }).getByRole('button', { name: 'Abspielen' }).click()
  const playing = () => page.evaluate(() => !document.querySelector('audio')!.paused)
  await expect.poll(playing).toBe(true)

  await page.evaluate(() => {
    const actions = (window as unknown as { __actions: Record<string, (() => void) | null> }).__actions
    actions.pause!()
    document.querySelector('audio')!.dataset.old = 'yes'
    // The phone locked while paused: the routed graph may be dead now.
    Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => 'hidden' })
    document.dispatchEvent(new Event('visibilitychange'))
  })
  await expect.poll(playing).toBe(false)

  await page.evaluate(() => (window as unknown as { __actions: Record<string, () => void> }).__actions.play!())
  // Played on a fresh element, the one iOS then knows as playing.
  await expect.poll(playing).toBe(true)
  await expect(page.locator('audio')).toHaveCount(1)
  expect(await page.evaluate(() => document.querySelector('audio')!.dataset.old)).toBeUndefined()
})

test('the full-screen player lights the line being sung and jumps to a tapped one', async ({ page, api }) => {
  api.runs = [
    run('run-1', {
      title: 'Nachtzug',
      lyrics: '[Verse]\nErste Zeile im Zug\nZweite Zeile am Fenster\n\n[Chorus]\nDritte Zeile',
    }),
  ]
  api.answers.set('GET /api/songs/run-1/song1/timing', () => ({
    timing: {
      lines: [
        { text: 'Erste Zeile im Zug', section: 'Verse', start: 0, end: 2.5, heard: 4 },
        { text: 'Zweite Zeile am Fenster', section: 'Verse', start: 3, end: 5.5, heard: 4 },
        { text: 'Dritte Zeile', section: 'Chorus', start: 6, end: 9, heard: 2 },
      ],
      source: 'vocals',
      coverage: 1,
      createdAt: '2026-10-13T18:00:00Z',
    },
    info: { installed: true, state: 'none', message: null },
  }))
  // Seeking needs range requests, which the plain answer above does not take.
  const wav = silence()
  await page.route('**/api/songs/run-1/song1/stream', (route) => {
    const [, from, to] = /bytes=(\d+)-(\d*)/.exec(route.request().headers()['range'] ?? '') ?? []
    if (from === undefined) {
      return route.fulfill({ status: 200, contentType: 'audio/wav', headers: { 'Accept-Ranges': 'bytes' }, body: wav })
    }
    const end = to ? Number(to) : wav.length - 1
    return route.fulfill({
      status: 206,
      contentType: 'audio/wav',
      headers: { 'Accept-Ranges': 'bytes', 'Content-Range': `bytes ${from}-${end}/${wav.length}` },
      body: wav.subarray(Number(from), end + 1),
    })
  })
  await page.goto('#/songs')
  await page.getByRole('group').filter({ hasText: 'Nachtzug' }).getByRole('button', { name: 'Abspielen' }).click()
  await page.getByRole('button', { name: 'Nachtzug' }).first().click()
  await page.getByRole('dialog').getByRole('button', { name: 'Text', exact: true }).click()

  const active = page.locator('.line.active')
  await expect(active).toHaveText('Erste Zeile im Zug')
  await expect(active).toHaveText('Zweite Zeile am Fenster', { timeout: 6000 })
  await page.screenshot({
    path: process.env.TIMING_SHOTS ? `${process.env.TIMING_SHOTS}/player-text-handy.png` : undefined,
  })

  await page.locator('.line', { hasText: 'Dritte Zeile' }).click()
  await expect(active).toHaveText('Dritte Zeile')
})
