import { beforeEach, describe, expect, test } from 'vitest'
import { locale } from './i18n'
import { machineField, machineOptions, ranOnNote } from './machines'
import type { MachineInfo } from './types'

function machine(id: string, name: string, local: boolean, extra: Partial<MachineInfo> = {}): MachineInfo {
  return { id, name, local, reachable: true, holder: null, busy: false, engines: ['speech', 'images:klein'], ...extra }
}

const alone = [machine('local', 'Mac mini', true)]
const withHelper = [
  machine('mini', 'oi-o-mat', false, { engines: ['speech'] }),
  machine('local', 'Mac mini', true),
  machine('old', 'Alter Mac', false, { reachable: false }),
]

beforeEach(() => {
  locale.value = 'en'
})

describe('ranOnNote', () => {
  test('says nothing on a server without helpers when the job ran here', () => {
    expect(ranOnNote('Mac mini', alone)).toBeNull()
    expect(ranOnNote(null, withHelper)).toBeNull()
  })

  test('names the Mac with helpers, or when another one ran it', () => {
    expect(ranOnNote('Mac mini', withHelper)).toBe('on Mac mini')
    expect(ranOnNote('oi-o-mat', alone)).toBe('on oi-o-mat')
  })
})

describe('machineOptions', () => {
  test('automatic first, this Mac before the helpers, missing engines and unreachable Macs marked', () => {
    expect(machineOptions('speech', '', withHelper).map((o) => [o.value, o.label])).toEqual([
      ['', 'Computer: automatic'],
      ['local', 'Mac mini'],
      ['mini', 'oi-o-mat'],
      ['old', 'Alter Mac · unreachable'],
    ])
    expect(machineOptions('images:klein', '', withHelper)[2]!.label).toBe('oi-o-mat · without image model')
  })

  test('a choice whose Mac is gone stays listed', () => {
    expect(machineOptions('speech', 'gone', alone).at(-1)).toEqual({ value: 'gone', label: 'gone · no longer there' })
  })

  test('automatic goes out as null', () => {
    expect(machineField('')).toBeNull()
    expect(machineField('mini')).toBe('mini')
  })
})
