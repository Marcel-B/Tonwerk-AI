<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { getSynthesisStats } from '../api'
import { formatDateTime, formatDuration, locale, t } from '../i18n'
import { chartEngines, compileBars, engineSlot, ratePoints, ticks, valueY, type Plot } from '../synthesisChart'
import type { AneProgramsRecord, SynthesisOverview, SynthesisRecord } from '../types'

/** Read when opened and again whenever a song finishes, since each finished synthesis adds a row. */
const props = defineProps<{ finished: number }>()

const open = ref(false)
const overview = ref<SynthesisOverview | null>(null)
const failed = ref(false)

async function load(): Promise<void> {
  try {
    overview.value = await getSynthesisStats()
    failed.value = false
  } catch {
    failed.value = true
  }
}

watch([open, () => props.finished], () => {
  if (open.value) {
    void load()
  }
})

const number = computed(
  () => new Intl.NumberFormat(locale.value, { maximumFractionDigits: 1, minimumFractionDigits: 1 }),
)
const axisNumber = computed(() => new Intl.NumberFormat(locale.value, { maximumFractionDigits: 1 }))
const rate = (value: number) => t('synthStatsRate', { rate: number.value.format(value) })

// The charts are drawn in pixels of their box, so text and dots keep their size on a phone.
const width = ref(0)
let observer: ResizeObserver | null = null
function measure(element: unknown): void {
  observer?.disconnect()
  observer = null
  if (element instanceof HTMLElement) {
    observer = new ResizeObserver((entries) => (width.value = Math.round(entries[0]?.contentRect.width ?? 0)))
    observer.observe(element)
  }
}
onBeforeUnmount(() => observer?.disconnect())

const plot = computed<Plot>(() => ({ width: Math.max(width.value, 200), height: 150, left: 32, top: 8, bottom: 8 }))
const rates = computed(() => ratePoints(overview.value?.recent ?? [], plot.value))
const compiles = computed(() => compileBars(overview.value?.aneRecent ?? [], plot.value))
const shownEngines = computed(() => {
  const seen = new Set((overview.value?.recent ?? []).map((r) => r.engine))
  return [...chartEngines.filter((e) => seen.has(e)), ...[...seen].filter((e) => engineSlot(e) === 4)]
})
const hasDrafts = computed(() => rates.value.points.some((p) => p.draft))
const waitedCount = computed(() => (overview.value?.aneRecent ?? []).filter((r) => r.waited).length)
const aneSyntheses = computed(() =>
  (overview.value?.engines ?? []).filter((e) => e.engine === 'ane').reduce((sum, e) => sum + e.count, 0),
)

/** The mark under the finger or pointer; its numbers show under the chart. */
const pickedRate = ref<SynthesisRecord | null>(null)
const pickedCompile = ref<AneProgramsRecord | null>(null)

function nearest<T extends { x: number }>(event: PointerEvent, marks: T[], centre: (mark: T) => number): T | null {
  const box = (event.currentTarget as SVGElement).getBoundingClientRect()
  const x = event.clientX - box.left
  let best: T | null = null
  for (const mark of marks) {
    if (best === null || Math.abs(centre(mark) - x) < Math.abs(centre(best) - x)) {
      best = mark
    }
  }
  return best
}

function pickRate(event: PointerEvent): void {
  pickedRate.value = nearest(event, rates.value.points, (p) => p.x)?.record ?? null
}

function pickCompile(event: PointerEvent): void {
  pickedCompile.value = nearest(event, compiles.value.bars, (b) => b.x + b.width / 2)?.record ?? null
}
</script>

