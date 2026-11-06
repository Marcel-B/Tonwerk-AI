import type { OnkelBummelSettings, OnkelBummelStatus } from '../src/types'
import { expect, test } from './fakeApi'

const off: OnkelBummelStatus = {
  configured: false,
  enabled: true,
  url: null,
  tokenSet: false,
  topic: 'tonwerk',
  link: null,
  editable: true,
}

test('the settings menu sets Onkel Bummel and sends a test message', async ({ page, api }) => {
  api.answers.set('GET /api/onkel-bummel', () => off)
  api.answers.set('PUT /api/onkel-bummel', (call) => {
    const settings = call.body as OnkelBummelSettings
    return { ...off, configured: true, url: settings.url, tokenSet: true, link: settings.link }
  })
  api.answers.set('POST /api/onkel-bummel/test', () => ({ state: 'topic', detail: 'Onkel Bummel answered 403' }))
  await page.goto('')

  await page.getByRole('button', { name: 'Einstellungen' }).click()
  await page.getByRole('menuitem', { name: /Onkel Bummel/ }).click()
  await page.getByLabel('Adresse von Onkel Bummel').fill('https://onkel-bummel.tail6105c0.ts.net')
  await page.getByLabel('Publish-Token').fill('ob_secret')
  await page.getByLabel(/Adresse von Tonwerk-AI/).fill('https://oi-o-mat.tail6105c0.ts.net:8443')
  await page.getByRole('button', { name: 'Speichern und Test senden' }).click()

  await expect(page.getByText('Der Token darf nicht an dieses Topic senden.', { exact: false })).toBeVisible()
  expect(api.callsTo('PUT', '/api/onkel-bummel')[0]!.body).toEqual({
    url: 'https://onkel-bummel.tail6105c0.ts.net',
    token: 'ob_secret',
    topic: 'tonwerk',
    link: 'https://oi-o-mat.tail6105c0.ts.net:8443',
    enabled: true,
  })
  await page.screenshot({
    path: process.env.ONKELBUMMEL_SHOTS ? `${process.env.ONKELBUMMEL_SHOTS}/einstellungen-handy.png` : undefined,
  })
})
