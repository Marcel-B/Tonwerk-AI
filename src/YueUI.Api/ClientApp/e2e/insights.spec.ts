import type { InsightReport } from '../src/types'
import { expect, test } from './fakeApi'

const report: InsightReport = {
  rated: 24,
  average: 3.4,
  groups: [
    {
      key: 'style',
      entries: [
        { value: 'half-time beat', count: 9, average: 4.4, score: 4.15 },
        { value: 'dark synthwave', count: 6, average: 4, score: 3.8 },
        { value: 'pop', count: 12, average: 3.4, score: 3.4 },
        { value: 'polka', count: 3, average: 1.7, score: 2.55 },
      ],
    },
    {
      key: 'engine',
      entries: [
        { value: 'yue2', count: 20, average: 3.5, score: 3.48 },
        { value: 'ace', count: 4, average: 3, score: 3.17 },
      ],
    },
    { key: 'lora', entries: [{ value: '', count: 20, average: 3.4, score: 3.4 }] },
  ],
}

test.beforeEach(({ api }) => {
  api.answers.set('GET /api/insights', () => report)
})

test('the insights page shows what the rated songs were made with', async ({ page }) => {
  await page.goto('#/insights')

  await expect(page.getByText('24 bewertete Songs, im Schnitt ★ 3,4.')).toBeVisible()
  await expect(page.getByText('half-time beat')).toBeVisible()
  await expect(page.locator('.row', { hasText: 'ACE-Step' })).toBeVisible()
  await expect(page.getByText('ohne LoRA')).toBeVisible()
  await page.screenshot({
    path: process.env.INSIGHTS_SHOTS ? `${process.env.INSIGHTS_SHOTS}/auswertung-handy.png` : undefined,
    fullPage: true,
  })
})

test('the create page offers the blocks of the best rated songs', async ({ page }) => {
  await page.goto('#/create')
  await page.getByRole('button', { name: 'Bausteine', exact: true }).click()

  await page.getByRole('tab', { name: 'Bewährt' }).click()
  const proven = page.getByRole('group', { name: 'Bewährt' })
  await expect(proven.getByRole('button')).toHaveText(['half-time beat', 'dark synthwave'])
  await proven.getByRole('button', { name: 'half-time beat' }).click()
  await expect(proven.getByRole('button', { name: 'half-time beat' })).toHaveAttribute('aria-pressed', 'true')
})
