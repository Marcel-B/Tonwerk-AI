<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { ApiError } from '../api'
import { locale, t, type MessageKey } from '../i18n'
import { insights, loadInsights } from '../insights'
import type { InsightEntry, InsightGroup } from '../types'

/** Which style blocks and settings the rated songs were made with, against their stars. */
const props = defineProps<{ active: boolean }>()

const loadError = ref<string | null>(null)

// Asked again each time the page is shown: songs are rated on other pages meanwhile.
watch(
  () => props.active,
  (active) => {
    if (active) {
      loadInsights(true)
        .then(() => (loadError.value = null))
        .catch((caught: unknown) => {
          loadError.value =
            caught instanceof ApiError && caught.status === 0
              ? t('errorNetwork')
              : t('errorGeneric', { message: caught instanceof Error ? caught.message : String(caught) })
        })
    }
  },
  { immediate: true },
)

/** The style blocks are many: the best and, apart, the weakest; the other groups are short enough to show whole. */
const shown = 10
const weakest = 5

const groups = computed(() =>
  (insights.value?.groups ?? []).map((group) => {
    if (group.key !== 'style' || group.entries.length <= shown + weakest) {
      return { group, best: group.entries, weak: [] as InsightEntry[] }
    }
    return { group, best: group.entries.slice(0, shown), weak: group.entries.slice(-weakest) }
  }),
)

function valueLabel(group: InsightGroup, value: string): string {
  switch (group.key) {
    case 'engine':
      return value === 'ace' ? 'ACE-Step' : 'YuE2'
    case 'quality':
      return t(value === 'full' ? 'qualityFull' : 'qualityDraft')
    case 'lora':
      return value === '' ? t('insightsNoLora') : value
    case 'instrumental':
      return t(value === 'yes' ? 'insightsYes' : 'insightsNo')
    case 'length':
      return t(
        `insightsLength_${value === '<2' ? 'short' : value === '4+' ? 'long' : value.replace('-', '_')}` as MessageKey,
      )
    default:
      return value
  }
}

function stars(value: number): string {
  return value.toLocaleString(locale.value, { minimumFractionDigits: 1, maximumFractionDigits: 1 })
}

/** Better or worse than the average song, by more than a little. */
function tone(entry: InsightEntry): string {
  const average = insights.value?.average ?? 0
  return entry.score > average + 0.15 ? 'good' : entry.score < average - 0.15 ? 'weak' : ''
}
</script>

<template>
  <div>
    <p v-if="loadError" class="danger">{{ loadError }}</p>
    <p v-else-if="insights && insights.rated === 0" class="muted">{{ t('insightsEmpty') }}</p>
    <template v-else-if="insights">
      <p class="mt-0">{{ t('insightsSummary', { n: insights.rated, average: stars(insights.average) }) }}</p>
      <section v-for="{ group, best, weak } in groups" :key="group.key" class="mb-5">
        <h3 class="mb-2">{{ t(`insightsGroup_${group.key}` as MessageKey) }}</h3>
        <ul class="m-0 p-0 list-none flex flex-col gap-1">
          <li v-for="entry in best" :key="entry.value" :class="['row', tone(entry)]">
            <span class="value truncate">{{ valueLabel(group, entry.value) }}</span>
            <span class="bar" :style="{ '--share': `${(entry.average / 5) * 100}%` }" />
            <span class="numbers">★ {{ stars(entry.average) }} · {{ t('insightsSongs', { n: entry.count }) }}</span>
          </li>
        </ul>
        <template v-if="weak.length > 0">
          <h4 class="mt-3 mb-1 text-sm text-muted-color font-medium">{{ t('insightsWeak') }}</h4>
          <ul class="m-0 p-0 list-none flex flex-col gap-1">
            <li v-for="entry in weak" :key="entry.value" :class="['row', tone(entry)]">
              <span class="value truncate">{{ valueLabel(group, entry.value) }}</span>
              <span class="bar" :style="{ '--share': `${(entry.average / 5) * 100}%` }" />
              <span class="numbers">★ {{ stars(entry.average) }} · {{ t('insightsSongs', { n: entry.count }) }}</span>
            </li>
          </ul>
        </template>
      </section>
      <p class="muted text-sm">{{ t('insightsHow') }}</p>
    </template>
    <p v-else class="muted"><i class="pi pi-spin pi-spinner" /></p>
  </div>
</template>

<style scoped>
.row {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 4.5rem 7.5rem;
  gap: 0.6rem;
  align-items: center;
}
.bar {
  height: 0.4rem;
  border-radius: 0.2rem;
  background: linear-gradient(to right, var(--text-muted) var(--share), var(--surface-sunken) var(--share));
}
.good .bar {
  background: linear-gradient(to right, var(--accent) var(--share), var(--surface-sunken) var(--share));
}
.good .value {
  font-weight: 600;
}
.weak .value {
  color: var(--text-muted);
}
.numbers {
  font-size: 0.85rem;
  color: var(--text-muted);
  white-space: nowrap;
  text-align: right;
}
</style>