<template>
  <details class="mt-4" @toggle="open = ($event.target as HTMLDetailsElement).open">
    <summary>{{ t('synthStats') }}</summary>
    <div v-if="open" :ref="measure" class="synth-stats mt-2 flex flex-col gap-4 text-sm">
      <p v-if="failed" class="danger">{{ t('synthStatsEmpty') }}</p>
      <template v-else-if="overview">
        <p v-if="overview.recent.length === 0" class="muted">{{ t('synthStatsEmpty') }}</p>
        <figure v-else class="m-0 flex flex-col gap-1">
          <figcaption class="font-medium">{{ t('synthStatsRateChart') }}</figcaption>
          <div class="legend muted">
            <span v-for="engine in shownEngines" :key="engine" class="flex items-center gap-1">
              <span class="swatch" :class="`slot-${engineSlot(engine)}`" />{{ engine }}
            </span>
            <span v-if="hasDrafts" class="flex items-center gap-1">
              <span class="swatch hollow" />{{ t('synthStatsDrafts') }}
            </span>
          </div>
          <svg
            :width="plot.width"
            :height="plot.height"
            class="chart"
            role="img"
            :aria-label="t('synthStatsRateChart')"
            @pointermove="pickRate"
            @pointerdown="pickRate"
          >
            <g v-for="tick in ticks(rates.top)" :key="tick">
              <line
                class="grid"
                :x1="plot.left"
                :x2="plot.width"
                :y1="valueY(plot, tick, rates.top)"
                :y2="valueY(plot, tick, rates.top)"
              />
              <text class="axis" :x="plot.left - 6" :y="valueY(plot, tick, rates.top) + 4" text-anchor="end">
                {{ axisNumber.format(tick) }}
              </text>
            </g>
            <circle
              v-for="(point, i) in rates.points"
              :key="i"
              :cx="point.x"
              :cy="point.y"
              :r="pickedRate === point.record ? 6 : 4"
              class="dot"
              :class="[`slot-${point.slot}`, { hollow: point.draft }]"
            />
          </svg>
          <p class="muted tabular-nums">
            <template v-if="pickedRate">
              {{ formatDateTime(pickedRate.at) }} · {{ pickedRate.engine }} ·
              {{ t('synthStatsSteps', { n: pickedRate.steps }) }} · {{ formatDuration(pickedRate.seconds) }} ·
              {{ rate(pickedRate.rate) }} · {{ pickedRate.song }}
            </template>
            <template v-else>{{ t('synthStatsHint') }}</template>
          </p>
        </figure>

        <figure v-if="overview.aneRecent.length > 0" class="m-0 flex flex-col gap-1">
          <figcaption class="font-medium">{{ t('synthStatsCompileChart') }}</figcaption>
          <div class="legend muted">
            <span class="flex items-center gap-1"><span class="swatch slot-2" />{{ t('synthStatsWaited') }}</span>
            <span class="flex items-center gap-1">
              <span class="swatch slot-2 hollow" />{{ t('synthStatsBackground') }}
            </span>
          </div>
          <svg
            :width="plot.width"
            :height="plot.height"
            class="chart"
            role="img"
            :aria-label="t('synthStatsCompileChart')"
            @pointermove="pickCompile"
            @pointerdown="pickCompile"
          >
            <g v-for="tick in ticks(compiles.top)" :key="tick">
              <line
                class="grid"
                :x1="plot.left"
                :x2="plot.width"
                :y1="valueY(plot, tick, compiles.top)"
                :y2="valueY(plot, tick, compiles.top)"
              />
              <text class="axis" :x="plot.left - 6" :y="valueY(plot, tick, compiles.top) + 4" text-anchor="end">
                {{ axisNumber.format(tick) }}
              </text>
            </g>
            <rect
              v-for="(bar, i) in compiles.bars"
              :key="i"
              :x="bar.x"
              :y="bar.y"
              :width="bar.width"
              :height="Math.max(1, valueY(plot, 0, compiles.top) - bar.y)"
              rx="2"
              class="bar slot-2"
              :class="{ hollow: !bar.record.waited, picked: pickedCompile === bar.record }"
            />
          </svg>
          <p class="muted tabular-nums">
            <template v-if="pickedCompile">
              {{ formatDateTime(pickedCompile.at) }} · {{ formatDuration(pickedCompile.seconds) }} ·
              {{ pickedCompile.waited ? t('synthStatsWaited') : t('synthStatsBackground') }} · {{ pickedCompile.song }}
            </template>
            <template v-else>
              {{
                t('synthStatsCompileHint', { n: overview.anePrograms.count, waited: waitedCount, ane: aneSyntheses })
              }}
            </template>
          </p>
        </figure>

        <details v-if="overview.engines.length > 0">
          <summary>{{ t('synthStatsNumbers') }}</summary>
          <div class="mt-2 flex flex-col gap-3">
            <ul class="m-0 flex flex-col gap-1 pl-0" style="list-style: none">
              <li v-for="row in overview.engines" :key="`${row.engine}-${row.steps}`" class="flex flex-wrap gap-x-3">
                <span class="font-medium">{{ row.engine }}</span>
                <span class="muted">{{ t('synthStatsSteps', { n: row.steps }) }}</span>
                <span class="tabular-nums">{{ rate(row.meanRate) }}</span>
                <span class="muted tabular-nums">{{ t('synthStatsBest', { rate: number.format(row.bestRate) }) }}</span>
                <span class="muted tabular-nums">{{ t('synthStatsCount', { n: row.count }) }}</span>
              </li>
            </ul>
            <p v-if="overview.anePrograms.count > 0" class="muted m-0">
              {{
                t('synthStatsAne', {
                  n: overview.anePrograms.count,
                  mean: formatDuration(overview.anePrograms.meanSeconds),
                  longest: formatDuration(overview.anePrograms.longestSeconds),
                })
              }}
            </p>
            <div>
              <div class="font-medium">{{ t('synthStatsRecent') }}</div>
              <div v-for="(entry, i) in overview.recent" :key="i" class="flex flex-wrap gap-x-3 tabular-nums">
                <span class="muted">{{ formatDateTime(entry.at) }}</span>
                <span>{{ entry.engine }}</span>
                <span>{{ formatDuration(entry.seconds) }}</span>
                <span class="muted">{{ rate(entry.rate) }}</span>
                <span class="muted truncate">{{ entry.song }}</span>
              </div>
            </div>
            <div v-if="overview.aneRecent.length > 0">
              <div class="font-medium">{{ t('synthStatsCompileChart') }}</div>
              <div v-for="(entry, i) in overview.aneRecent" :key="i" class="flex flex-wrap gap-x-3 tabular-nums">
                <span class="muted">{{ formatDateTime(entry.at) }}</span>
                <span>{{ formatDuration(entry.seconds) }}</span>
                <span class="muted">{{ entry.waited ? t('synthStatsWaited') : t('synthStatsBackground') }}</span>
                <span class="muted truncate">{{ entry.song }}</span>
              </div>
            </div>
          </div>
        </details>
      </template>
    </div>
  </details>
