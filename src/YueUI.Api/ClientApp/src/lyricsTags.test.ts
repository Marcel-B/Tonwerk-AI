import { describe, expect, it } from 'vitest'
import { insertSectionTag, shout } from './lyricsTags'

describe('insertSectionTag', () => {
  it('puts the tag before the line the caret starts', () => {
    const lyrics = '[Verse]\nHallo\nWelt\n'
    expect(insertSectionTag(lyrics, '[Chorus]', lyrics.indexOf('Welt')).text).toBe('[Verse]\nHallo\n\n[Chorus]\nWelt\n')
  })
})

describe('shout', () => {
  it('writes the selection in capitals and keeps it selected', () => {
    const lyrics = '[Chorus]\nCold chrome, hollow heart\nTearing every nerve apart!'
    const start = lyrics.indexOf('Cold')
    const end = lyrics.indexOf('Tearing') + 'Tearing'.length
    const result = shout(lyrics, start, end)
    expect(result.text).toBe('[Chorus]\nCOLD CHROME, HOLLOW HEART\nTEARING every nerve apart!')
    expect(result.text.slice(result.start, result.end)).toBe('COLD CHROME, HOLLOW HEART\nTEARING')
  })

  it('takes the caret line without a selection and leaves tags and directions alone', () => {
    const lyrics = '[Chorus]\n(Screaming) I am a ghost\nnext'
    const at = lyrics.indexOf('ghost')
    expect(shout(lyrics, at, at).text).toBe('[Chorus]\n(Screaming) I AM A GHOST\nnext')
    expect(shout(lyrics, 0, lyrics.length).text).toBe('[Chorus]\n(Screaming) I AM A GHOST\nNEXT')
  })

  it('turns capitals back, keeping a line start and the pronoun I', () => {
    const lyrics = 'COLD CHROME\nI’M AN UPGRADE, I FEEL'
    expect(shout(lyrics, 0, lyrics.length).text).toBe('Cold chrome\nI’m an upgrade, I feel')
  })

  it('counts ß growing into SS', () => {
    const result = shout('heiß', 0, 4)
    expect(result).toEqual({ text: 'HEISS', start: 0, end: 5 })
  })
})

describe('directions', () => {
  it('stay in their section and join the direction line before them', () => {
    const first = insertSectionTag('[Verse]\n', '(screaming)', null)
    expect(first.text).toBe('[Verse]\n(screaming)\n')
    const second = insertSectionTag(first.text, '(full power)', first.caret)
    expect(second.text).toBe('[Verse]\n(screaming, full power)\n')
    const mid = 'Line one\nLine two\n'
    expect(insertSectionTag(mid, '(belting)', mid.indexOf('Line two')).text).toBe('Line one\n(belting)\nLine two\n')
  })
})
