import { describe, expect, it } from 'vitest'
import { fitReferences, styleReferences, withStyleText, type ReferenceItem } from './coverStyles'
import type { CoverStyle } from './types'

const item = (id: string, role: 'style' | 'figure' | 'place' = 'style'): ReferenceItem => ({
  choice: { source: 'upload', id, role },
  preview: `blob:${id}`,
  label: id,
})

describe('fitReferences', () => {
  const items = [item('a', 'figure'), item('b'), item('c', 'place')]

  it('gives a model that looks at pictures as many as it takes, roles kept', () => {
    expect(fitReferences(items, { references: 2 })).toEqual([
      { source: 'upload', id: 'a', role: 'figure', strength: null },
      { source: 'upload', id: 'b', role: 'style', strength: null },
    ])
  })

  it('gives Z-Image and Krea the first picture to paint over', () => {
    expect(fitReferences(items, { references: 0, startImage: true }, 0.7)).toEqual([
      { source: 'upload', id: 'a', role: 'start', strength: 0.7 },
    ])
  })

  it('gives a model that takes no pictures none', () => {
    expect(fitReferences(items, { references: 0 })).toEqual([])
    expect(fitReferences(items, null)).toEqual([])
  })
})

describe('styleReferences', () => {
  it('names a style picture by style and picture id', () => {
    const style = { id: 3, name: 'Noir', images: [{ id: 'f00', role: 'place' }] } as unknown as CoverStyle
    expect(styleReferences(style, (s, i) => `/s/${s}/${i}`)).toEqual([
      { choice: { source: 'style', id: '3/f00', role: 'place' }, preview: '/s/3/f00', label: 'Noir' },
    ])
  })
})

describe('withStyleText', () => {
  it('swaps one style text for the next and never doubles it', () => {
    const first = withStyleText('A city at night.', null, 'Neon rain.')
    expect(first).toBe('A city at night. Neon rain.')
    expect(withStyleText(first, 'Neon rain.', 'Pencil sketch.')).toBe('A city at night. Pencil sketch.')
    expect(withStyleText(first, null, 'Neon rain.')).toBe(first)
    expect(withStyleText(first, 'Neon rain.', null)).toBe('A city at night.')
  })

  it('leaves a description whose style text was edited away alone', () => {
    expect(withStyleText('A city.', 'Neon rain.', null)).toBe('A city.')
  })
})
