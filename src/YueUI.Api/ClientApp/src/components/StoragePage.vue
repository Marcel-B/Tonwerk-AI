<script setup lang="ts">
import { useConfirm } from 'primevue/useconfirm'
import { computed, ref, watch } from 'vue'
import {
  cleanupStorage,
  deleteCover,
  deleteImage,
  deleteSong,
  deleteSpeechTake,
  deleteStems,
  deleteSwap,
  deleteTranscription,
  deleteVersion,
  deleteVideo,
  getStorageOverview,
} from '../api'
import { formatBytes, formatDateTime, hasMessage, t, type MessageKey } from '../i18n'
import { ratings } from '../ratings'
import { needsReview, reviewDays } from '../review'
import type {
  RunInfo,
  StorageCategory,
  StorageCleanupTarget,
  StorageGroup,
  StorageItem,
  StorageOverview,
  StorageVolume,
} from '../types'

/**
 * Where the disk space goes: the volumes, what can be cleaned up in one go, and each part with its largest entries,
 * which are deleted through the same endpoints the library and the other pages use. Models are only shown, since
 * deleting one would break a tool; moving them is deploy/move-to-volume.sh's job.
 */
const props = defineProps<{
  /** Measured when the page is shown; the server walks the disk anew each time. */
  active: boolean
  /** The library, for the songs left unrated (the same rule as the library's review filter). */
  runs: RunInfo[]
}>()

const emit = defineEmits<{ notice: [message: string]; error: [message: string] }>()

const confirm = useConfirm()
const overview = ref<StorageOverview | null>(null)
const loading = ref(false)
const error = ref<string | null>(null)
const open = ref(new Set<string>())
const shown = ref<Record<string, number>>({})
const busy = ref<string | null>(null)

/** Entries a part lists before "show more". */
const firstItems = 5

async function load(): Promise<void> {
  loading.value = true
  try {
    overview.value = await getStorageOverview()
    error.value = null
  } catch (caught) {
    error.value = t('storageError', { message: caught instanceof Error ? caught.message : String(caught) })
  } finally {
    loading.value = false
  }
}

watch(
  () => props.active,
  (active) => {
    if (active) {
      void load()
    }
  },
  { immediate: true },
)

const categories = computed(() => overview.value?.categories.filter((c) => c.bytes > 0) ?? [])
const groups: StorageGroup[] = ['work', 'app', 'models']
const largest = computed(() => Math.max(1, ...categories.value.map((c) => c.bytes)))
const ofGroup = (group: StorageGroup) =>
  categories.value.filter((c) => c.group === group).sort((a, b) => b.bytes - a.bytes)
const groupBytes = (group: StorageGroup) => ofGroup(group).reduce((sum, c) => sum + c.bytes, 0)

/** The volume the database lies on; parts elsewhere say where they are. */
const homeVolume = computed(() => overview.value?.categories.find((c) => c.key === 'database')?.volume ?? '/')

function volumeName(mount: string): string {
  return mount === '/' ? t('storageMac') : (mount.split('/').filter(Boolean).at(-1) ?? mount)
}

function volumeShares(volume: StorageVolume) {
  const tonwerk = categories.value.filter((c) => c.volume === volume.mount).reduce((sum, c) => sum + c.bytes, 0)
  const used = Math.max(0, volume.total - volume.free)
  const percent = (bytes: number) => `${Math.min(100, (bytes / Math.max(1, volume.total)) * 100)}%`
  return { tonwerk, tonwerkWidth: percent(Math.min(tonwerk, used)), otherWidth: percent(Math.max(0, used - tonwerk)) }
}

function category(key: string): StorageCategory | undefined {
  return categories.value.find((c) => c.key === key)
}

/** Songs the library's review filter would list, with what deleting all of them would free. */
const review = computed(() => {
  const songs = props.runs.flatMap((run) =>
    run.songs.filter((song) => needsReview(run, song, ratings.value, reviewDays.value)),
  )
  return { count: songs.length, bytes: songs.reduce((sum, song) => sum + song.bytes, 0) }
})

/** Songs given one star: rated as not worth keeping, so they are offered for deletion as a whole. */
const oneStar = computed(() => {
  const songs = props.runs.flatMap((run) => run.songs.filter((song) => ratings.value[song.id] === 1))
  return { ids: songs.map((song) => song.id), bytes: songs.reduce((sum, song) => sum + song.bytes, 0) }
})

