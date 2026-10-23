import { expect, test } from './fakeApi'

function at(hour: number): string {
  return new Date(Date.UTC(2026, 9, 23, hour)).toISOString()
}

test.beforeEach(({ api }) => {
  const engines = ['mlx', 'ane', 'mlx', 'mlx', 'ane', 'mlx', 'ane', 'mlx']
  api.answers.set('GET /api/stats/synthesis', () => ({
    engines: [
      { engine: 'mlx', steps: 32, count: 4, meanRate: 3.1, bestRate: 2.5, meanSeconds: 690, lastAt: at(20) },
      { engine: 'ane', steps: 32, count: 3, meanRate: 7.9, bestRate: 6.8, meanSeconds: 1800, lastAt: at(19) },
      { engine: 'mlx', steps: 8, count: 1, meanRate: 0.9, bestRate: 0.9, meanSeconds: 200, lastAt: at(13) },
    ],
    anePrograms: { count: 3, meanSeconds: 400, longestSeconds: 620, lastAt: at(19) },
    recent: engines
      .map((engine, i) => ({
        at: at(13 + i),
        song: `20261023-run/song${i + 1}`,
        engine,
        steps: i === 0 ? 8 : 32,
        seconds: 700,
        rate: i === 0 ? 0.9 : engine === 'ane' ? 7 + i / 5 : 2.5 + i / 10,
      }))
      .reverse(),
    aneRecent: [
      { at: at(19), song: '20261023-run/song7', seconds: 380, waited: true },
      { at: at(16), song: '20261023-run/song5', seconds: 200, waited: false },
      { at: at(14), song: '20261023-run/song2', seconds: 620, waited: true },
    ],
  }))
})

test('the synthesis statistics are charts first, their numbers on request', async ({ page }) => {
  await page.goto('#/queue')
  await page.getByText('Statistik', { exact: true }).click()
  const rates = page.getByRole('img', { name: 'Rechenzeit pro Sekunde Audio' })
  await expect(rates).toBeVisible()
  await expect(rates.locator('circle')).toHaveCount(8)
  await expect(page.getByRole('img', { name: /Neural-Engine-Programme/ }).locator('rect')).toHaveCount(3)
  await expect(page.getByText('3× kompiliert, davon 2× während ein Song wartete')).toBeVisible()
  await expect(page.getByText('Zuletzt')).toBeHidden()

  await rates.locator('circle').last().click()
  await expect(page.getByText(/· mlx · 32 Schritte ·/)).toBeVisible()
  if (process.env.TONWERK_SHOT) {
    await page.getByText('Statistik', { exact: true }).scrollIntoViewIfNeeded()
    await page.screenshot({ path: process.env.TONWERK_SHOT, fullPage: true })
  }

  await page.getByText('Zahlen', { exact: true }).click()
  await expect(page.getByText('Zuletzt')).toBeVisible()
})
