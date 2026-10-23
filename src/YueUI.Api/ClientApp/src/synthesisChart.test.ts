import { describe, expect, it } from 'vitest'
import { compileBars, engineSlot, niceMax, ratePoints, ticks, type Plot } from './synthesisChart'
import type { SynthesisRecord } from './types'

const plot: Plot = { width: 340, height: 150, left: 32, top: 8, bottom: 8 }

function record(rate: number, engine = 'mlx', steps = 32): SynthesisRecord {
  return { at: '2026-10-24T00:00:00Z', song: 'run/song1', engine, steps, seconds: rate * 200, rate }
}

describe('synthesis chart', () => {
  it('rounds the axis up to 1, 2 or 5 of a power of ten', () => {
    expect(niceMax(3.5)).toBe(5)
    expect(niceMax(8.3)).toBe(10)
    expect(niceMax(12)).toBe(20)
    expect(niceMax(0)).toBe(1)
    expect(ticks(5)).toEqual([0, 1, 2, 3, 4, 5])
    expect(ticks(20)).toEqual([0, 5, 10, 15, 20])
  })

  it('keeps each engine on its colour whatever else is shown', () => {
    expect(engineSlot('mlx')).toBe(1)
    expect(engineSlot('ane')).toBe(2)
    expect(engineSlot('torch')).toBe(4)
  })

  it('puts the oldest synthesis on the left and a draft hollow', () => {
    const { top, points } = ratePoints([record(8.3, 'ane'), record(2.5, 'mlx', 8)], plot)
    expect(top).toBe(10)
    expect(points.map((p) => p.record.engine)).toEqual(['mlx', 'ane'])
    expect(points[0]!.draft).toBe(true)
    expect(points[0]!.x).toBeLessThan(points[1]!.x)
    expect(points[1]!.y).toBeLessThan(points[0]!.y)
  })

  it('draws compiles as bars of minutes inside the plot', () => {
    const recent = Array.from({ length: 30 }, (_, i) => ({
      at: '',
      song: `s${i}`,
      seconds: 60 * (i + 1),
      waited: i % 2 === 0,
    }))
    const { top, bars } = compileBars(recent, plot)
    expect(top).toBe(50)
    expect(bars[0]!.record.song).toBe('s29')
    expect(bars.at(-1)!.x + bars.at(-1)!.width).toBeLessThanOrEqual(plot.width - 8 + 1e-9)
    expect(bars[1]!.x - (bars[0]!.x + bars[0]!.width)).toBeGreaterThanOrEqual(2 - 1e-9)
  })
})
