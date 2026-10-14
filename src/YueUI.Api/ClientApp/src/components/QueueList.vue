<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, useTemplateRef, watch } from 'vue'
import {
  cancel,
  cancelQueued,
  cancelTranscription,
  deleteSpeechTake,
  deleteStems,
  deleteSwap,
  deleteVersion,
  deleteVideo,
  deleteImage,
  deleteAceJob,
  moveQueued,
  shutdownWorker,
  stopAll,
} from '../api'
import { formatDuration, formatTime, stageLabel, t, versionProgress } from '../i18n'
import QueueOverview from './QueueOverview.vue'
import SongTimeline from './SongTimeline.vue'
import { holderOf, speechWaitReason, transcribing, waitReason } from '../queueModels'
import { navigate, showSong, songHref } from '../view'
import { addPlaylist } from '../playlist'
import type {
  AceJob,
  LogEntry,
  LyricsState,
  QueuedJob,
  SongState,
  SpeechTake,
  StemSetState,
  SwapState,
  TranscriptionState,
  VersionState,
  VideoState,
  ImageState,
  LargeModel,
  WorkerInfo,
} from '../types'

defineExpose({ run, stopAll, shutdownWorker })

const props = defineProps<{
  songs: SongState[]
  /** Jobs the server holds back until the memory is free, in the order they start. */
  jobs: QueuedJob[]
  worker: WorkerInfo
  lyricsDraft: LyricsState | null
  /** The large model besides YuE2 that holds the memory, as the server says. */
  memory: LargeModel | null
  versions: VersionState[]
  /** Songs being split into stems; they share the voices' queue and memory. */
  stems: StemSetState[]
  /** Uploaded recordings being sung with another voice, in the same queue. */
  swaps: SwapState[]
  /** Music videos; ffmpeg renders them one at a time beside everything else, they wait only for each other. */
  videos: VideoState[]
  /** Painted covers; they wait for the memory like the speech lab's takes. */
  images: ImageState[]
  /** ACE-Step's songs; they wait for the memory too, one job at a time. */
  ace: AceJob[]
  /** Queue:BundleWindow; null for a server from before it was sent. */
  bundleWindowSeconds: number | null
  /** The speech lab's takes; those in the works wait for the same memory. */
  takes: SpeechTake[]
  /** SheetSage2's; a running one keeps the worker, the waiting ones are in jobs. */
  transcriptions: TranscriptionState[]
  log: LogEntry[]
  /** Songs the library lists; those have a place on the songs page to jump to. */
  listed: Set<string>
}>()

const emit = defineEmits<{
  hideFinished: []
  error: [message: string]
}>()

const hasFinished = computed(() => props.songs.some((s) => s.finished))

/**
 * The finished songs the queue still shows, in the order they were made, so they can be heard and rated in one go
 * from a playlist of their own. ACE-Step's jobs name only their run, so its songs come from the library's listing.
 */
const readySongIds = computed(() => {
  const ids = props.songs.filter((s) => s.stage === 'ready' && props.listed.has(s.id)).map((s) => s.id)
  for (const job of props.ace) {
    if (job.stage === 'done' && job.run) {
      ids.push(...[...props.listed].filter((id) => id.startsWith(`${job.run}/`)).sort(bySongNumber))
    }
  }
  return [...new Set(ids)]
})

function bySongNumber(a: string, b: string): number {
  return a.localeCompare(b, undefined, { numeric: true })
}

async function playlistOfFinished(): Promise<void> {
  const name = t('queuePlaylistName', {
    date: new Date().toLocaleString(undefined, { dateStyle: 'short', timeStyle: 'short' }),
  })
  await run(async () => {
    await addPlaylist(name, readySongIds.value)
    navigate('playlist')
  })
}

const jobIcons: Record<QueuedJob['kind'], string> = {
  song: 'pi pi-sparkles',
  render: 'pi pi-refresh',
  lyrics: 'pi pi-pen-to-square',
  transcription: 'pi pi-microphone',
}

