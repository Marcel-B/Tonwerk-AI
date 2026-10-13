import { expect, queued, run, song, test } from './fakeApi'

test('a song is asked for with what the form holds', async ({ page, api }) => {
  await page.goto('')
  await page.getByRole('textbox', { name: 'Titel' }).fill('Nachtzug')
  await page.getByRole('textbox', { name: 'Stil' }).fill('synthwave, male vocals')
  await page.getByRole('textbox', { name: 'Songtext' }).fill('[verse]\nUnter der Stadt')
  await page.getByRole('button', { name: 'Erzeugen' }).click()

  await expect(page.getByRole('status').filter({ hasText: 'In der Warteschlange' })).toBeVisible()
  const [call] = api.callsTo('POST', '/api/generate')
  expect(call!.body).toMatchObject({
    title: 'Nachtzug',
    style: 'synthwave, male vocals',
    lyrics: '[verse]\nUnter der Stadt',
    quality: 'draft',
    batch: 1,
    seed: null,
    abc: null,
  })
})

test('a song that has to wait says so', async ({ page, api }) => {
  api.answers.set('POST /api/generate', () => queued('job1'))
  await page.goto('')
  await page.getByRole('textbox', { name: 'Stil' }).fill('jazz')
  await page.getByRole('textbox', { name: 'Songtext' }).fill('la la')
  await page.getByRole('button', { name: 'Erzeugen' }).click()

  await expect(page.getByRole('status').filter({ hasText: 'Wartet in der Warteschlange' })).toBeVisible()
})

test('the form outlasts a reload', async ({ page }) => {
  await page.goto('')
  await page.getByRole('textbox', { name: 'Stil' }).fill('bossa nova')
  // Kept on change; give the watcher its tick before reloading.
  await expect.poll(() => page.evaluate(() => localStorage.getItem('yue-ui.form') ?? '')).toContain('bossa nova')
  await page.reload()

  await expect(page.getByRole('textbox', { name: 'Stil' })).toHaveValue('bossa nova')
})

test('the queue page lists what waits and why', async ({ page, api }) => {
  api.snapshot.queue = [queued('job1', { title: 'Erster' }), queued('job2', { kind: 'lyrics', title: 'Herbst' })]
  api.snapshot.worker.busy = true
  await page.goto('#/queue')

  await expect(page.getByText('Erster')).toBeVisible()
  await expect(page.getByText('Herbst')).toBeVisible()

  await api.emit('queue', [])
  await expect(page.getByText('Erster')).toBeHidden()
})

test("use everything brings the song's written score along switched off, to switch on for another key", async ({
  page,
  api,
}) => {
  const score = 'X:1\nL:1/8\nQ:1/4=100\nM:4/4\nK:C\nV: Vocal\n"C" C2 D2 E2 F2 |'
  api.runs = [run('run-1', { title: 'Nachtzug', songs: [song('run-1/song1', { hasScore: true, seed: 42 })] })]
  api.answers.set('GET /api/songs/run-1/song1/request', () => ({
    title: 'Nachtzug',
    style: 'synthwave',
    lyrics: 'la',
    instrumental: null,
    quality: 'full',
    cot: 'full',
    seed: 42,
    engines: null,
    draftSteps: null,
    maxTokens: null,
    abc: null,
    fullSteps: null,
    abcSampling: null,
    semanticSampling: null,
    lora: null,
    loraStrength: null,
  }))
  // The score is plain text, which the fake's JSON answers cannot give.
  await page.route('**/api/songs/run-1/song1/score', (route) =>
    route.fulfill({ body: score, contentType: 'text/plain' }),
  )
  await page.goto('#/songs')
  await page
    .getByRole('group')
    .filter({ hasText: 'Nachtzug' })
    .getByRole('button', { name: 'Weitere Aktionen' })
    .click()
  await page.getByRole('menuitem', { name: 'Alles übernehmen' }).click()

  await expect(page.getByRole('textbox', { name: 'Seed' })).toHaveValue('42')
  const use = page.getByRole('checkbox', { name: 'Partitur verwenden' })
  await expect(use).not.toBeChecked()
  await page.getByRole('button', { name: 'Erzeugen' }).click()
  await expect.poll(() => api.callsTo('POST', '/api/generate').length).toBe(1)
  expect(api.callsTo('POST', '/api/generate')[0]!.body).toMatchObject({ seed: 42, quality: 'full', abc: null })

  await use.check()
  await page.getByRole('button', { name: 'Erzeugen' }).click()
  await expect.poll(() => api.callsTo('POST', '/api/generate').length).toBe(2)
  expect(api.callsTo('POST', '/api/generate')[1]!.body).toMatchObject({ seed: 42, abc: score, cot: 'full' })
})
