<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import DataView from 'primevue/dataview'
import Dialog from 'primevue/dialog'
import Fieldset from 'primevue/fieldset'
import { useConfirm } from 'primevue/useconfirm'
import { deleteRun, deleteSong, renameRun } from '../api'
import { formatBytes, formatDateTime, t } from '../i18n'
import { libraryTracks, play, trackOf } from '../player'
import { rate, ratings } from '../ratings'
import { matchesRun, matchingLines, parseQuery, type SearchScope } from '../search'
import { matchesTrait, type TraitFilter } from '../traits'
import { librarySort, sortRuns, type SortedRun } from '../sort'
import { needsReview, reviewDays } from '../review'
import { deletionPlan } from '../librarySelection'
import type { RunInfo, SongInfo, VersionState } from '../types'
import { focusedSong, focusRequest, view } from '../view'
import LibraryFilters from './LibraryFilters.vue'
import SingDialog from './SingDialog.vue'
import SongRow from './SongRow.vue'
import TextActions from './TextActions.vue'

const props = defineProps<{
  runs: RunInfo[]
  loading: boolean
  error: string | null
  /** Songs the worker is working on; they cannot be rendered again meanwhile. */
  busyIds: Set<string>
  /** Seed-VC and the separator are set up, so songs can be sung with another voice. */
  voices: boolean
  /** The separator is set up, so songs can be split into stems on the voices page. */
  stems?: boolean
  /** Versions as the event stream reports them, newer than the listing while they are in the works. */
  liveVersions: VersionState[]
}>()

const emit = defineEmits<{
  template: [run: RunInfo]
  /** The song's score for the next song, together with its run's style and lyrics. */
  /** `warnings` only when the score came from a MIDI file, which then replaces the song's own. */
  useScore: [run: RunInfo, song: SongInfo, abc: string, warnings?: string[]]
  /** Everything the song was made with into the form, its seed and score included, to make it again or vary it. */
  newSong: [songId: string]
  /** Title, style and lyrics as a new ACE-Step song's, on its page. */
  aceTemplate: [songId: string]
  /** Something was deleted; carries the run's title for the notice. */
  deleted: [title: string]
  notice: [message: string]
  error: [message: string]
}>()

const confirm = useConfirm()

function failed(caught: unknown): void {
  emit('error', caught instanceof Error ? caught.message : String(caught))
}

// ---- Filters (LibraryFilters.vue shows them; kept here, since the list filters by them) ---------------

const query = ref('')
const scope = ref<SearchScope>('titleStyle')
const terms = computed(() => parseQuery(query.value))
const searching = computed(() => terms.value.length > 0)
const minRating = ref<number | 'review'>(0)
const trait = ref<TraitFilter>('all')
const reviewing = computed(() => minRating.value === 'review')

/**
 * Songs rated while going through the unrated ones stay in the list until it is left, so a song does not vanish from
 * under the finger that gave it its stars, and the player's queue matches what is listed.
 */
const reviewed = ref(new Set<string>())
watch(reviewing, () => {
  reviewed.value = new Set()
})

/** The songs of the run the rating filter lets through; the run itself (its size, deleting it) stays whole. */
function shownSongs(run: RunInfo): SongInfo[] {
  // Read here, so the list follows a rating that changes while filtered.
  const stars = ratings.value
  const filter = minRating.value
  const heard = run.songs.filter((song) => matchesTrait(song, trait.value))
  if (filter === 'review') {
    return heard.filter((song) => reviewed.value.has(song.id) || needsReview(run, song, stars, reviewDays.value))
  }
  return filter === 0 ? heard : heard.filter((song) => (stars[song.id] ?? 0) >= filter)
}

/** The runs the search and the rating filter let through, in the chosen order, each with the songs to list. */
const shownRuns = computed(() =>
  sortRuns(
    props.runs.filter((run) => matchesRun(run, terms.value, scope.value)),
    librarySort.value,
    ratings.value,
    shownSongs,
  ).filter((entry) => entry.songs.length > 0),
)

// ---- Pages and a song's address ------------------------------------------------------------------------

const pageSize = 8
/** Index of the first run on the current page (DataView's `first`). */
const first = ref(0)
const list = ref<HTMLElement | null>(null)

