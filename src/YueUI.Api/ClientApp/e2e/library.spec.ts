import { expect, run, song, test } from './fakeApi'

test.beforeEach(({ api }) => {
  api.runs = [
    run('run-1', { title: 'Nachtzug', style: 'synthwave', lyrics: '[verse]\nLichter der Stadt' }),
    run('run-2', {
      title: 'Sommerregen',
      style: 'acoustic folk',
      lyrics: '[chorus]\nRegen auf dem Dach',
      songs: [song('run-2/song1', { rating: 3 }), song('run-2/song2')],
    }),
  ]
})

test('the search filters title and style, or the lyrics', async ({ page }) => {
  await page.goto('#/songs')
  await expect(page.getByText('Nachtzug')).toBeVisible()
  await expect(page.getByText('Sommerregen')).toBeVisible()

  const search = page.getByRole('searchbox', { name: 'Suchen' })
  await search.fill('folk')
  await expect(page.getByText('1 von 2 Einträgen')).toBeVisible()
  await expect(page.getByText('Nachtzug')).toBeHidden()

  // The lyrics are not searched with the title.
  await search.fill('stadt')
  await expect(page.getByText('Keine Treffer.')).toBeVisible()
})

test('a rating goes to the server', async ({ page, api }) => {
  await page.goto('#/songs')
  const first = page.getByRole('group').filter({ hasText: 'Nachtzug' })
  // The visible star, as a finger taps it. Checking the hidden radio underneath fails since the fixture's songs are a
  // week old: the review hint then shows above the list and can move it between scrolling and the forced click, and a
  // click on the radio itself reaches both of the rating's handlers, which set the stars and take them back.
  await first.locator('.p-rating-option').nth(3).click()

  await expect
    .poll(() => api.callsTo('PUT', '/api/songs/run-1/song1/rating').map((call) => call.body))
    .toEqual([{ rating: 4 }])
})

test("on a phone a song's further actions are in its menu", async ({ page }) => {
  await page.goto('#/songs')
  const first = page.getByRole('group').filter({ hasText: 'Nachtzug' })
  await first.getByRole('button', { name: 'Weitere Aktionen' }).click()

  const menu = page.getByRole('menu')
  await expect(menu.getByRole('menuitem', { name: 'Alles übernehmen' })).toBeVisible()
  await expect(menu.getByRole('menuitem', { name: 'Song löschen' })).toBeVisible()
  // No score, so nothing that needs one.
  await expect(menu.getByRole('menuitem', { name: 'In Logic öffnen' })).toHaveCount(0)
})

test('deleting a song asks first and keeps it when told to', async ({ page, api }) => {
  await page.goto('#/songs')
  const rain = page.getByRole('group').filter({ hasText: 'Sommerregen' })
  await rain.getByRole('button', { name: 'Weitere Aktionen' }).first().click()
  await page.getByRole('menuitem', { name: 'Song löschen' }).click()

  const dialog = page.getByRole('alertdialog')
  await expect(dialog).toContainText('Sommerregen')
  await dialog.getByRole('button', { name: 'Behalten' }).click()
  expect(api.calls.filter((call) => call.method === 'DELETE')).toEqual([])

  await rain.getByRole('button', { name: 'Weitere Aktionen' }).first().click()
  await page.getByRole('menuitem', { name: 'Song löschen' }).click()
  await page.getByRole('alertdialog').getByRole('button', { name: 'Löschen' }).click()
  await expect.poll(() => api.callsTo('DELETE', '/api/songs/run-2/song1').length).toBe(1)
})

test('a note opens under its song and is saved', async ({ page, api }) => {
  await page.goto('#/songs')
  const first = page.getByRole('group').filter({ hasText: 'Nachtzug' })
  await first.getByRole('button', { name: 'Notiz', exact: true }).click()
  await first.getByRole('textbox', { name: 'Notiz' }).fill('Refrain lauter')
  await first.getByRole('textbox', { name: 'Notiz' }).blur()

  await expect
    .poll(() => api.callsTo('PUT', '/api/songs/run-1/song1/note').map((call) => call.body))
    .toContainEqual({
      note: 'Refrain lauter',
    })
  // Filled while a note is written, even before the library reloads.
  await expect(first.getByRole('button', { name: 'Notiz (vorhanden)' })).toBeVisible()
})