function jobTitle(job: QueuedJob): string {
  if (job.title) {
    return job.title
  }
  return job.kind === 'render' ? (job.songId ?? '') : job.kind === 'lyrics' ? t('jobLyrics') : t('untitled')
}

function taskLabel(task: string | null | undefined): string {
  return task === 'melody-vocal' ? t('taskVocal') : t('taskFull')
}

function jobDetail(job: QueuedJob): string {
  const quality = job.quality ? t(job.quality === 'full' ? 'qualityFull' : 'qualityDraft') : ''
  switch (job.kind) {
    case 'song':
      return [
        job.batch && job.batch > 1 ? t('jobSongs', { n: job.batch }) : '',
        quality,
        job.voiceLabel ? t('jobVoice', { voice: job.voiceLabel }) : '',
      ]
        .filter(Boolean)
        .join(' · ')
    case 'render':
      return [t('jobRender'), quality].join(' · ')
    case 'lyrics':
      return t(job.revision ? 'jobRevision' : 'jobLyrics')
    case 'transcription':
      return t('jobTranscription', { task: taskLabel(job.transcriptionTask) })
  }
}

/** Ticks for the bundling window's countdown in the wait reasons. */
const now = ref(Date.now())
const timer = setInterval(() => (now.value = Date.now()), 15_000)
onBeforeUnmount(() => clearInterval(timer))

const voiceWork = computed(() => [...props.versions, ...props.stems, ...props.swaps])
const holder = computed(() => holderOf(props.worker, props.memory, props.transcriptions))

/** The one transcription in the worker (SheetSage2 takes one at a time). */
const runningTranscription = computed(() => props.transcriptions.find((tr) => !tr.finished) ?? null)

const reasons = computed(() =>
  props.jobs.map((_, i) =>
    waitReason(
      props.jobs,
      i,
      holder.value,
      voiceWork.value,
      props.bundleWindowSeconds,
      now.value,
      transcribing(props.transcriptions),
    ),
  ),
)

/** The speech lab's takes in the works, in the order the lab speaks them (it keeps its own queue, like the voices). */
const takesInWork = computed(() => props.takes.filter((take) => !take.finished))

function takeDetail(take: SpeechTake): string {
  if (take.source !== null) {
    // A recording being cleaned: its name, and how far it is.
    return [
      take.source,
      ...(take.progress !== null && !take.finished ? [`${Math.round(take.progress * 100)} %`] : []),
    ].join(' · ')
  }
  return [take.voiceLabel ?? t('labOwnVoice'), take.text.replace(/\s+/g, ' ').trim()].join(' · ')
}

/**
 * Voice versions and stem separations in the works, oldest first, as VoiceConverter makes them one at a time. They
 * are not in the server's job queue (they keep their own), but they wait for the same memory.
 */
interface VoiceItem {
  key: string
  /** Null for an uploaded file, which has no song to link to. */
  songId: string | null
  title: string
  detail: string
  stage: string
  stageLabel: string
  createdAt: string
  icon: string
  remove: () => Promise<void>
}

const voiceItems = computed<VoiceItem[]>(() =>
  [
    ...props.versions
      .filter((v) => !v.finished)
      .map((v) => ({
        key: `version:${v.id}`,
        songId: v.songId,
        title: v.title,
        detail: t('queueVersion', { voice: v.voiceLabel }),
        stage: v.stage,
        stageLabel: [t(`versionStage_${v.stage}`), versionProgress(v)].filter(Boolean).join(' '),
        createdAt: v.createdAt,
        icon: 'pi pi-user',
        remove: () => deleteVersion(v.songId, v.id),
      })),
    ...props.stems
      .filter((s) => !s.finished)
      .map((s) => ({
        key: `stems:${s.id}`,
        songId: s.upload ? null : s.songId,
        title: s.title,
        detail: t('queueStems', { model: s.model }),
        stage: s.stage,
        stageLabel: t(`stemsStage_${s.stage}`),
        createdAt: s.createdAt,
        icon: 'pi pi-sliders-v',
        remove: () => deleteStems(s.id),
      })),
    ...props.swaps
      .filter((s) => !s.finished)
      .map((s) => ({
        key: `swap:${s.id}`,
        songId: null,
        title: s.fileName,
        detail: t('queueSwap', { voice: s.voiceLabel }),
        stage: s.stage,
        stageLabel: [t(`versionStage_${s.stage}`), versionProgress(s)].filter(Boolean).join(' '),
        createdAt: s.createdAt,
        icon: 'pi pi-user-edit',
        remove: () => deleteSwap(s.id),
      })),
  ].sort((a, b) => a.createdAt.localeCompare(b.createdAt)),
)