// A new search starts on the first page; its hits would otherwise hide behind a page number.
watch([terms, scope, minRating, trait, reviewDays], () => {
  first.value = 0
})

// A deletion can empty the last page; go back to the last one that still has runs.
watch(
  () => shownRuns.value.length,
  (length) => {
    if (first.value >= length && length > 0) {
      first.value = Math.floor((length - 1) / pageSize) * pageSize
    }
  },
)

/** The paginator sits below the list, so a new page starts at its top rather than wherever the old one ended. */
function pageChanged(value: number): void {
  first.value = value
  list.value?.scrollIntoView({ behavior: 'smooth', block: 'start' })
}

function anchor(songId: string): string {
  return `song-${songId}`
}

/**
 * A song's address (`#/songs/<run>/songN`) scrolls to it, once per request: a library reload must not pull the page
 * back. A song not listed yet (just finished, or the library still loading) is tried again with the next load.
 */
let scrolledFor = -1
watch(
  [focusRequest, () => props.runs, view],
  async () => {
    const id = focusedSong.value
    if (!id || view.value !== 'songs' || scrolledFor === focusRequest.value) {
      return
    }
    if (!props.runs.some((run) => run.songs.some((song) => song.id === id))) {
      return
    }
    // A link to a song the search hides (from the player or the playlist) must still show it.
    if (!shownRuns.value.some((entry) => entry.songs.some((song) => song.id === id))) {
      query.value = ''
      minRating.value = 0
      trait.value = 'all'
      await nextTick()
    }
    const index = shownRuns.value.findIndex((entry) => entry.songs.some((song) => song.id === id))
    if (index < 0) {
      return
    }
    scrolledFor = focusRequest.value
    first.value = Math.floor(index / pageSize) * pageSize
    await nextTick()
    document.getElementById(anchor(id))?.scrollIntoView({ behavior: 'smooth', block: 'center' })
  },
  { immediate: true },
)

// ---- Songs -----------------------------------------------------------------------------------------------

/**
 * The player goes on with the songs below this one, as the library lists them, so a search, the rating filter or the
 * order is also a play queue ("everything with four stars", "the best first").
 */
function playSong(run: RunInfo, song: SongInfo): void {
  play(trackOf(run, song), libraryTracks(shownRuns.value.map((shown) => ({ ...shown.run, songs: shown.songs }))))
}

async function rateSong(song: SongInfo, rating: number | null): Promise<void> {
  if (reviewing.value) {
    reviewed.value.add(song.id)
  }
  try {
    await rate(song.id, rating)
  } catch (caught) {
    failed(caught)
  }
}

/** The song the voice dialog is for; it shows while set. */
const singing = ref<{ run: RunInfo; song: SongInfo } | null>(null)

// ---- Runs: renaming and deleting -------------------------------------------------------------------------

/** Also a song of the run the worker has not written a folder for yet. */
function runBusy(run: RunInfo): boolean {
  return [...props.busyIds].some((id) => id.startsWith(`${run.id}/`))
}

/** Deleting cannot be undone (the files do not go to the Trash, that would not free the space), so it asks first. */
function askDelete(message: string, title: string, remove: () => Promise<void>): void {
  confirm.require({
    header: t('confirmDelete'),
    message,
    icon: 'pi pi-trash',
    rejectProps: { label: t('keep'), severity: 'secondary', outlined: true },
    acceptProps: { label: t('delete'), severity: 'danger' },
    accept: async () => {
      try {
        await remove()
        emit('deleted', title)
      } catch (caught) {
        failed(caught)
      }
    },
  })
}

function askDeleteRun(run: RunInfo): void {
  const title = run.title || t('untitled')
  askDelete(t('confirmDeleteRun', { count: run.songs.length, title, size: formatBytes(run.bytes) }), title, () =>
    deleteRun(run.id),
  )
}

function askDeleteSong(run: RunInfo, song: SongInfo): void {
  const title = run.title || t('untitled')
  const params = { song: t('songN', { n: song.index }), title, size: formatBytes(song.bytes) }
  askDelete(
    run.songs.length > 1 ? t('confirmDeleteSong', params) : t('confirmDeleteLastSong', params),
    `${title} – ${params.song}`,
    () => deleteSong(song.id),
  )
}

// ---- Choosing several songs to delete together ---------------------------------------------------------

