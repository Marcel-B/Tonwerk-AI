<script setup lang="ts">
import Message from 'primevue/message'
import ProgressBar from 'primevue/progressbar'
import { computed, ref, watch } from 'vue'
import {
  deleteCover,
  deleteImage,
  deleteSong,
  deleteStems,
  deleteStorageLeftovers,
  deleteSwap,
  deleteTranscription,
  deleteVersion,
  deleteVideo,
  getStorageUsage,
} from '../api'
import { formatBytes, hasMessage, locale, t } from '../i18n'
import type { StorageCategory, StorageGroup, StorageItem, StorageKind, StorageUsage } from '../types'

/**
 * Where the disk went: one bar per volume, then every kind of file with its size, and for what Tonwerk made the largest
 * pieces to delete. Deleting goes through each piece's own endpoint, which refuses what is still being worked on.
 */
const props = defineProps<{
  /** Measured when the page is shown; walking the disk is not worth doing in the background. */
  active: boolean
}>()

const emit = defineEmits<{
  notice: [message: string]
  error: [message: string]
}>()

/** Which library category lists which kind of piece. */
const kindOf: Partial<Record<string, StorageKind>> = {
  songs: 'song',
  versions: 'version',
  stems: 'stems',
  videos: 'video',
  swaps: 'swap',
  covers: 'cover',
  images: 'image',
  transcriptions: 'transcription',
}

const groups: StorageGroup[] = ['library', 'app', 'models']

const usage = ref<StorageUsage | null>(null)
const loading = ref(false)
const error = ref<string | null>(null)
const open = ref<string | null>(null)
/** The piece being deleted, so its button spins and the others wait. */
const deleting = ref<string | null>(null)