const videosInWork = computed(() =>
  props.videos.filter((v) => !v.finished).sort((a, b) => a.createdAt.localeCompare(b.createdAt)),
)

const imagesInWork = computed(() =>
  props.images.filter((i) => !i.finished).sort((a, b) => a.createdAt.localeCompare(b.createdAt)),
)

const aceInWork = computed(() =>
  props.ace.filter((job) => !job.finished).sort((a, b) => a.createdAt.localeCompare(b.createdAt)),
)

function aceStage(job: AceJob): string {
  const label = t(`aceStage_${job.stage}`)
  return job.stage === 'queued' || job.stage === 'saving' ? label : `${label} ${Math.round(job.fraction * 100)} %`
}

/** Like a take of the speech lab: one at a time, each once the others let go of the memory. */
function aceReason(index: number): string {
  return speechWaitReason(index, holder.value, 'ace')
}

function imageStage(image: ImageState): string {
  return image.stage === 'painting'
    ? t('imageStage_painting', { percent: Math.round(image.fraction * 100) })
    : image.stage === 'loading'
      ? t('imageStage_loading')
      : t('imageStage_queued')
}

function videoDetail(video: VideoState): string {
  return [t(`videoFormat_${video.format}`), video.effect === 'none' ? null : t(`videoEffect_${video.effect}`)]
    .filter(Boolean)
    .join(' · ')
}

/** A waiting one waits for whoever holds the memory, or for the one before it in the voices' queue. */
function voiceReason(item: VoiceItem): string {
  if (item.stage !== 'queued') {
    return ''
  }
  if (holder.value === 'speech') {
    return t('waitSpeech')
  }
  if (holder.value === 'yue') {
    return t('waitYue')
  }
  if (holder.value === 'lyrics') {
    return t('waitLyrics')
  }
  if (holder.value === 'image') {
    return t('waitImage')
  }
  if (holder.value === 'ace') {
    return t('waitAce')
  }
  if (holder.value === 'transcription') {
    return t('waitTranscription')
  }
  return holder.value === 'voice' ? t('waitTurn') : t('waitStarting')
}

function songNumber(songId: string): string {
  return t('songN', { n: songId.slice(songId.lastIndexOf('/') + 5) })
}

/** From joining the queue to the end, or null for a song without its stages (a server from before they were kept). */
function totalTime(song: SongState): string | null {
  const first = song.stages?.[0]
  const last = song.stages?.[song.stages.length - 1]
  if (!first || !last || first === last) {
    return null
  }
  return formatDuration((new Date(last.startedAt).getTime() - new Date(first.startedAt).getTime()) / 1000)
}

/** Finished songs fold to one line; their steps (and how long each took) open on request. */
const expanded = ref(new Set<string>())
function toggleSteps(id: string): void {
  const next = new Set(expanded.value)
  if (!next.delete(id)) {
    next.add(id)
  }
  expanded.value = next
}

async function run(action: () => Promise<void>): Promise<void> {
  try {
    await action()
  } catch (caught) {
    emit('error', caught instanceof Error ? caught.message : String(caught))
  }
}