const selecting = ref(false)
const selected = ref(new Set<string>())
const deleting = ref(false)
const plan = computed(() => deletionPlan(props.runs, selected.value))

/** Every song the search and filters list now, on every page; one the worker is on cannot go. */
const selectable = computed(() =>
  shownRuns.value.flatMap((entry) => entry.songs.map((song) => song.id)).filter((id) => !props.busyIds.has(id)),
)
const allSelected = computed(
  () => selectable.value.length > 0 && selectable.value.every((id) => selected.value.has(id)),
)

function select(songId: string, on: boolean): void {
  const next = new Set(selected.value)
  if (on) {
    next.add(songId)
  } else {
    next.delete(songId)
  }
  selected.value = next
}

/** Adds what is listed to what was chosen before, so a second search can add to the first. */
function selectAll(): void {
  selected.value = new Set([...selected.value, ...selectable.value])
}

function startSelecting(): void {
  selected.value = new Set()
  selecting.value = true
}

function stopSelecting(): void {
  selecting.value = false
  selected.value = new Set()
}

// A song deleted elsewhere, or one the worker took up meanwhile, is no longer chosen.
watch([() => props.runs, () => props.busyIds], () => {
  const listed = new Set(props.runs.flatMap((run) => run.songs.map((song) => song.id)))
  const kept = [...selected.value].filter((id) => listed.has(id) && !props.busyIds.has(id))
  if (kept.length !== selected.value.size) {
    selected.value = new Set(kept)
  }
})

/** Always asks: deleting cannot be undone, least of all for many songs at once. */
function askDeleteSelected(): void {
  const { runIds, songIds, count, bytes } = plan.value
  if (count === 0) {
    return
  }
  const size = formatBytes(bytes)
  let message: string
  if (count === 1) {
    const run = props.runs.find((entry) => entry.songs.some((song) => selected.value.has(song.id)))!
    const song = run.songs.find((entry) => selected.value.has(entry.id))!
    const params = { song: t('songN', { n: song.index }), title: run.title || t('untitled'), size }
    message = run.songs.length > 1 ? t('confirmDeleteSong', params) : t('confirmDeleteLastSong', params)
  } else if (runIds.length === 1) {
    message = t('confirmDeleteSelectedRun', { count, size })
  } else if (runIds.length > 1) {
    message = t('confirmDeleteSelectedRuns', { count, size, runs: runIds.length })
  } else {
    message = t('confirmDeleteSelected', { count, size })
  }
  confirm.require({
    header: t('confirmDelete'),
    message,
    icon: 'pi pi-trash',
    rejectProps: { label: t('keep'), severity: 'secondary', outlined: true },
    acceptProps: { label: count === 1 ? t('delete') : t('deleteSelected', { count }), severity: 'danger' },
    accept: () => deleteSelected(runIds, songIds, count),
  })
}

/**
 * One after another through the library's own endpoints, which refuse a song the worker took up meanwhile; a refusal
 * does not stop the rest, and what could not go stays chosen.
 */
async function deleteSelected(runIds: string[], songIds: string[], count: number): Promise<void> {
  deleting.value = true
  let failures = 0
  let firstError = ''
  const attempt = async (remove: () => Promise<void>, ids: string[]): Promise<void> => {
    try {
      await remove()
      const next = new Set(selected.value)
      ids.forEach((id) => next.delete(id))
      selected.value = next
    } catch (caught) {
      failures += ids.length
      firstError ||= caught instanceof Error ? caught.message : String(caught)
    }
  }
  try {
    for (const runId of runIds) {
      const ids = props.runs.find((run) => run.id === runId)?.songs.map((song) => song.id) ?? []
      await attempt(() => deleteRun(runId), ids)
    }
    for (const songId of songIds) {
      await attempt(() => deleteSong(songId), [songId])
    }
  } finally {
    deleting.value = false
  }
  if (failures > 0) {
    emit('error', t('deleteSelectedFailed', { failed: failures, count, message: firstError }))
    return
  }
  emit('notice', t('deletedSelected', { count }))
  stopSelecting()
}

/** The run being renamed; the dialog shows while it is set. */
const renaming = ref<RunInfo | null>(null)
const newTitle = ref('')
const savingTitle = ref(false)

function startRename(run: RunInfo): void {
  renaming.value = run
  newTitle.value = run.title
}

