<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import Dialog from 'primevue/dialog'
import Checkbox from 'primevue/checkbox'
import { addVideo, deleteVideo, fetchMediaFolders, fetchVideoFile, listVideos, saveBlob, videoUrl } from '../api'
import { loadExportSettings, type ExportTarget } from '../export'
import { formatBytes, t } from '../i18n'
import {
  drawPreview,
  drawVideoLayers,
  loadVideoSettings,
  saveVideoSettings,
  toPngs,
  videoEffects,
  videoFormats,
  videoMotions,
  videoSources,
  videoTarget,
  type VideoLayers,
} from '../video'
import type { MediaInfo, VideoState } from '../types'

/**
 * App.vue holds the one dialog for music videos; the library and the song menu open it through `openVideo`. The
 * browser draws the still layers and shows a still of the result, the server renders the video in the background and
 * lists it here (and in the queue) with its progress; a finished one is watched, saved or handed to the share sheet.
 */
const props = defineProps<{
  /** Videos in the works and finished while the page was open, from the event stream. */
  live: VideoState[]
}>()

const emit = defineEmits<{ error: [message: string] }>()

const stored = loadVideoSettings()
const format = ref(stored.format)
const effect = ref(stored.effect)
const motion = ref(stored.motion)
const showCover = ref(stored.showCover)
const showTitle = ref(stored.showTitle)
const source = ref(stored.source)
const media = ref(stored.media)
const motif = ref(stored.motif)
const photos = computed(() => source.value === 'photos')
/** What the picture looks like; the motif only matters on the server, so typing it draws nothing again. */
const settings = computed(() => ({
  format: format.value,
  effect: effect.value,
  motion: motion.value,
  showCover: showCover.value,
  showTitle: showTitle.value,
  source: source.value,
  media: media.value,
  motif: '',
}))

/** The artist the export dialog remembers goes under the title. */
const subtitle = loadExportSettings().artist

const formatOptions = computed(() => videoFormats.map((value) => ({ value, label: t(`videoFormat_${value}`) })))
const effectOptions = computed(() => videoEffects.map((value) => ({ value, label: t(`videoEffect_${value}`) })))
const motionOptions = computed(() => videoMotions.map((value) => ({ value, label: t(`videoMotion_${value}`) })))
const sourceOptions = computed(() => videoSources.map((value) => ({ value, label: t(`videoSource_${value}`) })))

/** The Mac's folders of pictures, asked for once photos are chosen and again on the refresh button. */
const mediaInfo = ref<MediaInfo | null>(null)
const loadingMedia = ref(false)
const folderOptions = computed(() =>
  (mediaInfo.value?.folders ?? []).map((folder) => ({
    value: folder.name,
    label: t('videoFolderCounts', { name: folder.name, photos: folder.photos, videos: folder.videos }),
    disabled: folder.photos + folder.videos === 0,
  })),
)

async function loadMedia(): Promise<void> {
  loadingMedia.value = true
  try {
    mediaInfo.value = await fetchMediaFolders()
    const usable = mediaInfo.value.folders.filter((folder) => folder.photos + folder.videos > 0)
    if (!usable.some((folder) => folder.name === media.value)) {
      media.value = usable[0]?.name ?? ''
    }
  } catch (caught) {
    emit('error', caught instanceof Error ? caught.message : String(caught))
  } finally {
    loadingMedia.value = false
  }
}

watch(
  [photos, videoTarget],
  ([wanted, target]) => {
    if (wanted && target) {
      void loadMedia()
    }
  },
  { immediate: true },
)

watch(motif, () => saveVideoSettings({ ...settings.value, motif: motif.value }))

const ready = computed(
  () => !!layers.value && !drawing.value && (!photos.value || (!!mediaInfo.value?.installed && media.value !== '')),
)
const layers = ref<VideoLayers | null>(null)
const preview = ref<string | null>(null)
const drawing = ref(false)
const sending = ref(false)
const loaded = ref<VideoState[]>([])

/** The one video being watched in the dialog; its file is only fetched once asked for. */
const watching = ref<string | null>(null)