/** The log opens scrolled to its end, and stays there while new lines come in. */
const logOpen = ref(false)
const logBox = useTemplateRef<HTMLElement>('logBox')
watch(
  () => [props.log.length, logOpen.value],
  async () => {
    await nextTick()
    const box = logBox.value
    if (box && logOpen.value) {
      box.scrollTop = box.scrollHeight
    }
  },
)
</script>

<template>
  <section>
    <!-- ml-auto keeps the stop button on the right even when there is nothing to hide -->
    <div class="flex flex-wrap items-center gap-2 mb-4">
      <Button
        :label="t('clearFinished')"
        v-if="hasFinished"
        icon="pi pi-eye-slash"
        severity="secondary"
        outlined
        size="small"
        @click="emit('hideFinished')"
      />
      <Button
        v-if="readySongIds.length > 0"
        :label="t('queuePlaylist', { n: readySongIds.length })"
        :title="t('queuePlaylistHint')"
        icon="pi pi-list"
        severity="secondary"
        outlined
        size="small"
        @click="playlistOfFinished"
      />
      <Button
        :label="t('stopAll')"
        v-if="worker.busy"
        icon="pi pi-stop-circle"
        severity="danger"
        size="small"
        class="ml-auto"
        @click="run(stopAll)"
      />
      <Button
        v-else-if="worker.status !== 'stopped'"
        severity="secondary"
        size="small"
        class="ml-auto"
        :label="t('shutdown')"
        :title="t('shutdownHint')"
        @click="run(shutdownWorker)"
      />
    </div>

    <QueueOverview
      :songs="songs"
      :jobs="jobs"
      :worker="worker"
      :lyrics-draft="lyricsDraft"
      :memory="memory"
      :versions="voiceWork"
      :takes="takes"
      :transcriptions="transcriptions"
      :images="images"
      :videos="videos"
      :ace="ace"
    />

    <div v-if="runningTranscription" class="mb-4">
      <h3 class="m-0 text-sm font-medium text-muted-color">{{ t('modelTranscription') }}</h3>
      <div class="flex gap-2 items-center">
        <i class="pi pi-microphone text-muted-color" />
        <div class="min-w-0 flex-1">
          <a
            href="#/transcribe"
            class="block truncate font-bold text-color no-underline hover:underline"
            :title="t('queueOpenTranscription')"
            @click.prevent="navigate('transcribe')"
            >{{ runningTranscription.fileName }}</a
          >
          <span class="block text-sm text-muted-color truncate">{{
            runningTranscription.detail || taskLabel(runningTranscription.task)
          }}</span>
        </div>
        <Tag class="shrink-0">
          <i class="pi pi-spin pi-spinner text-xs" />
          {{ t(`transcriptionStage_${runningTranscription.stage}`) }}
        </Tag>
        <Button
          icon="pi pi-times"
          text
          rounded
          severity="danger"
          :aria-label="t('queueCancel')"
          @click="run(() => cancelTranscription(runningTranscription!.id))"
        />
      </div>
    </div>

    <div v-if="voiceItems.length > 0" class="mb-4">
      <h3 class="m-0 text-sm font-medium text-muted-color">{{ t('queueVoice') }}</h3>
      <ul class="m-0 p-0 list-none flex flex-col gap-1">
        <li v-for="item in voiceItems" :key="item.key" class="flex gap-2 items-center">
          <i :class="[item.icon, 'text-muted-color']" />
          <div class="min-w-0 flex-1">
            <a
              v-if="item.songId === null"
              href="#/voices"
              class="block truncate font-bold text-color no-underline hover:underline"
              @click.prevent="navigate('voices')"
              >{{ item.title }}</a
            >
            <a
              v-else-if="listed.has(item.songId)"
              :href="songHref(item.songId)"
              class="block truncate font-bold text-color no-underline hover:underline"
              :title="t('showSong')"
              @click.prevent="showSong(item.songId)"
              >{{ item.title || t('untitled') }} · {{ songNumber(item.songId) }}</a
            >
            <strong v-else class="block truncate"
              >{{ item.title || t('untitled') }} · {{ songNumber(item.songId) }}</strong
            >
            <span class="block text-sm text-muted-color truncate">{{ item.detail }}</span>
            <span v-if="voiceReason(item)" class="block text-xs text-muted-color truncate"
              ><i class="pi pi-clock text-xs" /> {{ voiceReason(item) }}</span
            >
          </div>
          <Tag :severity="item.stage === 'queued' ? 'secondary' : undefined" class="shrink-0">
            <i v-if="item.stage !== 'queued'" class="pi pi-spin pi-spinner text-xs" />
            {{ item.stageLabel }}
          </Tag>
          <Button
            icon="pi pi-times"
            text
            rounded
            severity="danger"
            :aria-label="t('queueCancel')"
            @click="run(item.remove)"
          />
        </li>
      </ul>
    </div>

    <div v-if="imagesInWork.length > 0" class="mb-4">
      <h3 class="m-0 text-sm font-medium text-muted-color">{{ t('queueImages') }}</h3>
      <ul class="m-0 p-0 list-none flex flex-col gap-1">
        <li v-for="image in imagesInWork" :key="image.id" class="flex gap-2 items-center">
          <i class="pi pi-palette text-muted-color" />
          <div class="min-w-0 flex-1">
            <a
              v-if="listed.has(image.songId)"
              :href="songHref(image.songId)"
              class="block truncate font-bold text-color no-underline hover:underline"
              :title="t('showSong')"
              @click.prevent="showSong(image.songId)"
              >{{ image.title || t('untitled') }} · {{ songNumber(image.songId) }}</a
            >
            <strong v-else class="block truncate"
              >{{ image.title || t('untitled') }} · {{ songNumber(image.songId) }}</strong
            >
            <span class="block text-sm text-muted-color truncate">{{ image.modelLabel }}</span>
          </div>
          <Tag :severity="image.stage === 'queued' ? 'secondary' : undefined" class="shrink-0">
            <i v-if="image.stage !== 'queued'" class="pi pi-spin pi-spinner text-xs" />
            {{ imageStage(image) }}
          </Tag>
          <Button
            icon="pi pi-times"
            text
            rounded
            severity="danger"
            :aria-label="t('queueCancel')"
            @click="run(() => deleteImage(image.id))"
          />
        </li>
      </ul>
    </div>

    <div v-if="aceInWork.length > 0" class="mb-4">
      <h3 class="m-0 text-sm font-medium text-muted-color">{{ t('queueAce') }}</h3>
      <ul class="m-0 p-0 list-none flex flex-col gap-1">
        <li v-for="(job, index) in aceInWork" :key="job.id" class="flex gap-2 items-center">
          <i class="pi pi-wave-pulse text-muted-color" />
          <div class="min-w-0 flex-1">
            <a
              href="#/ace"
              class="block truncate font-bold text-color no-underline hover:underline"
              @click.prevent="navigate('ace')"
              >{{ job.title }}</a
            >
            <span class="block text-sm text-muted-color truncate">{{ job.request.caption }}</span>
            <span v-if="job.stage === 'queued'" class="block text-xs text-muted-color truncate"
              ><i class="pi pi-clock text-xs" /> {{ aceReason(index) }}</span
            >
          </div>
          <Tag :severity="job.stage === 'queued' ? 'secondary' : undefined" class="shrink-0">
            <i v-if="job.stage !== 'queued'" class="pi pi-spin pi-spinner text-xs" />
            {{ aceStage(job) }}
          </Tag>
          <Button
            icon="pi pi-times"
            text
            rounded
            severity="danger"
            :aria-label="t('queueCancel')"
            @click="run(() => deleteAceJob(job.id))"
          />
        </li>
      </ul>
    </div>

    <div v-if="videosInWork.length > 0" class="mb-4">
      <h3 class="m-0 text-sm font-medium text-muted-color">{{ t('queueVideo') }}</h3>
      <ul class="m-0 p-0 list-none flex flex-col gap-1">
        <li v-for="video in videosInWork" :key="video.id" class="flex gap-2 items-center">
          <i class="pi pi-video text-muted-color" />
          <div class="min-w-0 flex-1">
            <a
              v-if="listed.has(video.songId)"
              :href="songHref(video.songId)"
              class="block truncate font-bold text-color no-underline hover:underline"
              :title="t('showSong')"
              @click.prevent="showSong(video.songId)"
              >{{ video.title || t('untitled') }} · {{ songNumber(video.songId) }}</a
            >
            <strong v-else class="block truncate"
              >{{ video.title || t('untitled') }} · {{ songNumber(video.songId) }}</strong
            >
            <span class="block text-sm text-muted-color truncate">{{ videoDetail(video) }}</span>
          </div>
          <Tag :severity="video.stage === 'queued' ? 'secondary' : undefined" class="shrink-0">
            <i v-if="video.stage === 'rendering'" class="pi pi-spin pi-spinner text-xs" />
            {{
              video.stage === 'queued'
                ? t('videoStage_queued')
                : t('videoStage_rendering', { percent: Math.round(video.fraction * 100) })
            }}
          </Tag>
          <Button
            icon="pi pi-times"
            text
            rounded
            severity="danger"
            :aria-label="t('queueCancel')"
            @click="run(() => deleteVideo(video.id))"
          />
        </li>
      </ul>
    </div>

    <div v-if="takesInWork.length > 0" class="mb-4">
      <h3 class="m-0 text-sm font-medium text-muted-color">{{ t('menuLab') }}</h3>
      <ul class="m-0 p-0 list-none flex flex-col gap-1">
        <li v-for="(take, i) in takesInWork" :key="take.id" class="flex gap-2 items-center">
          <i :class="take.source !== null ? 'pi pi-filter' : 'pi pi-comments'" class="text-muted-color" />
          <div class="min-w-0 flex-1">
            <a
              href="#/lab"
              class="block truncate font-bold text-color no-underline hover:underline"
              :title="t('queueOpenLab')"
              @click.prevent="navigate('lab')"
              >{{ take.modelLabel }}</a
            >
            <span class="block text-sm text-muted-color truncate">{{ takeDetail(take) }}</span>
            <span v-if="take.stage === 'queued'" class="block text-xs text-muted-color truncate"
              ><i class="pi pi-clock text-xs" /> {{ speechWaitReason(i, holder) }}</span
            >
          </div>
          <Tag :severity="take.stage === 'queued' ? 'secondary' : undefined" class="shrink-0">
            <i v-if="take.stage !== 'queued'" class="pi pi-spin pi-spinner text-xs" />
            {{ t(`labStage_${take.stage}`) }}
          </Tag>
          <Button
            icon="pi pi-times"
            text
            rounded
            severity="danger"
            :aria-label="t('queueCancel')"
            @click="run(() => deleteSpeechTake(take.id))"
          />
        </li>
      </ul>
    </div>

    <div v-if="jobs.length > 0" class="mb-4">
      <h3 class="m-0 text-sm font-medium text-muted-color" :title="t('queueWaitingHint')">{{ t('queueWaiting') }}</h3>
      <ul class="m-0 p-0 list-none flex flex-col gap-1">
        <li v-for="(job, i) in jobs" :key="job.id" class="flex gap-2 items-center">
          <i :class="[jobIcons[job.kind], 'text-muted-color']" />
          <div class="min-w-0 flex-1">
            <a
              v-if="job.kind === 'transcription'"
              href="#/transcribe"
              class="block truncate font-bold text-color no-underline hover:underline"
              :title="t('queueOpenTranscription')"
              @click.prevent="navigate('transcribe')"
              >{{ jobTitle(job) }}</a
            >
            <a
              v-else-if="job.songId && listed.has(job.songId)"
              :href="songHref(job.songId)"
              class="block truncate font-bold text-color no-underline hover:underline"
              :title="t('showSong')"
              @click.prevent="showSong(job.songId)"
              >{{ jobTitle(job) }}</a
            >
            <strong v-else class="block truncate">{{ jobTitle(job) }}</strong>
            <span class="block text-sm text-muted-color truncate">{{ jobDetail(job) }}</span>
            <span class="block text-xs text-muted-color truncate"
              ><i class="pi pi-clock text-xs" /> {{ reasons[i] }}</span
            >
          </div>
          <Button
            icon="pi pi-arrow-up"
            text
            rounded
            size="small"
            :disabled="i === 0"
            :aria-label="t('moveUp')"
            @click="run(() => moveQueued(job.id, -1))"
          />
          <Button
            icon="pi pi-arrow-down"
            text
            rounded
            size="small"
            :disabled="i === jobs.length - 1"
            :aria-label="t('moveDown')"
            @click="run(() => moveQueued(job.id, 1))"
          />
          <Button
            icon="pi pi-times"
            text
            rounded
            severity="danger"
            :aria-label="t('removeFromQueue')"
            @click="run(() => cancelQueued(job.id))"
          />
        </li>
      </ul>
    </div>

    <p
      v-if="
        songs.length === 0 &&
        jobs.length === 0 &&
        voiceItems.length === 0 &&
        takesInWork.length === 0 &&
        videosInWork.length === 0 &&
        imagesInWork.length === 0 &&
        aceInWork.length === 0 &&
        !runningTranscription
      "
      class="muted empty"
    >
      {{ t('queueEmpty') }}
    </p>
    <ul v-else-if="songs.length > 0">
      <li v-for="song in songs" :key="song.id" :class="['song', song.stage]">
        <div class="flex gap-3 items-center">
          <a
            v-if="listed.has(song.id)"
            :href="songHref(song.id)"
            class="name font-bold text-color no-underline hover:underline"
            :title="t('showSong')"
            @click.prevent="showSong(song.id)"
            >{{ song.title || song.run }}</a
          >
          <strong v-else class="name">{{ song.title || song.run }}</strong>
          <span class="muted">{{ t('songN', { n: song.index }) }}</span>
          <span
            v-if="song.voice"
            class="muted truncate text-sm"
            :title="t('jobVoice', { voice: song.voice.voiceLabel })"
            ><i class="pi pi-user text-xs" /> {{ song.voice.voiceLabel }}</span
          >
          <Button
            v-if="!song.finished"
            icon="pi pi-times"
            text
            rounded
            severity="danger"
            @click="run(() => cancel(song.id))"
          />
        </div>
        <div v-if="song.finished" class="flex flex-wrap gap-x-3 items-center">
          <span class="text-sm font-medium">{{ stageLabel(song.stage) }}</span>
          <span v-if="totalTime(song)" class="text-sm text-muted-color whitespace-nowrap">{{
            t('stageTotal', { time: totalTime(song)! })
          }}</span>
          <span class="muted detail">{{ song.message ?? song.detail }}</span>
          <Button
            v-if="song.stages?.length"
            :label="expanded.has(song.id) ? t('hideSteps') : t('showSteps')"
            text
            size="small"
            @click="toggleSteps(song.id)"
          />
        </div>
        <SongTimeline v-if="!song.finished || expanded.has(song.id)" :song="song" />
      </li>
    </ul>

    <details class="log" @toggle="logOpen = ($event.target as HTMLDetailsElement).open">
      <summary>{{ t('log') }}</summary>
      <!-- Only while open: the worker's stderr can bring several lines a second, and each one shifts all the lines. -->
      <div v-if="logOpen" ref="logBox" class="text-sm">
        <p v-if="log.length === 0" class="muted">{{ t('logEmpty') }}</p>
        <div v-for="(entry, i) in log" :key="i" :class="['entry', entry.level]">
          <span class="muted">{{ formatTime(entry.time) }}</span> {{ entry.message }}
        </div>
      </div>
    </details>
  </section>
</template>

<style scoped></style>
