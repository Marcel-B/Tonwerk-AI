<script setup lang="ts">
import { computed, onUnmounted, ref, watchEffect } from 'vue'
import { formatDuration, stageLabel, stepLabel, t } from '../i18n'
import type { SongState, Stage } from '../types'

const props = defineProps<{ song: SongState }>()

/**
 * The stages a song still has ahead of it, in the worker's order. A render starts from saved tokens, so it only
 * synthesizes and decodes. Planning is skipped when planning is off, which the worker does not announce: the step
 * shows as pending until composing begins and then drops out.
 */
const PIPELINE: Stage[] = ['planning', 'tokens', 'synth', 'decode']
const RENDER_PIPELINE: Stage[] = ['synth', 'decode']

type Step = {
  stage: Stage
  state: 'done' | 'current' | 'pending'
  /** Seconds the stage worked, or has worked so far: its clock stops while it waits for an engine. */
  seconds?: number
  /** Seconds it waited meanwhile, shown apart. */
  waited?: number
}

// The current stage's time runs while the page is open; one tick a second is enough for m:ss.
const now = ref(Date.now())
let timer: number | undefined
watchEffect(() => {
  if (!props.song.finished && timer === undefined) {
    timer = window.setInterval(() => (now.value = Date.now()), 1000)
  } else if (props.song.finished && timer !== undefined) {
    window.clearInterval(timer)
    timer = undefined
  }
})
onUnmounted(() => window.clearInterval(timer))

function seconds(from: string, to: string | number): number {
  return Math.max(0, (new Date(to).getTime() - new Date(from).getTime()) / 1000)
}

const steps = computed<Step[]>(() => {
  const song = props.song
  const history = song.stages ?? []
  // A finished song's last entry is its end (ready, failed, cancelled); the queue says that in the line above,
  // and a sixth step would not fit a phone's width.
  const shown = song.finished ? history.slice(0, -1) : history
  const steps: Step[] = shown.map((entry, i) => {
    const next = history[i + 1]
    // A wait still running belongs to the current stage; a server from before waits were counted sends none.
    const open = !next && song.waitingSince ? seconds(song.waitingSince, now.value) : 0
    const waited = (entry.waited ?? 0) + open
    const length = next ? seconds(entry.startedAt, next.startedAt) : seconds(entry.startedAt, now.value)
    return {
      stage: entry.stage,
      state: next ? 'done' : 'current',
      seconds: Math.max(0, length - waited),
      waited,
    }
  })
  if (!song.finished) {
    const pipeline = song.render ? RENDER_PIPELINE : PIPELINE
    const reached = pipeline.indexOf(song.stage)
    steps.push(...pipeline.slice(reached + 1).map((stage): Step => ({ stage, state: 'pending' })))
  }
  return steps
})

const RADIUS = 8.5
const CIRCUMFERENCE = 2 * Math.PI * RADIUS

function icon(step: Step): string {
  if (step.state === 'done') return 'pi pi-check'
  if (step.state === 'current') return 'pi pi-spin pi-spinner'
  return 'pi pi-circle'
}

function iconColor(step: Step): string {
  return step.state === 'pending' ? 'text-muted-color' : 'text-primary'
}
</script>

<template>
  <Timeline
    :value="steps"
    layout="horizontal"
    class="mt-3"
    :pt="{
      event: { class: 'flex-1 min-w-0' },
      eventOpposite: { class: 'hidden' },
      eventContent: { class: 'min-w-0' },
    }"
  >
    <template #marker="{ item }">
      <!-- The running stage's marker is its progress: a ring that fills as the worker reports it. Waiting has no
           fraction to show, so it spins. -->
      <svg
        v-if="item.state === 'current' && item.stage !== 'queued'"
        viewBox="0 0 20 20"
        class="size-4 -rotate-90 text-primary"
        role="progressbar"
        :aria-valuenow="Math.round(song.fraction * 100)"
        aria-valuemin="0"
        aria-valuemax="100"
        :aria-label="stageLabel(item.stage)"
      >
        <circle cx="10" cy="10" :r="RADIUS" fill="none" stroke="currentColor" stroke-width="3" opacity="0.25" />
        <circle
          cx="10"
          cy="10"
          :r="RADIUS"
          fill="none"
          stroke="currentColor"
          stroke-width="3"
          :stroke-dasharray="CIRCUMFERENCE"
          :stroke-dashoffset="CIRCUMFERENCE * (1 - Math.min(1, Math.max(0, song.fraction)))"
          class="transition-[stroke-dashoffset] duration-300"
        />
      </svg>
      <span v-else class="inline-flex size-4 items-center justify-center">
        <i :class="[icon(item), iconColor(item), 'text-sm']" aria-hidden="true" />
      </span>
    </template>
    <template #content="{ item }">
      <div class="pr-1 text-xs leading-tight sm:text-sm" :title="stageLabel(item.stage)">
        <div :class="item.state === 'pending' ? 'text-muted-color' : 'font-medium'">
          {{ stepLabel(item.stage) }}
        </div>
        <div v-if="item.seconds !== undefined" class="text-muted-color tabular-nums">
          {{ formatDuration(item.seconds) }}
        </div>
        <div v-if="(item.waited ?? 0) >= 1" class="text-muted-color tabular-nums text-xs">
          {{ t('stageWaited', { time: formatDuration(item.waited!) }) }}
        </div>
      </div>
    </template>
  </Timeline>
  <div v-if="!song.finished && (song.engine || song.detail)" class="mt-1 mb-3 text-sm text-muted-color">
    <span v-if="song.engine">{{ song.engine }}</span>
    <span v-if="song.engine && song.detail"> · </span>{{ song.detail }}
  </div>
</template>