/**
 * Sharing a video takes two taps, like the export: Safari opens the share sheet only right after a tap, and the file
 * (tens of megabytes) has to be fetched first.
 */
const shareFile = ref<{ id: string; file: File } | null>(null)
const preparing = ref<string | null>(null)

/** Drawn again on every change; the newest drawing wins if one overtakes another. */
let drawn = 0
async function redraw(target: ExportTarget): Promise<void> {
  const ticket = ++drawn
  drawing.value = true
  try {
    const next = await drawVideoLayers(target, settings.value, subtitle)
    if (ticket === drawn && videoTarget.value === target) {
      layers.value = next
      preview.value = drawPreview(next, settings.value)
    }
  } catch (caught) {
    if (ticket === drawn) {
      emit('error', caught instanceof Error ? caught.message : String(caught))
    }
  } finally {
    if (ticket === drawn) {
      drawing.value = false
    }
  }
}

watch(
  videoTarget,
  async (target) => {
    if (!target) {
      return
    }
    layers.value = null
    preview.value = null
    shareFile.value = null
    loaded.value = []
    void redraw(target)
    try {
      const videos = await listVideos(target.songId)
      if (videoTarget.value === target) {
        loaded.value = videos
      }
    } catch (caught) {
      emit('error', caught instanceof Error ? caught.message : String(caught))
    }
  },
  { immediate: true },
)

watch(settings, () => {
  saveVideoSettings({ ...settings.value, motif: motif.value })
  if (videoTarget.value) {
    void redraw(videoTarget.value)
  }
})

/** What the server listed, with newer states from the event stream laid over it; deleted ones leave. */
const videos = computed(() => {
  const songId = videoTarget.value?.songId
  const live = props.live.filter((video) => video.songId === songId)
  const byId = new Map(loaded.value.map((video) => [video.id, video]))
  for (const video of live) {
    const known = byId.get(video.id)
    if (!known || video.updatedAt >= known.updatedAt) {
      byId.set(video.id, video)
    }
  }
  return [...byId.values()]
    .filter((video) => video.stage !== 'cancelled')
    .sort((a, b) => b.createdAt.localeCompare(a.createdAt))
})

async function start(): Promise<void> {
  const target = videoTarget.value
  if (!target || !layers.value || sending.value || !ready.value) {
    return
  }
  sending.value = true
  try {
    const { format, effect, motion, showCover, showTitle } = settings.value
    const video = await addVideo(
      target.songId,
      {
        format,
        effect,
        motion: photos.value ? 'none' : motion,
        showCover,
        showTitle,
        color: layers.value.color,
        ...(photos.value ? { media: media.value, motif: motif.value.trim() } : {}),
      },
      await toPngs(layers.value),
    )
    if (videoTarget.value === target) {
      loaded.value = [video, ...loaded.value.filter((v) => v.id !== video.id)]
    }
  } catch (caught) {
    emit('error', t('videoFailedToStart', { message: caught instanceof Error ? caught.message : String(caught) }))
  } finally {
    sending.value = false
  }
}

async function remove(video: VideoState): Promise<void> {
  try {
    await deleteVideo(video.id)
    loaded.value = loaded.value.filter((v) => v.id !== video.id)
  } catch (caught) {
    emit('error', caught instanceof Error ? caught.message : String(caught))
  }
}

function describe(video: VideoState): string {
  return [
    video.media ? t('videoFromFolder', { name: video.media }) : null,
    t(`videoFormat_${video.format}`),
    video.effect === 'none' ? null : t(`videoEffect_${video.effect}`),
    video.motion === 'none' ? null : t(`videoMotion_${video.motion}`),
    video.showCover || video.media ? null : t('videoWithoutCover'),
  ]
    .filter(Boolean)
    .join(' · ')
}

const canShareFiles = typeof navigator.canShare === 'function'

async function prepareShare(video: VideoState): Promise<void> {
  preparing.value = video.id
  try {
    const file = await fetchVideoFile(video.id)
    if (navigator.canShare({ files: [file] })) {
      shareFile.value = { id: video.id, file }
    } else {
      saveBlob(file, file.name)
    }
  } catch (caught) {
    emit('error', caught instanceof Error ? caught.message : String(caught))
  } finally {
    preparing.value = null
  }
}