</template>

<style scoped>
/* Categorical slots in fixed order (mlx, ane, remote, other), each stepped for its theme. */
.synth-stats {
  --series-1: #2a78d6;
  --series-2: #eb6834;
  --series-3: #1baf7a;
  --series-4: #8a8a86;
}

@media (prefers-color-scheme: dark) {
  .synth-stats {
    --series-1: #3987e5;
    --series-2: #d95926;
    --series-3: #199e70;
    --series-4: #9a9a95;
  }
}

.chart {
  display: block;
  touch-action: pan-y;
  overflow: visible;
}

.grid {
  stroke: var(--border);
  stroke-width: 1;
}

.axis {
  fill: var(--text-muted);
  font-size: 11px;
  font-variant-numeric: tabular-nums;
}

.legend {
  display: flex;
  flex-wrap: wrap;
  gap: 0.25rem 0.9rem;
  font-size: 0.8rem;
}

.swatch {
  display: inline-block;
  width: 0.65rem;
  height: 0.65rem;
  border-radius: 999px;
  background: var(--swatch, var(--text-muted));
  border: 2px solid var(--swatch, var(--text-muted));
}

.swatch.hollow {
  background: transparent;
}

.slot-1 {
  --swatch: var(--series-1);
}
.slot-2 {
  --swatch: var(--series-2);
}
.slot-3 {
  --swatch: var(--series-3);
}
.slot-4 {
  --swatch: var(--series-4);
}

/* A ring in the surface's colour keeps overlapping dots apart. */
.dot {
  fill: var(--swatch);
  stroke: var(--surface);
  stroke-width: 2;
}

.dot.hollow {
  fill: var(--surface);
  stroke: var(--swatch);
}

.bar {
  fill: var(--swatch);
  stroke: var(--swatch);
  stroke-width: 1.5;
}

.bar.hollow {
  fill: transparent;
}

.bar.picked {
  stroke: var(--text);
}
</style>