function askOneStar(): void {
  const { ids, bytes } = oneStar.value
  confirm.require({
    header: t('confirmDelete'),
    message: t('storageConfirmOneStar', { count: ids.length, size: formatBytes(bytes) }),
    acceptProps: { label: t('storageDelete'), severity: 'danger' },
    rejectProps: { label: t('cancel'), severity: 'secondary', outlined: true },
    // One after another through the library's own endpoint, which refuses a song the worker is on.
    accept: () =>
      run('oneStar', async () => {
        for (const id of ids) {
          await deleteSong(id)
        }
        emit('notice', t('storageCleaned', { size: formatBytes(bytes) }))
      }),
  })
}

type Suggestion = { target: StorageCleanupTarget; label: MessageKey; hint: MessageKey; bytes: number }
const suggestions = computed<Suggestion[]>(() =>
  (
    [
      {
        target: 'stream',
        label: 'storageCleanupStream',
        hint: 'storageCleanupStreamHint',
        bytes: category('stream')?.bytes ?? 0,
      },
      {
        target: 'images',
        label: 'storageCleanupImages',
        hint: 'storageCleanupImagesHint',
        bytes: category('images')?.bytes ?? 0,
      },
      {
        target: 'videos',
        label: 'storageCleanupVideos',
        hint: 'storageCleanupVideosHint',
        bytes: category('videos')?.bytes ?? 0,
      },
      {
        target: 'failed',
        label: 'storageCleanupFailed',
        hint: 'storageCleanupFailedHint',
        bytes: overview.value?.failed ?? 0,
      },
      {
        target: 'orphans',
        label: 'storageCleanupOrphans',
        hint: 'storageCleanupOrphansHint',
        bytes: categories.value.reduce((sum, c) => sum + c.orphaned, 0),
      },
    ] satisfies Suggestion[]
  ).filter((s) => s.bytes > 0),
)

function askCleanup(suggestion: Suggestion): void {
  confirm.require({
    header: t('confirmDelete'),
    message: t('storageConfirmCleanup', { what: t(suggestion.label), size: formatBytes(suggestion.bytes) }),
    acceptProps: { label: t('storageDelete'), severity: 'danger' },
    rejectProps: { label: t('cancel'), severity: 'secondary', outlined: true },
    accept: () =>
      run(suggestion.target, async () => {
        const result = await cleanupStorage(suggestion.target)
        emit('notice', t('storageCleaned', { size: formatBytes(result.bytes) }))
      }),
  })
}

function label(key: string): string {
  const message = `storage_${key}`
  return hasMessage(message) ? t(message) : key
}

function countText(count: number): string {
  return count === 1 ? t('storageCountOne') : t('storageCount', { count })
}

function toggle(key: string): void {
  const next = new Set(open.value)
  if (!next.delete(key)) {
    next.add(key)
  }
  open.value = next
}

function visibleItems(c: StorageCategory): StorageItem[] {
  return c.largest.slice(0, shown.value[c.key] ?? firstItems)
}

function detail(item: StorageItem): string | null {
  if (item.kind === 'video' && item.detail) {
    const key = `videoFormat_${item.detail}`
    return hasMessage(key) ? t(key) : item.detail
  }
  return item.detail
}

function askDelete(item: StorageItem): void {
  const params = { title: item.title, size: formatBytes(item.bytes) }
  confirm.require({
    header: t('confirmDelete'),
    message: item.kind === 'song' ? t('storageConfirmSong', params) : t('storageConfirmItem', params),
    acceptProps: { label: t('storageDelete'), severity: 'danger' },
    rejectProps: { label: t('cancel'), severity: 'secondary', outlined: true },
    accept: () => run(`${item.kind}:${item.id}`, () => remove(item)),
  })
}

function remove(item: StorageItem): Promise<void> {
  switch (item.kind) {
    case 'song':
      return deleteSong(item.id)
    case 'version':
      return deleteVersion(item.songId ?? '', item.id)
    case 'stems':
      return deleteStems(item.id)
    case 'swap':
      return deleteSwap(item.id)
    case 'video':
      return deleteVideo(item.id)
    case 'cover':
      return deleteCover(item.id)
    case 'image':
      return deleteImage(item.id)
    case 'transcription':
      return deleteTranscription(item.id)
    case 'take':
      return deleteSpeechTake(item.id)
  }
}

