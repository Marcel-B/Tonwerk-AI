import { describe, expect, it } from 'vitest'
import { blocksForStyle, blocksIn, coverBlocks, toggleBlock } from './coverBlocks'
import { suggestPrompt } from './images'

const pencil = coverBlocks.find((block) => block.key === 'pencil')!.text
const photo = coverBlocks.find((block) => block.key === 'photo')!.text
const neon = coverBlocks.find((block) => block.key === 'neon')!.text

describe('cover blocks', () => {
  it('have unique keys and sentences', () => {
    expect(new Set(coverBlocks.map((b) => b.key)).size).toBe(coverBlocks.length)
    expect(new Set(coverBlocks.map((b) => b.text)).size).toBe(coverBlocks.length)
    // No sentence may hold another, or choosing one would show both as chosen.
    for (const a of coverBlocks) {
      for (const b of coverBlocks) {
        if (a !== b) expect(a.text.includes(b.text)).toBe(false)
      }
    }
  })

  it('adds a sentence at the end and takes it out again', () => {
    const added = toggleBlock('A fox in snow.', 'neon')
    expect(added).toBe(`A fox in snow. ${neon}`)
    expect(blocksIn(added)).toEqual(['neon'])
    expect(toggleBlock(added, 'neon')).toBe('A fox in snow.')
  })

  it('keeps one medium', () => {
    const withPhoto = toggleBlock('A fox.', 'photo')
    const withPencil = toggleBlock(withPhoto, 'pencil')
    expect(withPencil).toBe(`A fox. ${pencil}`)
    expect(withPencil.includes(photo)).toBe(false)
  })

  it('counts a sentence typed by hand as chosen', () => {
    expect(blocksIn(`${neon} A fox. ${pencil}`)).toEqual(['pencil', 'neon'])
  })

  it('suggests blocks from whole words of the style', () => {
    expect(blocksForStyle('German, dark synthwave, 110 BPM')).toEqual(['synthwave80s', 'neon', 'charcoal', 'dramatic'])
    expect(blocksForStyle('trap')).toEqual(['dramatic', 'photo'])
    expect(blocksForStyle('post-rock')).toEqual(['landscape', 'mist'])
    expect(blocksForStyle('')).toEqual([])
  })
})

describe('suggestPrompt with blocks', () => {
  it('stays as before without blocks', () => {
    expect(suggestPrompt('Nachtzug', 'dreamy', false)).toBe(
      'A square painting, one striking scene with a strong central motif, rich colour and dramatic light, painterly, high detail. Its mood and colours evoke dreamy music.',
    )
  })

  it('drops painting for a medium and lists the blocks before the title', () => {
    expect(suggestPrompt('Nachtzug', 'dreamy', true, ['neon', 'pencil'])).toBe(
      `A square picture, one striking scene with a strong central motif, high detail. Its mood and colours evoke dreamy music. ${pencil} ${neon} The words "Nachtzug" are written once in elegant, legible lettering.`,
    )
  })
})
