import { computed, ref, watch, type Ref } from 'vue'
import { listMachines } from './api'
import { t, type MessageKey } from './i18n'
import type { MachineInfo } from './types'

/** The engines a helper can run, by the server's names (`Machines.Engines`). */
export type MachineEngine = 'lyrics' | 'speech' | 'images' | 'ace' | 'voices' | 'stems' | 'singing'

/** An engine to ask for: one of those, or one image model (`images:<id>`), since helpers differ in which they can paint. */
export type MachineNeed = MachineEngine | `images:${string}`

/** This Mac and the helpers the server may hand a job to; empty where it has none to say (an older server). */
export const machines = ref<MachineInfo[]>([])

let loading: Promise<void> | null = null

/**
 * Reads the machines once per page visit: the pages with a choice call it when they open, so a helper switched on
 * meanwhile shows up, while several pages opening together share one request.
 */
export function loadMachines(): Promise<void> {
  loading ??= listMachines()
    .then((list) => {
      machines.value = list
    })
    .catch(() => {
      // Without the list there is no choice; jobs go wherever the server puts them.
    })
    .finally(() => {
      loading = null
    })
  return loading
}

/** Whether another Mac than this one is offered, the only case the pages show a choice. */
export const helpersOffered = computed(() => machines.value.some((machine) => !machine.local))

/** "auf <Mac>" under a job, only where it says something: with helpers, or when it ran on another Mac than this one. */
export function ranOnNote(ranOn: string | null | undefined, list: MachineInfo[] = machines.value): string | null {
  if (!ranOn) {
    return null
  }
  const local = list.find((machine) => machine.local)?.name
  const helpers = list.some((machine) => !machine.local)
  return helpers || (local !== undefined && local !== ranOn) ? t('ranOn', { name: ranOn }) : null
}

/** Engine names as the picker marks a Mac without one. */
const engineLabels: Record<MachineEngine, MessageKey> = {
  lyrics: 'machineEngineLyrics',
  speech: 'machineEngineSpeech',
  images: 'machineEngineImages',
  ace: 'machineEngineAce',
  voices: 'machineEngineVoices',
  stems: 'machineEngineStems',
  singing: 'machineEngineSinging',
}

export interface MachineOption {
  value: string
  label: string
}

/**
 * The picker's entries: automatic (empty), this Mac first, then the helpers in the server's order. A Mac that cannot be
 * reached or lacks the engine is marked, not hidden, and a choice whose Mac is gone stays listed, so a choice kept from
 * before stays visible.
 */
export function machineOptions(
  engine: MachineNeed,
  selected: string,
  list: MachineInfo[] = machines.value,
): MachineOption[] {
  const ordered = [...list.filter((m) => m.local), ...list.filter((m) => !m.local)]
  const options: MachineOption[] = [{ value: '', label: t('machineAuto') }]
  for (const machine of ordered) {
    let label = machine.name
    if (!machine.reachable) {
      label = t('machineUnreachable', { name: label })
    } else if (!machine.engines.includes(engine)) {
      label = t('machineWithout', { name: label, engine: t(engineLabels[engine.split(':')[0] as MachineEngine]) })
    }
    options.push({ value: machine.id, label })
  }
  if (selected && !list.some((m) => m.id === selected)) {
    options.push({ value: selected, label: t('machineGone', { name: selected }) })
  }
  return options
}

/** A choice as a request sends it: null for automatic. */
export function machineField(selected: string): string | null {
  return selected === '' ? null : selected
}

const stored = new Map<string, Ref<string>>()

/**
 * A page's choice kept per browser, like the other pickers' (`yue-ui.machine.<page>`). One ref per page, so panels of
 * one page (the speech lab's takes and cleanups) share it.
 */
export function storedMachine(page: string): Ref<string> {
  const known = stored.get(page)
  if (known) {
    return known
  }
  const key = `yue-ui.machine.${page}`
  let initial = ''
  try {
    initial = localStorage.getItem(key) ?? ''
  } catch {
    // Storage may be blocked; automatic then.
  }
  const choice = ref(initial)
  watch(choice, (value) => {
    try {
      if (value) {
        localStorage.setItem(key, value)
      } else {
        localStorage.removeItem(key)
      }
    } catch {
      // Remembering the choice is a convenience only.
    }
  })
  stored.set(page, choice)
  return choice
}
