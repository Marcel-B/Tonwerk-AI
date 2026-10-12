<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useConfirm } from 'primevue/useconfirm'
import { ApiError, getStorageReport } from '../api'
import { formatBytes, formatDateTime, t } from '../i18n'
import { deleteAll, sectionOf, songNumber } from '../storage'
import type { StorageGroup, StorageItem, StorageReport, StorageSection, StorageSuggestion } from '../types'

/**
 * What the disk holds, by part, with each part's biggest entries and a few suggestions of what can go. Deleting goes
 * through the endpoints the other pages use; models are only shown, since setup-mac.sh installed them.
 */
const props = defineProps<{
  /** Measured when the page is shown; what other pages made meanwhile is only seen by measuring again. */
  active: boolean
}>()
const emit = defineEmits<{
  notice: [message: string]
  error: [message: string]
}>()

const report = ref<StorageReport | null>(null)
const loading = ref(false)
const loadError = ref<string | null>(null)
/** Parts and suggestions shown open, by key. */
const open = ref(new Set<string>())
const deleting = ref(new Set<string>())

async function load(): Promise<void> {
  loading.value = true
  try {
    report.value = await getStorageReport()
    loadError.value = null
  } catch (caught) {
    loadError.value = errorText(caught)
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

function errorText(caught: unknown): string {
  return caught instanceof ApiError && caught.status === 0
    ? t('errorNetwork')
    : t('errorGeneric', { message: caught instanceof Error ? caught.message : String(caught) })
}

const sections = computed(() =>
  (['data', 'models'] as StorageSection[]).map((section) => ({
    section,
    parts: sectionOf(report.value?.groups ?? [], section),
    bytes: (report.value?.groups ?? []).filter((g) => g.section === section).reduce((sum, g) => sum + g.bytes, 0),
  })),
)

function toggle(key: string): void {
  const next = new Set(open.value)
  if (!next.delete(key)) {
    next.add(key)
  }
  open.value = next
}

function entries(count: number): string {
  return count === 1 ? t('storageEntry') : t('storageEntries', { count })
}

function itemKey(item: StorageItem): string {
  return `${item.kind}:${item.id}`
}

function label(item: StorageItem): string {
  const number = songNumber(item)
  return number !== null && item.kind !== 'transcription' ? `${item.title} · ${t('songN', { n: number })}` : item.title
}

function detail(item: StorageItem): string[] {
  const parts: string[] = []
  if (item.kind === 'video' && item.detail) {
    // "portrait" or "portrait · Urlaub" (a photo video's folder).
    const [format, ...media] = item.detail.split(' · ')
    parts.push(format === 'portrait' || format === 'landscape' ? t(`videoFormat_${format}`) : (format ?? ''), ...media)
  } else if (item.detail) {
    parts.push(item.detail)
  }
  if (item.stage) {
    parts.push(t(`storageStage_${item.stage}`))
  }
  if (item.createdAt) {
    parts.push(formatDateTime(item.createdAt))
  }
  return parts
}

const confirm = useConfirm()

function askDelete(item: StorageItem): void {
  confirm.require({
    header: t('confirmDelete'),
    message: t(item.kind === 'song' ? 'storageConfirmDeleteSong' : 'storageConfirmDelete', {
      name: label(item),
      size: formatBytes(item.bytes),
    }),
    icon: 'pi pi-trash',
    rejectProps: { label: t('keep'), severity: 'secondary', outlined: true },
    acceptProps: { label: t('delete'), severity: 'danger' },
    accept: () => remove([item]),
  })
}

function askDeleteAll(suggestion: StorageSuggestion): void {
  confirm.require({
    header: t('confirmDelete'),
    message: t('storageConfirmDeleteAll', {
      entries: entries(suggestion.items.length),
      size: formatBytes(suggestion.bytes),
    }),
    icon: 'pi pi-trash',
    rejectProps: { label: t('keep'), severity: 'secondary', outlined: true },
    acceptProps: { label: t('delete'), severity: 'danger' },
    accept: () => remove(suggestion.items),
  })
}

async function remove(items: StorageItem[]): Promise<void> {
  deleting.value = new Set([...deleting.value, ...items.map(itemKey)])
  try {
    const { deleted, failed } = await deleteAll(items)
    const freed = deleted.reduce((sum, i) => sum + i.bytes, 0)
    if (failed.length === 0) {
      emit('notice', t('storageDeleted', { size: formatBytes(freed) }))
    } else if (deleted.length === 0 && failed.length === 1) {
      emit('error', errorText(failed[0]?.error))
    } else {
      const first = failed[0]?.error
      emit(
        'error',
        t('storageDeletedSome', {
          deleted: deleted.length,
          failed: failed.length,
          message: first instanceof Error ? first.message : String(first),
        }),
      )
    }
  } finally {
    const left = new Set(deleting.value)
    items.forEach((i) => left.delete(itemKey(i)))
    deleting.value = left
    await load()
  }
}

function songLink(item: StorageItem): string | null {
  return item.songId ? `#/songs/${item.songId}` : null
}

function groupNote(group: StorageGroup): string | null {
  if (group.key === 'songs') {
    return t('storageSongsFree')
  }
  return group.key === 'streams' ? t('storageHint_streams') : null
}
</script>

<template>
  <section class="flex flex-col gap-5">
    <div class="flex items-center gap-2">
      <div class="min-w-0 flex-1">
        <template v-if="report">
          <p v-for="volume in report.volumes" :key="volume.name" class="m-0">
            <span v-if="report.volumes.length > 1">
              {{
                t('storageVolumeFree', {
                  name: volume.name,
                  free: formatBytes(volume.freeBytes),
                  total: formatBytes(volume.totalBytes),
                })
              }}
            </span>
            <span v-else>
              {{ t('storageFree', { free: formatBytes(volume.freeBytes), total: formatBytes(volume.totalBytes) }) }}
            </span>
            <span class="bar volume" aria-hidden="true">
              <span :style="{ width: `${(1 - volume.freeBytes / Math.max(1, volume.totalBytes)) * 100}%` }" />
            </span>
          </p>
        </template>
        <p v-else-if="loading" class="muted m-0">{{ t('storageLoading') }}</p>
      </div>
      <Button
        icon="pi pi-refresh"
        text
        rounded
        severity="secondary"
        :loading="loading"
        :aria-label="t('storageRefresh')"
        v-tooltip.top="t('storageRefresh')"
        @click="load"
      />
    </div>
    <p v-if="loadError" class="danger m-0">{{ loadError }}</p>

    <template v-if="report">
      <div>
        <h3 class="mt-0 mb-2">{{ t('storageSuggestions') }}</h3>
        <p v-if="report.suggestions.length === 0" class="muted m-0">{{ t('storageSuggestionsEmpty') }}</p>
        <ul class="m-0 flex list-none flex-col gap-2 p-0">
          <li v-for="suggestion in report.suggestions" :key="suggestion.key" class="box">
            <!-- Title on a line of its own, the buttons below: side by side they squeezed it into three lines on a phone. -->
            <div class="flex flex-wrap items-center gap-2">
              <div class="min-w-0 basis-full">
                <strong>{{ t(`storageSuggestion_${suggestion.key}`) }}</strong>
                <small class="muted block">
                  {{
                    t('storageSuggestionSize', {
                      count: entries(suggestion.items.length),
                      size: formatBytes(suggestion.bytes),
                    })
                  }}
                </small>
              </div>
              <Button
                :label="open.has(`s:${suggestion.key}`) ? t('storageHide') : t('storageShow')"
                text
                size="small"
                severity="secondary"
                @click="toggle(`s:${suggestion.key}`)"
              />
              <Button
                :label="t('storageDeleteAll')"
                icon="pi pi-trash"
                size="small"
                severity="danger"
                outlined
                :loading="suggestion.items.some((i) => deleting.has(itemKey(i)))"
                @click="askDeleteAll(suggestion)"
              />
            </div>
            <template v-if="open.has(`s:${suggestion.key}`)">
              <p class="muted mt-2 mb-1 text-sm">{{ t(`storageSuggestionHint_${suggestion.key}`) }}</p>
              <ul class="items">
                <li v-for="item in suggestion.items" :key="itemKey(item)">
                  <div class="min-w-0 flex-1">
                    <span class="block truncate">
                      <Tag :value="t(`storageKind_${item.kind}`)" severity="secondary" class="mr-1" />{{ label(item) }}
                    </span>
                    <small class="muted block truncate">{{
                      [formatBytes(item.bytes), ...detail(item)].join(' · ')
                    }}</small>
                  </div>
                  <Button
                    v-if="songLink(item)"
                    as="a"
                    :href="songLink(item)!"
                    icon="pi pi-arrow-right"
                    text
                    rounded
                    size="small"
                    severity="secondary"
                    :aria-label="t('storageOpenSong')"
                    v-tooltip.top="t('storageOpenSong')"
                  />
                  <Button
                    icon="pi pi-trash"
                    text
                    rounded
                    size="small"
                    severity="danger"
                    :loading="deleting.has(itemKey(item))"
                    :aria-label="t('delete')"
                    v-tooltip.top="t('delete')"
                    @click="askDelete(item)"
                  />
                </li>
              </ul>
            </template>
          </li>
        </ul>
      </div>

      <div v-for="{ section, parts, bytes } in sections" :key="section">
        <h3 class="mt-0 mb-2 flex items-baseline gap-2">
          <span class="flex-1">{{ t(section === 'data' ? 'storageMade' : 'storageModels') }}</span>
          <small class="muted font-normal">{{ formatBytes(bytes) }}</small>
        </h3>
        <p v-if="section === 'models'" class="muted mt-0 mb-2 text-sm">{{ t('storageModelsHint') }}</p>
        <ul class="m-0 flex list-none flex-col gap-1 p-0">
          <li v-for="{ group, share } in parts" :key="group.key" class="part">
            <component
              :is="group.largest.length > 0 ? 'button' : 'div'"
              type="button"
              class="part-head"
              :class="{ clickable: group.largest.length > 0 }"
              :aria-expanded="group.largest.length > 0 ? open.has(group.key) : undefined"
              @click="group.largest.length > 0 && toggle(group.key)"
            >
              <span class="flex items-center gap-2">
                <i
                  v-if="group.largest.length > 0"
                  class="pi text-xs"
                  :class="open.has(group.key) ? 'pi-chevron-down' : 'pi-chevron-right'"
                />
                <span class="min-w-0 flex-1 truncate text-left">
                  {{ t(`storageGroup_${group.key}`) }}
                  <small v-if="group.count !== null" class="muted"> · {{ entries(group.count) }} </small>
                </span>
                <Tag v-if="group.missing" :value="t('storageMissing')" severity="warn" />
                <span class="tabular-nums">{{ formatBytes(group.bytes) }}</span>
              </span>
              <span class="bar" aria-hidden="true"><span :style="{ width: `${share * 100}%` }" /></span>
              <small v-if="group.moved" class="muted block truncate text-left">
                {{ t('storageMoved', { path: group.moved }) }}
              </small>
            </component>
            <template v-if="open.has(group.key)">
              <p v-if="groupNote(group)" class="muted mt-1 mb-1 text-sm">{{ groupNote(group) }}</p>
              <ul class="items">
                <li v-for="item in group.largest" :key="itemKey(item)">
                  <div class="min-w-0 flex-1">
                    <span class="block truncate">{{ label(item) }}</span>
                    <small class="muted block truncate">{{
                      [formatBytes(item.bytes), ...detail(item)].join(' · ')
                    }}</small>
                  </div>
                  <Button
                    v-if="songLink(item)"
                    as="a"
                    :href="songLink(item)!"
                    icon="pi pi-arrow-right"
                    text
                    rounded
                    size="small"
                    severity="secondary"
                    :aria-label="t('storageOpenSong')"
                    v-tooltip.top="t('storageOpenSong')"
                  />
                  <Button
                    icon="pi pi-trash"
                    text
                    rounded
                    size="small"
                    severity="danger"
                    :loading="deleting.has(itemKey(item))"
                    :aria-label="t('delete')"
                    v-tooltip.top="t('delete')"
                    @click="askDelete(item)"
                  />
                </li>
              </ul>
            </template>
          </li>
        </ul>
      </div>
    </template>
  </section>
</template>

<style scoped>
.box {
  padding: 0.75rem;
  border: 1px solid var(--p-content-border-color);
  border-radius: var(--p-border-radius-md);
}

.part-head {
  display: block;
  width: 100%;
  padding: 0.4rem 0;
  border: 0;
  background: none;
  color: inherit;
  font: inherit;
}

.part-head.clickable {
  cursor: pointer;
}

.bar {
  display: block;
  height: 0.3rem;
  margin-top: 0.25rem;
  overflow: hidden;
  border-radius: 999px;
  background: var(--surface-sunken);
}

.bar > span {
  display: block;
  height: 100%;
  border-radius: inherit;
  background: var(--accent);
}

.bar.volume {
  height: 0.5rem;
}

.items {
  margin: 0 0 0.5rem;
  padding: 0;
  list-style: none;
}

.items > li {
  display: flex;
  align-items: center;
  gap: 0.25rem;
  padding: 0.35rem 0;
  border-top: 1px solid var(--p-content-border-color);
}
</style>