/** One deletion at a time; the page measures again afterwards, so every number matches the disk. */
async function run(key: string, action: () => Promise<void>): Promise<void> {
  busy.value = key
  try {
    await action()
  } catch (caught) {
    emit('error', caught instanceof Error ? caught.message : String(caught))
  } finally {
    busy.value = null
  }
  await load()
}
</script>

<template>
  <section class="flex flex-col gap-4">
    <div class="flex items-start justify-between gap-3">
      <div class="min-w-0">
        <h2 class="mt-0 mb-1">{{ t('menuStorage') }}</h2>
        <p class="muted m-0 text-sm">{{ t('storageIntro') }}</p>
      </div>
      <Button
        icon="pi pi-refresh"
        severity="secondary"
        text
        :loading="loading"
        :aria-label="t('storageRefresh')"
        v-tooltip.bottom="t('storageRefresh')"
        @click="load()"
      />
    </div>

    <p v-if="error" class="danger m-0">{{ error }}</p>

    <template v-if="overview">
      <div v-for="volume in overview.volumes" :key="volume.mount" class="flex flex-col gap-1">
        <div class="flex flex-wrap items-baseline justify-between gap-x-3 text-sm">
          <span class="font-medium">{{ volumeName(volume.mount) }}</span>
          <span class="muted">
            {{ t('storageVolumeTonwerk', { size: formatBytes(volumeShares(volume).tonwerk) }) }} ·
            {{ t('storageVolumeFree', { free: formatBytes(volume.free), total: formatBytes(volume.total) }) }}
          </span>
        </div>
        <div
          class="disk"
          role="img"
          :aria-label="t('storageVolumeFree', { free: formatBytes(volume.free), total: formatBytes(volume.total) })"
        >
          <span class="tonwerk" :style="{ width: volumeShares(volume).tonwerkWidth }" />
          <span class="other" :style="{ width: volumeShares(volume).otherWidth }" />
        </div>
      </div>

      <div class="flex flex-col gap-2">
        <h3 class="m-0 text-base">{{ t('storageCleanup') }}</h3>
        <p v-if="suggestions.length === 0 && review.count === 0 && oneStar.ids.length === 0" class="muted m-0 text-sm">
          {{ t('storageNothingToClean') }}
        </p>
        <div v-for="s in suggestions" :key="s.target" class="suggestion">
          <div class="min-w-0 flex-1">
            <div class="flex items-baseline justify-between gap-2">
              <span>{{ t(s.label) }}</span>
              <span class="tabular-nums">{{ formatBytes(s.bytes) }}</span>
            </div>
            <div class="muted text-xs">{{ t(s.hint) }}</div>
          </div>
          <Button
            icon="pi pi-trash"
            severity="danger"
            text
            :loading="busy === s.target"
            :disabled="busy !== null"
            :aria-label="t('storageDelete')"
            @click="askCleanup(s)"
          />
        </div>
        <div v-if="oneStar.ids.length > 0" class="suggestion">
          <div class="min-w-0 flex-1">
            <div class="flex items-baseline justify-between gap-2">
              <span>{{ t('storageCleanupOneStar') }}</span>
              <span class="tabular-nums">{{ formatBytes(oneStar.bytes) }}</span>
            </div>
            <div class="muted text-xs">
              {{
                t(oneStar.ids.length === 1 ? 'storageCleanupOneStarHintOne' : 'storageCleanupOneStarHint', {
                  count: oneStar.ids.length,
                })
              }}
            </div>
          </div>
          <Button
            icon="pi pi-trash"
            severity="danger"
            text
            :loading="busy === 'oneStar'"
            :disabled="busy !== null"
            :aria-label="t('storageDelete')"
            @click="askOneStar"
          />
        </div>
        <div v-if="review.count > 0" class="suggestion">
          <div class="min-w-0 flex-1">
            <div class="flex items-baseline justify-between gap-2">
              <span>{{ t('storageCleanupReview') }}</span>
              <span class="tabular-nums">{{ formatBytes(review.bytes) }}</span>
            </div>
            <div class="muted text-xs">
              {{
                t(review.count === 1 ? 'storageCleanupReviewHintOne' : 'storageCleanupReviewHint', {
                  count: review.count,
                  n: reviewDays,
                })
              }}
            </div>
          </div>
          <Button as="a" href="#/songs" :label="t('storageReviewOpen')" size="small" text />
        </div>
      </div>

      <div v-for="group in groups" :key="group" class="flex flex-col">
        <div class="flex items-baseline justify-between gap-2">
          <h3 class="my-1 text-base">{{ t(`storageGroup_${group}`) }}</h3>
          <span class="muted text-sm tabular-nums">{{ formatBytes(groupBytes(group)) }}</span>
        </div>
        <p v-if="group === 'models'" class="muted mt-0 mb-1 text-xs">{{ t('storageModelsHint') }}</p>
        <div v-for="c in ofGroup(group)" :key="c.key" class="part">
          <button
            type="button"
            class="head"
            :disabled="c.largest.length === 0"
            :aria-expanded="c.largest.length > 0 ? open.has(c.key) : undefined"
            @click="toggle(c.key)"
          >
            <i
              class="pi text-xs muted"
              :class="
                c.largest.length === 0 ? 'pi-circle-fill dot' : open.has(c.key) ? 'pi-chevron-down' : 'pi-chevron-right'
              "
            />
            <span class="min-w-0 flex-1 text-left">
              <span>{{ label(c.key) }}</span>
              <span v-if="c.count > 0" class="muted text-xs"> · {{ countText(c.count) }}</span>
              <span v-if="c.volume && c.volume !== homeVolume" class="muted text-xs">
                · {{ t('storageOnVolume', { volume: volumeName(c.volume) }) }}</span
              >
              <span v-if="c.orphaned > 0" class="muted text-xs">
                · {{ t('storageOrphaned', { size: formatBytes(c.orphaned) }) }}</span
              >
            </span>
            <span class="tabular-nums">{{ formatBytes(c.bytes) }}</span>
          </button>
          <div class="share"><span :style="{ width: `${(c.bytes / largest) * 100}%` }" /></div>
          <ul v-if="open.has(c.key)" class="m-0 flex list-none flex-col p-0">
            <li v-for="item in visibleItems(c)" :key="item.id" class="item">
              <div class="min-w-0 flex-1">
                <a v-if="item.songId" :href="`#/songs/${item.songId}`" class="title">{{ item.title }}</a>
                <span v-else class="title">{{ item.title }}</span>
                <div class="muted text-xs">
                  <template v-if="detail(item)">{{ detail(item) }}</template>
                  <template v-if="detail(item) && item.createdAt"> · </template>
                  <template v-if="item.createdAt">{{ formatDateTime(item.createdAt) }}</template>
                </div>
              </div>
              <span class="tabular-nums text-sm">{{ formatBytes(item.bytes) }}</span>
              <Button
                icon="pi pi-trash"
                severity="danger"
                text
                size="small"
                :loading="busy === `${item.kind}:${item.id}`"
                :disabled="busy !== null"
                :aria-label="t('storageDelete')"
                @click="askDelete(item)"
              />
            </li>
            <li v-if="c.largest.length > visibleItems(c).length">
              <Button
                :label="t('storageMore')"
                size="small"
                text
                @click="shown = { ...shown, [c.key]: (shown[c.key] ?? firstItems) + 10 }"
              />
            </li>
          </ul>
        </div>
      </div>
    </template>
  </section>
