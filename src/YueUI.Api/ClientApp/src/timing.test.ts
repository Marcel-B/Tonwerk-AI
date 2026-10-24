import { describe, expect, it } from 'vitest'
import { hasDirections, lineAt, shownAt, shownLines, shownStart } from './timing'
import type { LyricsTiming } from './types'

const timing: LyricsTiming = {
  lines: [
    { text: 'Rain on the roof', section: 'Verse', start: 10, end: 12, heard: 4 },
    { text: 'Nobody knows', section: 'Verse', start: 13, end: 15, heard: 2 },
    { text: 'Hold on', section: 'Chorus', start: 30, end: 32, heard: 2 },
  ],
  source: 'mix',
  coverage: 1,
  createdAt: '2026-10-13T00:00:00Z',
}

describe('lyrics timing', () => {
  it('numbers only the sung lines, as the server does', () => {
    const lines = shownLines('[Verse]\nRain on the roof\nNobody knows\n\n[Chorus]\n...\nHold on')
    expect(lines.map((line) => line.timed)).toEqual([-1, 0, 1, -1, -1, -1, 2])
  })

  it('lights the line being sung and nothing in a long break', () => {
    expect(lineAt(timing, 5)).toBe(-1)
    expect(lineAt(timing, 10.5)).toBe(0)
    expect(lineAt(timing, 12.5)).toBe(0)
    expect(lineAt(timing, 14)).toBe(1)
    expect(lineAt(timing, 25)).toBe(-1)
    expect(lineAt(timing, 31)).toBe(2)
    expect(lineAt(null, 31)).toBe(-1)
  })
})

const paragraph =
  '[Intro] (Heavy distorted synth bass intro, 140 BPM) (Sound of electronic glitches)\n\n' +
  '[Verse 1] (Cold, robotic female vocals) Neon rain on carbon glass The ghosts of who we were, they pass ' +
  'Where does the circuit end, and I begin? (Slightly more aggressive) I traded breath for silver veins\n\n' +
  '[Chorus]\nHold on'

describe('lyrics shown cleaned', () => {
  it('hides directions, keeps the tags on lines of their own and breaks lost line ends', () => {
    expect(shownLines(paragraph).map((line) => line.text)).toEqual([
      '[Intro]',
      '',
      '[Verse 1]',
      'Neon rain on carbon glass',
      'The ghosts of who we were, they pass',
      'Where does the circuit end, and I begin?',
      'I traded breath for silver veins',
      '',
      '[Chorus]',
      'Hold on',
    ])
  })

  it('shows the directions apart on request, never lit by the timing', () => {
    const lines = shownLines(paragraph, true)
    expect(lines.filter((line) => line.direction).map((line) => line.text)).toEqual([
      '(Heavy distorted synth bass intro, 140 BPM)',
      '(Sound of electronic glitches)',
      '(Cold, robotic female vocals)',
      '(Slightly more aggressive)',
    ])
    expect(lines.filter((line) => line.direction).every((line) => line.timed === -1)).toBe(true)
  })

  it('keeps the server’s numbering: pieces of one line share it', () => {
    const lines = shownLines(paragraph)
    // The intro's line counts as sung on the server (it has letters), so the verse is line 1, the chorus line 2.
    expect(lines.filter((line) => line.timed === 1)).toHaveLength(4)
    expect(lines.find((line) => line.text === 'Hold on')!.timed).toBe(2)
  })

  it('leaves German capitals alone and short lines as they are', () => {
    const german = 'Ich laufe durch die Stadt im Regen und der Wind singt mir ein Lied von Morgen'
    expect(shownLines(german).map((line) => line.text)).toEqual([german])
    expect(shownLines('Rain on the roof Nobody knows').map((line) => line.text)).toEqual([
      'Rain on the roof Nobody knows',
    ])
  })

  it('lights a paragraph line by line, by its share of the words', () => {
    const lines = shownLines(paragraph)
    // The verse is timed line 1 from 10 s to 37 s: 27 words, a second each.
    const paragraphTiming: LyricsTiming = {
      ...timing,
      lines: [
        { text: 'intro', section: 'Intro', start: 0, end: 5, heard: 0 },
        { text: 'verse', section: 'Verse 1', start: 10, end: 37, heard: 27 },
        { text: 'Hold on', section: 'Chorus', start: 40, end: 42, heard: 2 },
      ],
    }
    const at = (seconds: number) => lines[shownAt(lines, paragraphTiming, seconds)]?.text
    expect(at(12)).toBe('Neon rain on carbon glass')
    expect(at(16)).toBe('The ghosts of who we were, they pass')
    expect(at(33)).toBe('I traded breath for silver veins')
    expect(at(39)).toBe('I traded breath for silver veins')
    expect(at(41)).toBe('Hold on')
    expect(shownAt(lines, paragraphTiming, 8)).toBe(-1)
    const third = lines.find((line) => line.text.startsWith('Where does'))!
    expect(shownStart(lines, lines.indexOf(third), paragraphTiming)).toBe(23)
  })

  it('takes a timing of the shown lines one by one, as the server now makes it', () => {
    const lines = shownLines(paragraph)
    const texts = [
      'Neon rain on carbon glass',
      'The ghosts of who we were, they pass',
      'Where does the circuit end, and I begin?',
      'I traded breath for silver veins',
      'Hold on',
    ]
    const exact: LyricsTiming = {
      ...timing,
      lines: texts.map((text, i) => ({ text, section: null, start: 10 + i * 10, end: 18 + i * 10, heard: 4 })),
    }
    expect(lines[shownAt(lines, exact, 21)]!.text).toBe('The ghosts of who we were, they pass')
    expect(lines[shownAt(lines, exact, 51)]!.text).toBe('Hold on')
    const third = lines.findIndex((line) => line.text.startsWith('Where does'))
    expect(shownStart(lines, third, exact)).toBe(30)
  })

  it('tells whether there is anything to show', () => {
    expect(hasDirections(paragraph)).toBe(true)
    expect(hasDirections('[Verse]\nRain on the roof')).toBe(false)
  })
})