async function saveTitle(): Promise<void> {
  const run = renaming.value
  if (!run) {
    return
  }
  savingTitle.value = true
  try {
    const title = newTitle.value.trim()
    // The library reloads on the server's event, in every open browser alike.
    await renameRun(run.id, title)
    renaming.value = null
    emit('notice', t('renamed', { title: title || run.originalTitle || t('untitled') }))
  } catch (caught) {
    failed(caught)
  } finally {
    savingTitle.value = false
  }
}
</script>

<template>
  <section ref="list">
    <p v-if="error" class="danger">{{ t('libraryError', { message: error }) }}</p>
    <p v-else-if="!loading && runs.length === 0" class="muted">{{ t('libraryEmpty') }}</p>
    <LibraryFilters
      v-model:query="query"
      v-model:scope="scope"
      v-model:min-rating="minRating"
      v-model:trait="trait"
      :runs="runs"
      :shown="shownRuns.length"
    />
    <div v-if="runs.length > 0" class="selection" :class="{ active: selecting }">
      <Button
        v-if="!selecting"
        icon="pi pi-check-square"
        :label="t('selectSongs')"
        text
        size="small"
        @click="startSelecting"
      />
      <template v-else>
        <span class="text-sm">{{
          plan.count === 0
            ? t('selectedNone')
            : t('selectedCount', { count: plan.count, size: formatBytes(plan.bytes) })
        }}</span>
        <div class="flex flex-wrap gap-1 ml-auto">
          <Button
            v-if="!allSelected"
            :label="t('selectAll', { count: selectable.length })"
            text
            size="small"
            :disabled="selectable.length === 0"
            @click="selectAll"
          />
          <Button v-else :label="t('selectNone')" text size="small" @click="selected = new Set()" />
          <Button
            icon="pi pi-trash"
            :label="t('delete')"
            severity="danger"
            size="small"
            :disabled="plan.count === 0"
            :loading="deleting"
            @click="askDeleteSelected"
          />
          <Button :label="t('selectDone')" severity="secondary" text size="small" @click="stopSelecting" />
        </div>
      </template>
    </div>
    <DataView
      :value="shownRuns"
      data-key="id"
      paginator
      :rows="pageSize"
      :first="first"
      :page-link-size="3"
      :always-show-paginator="false"
      @update:first="pageChanged"
    >
      <!-- The paragraph above already says the library is empty; without this slot DataView adds its own text. -->
      <template #empty />
      <template #list="slotProps">
        <div v-for="{ run, songs } in slotProps.items as SortedRun[]" :key="run.id">
          <!-- A fieldset is as wide as its content by default, which pushed a card past a phone's edge. -->
          <Fieldset :legend="run.title || t('untitled')" class="min-w-0">
            <div class="flex justify-between items-center">
              <div>
                <span v-if="run.createdAt" class="muted text-sm">{{ formatDateTime(run.createdAt) }} · </span>
                <span class="muted text-sm">{{ formatBytes(run.bytes) }}</span>
              </div>
              <div class="flex">
                <Button
                  icon="pi pi-pencil"
                  v-tooltip="t('rename')"
                  :aria-label="t('rename')"
                  text
                  @click="startRename(run)"
                />
                <Button
                  icon="pi pi-upload"
                  v-tooltip="t('useAsTemplate')"
                  :aria-label="t('useAsTemplate')"
                  text
                  @click="emit('template', run)"
                />
                <Button
                  v-if="run.songs.length > 1"
                  icon="pi pi-trash"
                  v-tooltip="runBusy(run) ? t('deleteBusy') : t('deleteRun')"
                  :aria-label="t('deleteRun')"
                  text
                  severity="danger"
                  :disabled="runBusy(run)"
                  @click="askDeleteRun(run)"
                />
              </div>
            </div>
            <p class="style muted">{{ run.style }}</p>
            <!-- Where the lyrics matched, so a hit makes sense without opening the whole text. -->
            <ul v-if="searching && scope === 'lyrics'" class="hits">
              <li v-for="(line, index) in matchingLines(run.lyrics, terms)" :key="index">
                <template v-for="(segment, part) in line" :key="part">
                  <mark v-if="segment.hit" class="bg-highlight">{{ segment.text }}</mark>
                  <template v-else>{{ segment.text }}</template>
                </template>
              </li>
            </ul>
            <div class="flex top-0">
              <div class="extras flex-1">
                <details v-if="run.lyrics" class="lyrics">
                  <summary>{{ t('showLyrics') }}</summary>
                  <TextActions :model-value="run.lyrics" :clearable="false" />
                  <pre>{{ run.lyrics }}</pre>
                </details>
              </div>
            </div>

            <ul class="songs">
              <SongRow
                v-for="song in songs"
                :id="anchor(song.id)"
                :key="song.id"
                :run="run"
                :song="song"
                :focused="focusedSong === song.id"
                :busy="busyIds.has(song.id)"
                :voices="voices"
                :stems="stems ?? false"
                :live-versions="liveVersions"
                :selecting="selecting"
                :selected="selected.has(song.id)"
                @select="select(song.id, $event)"
                @play="playSong(run, song)"
                @rate="rateSong(song, $event)"
                @use-score="(abc, warnings) => emit('useScore', run, song, abc, warnings)"
                @new-song="emit('newSong', song.id)"
                @ace-template="emit('aceTemplate', song.id)"
                @sing="singing = { run, song }"
                @delete="askDeleteSong(run, song)"
                @error="emit('error', $event)"
              />
            </ul>
          </Fieldset>
        </div>
      </template>
    </DataView>
    <Dialog
      :visible="renaming !== null"
      modal
      :header="t('rename')"
      :draggable="false"
      :style="{ width: 'min(28rem, calc(100vw - 2rem))' }"
      @update:visible="(open: boolean) => !open && (renaming = null)"
    >
      <form v-if="renaming" class="flex flex-col gap-3" @submit.prevent="saveTitle">
        <InputText
          v-model="newTitle"
          :placeholder="renaming.originalTitle"
          :maxlength="200"
          autofocus
          fluid
          aria-describedby="rename-hint"
        />
        <p id="rename-hint" class="muted text-sm m-0">
          {{ t('renameHint', { title: renaming.originalTitle || t('untitled') }) }}
        </p>
        <div class="flex justify-end gap-2">
          <Button type="button" :label="t('cancel')" severity="secondary" text @click="renaming = null" />
          <Button type="submit" :label="t('save')" :loading="savingTitle" />
        </div>
      </form>
    </Dialog>
    <SingDialog v-model="singing" @notice="emit('notice', $event)" @error="emit('error', $event)" />
  </section>