</template>

<style scoped>
.disk {
  display: flex;
  height: 0.6rem;
  border-radius: 999px;
  overflow: hidden;
  background: var(--surface-sunken);
}

.disk .tonwerk {
  background: var(--accent);
}

.disk .other {
  background: var(--text-muted, var(--border));
  opacity: 0.45;
}

.suggestion {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0.4rem 0;
  border-bottom: 1px solid var(--border);
}

.part {
  border-bottom: 1px solid var(--border);
  padding: 0.35rem 0;
}

.head {
  display: flex;
  width: 100%;
  align-items: center;
  gap: 0.5rem;
  padding: 0.25rem 0;
  border: 0;
  background: none;
  color: inherit;
  font: inherit;
  cursor: pointer;
}

.head:disabled {
  cursor: default;
}

.dot {
  font-size: 0.35rem;
  width: 0.75rem;
  text-align: center;
}

.share {
  height: 3px;
  margin-left: 1.25rem;
  border-radius: 999px;
  background: var(--surface-sunken);
  overflow: hidden;
}

.share span {
  display: block;
  height: 100%;
  background: var(--accent);
  opacity: 0.7;
}

.item {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0.3rem 0 0.3rem 1.25rem;
}

.title {
  display: block;
  color: inherit;
  text-decoration: none;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
</style>