test("a song's address marks it, even when the search would hide it", async ({ page }) => {
  await page.goto('#/songs')
  await page.getByRole('searchbox', { name: 'Suchen' }).fill('folk')
  await page.goto('#/songs/run-1/song1')

  await expect(page.locator('#song-run-1\\/song1')).toHaveClass(/focused/)
  await expect(page.getByRole('searchbox', { name: 'Suchen' })).toHaveValue('')
})

test('songs left unrated are offered for review', async ({ page, api }) => {
  for (const run of api.runs) {
    run.createdAt = '2026-01-01T10:00:00Z'
  }
  await page.goto('#/songs')
  await expect(page.getByText(/Songs? ohne Sterne/)).toBeVisible()
  await page.getByRole('button', { name: 'Durchgehen' }).click()

  // Sommerregen's first song has stars; only its second and Nachtzug's are left.
  await expect(page.getByText('2 von 2 Einträgen')).toBeVisible()
  await expect(page.getByRole('group').filter({ hasText: 'Sommerregen' }).getByText('Song 1')).toHaveCount(0)
})

test('songs show tempo, key and clarity, and filter by them', async ({ page, api }) => {
  api.runs = [
    run('run-1', {
      title: 'Nachtzug',
      songs: [
        song('run-1/song1', {
          traits: { tempo: 91.6, key: 'F#m', from: 'audio', clarity: 0.38, clarityFrom: 'mix' },
        }),
      ],
    }),
    run('run-2', {
      title: 'Sommerregen',
      songs: [
        song('run-2/song1', { traits: { tempo: 124, key: 'Bb', from: 'score', clarity: null, clarityFrom: null } }),
      ],
    }),
  ]
  await page.goto('#/songs')
  const night = page.getByRole('group').filter({ hasText: 'Nachtzug' })
  await expect(night.getByText('92 BPM · F♯-Moll')).toBeVisible()
  await expect(night.getByText('Text 38 %')).toBeVisible()
  await expect(page.getByText('124 BPM · B♭-Dur')).toBeVisible()
  await page.screenshot({ path: 'test-results/library-traits.png', fullPage: true })

  await page.getByRole('combobox', { name: 'Nach Tempo, Tonart oder Text filtern' }).click()
  await page.getByRole('option', { name: 'Moll' }).click()
  await expect(page.getByText('1 von 2 Einträgen')).toBeVisible()
  await expect(page.getByText('Sommerregen')).toBeHidden()
})

test('songs chosen together are deleted after one question, whole entries in one go', async ({ page, api }) => {
  await page.goto('#/songs')
  await page.getByRole('button', { name: 'Auswählen' }).click()

  // The search narrows what "select all" takes.
  await page.getByRole('searchbox', { name: 'Suchen' }).fill('folk')
  await page.getByRole('button', { name: 'Alle auswählen (2)' }).click()
  await page.getByRole('searchbox', { name: 'Suchen' }).fill('')
  await page.getByRole('checkbox', { name: 'Song 1 von „Nachtzug“ auswählen' }).check()
  await expect(page.getByText('3 ausgewählt')).toBeVisible()

  await page.getByRole('button', { name: 'Löschen', exact: true }).click()
  const dialog = page.getByRole('alertdialog')
  await expect(dialog).toContainText('3 Songs')
  await dialog.getByRole('button', { name: 'Behalten' }).click()
  expect(api.calls.filter((call) => call.method === 'DELETE')).toEqual([])

  await page.getByRole('button', { name: 'Löschen', exact: true }).click()
  await page.getByRole('alertdialog').getByRole('button', { name: '3 Songs löschen' }).click()
  await expect.poll(() => api.callsTo('DELETE', '/api/runs/run-1').length).toBe(1)
  await expect.poll(() => api.callsTo('DELETE', '/api/runs/run-2').length).toBe(1)
  expect(api.calls.filter((call) => call.method === 'DELETE' && call.path.startsWith('/api/songs/'))).toEqual([])
})
