import type { SpeechInfo, SpeechModelInfo, SpeechTake } from '../src/types'
import { expect, test } from './fakeApi'

const model = (id: string, label: string, license: string, downloadGb: number): SpeechModelInfo => ({
  id,
  label,
  repo: id,
  downloadGb,
  clones: false,
  license,
  note: '',
  downloaded: false,
})

const take = (id: string, source: string, change: Partial<SpeechTake>): SpeechTake => ({
  id,
  modelId: 'mossformer2-se',
  modelLabel: 'MossFormer2 SE 48K',
  text: '',
  source,
  progress: null,
  voiceId: null,
  voiceLabel: null,
  stage: 'done',
  message: null,
  seconds: null,
  loadSeconds: null,
  speakSeconds: null,
  peakMemoryGb: null,
  createdAt: '2026-10-14T10:00:00Z',
  updatedAt: '2026-10-14T10:00:00Z',
  finished: true,
  ...change,
})

const info: SpeechInfo = {
  installed: true,
  python: '/env/bin/python',
  ffmpegInstalled: true,
  models: [model('chatterbox', 'Chatterbox Multilingual v3', 'MIT', 3)],
  cleanModels: [
    model('mossformer2-se', 'MossFormer2 SE 48K', 'Apache-2.0', 0.25),
    model('deepfilternet3', 'DeepFilterNet 3', 'MIT / Apache-2.0', 0.01),
  ],
  voices: [],
  takes: [
    take('b', 'Interview Bahnhof.m4a', {
      stage: 'cleaning',
      progress: 0.42,
      finished: false,
      createdAt: '2026-10-14T11:00:00Z',
    }),
    take('a', 'Vortrag Mai.mp4', { seconds: 2712 }),
  ],
}

test('a speech recording is uploaded to be cleaned and heard against the original', async ({ page, api }) => {
  api.answers.set('GET /api/speech', () => info)
  api.answers.set('POST /api/speech/cleanups', () =>
    take('c', 'Rede.m4a', { stage: 'queued', finished: false, createdAt: '2026-10-14T12:00:00Z' }),
  )
  await page.goto('#/lab')

  await expect(page.getByText('Säubert 42 %')).toBeVisible()
  await expect(page.getByText('MossFormer2 SE 48K · 45:12')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Original anhören' })).toBeVisible()
  await page.locator('.p-card', { hasText: 'Sprache säubern' }).screenshot({
    path: process.env.CLEAN_SHOTS ? `${process.env.CLEAN_SHOTS}/sprachlabor-handy.png` : undefined,
  })

  await page
    .getByLabel('Sprachaufnahme')
    .setInputFiles({ name: 'Rede.m4a', mimeType: 'audio/mp4', buffer: Buffer.from('m4a') })
  await page.getByLabel('DeepFilterNet 3').check()
  await page.getByRole('button', { name: 'Säubern' }).click()

  await expect(page.getByText('Rede.m4a')).toBeVisible()
  expect(api.callsTo('POST', '/api/speech/cleanups')).toHaveLength(1)
})
