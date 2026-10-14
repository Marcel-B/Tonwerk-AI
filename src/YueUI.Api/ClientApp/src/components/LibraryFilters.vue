<script setup lang="ts">
import { computed } from 'vue'
import Message from 'primevue/message'
import { t } from '../i18n'
import { ratings } from '../ratings'
import { reviewCount, reviewDayChoices, reviewDays } from '../review'
import { parseQuery, type SearchScope } from '../search'
import { librarySort, sorts } from '../sort'
import { traitFilters, type TraitFilter } from '../traits'
import type { RunInfo } from '../types'

/**
 * Search, rating filter and order above the library, and the hint at songs left unrated. The list keeps the values,
 * since it filters by them and a link to a hidden song clears them.
 */
const props = defineProps<{
  runs: RunInfo[]
  /** How many runs the filters let through, for the count of hits. */
  shown: number
}>()

const query = defineModel<string>('query', { required: true })
const scope = defineModel<SearchScope>('scope', { required: true })
/**
 * Only songs with at least this many stars (5: only those with five); 0 shows every song. `review` shows the songs
 * left unrated for longer than `reviewDays`, to be heard again and rated or deleted.
 */
const minRating = defineModel<number | 'review'>('minRating', { required: true })
/** Only songs heard to be slow, fast, in minor, sung unclearly, … */
const trait = defineModel<TraitFilter>('trait', { required: true })

const scopes = computed(() => [
  { value: 'titleStyle', label: t('searchTitleStyle') },
  { value: 'lyrics', label: t('searchLyrics') },
])
const ratingFilters = computed(() => [
  { value: 0, label: t('ratingAll') },
  { value: 5, label: t('ratingOnly', { n: 5 }) },
  ...[4, 3, 2, 1].map((n) => ({ value: n, label: t('ratingAtLeast', { n }) })),
  { value: 'review', label: t('ratingReview') },
])
const sortOptions = computed(() =>
  sorts.map((value) => ({
    value,
    label: t(`sort_${value}`),
  })),
)
const traitOptions = computed(() => traitFilters.map((value) => ({ value, label: t(`trait_${value}`) })))
const dayOptions = computed(() =>
  reviewDayChoices.map((days) => ({ value: days, label: t('reviewDays', { n: days }) })),
)

const reviewing = computed(() => minRating.value === 'review')
const toReview = computed(() => reviewCount(props.runs, ratings.value, reviewDays.value))
/** Some runs are hidden, by the search or the rating filter; the count of hits shows. */
const filtering = computed(() => parseQuery(query.value).length > 0 || minRating.value !== 0 || trait.value !== 'all')

function startReview(): void {
  query.value = ''
  minRating.value = 'review'
}

function clearSearch(): void {
  query.value = ''
  minRating.value = 0
  trait.value = 'all'
}
</script>

<template>
  <!-- Nothing is deleted by itself: songs left unrated for a while are only offered, to be heard, rated or deleted. -->
  <div v-if="reviewing || toReview > 0" class="mb-3">
    <Message v-if="!reviewing" severity="info" size="small">
      <div class="flex flex-wrap items-center gap-x-3 gap-y-1">
        <span class="flex-1 min-w-0">
          <i class="pi pi-star mr-1" aria-hidden="true" />
          {{ t(toReview === 1 ? 'reviewWaitingOne' : 'reviewWaiting', { count: toReview, n: reviewDays }) }}
        </span>
        <Button :label="t('reviewStart')" size="small" text class="shrink-0" @click="startReview" />
      </div>
    </Message>
    <Message v-else severity="info" size="small">
      <div class="flex flex-wrap items-center gap-x-2 gap-y-1">
        <span>{{ t('reviewOlderThan') }}</span>
        <Select
          v-model="reviewDays"
          :options="dayOptions"
          option-label="label"
          option-value="value"
          :aria-label="t('reviewOlderThan')"
          size="small"
        />
        <span class="basis-full muted">{{ t('reviewHint') }}</span>
      </div>
    </Message>
  </div>
  <form v-if="runs.length > 0" class="search" role="search" @submit.prevent>
    <InputText
      v-model="query"
      type="search"
      :placeholder="scope === 'lyrics' ? t('searchLyricsPlaceholder') : t('searchPlaceholder')"
      :aria-label="t('search')"
      enterkeyhint="search"
      autocomplete="off"
      class="min-w-0 basis-full sm:flex-1 sm:basis-0"
    />
    <SelectButton
      v-model="scope"
      :options="scopes"
      option-label="label"
      option-value="value"
      :allow-empty="false"
      :aria-label="t('searchScope')"
      size="small"
    />
    <Select
      v-model="minRating"
      :options="ratingFilters"
      option-label="label"
      option-value="value"
      :aria-label="t('ratingFilter')"
      v-tooltip.top="t('ratingFilter')"
      size="small"
      class="shrink-0"
    />
    <Select
      v-model="trait"
      :options="traitOptions"
      option-label="label"
      option-value="value"
      :aria-label="t('traitFilter')"
      v-tooltip.top="t('traitFilter')"
      size="small"
      class="shrink-0"
    />
    <Select
      v-model="librarySort"
      :options="sortOptions"
      option-label="label"
      option-value="value"
      :aria-label="t('sortBy')"
      v-tooltip.top="t('sortBy')"
      size="small"
      class="shrink-0"
    />
  </form>
  <p v-if="filtering" class="muted text-sm mt-2 mb-0">
    <template v-if="reviewing && shown === 0">{{ t('reviewDone') }}</template>
    <template v-else>{{
      shown === 0 ? t('searchNone') : t('searchHits', { count: shown, total: runs.length })
    }}</template>
    <Button :label="t('searchClear')" text size="small" @click="clearSearch" />
  </p>
</template>

<style scoped>
/* On a phone the field takes the first line, scope, rating filter and order share the second. */
.search {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 0.5rem;
  margin-bottom: 0.5rem;
}
</style>
