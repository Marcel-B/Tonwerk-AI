import { computed, ref } from 'vue'
import { getInsights } from './api'
import type { InsightReport } from './types'

/** The last report; null until asked for. The songs page's ratings change it, so the page asks again when shown. */
export const insights = ref<InsightReport | null>(null)

let loading: Promise<void> | null = null

export function loadInsights(force = false): Promise<void> {
  if (loading && !force) {
    return loading
  }
  loading = getInsights()
    .then((report) => {
      insights.value = report
    })
    .catch((caught: unknown) => {
      loading = null
      throw caught
    })
  return loading
}

/** How many proven blocks the create page offers. */
const provenCount = 12

/**
 * The style blocks of songs rated above the average, best first: what the create page offers as "proven". Blocks
 * written only once say nothing yet and are left out by the server.
 */
export const provenStyleBlocks = computed(() => {
  const report = insights.value
  const style = report?.groups.find((group) => group.key === 'style')
  return (style?.entries ?? [])
    .filter((entry) => entry.score > report!.average)
    .slice(0, provenCount)
    .map((entry) => entry.value)
})
