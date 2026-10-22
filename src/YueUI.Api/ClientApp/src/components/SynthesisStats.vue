<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { getSynthesisStats } from '../api'
import { formatDateTime, formatDuration, locale, t } from '../i18n'
import type { SynthesisOverview } from '../types'

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
const rate = (value: number) => t('synthStatsRate', { rate: number.value.format(value) })
</script>

<template>
  <details class="mt-4" @toggle="open = ($event.target as HTMLDetailsElement).open">
    <summary>{{ t('synthStats') }}</summary>
    <div v-if="open" class="mt-2 flex flex-col gap-3 text-sm">
      <p class="muted">{{ t('synthStatsHint') }}</p>
      <p v-if="failed" class="danger">{{ t('synthStatsEmpty') }}</p>
      <template v-else-if="overview">
        <p v-if="overview.engines.length === 0" class="muted">{{ t('synthStatsEmpty') }}</p>
        <ul v-else class="flex flex-col gap-1">
          <li v-for="row in overview.engines" :key="`${row.engine}-${row.steps}`" class="flex flex-wrap gap-x-3">
            <span class="font-medium">{{ row.engine }}</span>
            <span class="muted">{{ t('synthStatsSteps', { n: row.steps }) }}</span>
            <span class="tabular-nums">{{ rate(row.meanRate) }}</span>
            <span class="muted tabular-nums">{{ t('synthStatsBest', { rate: number.format(row.bestRate) }) }}</span>
            <span class="muted tabular-nums">{{ t('synthStatsCount', { n: row.count }) }}</span>
          </li>
        </ul>
        <p v-if="overview.anePrograms.count > 0" class="muted">
          {{
            t('synthStatsAne', {
              n: overview.anePrograms.count,
              mean: formatDuration(overview.anePrograms.meanSeconds),
              longest: formatDuration(overview.anePrograms.longestSeconds),
            })
          }}
        </p>
        <div v-if="overview.recent.length > 0">
          <div class="font-medium">{{ t('synthStatsRecent') }}</div>
          <div v-for="(entry, i) in overview.recent" :key="i" class="flex flex-wrap gap-x-3 tabular-nums">
            <span class="muted">{{ formatDateTime(entry.at) }}</span>
            <span>{{ entry.engine }}</span>
            <span>{{ formatDuration(entry.seconds) }}</span>
            <span class="muted">{{ rate(entry.rate) }}</span>
            <span class="muted truncate">{{ entry.song }}</span>
          </div>
        </div>
      </template>
    </div>
  </details>
</template>