async function load(): Promise<void> {
  loading.value = true
  try {
    usage.value = await getStorageUsage()
    error.value = null
  } catch (caught) {
    error.value = caught instanceof Error ? caught.message : String(caught)
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

/** The bars compare every category with the largest, so the models (tens of GB) do not flatten the songs. */
const largest = computed(() => Math.max(1, ...(usage.value?.categories.map((c) => c.bytes) ?? [])))

function categoriesOf(group: StorageGroup): StorageCategory[] {
  return usage.value?.categories.filter((c) => c.group === group) ?? []
}

function groupBytes(group: StorageGroup): number {
  return categoriesOf(group).reduce((sum, c) => sum + c.bytes, 0)
}

function itemsOf(category: StorageCategory): StorageItem[] {
  const kind = kindOf[category.id]
  return kind ? (usage.value?.items.filter((i) => i.kind === kind) ?? []) : []
}

function label(id: string): string {
  const key = `storageCat_${id}`
  return hasMessage(key) ? t(key) : id
}

function volumeName(path: string): string {
  return path === '/' ? t('storageInternal') : path
}

/** Only worth saying for what lies on another volume than the internal disk, e.g. moved to an external SSD. */
function where(category: StorageCategory): string | null {
  return category.volume !== '/' ? t('storageOn', { volume: volumeName(category.volume) }) : null
}

function toggle(category: StorageCategory): void {
  if (itemsOf(category).length > 0) {
    open.value = open.value === category.id ? null : category.id
  }
}

function detail(item: StorageItem): string {
  const parts: string[] = []
  if (item.kind === 'video' && item.detail) {
    const key = `videoFormat_${item.detail}`
    parts.push(hasMessage(key) ? t(key) : item.detail)
  } else if (item.detail) {
    parts.push(item.detail)
  }
  if (item.rating) {
    parts.push('★'.repeat(item.rating))
  }
  if (item.createdAt) {
    const date = new Date(item.createdAt)
    if (!Number.isNaN(date.getTime())) {
      parts.push(date.toLocaleDateString(locale.value, { dateStyle: 'medium' }))
    }
  }
  return parts.join(' · ')
}

function key(item: StorageItem): string {
  return `${item.kind}:${item.id}`
}

function confirmText(item: StorageItem): string {
  const params = { title: item.title || item.id, detail: item.detail ?? '', size: formatBytes(item.bytes) }
  return item.kind === 'song'
    ? t('storageConfirm_song', params)
    : item.kind === 'cover'
      ? t('storageConfirm_cover', params)
      : t('storageConfirm', params)
}

function remove(item: StorageItem): Promise<void> {
  switch (item.kind) {
    case 'song':
      return deleteSong(item.id)
    case 'version':
      return deleteVersion(item.songId ?? '', item.id)
    case 'stems':
      return deleteStems(item.id)
    case 'video':
      return deleteVideo(item.id)
    case 'swap':
      return deleteSwap(item.id)
    case 'cover':
      return deleteCover(item.id)
    case 'image':
      return deleteImage(item.id)
    case 'transcription':
      return deleteTranscription(item.id)
  }
}

async function confirmDelete(item: StorageItem): Promise<void> {
  if (deleting.value || !window.confirm(confirmText(item))) {
    return
  }
  deleting.value = key(item)
  try {
    await remove(item)
    emit('notice', t('storageDeleted', { size: formatBytes(item.bytes) }))
    await load()
  } catch (caught) {
    emit('error', caught instanceof Error ? caught.message : String(caught))
  } finally {
    deleting.value = null
  }
}

const clearing = ref(false)

async function clearLeftovers(): Promise<void> {
  const leftovers = usage.value?.leftovers
  if (!leftovers || clearing.value) {
    return
  }
  if (!window.confirm(t('storageLeftoversConfirm', { n: leftovers.count, size: formatBytes(leftovers.bytes) }))) {
    return
  }
  clearing.value = true
  try {
    const removed = await deleteStorageLeftovers()
    emit('notice', t('storageLeftoversDone', { n: removed.count, size: formatBytes(removed.bytes) }))
    await load()
  } catch (caught) {
    emit('error', caught instanceof Error ? caught.message : String(caught))
  } finally {
    clearing.value = false
  }
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
    <p v-else-if="!usage && loading" class="muted m-0">{{ t('storageMeasuring') }}</p>

    <template v-if="usage">
      <div v-for="volume in usage.volumes" :key="volume.path" class="flex flex-col gap-1">
        <div class="flex items-baseline justify-between gap-2 text-sm">
          <span class="font-medium">{{ volumeName(volume.path) }}</span>
          <span class="muted tabular-nums">
            {{ t('storageFree', { free: formatBytes(volume.freeBytes), total: formatBytes(volume.totalBytes) }) }}
          </span>
        </div>
        <ProgressBar
          :value="
            volume.totalBytes > 0 ? Math.round((100 * (volume.totalBytes - volume.freeBytes)) / volume.totalBytes) : 0
          "
          :show-value="false"
          class="volume"
        />
      </div>

      <Message v-if="usage.leftovers.count > 0" severity="warn" :closable="false">
        <div class="flex flex-wrap items-center justify-between gap-2">
          <span>{{
            t('storageLeftovers', { n: usage.leftovers.count, size: formatBytes(usage.leftovers.bytes) })
          }}</span>
          <Button
            :label="t('storageLeftoversDelete')"
            icon="pi pi-trash"
            size="small"
            severity="warn"
            :loading="clearing"
            @click="clearLeftovers"
          />
        </div>
      </Message>

      <section v-for="group in groups" v-show="categoriesOf(group).length > 0" :key="group" class="flex flex-col">
        <div class="group-head flex items-baseline justify-between gap-2 pb-1">
          <h3 class="m-0 text-base">{{ t(`storageGroup_${group}`) }}</h3>
          <span class="muted text-sm tabular-nums">{{ formatBytes(groupBytes(group)) }}</span>
        </div>
        <p v-if="group === 'models'" class="muted mt-1 mb-0 text-xs">{{ t('storageModelsHint') }}</p>

        <div v-for="category in categoriesOf(group)" :key="category.id" class="category">
          <component
            :is="itemsOf(category).length > 0 ? 'button' : 'div'"
            type="button"
            class="row"
            :class="{ clickable: itemsOf(category).length > 0 }"
            :aria-expanded="itemsOf(category).length > 0 ? open === category.id : undefined"
            @click="toggle(category)"
          >
            <span class="flex min-w-0 flex-1 flex-col gap-1 text-left">
              <span class="flex items-baseline justify-between gap-2">
                <span class="min-w-0 truncate">
                  <i
                    v-if="itemsOf(category).length > 0"
                    :class="open === category.id ? 'pi pi-chevron-down' : 'pi pi-chevron-right'"
                    class="chevron"
                  />
                  {{ label(category.id) }}
                </span>
                <span class="shrink-0 tabular-nums">{{ formatBytes(category.bytes) }}</span>
              </span>
              <span class="bar"><span :style="{ width: `${(100 * category.bytes) / largest}%` }" /></span>
              <span v-if="category.count !== null || where(category)" class="muted text-xs">
                {{
                  [category.count !== null ? t('storageCount', { n: category.count }) : null, where(category)]
                    .filter(Boolean)
                    .join(' · ')
                }}
              </span>
            </span>
          </component>

          <ol v-if="open === category.id" class="items m-0 list-none p-0">
            <li v-for="item in itemsOf(category)" :key="key(item)" class="item">
              <div class="flex min-w-0 flex-1 flex-col">
                <span class="truncate">{{ item.title || item.id }}</span>
                <span class="muted truncate text-xs">{{ detail(item) }}</span>
                <span v-if="item.kind === 'song'" class="muted text-xs">{{ t('storageSongIncludes') }}</span>
              </div>
              <span class="shrink-0 text-sm tabular-nums">{{ formatBytes(item.bytes) }}</span>
              <Button
                icon="pi pi-trash"
                severity="danger"
                text
                size="small"
                :loading="deleting === key(item)"
                :disabled="deleting !== null && deleting !== key(item)"
                :aria-label="t('storageDelete')"
                v-tooltip.left="t('storageDelete')"
                @click="confirmDelete(item)"
              />
            </li>
            <li v-if="itemsOf(category).length < (category.count ?? 0)" class="muted pt-1 text-xs">
              {{ t('storageLargest', { n: itemsOf(category).length }) }}
            </li>
          </ol>
        </div>
      </section>
    </template>
  </section>
</template>

<style scoped>
.volume {
  height: 0.5rem;
}

.group-head {
  border-bottom: 1px solid var(--border);
}

.category {
  border-bottom: 1px solid var(--border);
}

.row {
  display: flex;
  width: 100%;
  padding: 0.6rem 0;
  border: 0;
  background: none;
  color: inherit;
  font: inherit;
}

.row.clickable {
  cursor: pointer;
}

.chevron {
  font-size: 0.7rem;
  margin-right: 0.25rem;
}

.bar {
  display: block;
  height: 0.25rem;
  border-radius: 0.125rem;
  background: var(--surface-sunken);
  overflow: hidden;
}

.bar > span {
  display: block;
  height: 100%;
  min-width: 2px;
  background: var(--accent);
}

.item {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0.4rem 0 0.4rem 1rem;
  border-top: 1px solid var(--border);
}

.items {
  padding-bottom: 0.5rem;
}
</style>
