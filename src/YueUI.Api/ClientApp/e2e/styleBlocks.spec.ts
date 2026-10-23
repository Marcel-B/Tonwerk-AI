import { expect, test } from './fakeApi'

// A mouse is needed for the tooltip; on the phone it stays away so a tap does not leave it standing.
test.use({ hasTouch: false, isMobile: false, viewport: { width: 1280, height: 900 } })

test('a timbre block explains itself on hover', async ({ page }) => {
  await page.goto('')
  await page.getByRole('button', { name: 'Bausteine' }).click()
  await page.getByRole('tab', { name: 'Klangfarbe' }).click()
  await page.getByRole('button', { name: 'raspy vocal' }).hover()
  await expect(page.getByText('Kratzig: ein reibendes Geräusch liegt auf dem Ton')).toBeVisible()
})
