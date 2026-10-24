import type { AneProgramsRecord, SynthesisRecord } from './types'

/** Engines in their fixed colour order (series slots in the component's style); anything else is slot 4. */
export const chartEngines = ['mlx', 'ane', 'remote'] as const

export function engineSlot(engine: string): number {
  const index = (chartEngines as readonly string[]).indexOf(engine)
  return index < 0 ? 4 : index + 1
}

/** A rounded top for a value axis starting at 0: 1, 2 or 5 times a power of ten, at least the largest value. */
export function niceMax(value: number): number {
  if (!(value > 0)) {
    return 1
  }
  const power = 10 ** Math.floor(Math.log10(value))
  return [1, 2, 5, 10].map((f) => f * power).find((top) => top >= value - 1e-9) ?? 10 * power
}

/** Ticks from 0 to the top: four steps when the top is a 2, five otherwise. */
export function ticks(top: number): number[] {
  const lead = top / 10 ** Math.floor(Math.log10(top))
  const count = Math.round(lead) === 2 ? 4 : 5
  return Array.from({ length: count + 1 }, (_, i) => (top * i) / count)
}

export interface Plot {
  width: number
  height: number
  left: number
  top: number
  bottom: number
}

/** Where entry i of n sits horizontally: evenly spaced, oldest on the left. */
export function slotX(plot: Plot, i: number, n: number): number {
  const inner = plot.width - plot.left - 8
  return plot.left + (n <= 1 ? inner / 2 : (inner * i) / (n - 1))
}

export function valueY(plot: Plot, value: number, top: number): number {
  const inner = plot.height - plot.top - plot.bottom
  return plot.top + inner * (1 - Math.min(value, top) / top)
}

export interface RatePoint {
  x: number
  y: number
  slot: number
  /** Fewer steps than full quality: drawn hollow, since a draft is quicker per second of audio. */
  draft: boolean
  record: SynthesisRecord
}

/** The syntheses as dots, oldest first (the server sends newest first). */
export function ratePoints(recent: SynthesisRecord[], plot: Plot): { top: number; points: RatePoint[] } {
  const ordered = [...recent].reverse()
  const top = niceMax(Math.max(0, ...ordered.map((r) => r.rate)))
  return {
    top,
    points: ordered.map((record, i) => ({
      x: slotX(plot, i, ordered.length),
      y: valueY(plot, record.rate, top),
      slot: engineSlot(record.engine),
      draft: record.steps < 32,
      record,
    })),
  }
}

export interface CompileBar {
  x: number
  y: number
  width: number
  record: AneProgramsRecord
}

/** The compiled Neural Engine programs as bars of minutes, oldest first; a 2px gap between neighbours. */
export function compileBars(recent: AneProgramsRecord[], plot: Plot): { top: number; bars: CompileBar[] } {
  const ordered = [...recent].reverse()
  const top = niceMax(Math.max(0, ...ordered.map((r) => r.seconds / 60)))
  const inner = plot.width - plot.left - 8
  const width = Math.max(2, Math.min(16, inner / Math.max(ordered.length, 1) - 2))
  return {
    top,
    bars: ordered.map((record, i) => {
      const centre =
        ordered.length <= 1
          ? plot.left + inner / 2
          : plot.left + width / 2 + ((inner - width) * i) / (ordered.length - 1)
      return { x: centre - width / 2, y: valueY(plot, record.seconds / 60, top), width, record }
    }),
  }
}