</template>

<style scoped>
.selection {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 0.25rem 0.75rem;
  margin-bottom: 0.5rem;
}

.selection.active {
  padding: 0.4rem 0.75rem;
  border-radius: var(--radius-small);
  background: var(--surface-sunken);
}

.hits {
  margin: 0.4rem 0 0;
  padding: 0 0 0 0.75rem;
  border-left: 2px solid var(--p-primary-color);
  font-size: 0.9rem;
  font-style: italic;
  list-style: none;
}

.hits mark {
  border-radius: 2px;
  font-style: normal;
}

.style {
  display: -webkit-box;
  margin: 0.5rem 0 0;
  overflow: hidden;
  font-size: 0.9rem;
  -webkit-box-orient: vertical;
  -webkit-line-clamp: 2;
  line-clamp: 2;
}

.extras {
  display: flex;
  flex-wrap: wrap;
  align-items: flex-start;
  gap: 0.4rem 1rem;
  margin-top: 0.4rem;
}

.lyrics {
  flex: 1 1 100%;
  font-size: 0.9rem;
}

/* Beside the closed "Lyrics" summary; an open one takes the whole row and the link moves below it. */
.lyrics:not([open]) {
  flex: 0 1 auto;
}

.lyrics summary {
  cursor: pointer;
  color: var(--accent);
}

.lyrics pre {
  max-height: 20rem;
  margin: 0.5rem 0 0;
  padding: 0.5rem 0.75rem;
  overflow: auto;
  border-radius: var(--radius-small);
  background: var(--surface-sunken);
  font-family: var(--font-mono);
  font-size: 0.8rem;
  white-space: pre-wrap;
}

.songs {
  display: flex;
  flex-direction: column;
  gap: 0.9rem;
  margin: 0.9rem 0 0;
  padding: 0.9rem 0 0;
  border-top: 1px solid var(--border);
  list-style: none;
}
</style>