async function share(file: File): Promise<void> {
  try {
    await navigator.share({ files: [file], title: file.name })
    shareFile.value = null
  } catch (caught) {
    if (!(caught instanceof DOMException && caught.name === 'AbortError')) {
      emit('error', caught instanceof Error ? caught.message : String(caught))
    }
  }
}

onBeforeUnmount(() => {
  shareFile.value = null
})

function close(): void {
  videoTarget.value = null
  watching.value = null
}
</script>

<template>
  <Dialog
    :visible="videoTarget !== null"
    modal
    :header="t('videoDialogTitle', { title: videoTarget?.title || t('untitled') })"
    :draggable="false"
    :style="{ width: 'min(32rem, calc(100vw - 2rem))' }"
    @update:visible="(open: boolean) => !open && close()"
  >
    <form class="flex flex-col gap-3" @submit.prevent="start">
      <div class="preview" :class="format">
        <img v-if="preview" :src="preview" :alt="t('videoPreview')" />
        <i v-if="drawing" class="pi pi-spin pi-spinner spinner" aria-hidden="true" />
      </div>
      <SelectButton
        v-model="format"
        :options="formatOptions"
        option-label="label"
        option-value="value"
        :allow-empty="false"
        :aria-label="t('videoFormat')"
        class="w-full"
        :pt="{ pcToggleButton: { root: { class: 'flex-1' } } }"
      />
      <span class="caption">{{ t('videoSource') }}</span>
      <SelectButton
        v-model="source"
        :options="sourceOptions"
        option-label="label"
        option-value="value"
        :allow-empty="false"
        :aria-label="t('videoSource')"
        class="w-full"
        :pt="{ pcToggleButton: { root: { class: 'flex-1' } } }"
      />
      <template v-if="photos">
        <small v-if="mediaInfo && !mediaInfo.installed" class="danger">{{ t('videoPhotosMissing') }}</small>
        <template v-else-if="mediaInfo">
          <div class="flex items-center gap-2">
            <Select
              v-model="media"
              :options="folderOptions"
              option-label="label"
              option-value="value"
              option-disabled="disabled"
              :placeholder="t('videoFolder')"
              :aria-label="t('videoFolder')"
              class="min-w-0 flex-1"
            />
            <Button
              icon="pi pi-refresh"
              text
              rounded
              :loading="loadingMedia"
              :aria-label="t('videoFoldersReload')"
              @click="loadMedia"
            />
          </div>
          <small class="muted">{{ t('videoFolderHint', { root: mediaInfo.root }) }}</small>
          <InputText v-model="motif" :placeholder="t('videoMotifPlaceholder')" :aria-label="t('videoMotif')" fluid />
          <small class="muted">{{ t('videoMotifHint') }}</small>
        </template>
        <i v-else class="pi pi-spin pi-spinner text-muted-color" aria-hidden="true" />
      </template>
      <span class="caption">{{ t('videoEffect') }}</span>
      <SelectButton
        v-model="effect"
        :options="effectOptions"
        option-label="label"
        option-value="value"
        :allow-empty="false"
        :aria-label="t('videoEffect')"
        class="w-full"
        :pt="{ pcToggleButton: { root: { class: 'flex-1' } } }"
      />
      <span v-if="!photos" class="caption">{{ t('videoMotion') }}</span>
      <SelectButton
        v-if="!photos"
        v-model="motion"
        :options="motionOptions"
        option-label="label"
        option-value="value"
        :allow-empty="false"
        :aria-label="t('videoMotion')"
        class="w-full"
        :pt="{ pcToggleButton: { root: { class: 'flex-1' } } }"
      />
      <div class="flex flex-wrap gap-x-5 gap-y-2">
        <label class="flex items-center gap-2">
          <Checkbox v-model="showCover" binary />
          <span>{{ t('videoShowCover') }}</span>
        </label>
        <label class="flex items-center gap-2">
          <Checkbox v-model="showTitle" binary />
          <span>{{ t('videoShowTitle') }}</span>
        </label>
      </div>
      <small class="muted">{{ photos ? t('videoPhotosHint') : t('videoHint') }}</small>
      <div class="flex justify-end">
        <Button type="submit" :label="t('videoStart')" icon="pi pi-video" :loading="sending" :disabled="!ready" />
      </div>
    </form>

    <div v-if="videos.length > 0" class="mt-4">
      <h3 class="m-0 mb-2 text-sm font-medium text-muted-color">{{ t('videoList') }}</h3>
      <ul class="m-0 p-0 list-none flex flex-col gap-3">
        <li v-for="video in videos" :key="video.id" class="flex flex-col gap-1">
          <div class="flex items-center gap-2">
            <i
              :class="['pi', video.format === 'portrait' ? 'pi-mobile' : 'pi-desktop', 'text-muted-color']"
              aria-hidden="true"
            />
            <div class="min-w-0 flex-1">
              <span class="block truncate">{{ describe(video) }}</span>
              <span v-if="video.stage === 'done'" class="block text-xs text-muted-color">{{
                video.bytes ? formatBytes(video.bytes) : ''
              }}</span>
              <span v-else-if="video.stage === 'failed'" class="block text-xs danger">{{
                t('videoFailed', { message: video.message ?? '' })
              }}</span>
            </div>
            <Tag v-if="!video.finished" :severity="video.stage === 'queued' ? 'secondary' : undefined" class="shrink-0">
              <i v-if="video.stage === 'rendering'" class="pi pi-spin pi-spinner text-xs" />
              {{
                video.stage === 'queued'
                  ? t('videoStage_queued')
                  : t('videoStage_rendering', { percent: Math.round(video.fraction * 100) })
              }}
            </Tag>
            <template v-if="video.stage === 'done'">
              <Button
                icon="pi pi-play"
                text
                rounded
                :aria-label="t('videoWatch')"
                @click="watching = watching === video.id ? null : video.id"
              />
              <Button
                as="a"
                icon="pi pi-download"
                text
                rounded
                :href="videoUrl(video.id, true)"
                :aria-label="t('shareSave')"
              />
              <Button
                v-if="canShareFiles"
                icon="pi pi-share-alt"
                text
                rounded
                :loading="preparing === video.id"
                :aria-label="t('share')"
                @click="prepareShare(video)"
              />
            </template>
            <Button
              icon="pi pi-trash"
              text
              rounded
              severity="danger"
              :aria-label="video.finished ? t('videoDelete') : t('queueCancel')"
              @click="remove(video)"
            />
          </div>
          <video
            v-if="watching === video.id"
            :src="videoUrl(video.id)"
            controls
            playsinline
            autoplay
            class="w-full rounded-md bg-black"
            :class="video.format === 'portrait' ? 'max-h-[28rem]' : ''"
          />
          <div v-if="shareFile?.id === video.id" class="flex items-center justify-end gap-2">
            <small class="muted">{{ t('shareReady', { name: shareFile.file.name }) }}</small>
            <Button :label="t('share')" icon="pi pi-share-alt" size="small" @click="share(shareFile.file)" />
          </div>
        </li>
      </ul>
    </div>
  </Dialog>
</template>

<style scoped>
.caption {
  margin-bottom: -0.5rem;
  font-size: 0.875rem;
  color: var(--text-muted);
}
.preview {
  position: relative;
  margin: 0 auto;
  border-radius: 0.5rem;
  overflow: hidden;
  background: var(--surface-sunken);
}
.preview.landscape {
  width: 100%;
  aspect-ratio: 16 / 9;
}
.preview.portrait {
  height: min(20rem, 50vh);
  aspect-ratio: 9 / 16;
}
.preview img {
  display: block;
  width: 100%;
  height: 100%;
}
.spinner {
  position: absolute;
  inset: 0;
  margin: auto;
  width: fit-content;
  height: fit-content;
  font-size: 1.5rem;
  color: white;
}
</style>
