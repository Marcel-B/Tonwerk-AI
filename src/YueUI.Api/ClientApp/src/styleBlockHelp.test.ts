import { describe, expect, it } from 'vitest'
import { blockHelp } from './styleBlockHelp'
import { styleCategories } from './styleTags'

describe('blockHelp', () => {
  it('explains every voice and timbre block in both languages', () => {
    const tags = styleCategories.filter((c) => c.key === 'voice' || c.key === 'timbre').flatMap((c) => c.tags)
    for (const tag of tags) {
      expect(blockHelp(tag, 'de'), tag).toBeTruthy()
      expect(blockHelp(tag, 'en'), tag).toBeTruthy()
    }
    expect(blockHelp('pop', 'de')).toBeUndefined()
  })
})
