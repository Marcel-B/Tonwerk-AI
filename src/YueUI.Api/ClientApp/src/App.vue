<script setup lang="ts">
import { computed, defineAsyncComponent, onBeforeUnmount, ref, useTemplateRef, watch } from 'vue'
import { coverUrl, getStorage, getVoiceInfo, listLibrary, songRequest, songScore, subscribe } from './api'
import AdvancedParameters from './components/AdvancedParameters.vue'
import GenerateForm from './components/GenerateForm.vue'
import SettingsMenu from './components/SettingsMenu.vue'
import QueueList from './components/QueueList.vue'
import AudioBackground from './components/AudioBackground.vue'
import PlayerBar from './components/PlayerBar.vue'
import PlaylistView from './components/PlaylistView.vue'
import TranscribePanel from './components/TranscribePanel.vue'
import AudioMidiPanel from './components/AudioMidiPanel.vue'
import VoicesPanel from './components/VoicesPanel.vue'
import StemsPanel from './components/StemsPanel.vue'
import VoiceSwapPanel from './components/VoiceSwapPanel.vue'
import Message from 'primevue/message'
import { applyTemplate, fromSongRequest, loadFormState, partsOf, planningFor, saveFormState } from './form'
import { formatBytes, t, workerLabel, type MessageKey } from './i18n'
import { current, refreshTracks } from './player'
import { loadPlaylists, playlistIds } from './playlist'
import { ratings, setRatings } from './ratings'
import { reviewCount, reviewDays } from './review'
import { exportTarget, registerExportLookup } from './export'
import { videoTarget } from './video'
import { imageTarget } from './images'
import { checkForUpdate, reload, updateAvailable } from './update'
import { visuals } from './spectrum'
import QueueOverview from './components/QueueOverview.vue'
import ScoreField from './components/ScoreField.vue'
import TemplatePicker from './components/TemplatePicker.vue'
import { loadedTemplateId, partList } from './templates'
import { navigate, view, type View } from './view'
import type {
  LogEntry,
  LyricsState,
  PromptTemplate,
  RunInfo,
  SongInfo,
  SongState,
  SpeechTake,
  StorageInfo,
  TranscriptionState,
  StemSetState,
  SwapState,
  VideoState,
  ImageState,
  LargeModel,
  VersionState,
  QueuedJob,
  VoiceInfo,
  WorkerInfo,
} from './types'

// Loaded after the first paint: the library brings DataView with its Paginator and InputNumber, Fieldset and
// Dialog, a good part of PrimeVue that the create page does not need; sharing needs its dialog only when used.
const LibraryList = defineAsyncComponent(() => import('./components/LibraryList.vue'))
const ExportDialog = defineAsyncComponent(() => import('./components/ExportDialog.vue'))
const VideoDialog = defineAsyncComponent(() => import('./components/VideoDialog.vue'))
const ImageDialog = defineAsyncComponent(() => import('./components/ImageDialog.vue'))
// The Logic page brings the options form, the piano roll and the MIDI preview; loaded only once it is opened.
const LogicPage = defineAsyncComponent(() => import('./components/LogicPage.vue'))
// Backing vocals share the Logic page's preview; loaded only once opened as well.
const HarmonyPage = defineAsyncComponent(() => import('./components/HarmonyPage.vue'))
// The instruments page shares the Logic page's synthesizers; likewise loaded only once it is opened.
const InstrumentsPage = defineAsyncComponent(() => import('./components/InstrumentsPage.vue'))
// The speech lab, likewise loaded and mounted only once it is opened.
const SpeechLab = defineAsyncComponent(() => import('./components/SpeechLab.vue'))
// The log, likewise.
const LogPage = defineAsyncComponent(() => import('./components/LogPage.vue'))
const TemplatesPage = defineAsyncComponent(() => import('./components/TemplatesPage.vue'))
const StoragePage = defineAsyncComponent(() => import('./components/StoragePage.vue'))

const logCapacity = 300
const form = ref(loadFormState())
watch(form, (value) => saveFormState(value), { deep: true })
/** The API's complaints about single fields, shown by the song's fields and the advanced parameters alike. */
const fieldErrors = ref<Record<string, string[]>>({})

// ---- Live state of the worker, from the event stream ----------------------------------------------

const worker = ref<WorkerInfo>({
  status: 'stopped',
  busy: false,
  studioRunning: false,
  lastError: null,
  extensions: null,
})
/** Green once the model is loaded; the accent while it starts or works; grey while no worker runs. */
const workerSeverity = computed(() =>
  worker.value.busy || worker.value.status === 'starting'
    ? undefined
    : worker.value.status === 'ready'
      ? 'success'
      : 'secondary',
)
const songs = ref<SongState[]>([])
const log = ref<LogEntry[]>([])
const transcriptions = ref<TranscriptionState[]>([])
const lyricsDraft = ref<LyricsState | null>(null)
/** Versions in the works, and those finished while the page was open; by id. */
const versions = ref<VersionState[]>([])
/** Stems in the works, and those finished while the page was open. */
const stemSets = ref<StemSetState[]>([])
/** Uploaded recordings being sung with another voice, and those finished while the page was open. */
const swaps = ref<SwapState[]>([])
/** Music videos in the works, and those finished while the page was open. */
const videos = ref<VideoState[]>([])
/** Painted covers in the works, and those finished while the page was open. */
const images = ref<ImageState[]>([])
/** The large model besides YuE2 that holds the memory, as the server says. */
const memory = ref<LargeModel | null>(null)
/**
 * The speech lab's takes: those in the works from the snapshot, then as the event stream reports them, by id. The queue
 * shows the unfinished ones; the lab lays them all over what it loaded.
 */
const speechTakes = ref<SpeechTake[]>([])
/** Songs, renders and drafts waiting for the memory. */
const jobs = ref<QueuedJob[]>([])
/** How long songs may pass a waiting draft or version (Queue:BundleWindow). */
const bundleWindowSeconds = ref<number | null>(null)
/** Lyrics drafts among them, so that the form knows its own is still on its way. */
const queuedIds = computed(() => new Set(jobs.value.map((j) => j.id)))
/** Starts optimistic: the warning is for a stream that broke, not for one that is still opening. */
const connected = ref(true)
/** Finished songs the user put away; the server keeps them, so they would come back with the next snapshot. */
const hidden = ref(new Set<string>())

const queue = computed(() => songs.value.filter((s) => !(s.finished && hidden.value.has(s.id))))
// Kept as the same Set while the busy songs stay the same: a progress event replaces a song, and a new Set would
// re-render the whole library with every one.
const busyIds = computed<Set<string>>((previous) => {
  // A render waiting in the queue counts too: the library offers it once, not a second time.
  const next = new Set([
    ...songs.value.filter((s) => !s.finished).map((s) => s.id),
    ...jobs.value.flatMap((j) => (j.songId ? [j.songId] : [])),
  ])
  return previous && previous.size === next.size && [...next].every((id) => previous.has(id)) ? previous : next
})

function upsert(song: SongState): void {
  const index = songs.value.findIndex((s) => s.id === song.id)
  if (index >= 0) {
    songs.value[index] = song
  } else {
    songs.value.push(song)
    songs.value.sort((a, b) => a.run.localeCompare(b.run) || a.index - b.index)
  }
  // A song rendered again after being hidden is news again.
  if (!song.finished) {
    hidden.value.delete(song.id)
  }
}

function upsertVersion(version: VersionState): void {
  const index = versions.value.findIndex((v) => v.id === version.id)
  if (index >= 0) {
    versions.value[index] = version
  } else {
    versions.value.push(version)
  }
}

function upsertStems(set: StemSetState): void {
  const index = stemSets.value.findIndex((s) => s.id === set.id)
  if (index >= 0) {
    stemSets.value[index] = set
  } else {
    stemSets.value.push(set)
  }
}

function upsertSwap(swap: SwapState): void {
  const index = swaps.value.findIndex((s) => s.id === swap.id)
  if (index >= 0) {
    swaps.value[index] = swap
  } else {
    swaps.value.push(swap)
  }
}

function upsertVideo(video: VideoState): void {
  const index = videos.value.findIndex((v) => v.id === video.id)
  if (index >= 0) {
    videos.value[index] = video
  } else {
    videos.value.push(video)
  }
}

function upsertImage(image: ImageState): void {
  const index = images.value.findIndex((i) => i.id === image.id)
  if (index >= 0) {
    images.value[index] = image
  } else {
    images.value.push(image)
  }
}

function hideFinished(): void {
  hidden.value = new Set([...hidden.value, ...songs.value.filter((s) => s.finished).map((s) => s.id)])
}

const unsubscribe = subscribe({
  snapshot(snapshot) {
    worker.value = snapshot.worker
    songs.value = snapshot.songs
    log.value = snapshot.log
    transcriptions.value = snapshot.transcriptions
    lyricsDraft.value = snapshot.lyrics
    versions.value = snapshot.versions
    stemSets.value = snapshot.stems ?? []
    swaps.value = snapshot.swaps ?? []
    videos.value = snapshot.videos ?? []
    images.value = snapshot.images ?? []
    speechTakes.value = snapshot.speech ?? []
    jobs.value = snapshot.queue ?? []
    bundleWindowSeconds.value = snapshot.bundleWindowSeconds ?? null
    memory.value = snapshot.memory?.holder ?? null
    // The stream (re)opened: whatever was written meanwhile is in the library now, the playlists may have changed on
    // another device.
    void loadLibrary()
    // A deploy restarts the server, so a reopened stream is also when a new build may be there.
    void checkForUpdate()
    loadPlaylists().catch((caught) => show(caught instanceof Error ? caught.message : String(caught), true))
  },
  song: upsert,
  worker(info) {
    worker.value = info
  },
  log(entry) {
    log.value.push(entry)
    if (log.value.length > logCapacity) {
      log.value.splice(0, log.value.length - logCapacity)
    }
  },
  library: () => scheduleLibraryReload(),
  transcription(transcription) {
    const index = transcriptions.value.findIndex((tr) => tr.id === transcription.id)
    if (index >= 0) {
      transcriptions.value[index] = transcription
    } else {
      transcriptions.value.push(transcription)
    }
  },
  lyrics(lyrics) {
    lyricsDraft.value = lyrics
  },
  version: upsertVersion,
  stems: upsertStems,
  swap: upsertSwap,
  video: upsertVideo,
  image: upsertImage,
  speech(take) {
    const index = speechTakes.value.findIndex((other) => other.id === take.id)
    if (index >= 0) {
      speechTakes.value[index] = take
    } else {
      speechTakes.value.push(take)
    }
  },
  queue(queue) {
    jobs.value = queue
  },
  memory(info) {
    memory.value = info.holder
  },
  connection(open) {
    connected.value = open
  },
})
onBeforeUnmount(unsubscribe)

// ---- Pages -------------------------------------------------------------------------------------------

/**
 * The pages used day to day stand in the bar; the tools around a song (transcribing, voices and stems, Logic, backing vocals, the
 * speech lab, the log) share one submenu, so the bar fits one line on a laptop. The queue concerns every page and has its
 * hourglass at the bar's end instead of an entry.
 */
type Page = { view: View; label: MessageKey; icon: string }
const pages: Page[] = [
  { view: 'create', label: 'menuCreate', icon: 'pi pi-sparkles' },
  { view: 'songs', label: 'menuSongs', icon: 'pi pi-list' },
  { view: 'playlist', label: 'menuPlaylist', icon: 'pi pi-play-circle' },
]
const tools: Page[] = [
  { view: 'templates', label: 'menuTemplates', icon: 'pi pi-bookmark' },
  { view: 'transcribe', label: 'menuTranscribe', icon: 'pi pi-microphone' },
  { view: 'voices', label: 'menuVoices', icon: 'pi pi-users' },
  { view: 'logic', label: 'menuLogic', icon: 'pi pi-box' },
  { view: 'harmony', label: 'menuHarmony', icon: 'pi pi-align-center' },
  { view: 'instruments', label: 'menuInstruments', icon: 'pi pi-sliders-h' },
  { view: 'lab', label: 'menuLab', icon: 'pi pi-comments' },
  { view: 'logs', label: 'menuLogs', icon: 'pi pi-file' },
  { view: 'storage', label: 'menuStorage', icon: 'pi pi-database' },
]

/**
 * Everything the queue page shows as in the works or waiting: songs, queued jobs, voice versions, stems, the speech
 * lab's takes, videos and painted covers.
 */
const queueCount = computed(
  () =>
    songs.value.filter((s) => !s.finished).length +
    jobs.value.length +
    versions.value.filter((v) => !v.finished).length +
    stemSets.value.filter((s) => !s.finished).length +
    swaps.value.filter((s) => !s.finished).length +
    videos.value.filter((v) => !v.finished).length +
    images.value.filter((i) => !i.finished).length +
    speechTakes.value.filter((take) => !take.finished).length +
    transcriptions.value.filter((tr) => !tr.finished).length,
)

/** Recordings waiting for SheetSage2; they are in the server's queue with the songs. */
const waitingTranscriptions = computed(() => jobs.value.filter((j) => j.kind === 'transcription'))

/**
 * The badge counts what the page holds that is worth a look: songs and transcriptions in the works or waiting, songs
 * left unrated long enough to be heard again, songs in the playlist.
 */
const badges = computed<Partial<Record<View, number>>>(() => ({
  songs: reviewCount(runs.value, ratings.value, reviewDays.value),
  transcribe: transcriptions.value.filter((tr) => !tr.finished).length + waitingTranscriptions.value.length,
  playlist: playlistIds.value.length,
  voices:
    versions.value.filter((v) => !v.finished).length +
    stemSets.value.filter((s) => !s.finished).length +
    swaps.value.filter((s) => !s.finished).length,
  lab: speechTakes.value.filter((take) => !take.finished).length,
}))

/**
 * Voices need Seed-VC (or ChangeMyVoice), stems the separator (or StemMyWav); without either the page is left out of the menu, and the library offers
 * only what is there.
 */
const voiceInfo = ref<VoiceInfo>({ voicesConfigured: false, conversionConfigured: false, stemsConfigured: false })
const voicesPage = computed(() => voiceInfo.value.voicesConfigured || voiceInfo.value.stemsConfigured)
getVoiceInfo()
  .then((info) => (voiceInfo.value = info))
  .catch(() => undefined)

/**
 * The Logic page is only built once it is opened, and then kept like the others: it asks the server for instruments
 * and presets on its first appearance, which a visit to the other pages does not need.
 */
const logicOpened = ref(view.value === 'logic')
const labOpened = ref(view.value === 'lab')
const logsOpened = ref(view.value === 'logs')
const templatesOpened = ref(view.value === 'templates')
const storageOpened = ref(view.value === 'storage')
const instrumentsOpened = ref(view.value === 'instruments')
const harmonyOpened = ref(view.value === 'harmony')
watch(view, (value) => {
  if (value === 'harmony') {
    harmonyOpened.value = true
  }
  if (value === 'instruments') {
    instrumentsOpened.value = true
  }
  if (value === 'logic') {
    logicOpened.value = true
  }
  if (value === 'lab') {
    labOpened.value = true
  }
  if (value === 'logs') {
    logsOpened.value = true
  }
  if (value === 'templates') {
    templatesOpened.value = true
  }
  if (value === 'storage') {
    storageOpened.value = true
  }
})

function menuItem(page: Page) {
  return {
    key: page.view,
    label: t(page.label),
    icon: page.icon,
    badge: badges.value[page.view] || undefined,
    active: page.view === view.value,
    command: () => navigate(page.view),
  }
}

const menu = computed(() => {
  const toolItems = tools.filter((page) => page.view !== 'voices' || voicesPage.value).map(menuItem)
  return [
    ...pages.map(menuItem),
    {
      key: 'tools',
      label: t('menuTools'),
      icon: 'pi pi-wrench',
      // What is in the works on a tool's page shows on the closed submenu too.
      badge: toolItems.reduce((sum, item) => sum + (item.badge ?? 0), 0) || undefined,
      active: toolItems.some((item) => item.active),
      items: toolItems,
    },
  ]
})

// ---- Library ----------------------------------------------------------------------------------------

const runs = ref<RunInfo[]>([])
const libraryLoading = ref(false)
const libraryError = ref<string | null>(null)
/** Every listed song with its run, for what the player, playlist and queue know only by id. */
const librarySongs = computed(
  () => new Map(runs.value.flatMap((run) => run.songs.map((song) => [song.id, { run, song }] as const))),
)
const listedIds = computed(() => new Set(librarySongs.value.keys()))
// The share and export dialog opened from the player or the playlist, which know a song only by its id.
registerExportLookup((songId) => {
  const found = librarySongs.value.get(songId)
  return found
    ? {
        songId,
        title: found.run.title,
        style: found.run.style,
        cover: found.song.coverUpdatedAt ? coverUrl(songId, found.song.coverUpdatedAt) : undefined,
      }
    : undefined
})
const storage = ref<StorageInfo | null>(null)
/** What all runs take, so it is clear what deleting would gain. */
const storageLabel = computed(() =>
  storage.value
    ? t('storage', {
        used: formatBytes(runs.value.reduce((sum, run) => sum + run.bytes, 0)),
        free: formatBytes(storage.value.freeBytes),
      })
    : '',
)

async function loadLibrary(): Promise<void> {
  libraryLoading.value = true
  try {
    // The free space is a hint only; the library shows without it.
    const [library, space] = await Promise.all([listLibrary(), getStorage().catch(() => null)])
    runs.value = library
    refreshTracks(library)
    setRatings(library)
    storage.value = space
    libraryError.value = null
  } catch (caught) {
    libraryError.value = caught instanceof Error ? caught.message : String(caught)
  } finally {
    libraryLoading.value = false
  }
}

/** The server tells every browser to reload as well; this one should not wait for that. */
function onDeleted(title: string): void {
  show(t('deleted', { title }))
  void loadLibrary()
}

/** A batch finishes its songs in quick succession; read the folder once for all of them. */
let libraryTimer: ReturnType<typeof setTimeout> | undefined
function scheduleLibraryReload(): void {
  clearTimeout(libraryTimer)
  libraryTimer = setTimeout(() => void loadLibrary(), 500)
}

// ---- Messages and templates ---------------------------------------------------------------------

const notice = ref<{ text: string; error: boolean } | null>(null)
let noticeTimer: ReturnType<typeof setTimeout> | undefined
function show(text: string, error = false): void {
  notice.value = { text, error }
  clearTimeout(noticeTimer)
  noticeTimer = setTimeout(() => (notice.value = null), 6000)
}

const generateForm = useTemplateRef<InstanceType<typeof GenerateForm>>('generateForm')
const queueList = useTemplateRef<InstanceType<typeof QueueList>>('queueList')
function useTemplate(run: RunInfo): void {
  form.value = { ...form.value, title: run.title, style: run.style, lyrics: run.lyrics }
  navigate('create')
  show(t('templateLoaded', { title: run.title || t('untitled') }))
}

/** A prompt template laid over the form; what it does not hold (title, lyrics, score, ...) stays. */
function useTemplateSettings(template: PromptTemplate): void {
  form.value = applyTemplate(form.value, template.settings)
  loadedTemplateId.value = template.id
  fieldErrors.value = {}
  navigate('create')
  show(t('templateApplied', { name: template.name, parts: partList(partsOf(template.settings)) }))
}

function resetForm(): void {
  generateForm.value?.reset()
  loadedTemplateId.value = null
}

/** A transcribed melody as the score of the next song: SheetSage2 writes it without chords, for planning "melody". */
function useScore(abc: string, name: string): void {
  form.value = { ...form.value, abc, cot: 'melody' }
  navigate('create')
  show(t('scoreApplied', { name }))
}

/**
 * A song's score to build on, e.g. with changed chords or tempo: with its run's style, lyrics and the song's seed,
 * so that a changed score is all that differs. Planning follows the score: chords are kept when it has them.
 */
function useSongScore(run: RunInfo, song: SongInfo, abc: string, midiWarnings?: string[]): void {
  const cot = planningFor(abc)
  form.value = {
    ...form.value,
    title: run.title,
    style: run.style,
    lyrics: run.lyrics,
    abc,
    cot,
    seed: song.seed === null ? form.value.seed : String(song.seed),
  }
  navigate('create')
  const applied = t(midiWarnings ? 'songMidiApplied' : 'songScoreApplied', {
    title: run.title || t('untitled'),
    song: t('songN', { n: song.index }),
    planning: t(cot === 'full' ? 'cotFull' : 'cotMelody'),
  })
  show(midiWarnings?.length ? `${applied} ${t('midiWarnings', { messages: midiWarnings.join(' ') })}` : applied)
}

/** A song the player or playlist names by id; one deleted meanwhile is gone from the library as well. */
function listedSong(songId: string): { run: RunInfo; song: SongInfo } | null {
  const found = librarySongs.value.get(songId)
  if (!found) {
    show(t('songGone'), true)
  }
  return found ?? null
}

async function useSongScoreById(songId: string): Promise<void> {
  const found = listedSong(songId)
  if (!found) {
    return
  }
  try {
    useSongScore(found.run, found.song, await songScore(songId))
  } catch (caught) {
    show(caught instanceof Error ? caught.message : String(caught), true)
  }
}

/** The song made again: everything it was generated with, from its request.json, the seed and a score included. */
async function useAsNewSong(songId: string): Promise<void> {
  const found = listedSong(songId)
  if (!found) {
    return
  }
  try {
    form.value = fromSongRequest(form.value, await songRequest(songId))
    fieldErrors.value = {}
    navigate('create')
    show(t('newSongApplied', { title: form.value.title || t('untitled'), song: t('songN', { n: found.song.index }) }))
  } catch (caught) {
    show(caught instanceof Error ? caught.message : String(caught), true)
  }
}
</script>

<template>
  <AudioBackground v-if="visuals.background" />
  <ConfirmDialog :style="{ width: 'min(28rem, calc(100vw - 2rem))' }" />
  <ExportDialog v-if="exportTarget" />
  <VideoDialog v-if="videoTarget" :live="videos" @error="show($event, true)" />
  <ImageDialog v-if="imageTarget" :live="images" @error="show($event, true)" @notice="show($event)" />
  <!--
    Header and player frame the page like the rollers of a scroll: both as wide as the page including its padding,
    flush at the top and the bottom, and the pages run between them.
  -->
  <header class="app-header mb-4">
    <Menubar
      :model="menu"
      breakpoint="640px"
      class="rounded-none border-0 border-b pl-[max(1rem,env(safe-area-inset-left))] pr-[max(1rem,env(safe-area-inset-right))]"
      :pt="{ button: { 'aria-label': t('menu') } }"
    >
      <template #start>
        <span class="brand whitespace-nowrap">Tonwerk</span>
      </template>
      <template #item="{ item, props, hasSubmenu, root }">
        <a v-bind="props.action" :class="['flex items-center gap-2', { 'text-primary font-semibold': item.active }]">
          <span :class="item.icon" />
          <span>{{ item.label }}</span>
          <Badge v-if="item.badge" :value="item.badge" size="small" />
          <span v-if="hasSubmenu" :class="['pi text-xs', root ? 'pi-angle-down' : 'pi-angle-right']" />
        </a>
      </template>
      <template #end>
        <div class="flex items-center gap-1">
          <!--
          The queue concerns every page (songs, drafts, versions, stems), so it sits here rather than in the menu, and
          on a phone its count stays in sight instead of behind the menu button.
        -->
          <Button
            as="a"
            href="#/queue"
            icon="pi pi-hourglass"
            :label="queueCount > 0 ? String(queueCount) : undefined"
            :severity="view === 'queue' ? undefined : 'secondary'"
            text
            size="small"
            :aria-label="queueCount > 0 ? t('queueActive', { n: queueCount }) : t('menuQueue')"
            :aria-current="view === 'queue' ? 'page' : undefined"
            v-tooltip.bottom="queueCount > 0 ? t('queueActive', { n: queueCount }) : t('menuQueue')"
            @click.prevent="navigate('queue')"
          />
          <Tag
            :value="workerLabel(worker.status, worker.busy)"
            :severity="workerSeverity"
            rounded
            class="whitespace-nowrap"
          />
          <SettingsMenu @notice="show" />
        </div>
      </template>
    </Menubar>
  </header>

  <div class="mb-4 flex flex-col gap-2 empty:hidden">
    <Message v-if="!connected" severity="warn">{{ t('disconnected') }}</Message>
    <Message v-if="updateAvailable" severity="info">
      <div class="flex flex-wrap items-center gap-x-3 gap-y-1">
        <span>{{ t('updateAvailable') }}</span>
        <Button :label="t('reload')" icon="pi pi-refresh" size="small" @click="reload" />
      </div>
    </Message>
    <Message v-if="worker.studioRunning" severity="warn">{{ t('studioRunning') }}</Message>
    <Message v-if="worker.lastError" severity="error">{{ t('workerError', { message: worker.lastError }) }}</Message>
    <Message v-if="notice" :severity="notice.error ? 'error' : 'info'" role="status">{{ notice.text }}</Message>
  </div>

  <!--
    v-show rather than v-if: a page keeps what was typed or uploaded on it while another one is open.
    On wide screens the song's fields are the left column; the right one holds the score, then the advanced parameters,
    collapsed since a normal song needs none of them, and a line with the models below. On a phone everything is one
    column in that order, so the score sits right under the song's fields.
  -->
  <main v-show="view === 'create'" class="grid gap-4 grid-cols-1 md:grid-cols-2 items-start">
    <Card>
      <template #title>
        <h2>
          <div class="flex justify-between">
            <div>
              {{ t('newSong') }}
            </div>

            <Button icon="pi pi-trash" rounded text :aria-label="t('resetForm')" @click="resetForm" />
          </div>
        </h2>
      </template>
      <template #content>
        <TemplatePicker
          v-model="form"
          class="mb-6"
          :voices="voiceInfo.conversionConfigured"
          @notice="show($event)"
          @error="show($event, true)"
        />
        <GenerateForm
          ref="generateForm"
          v-model="form"
          v-model:errors="fieldErrors"
          :queued-ids="queuedIds"
          :lyrics-draft="lyricsDraft"
          :voices="voiceInfo.conversionConfigured"
        />
      </template>
    </Card>

    <div class="flex min-w-0 flex-col gap-4">
      <ScoreField v-model="form" v-model:errors="fieldErrors" @notice="show($event)" @error="show($event, true)" />
      <AdvancedParameters v-model="form" v-model:errors="fieldErrors" :extensions="worker.extensions" />

      <!-- The whole queue has a page of its own; here only which model holds the memory, after a song was sent. -->
      <a
        v-if="queueCount > 0"
        href="#/queue"
        class="block text-color no-underline"
        :title="t('queueActive', { n: queueCount })"
        @click.prevent="navigate('queue')"
      >
        <QueueOverview
          :songs="songs"
          :jobs="jobs"
          :worker="worker"
          :lyrics-draft="lyricsDraft"
          :memory="memory"
          :versions="[...versions, ...stemSets, ...swaps]"
          :takes="speechTakes"
          :transcriptions="transcriptions"
          :images="images"
          :videos="videos"
        />
      </a>
    </div>
  </main>

  <main v-show="view === 'queue'">
    <Card>
      <template #title>
        <div class="flex flex-wrap justify-between">
          <h2>{{ t('queue') }}</h2>
          <Button
            icon="pi pi-stop-filled"
            text
            rounded
            v-if="worker.busy"
            @click="queueList?.run(queueList?.stopAll)"
          />
        </div>
      </template>
      <template #content>
        <QueueList
          ref="queueList"
          :songs="queue"
          :jobs="jobs"
          :listed="listedIds"
          :worker="worker"
          :lyrics-draft="lyricsDraft"
          :memory="memory"
          :versions="versions"
          :stems="stemSets"
          :swaps="swaps"
          :videos="videos"
          :images="images"
          :bundle-window-seconds="bundleWindowSeconds"
          :takes="speechTakes"
          :transcriptions="transcriptions"
          :log="log"
          @hide-finished="hideFinished"
          @error="show($event, true)"
        />
      </template>
    </Card>
  </main>

  <main v-show="view === 'transcribe'">
    <Card>
      <template #title>
        <h2>{{ t('transcription') }}</h2>
      </template>
      <template #content>
        <TranscribePanel
          :transcriptions="transcriptions"
          :waiting="waitingTranscriptions"
          @use-score="useScore"
          @error="show($event, true)"
        />
      </template>
    </Card>
    <Card>
      <template #title>
        <h2>{{ t('audioMidi') }}</h2>
      </template>
      <template #content>
        <AudioMidiPanel />
      </template>
    </Card>
  </main>

  <main v-show="view === 'songs'">
    <Card>
      <template #title>
        <div class="flex justify-between items-center">
          <h2>{{ t('library') }}</h2>
          <!-- Leads to the storage page, which says what else takes the disk and offers to clean up. -->
          <a v-if="storageLabel" href="#/storage" class="storage-link muted text-sm font-normal ml-auto mr-2">{{
            storageLabel
          }}</a>
          <Button
            icon="pi pi-refresh"
            :disabled="libraryLoading"
            text
            rounded
            :aria-label="t('refresh')"
            @click="loadLibrary"
          />
        </div>
      </template>
      <template #content>
        <LibraryList
          :runs="runs"
          :loading="libraryLoading"
          :error="libraryError"
          :busy-ids="busyIds"
          :voices="voiceInfo.conversionConfigured"
          :stems="voiceInfo.stemsConfigured"
          :live-versions="versions"
          @template="useTemplate"
          @use-score="useSongScore"
          @new-song="useAsNewSong"
          @deleted="onDeleted"
          @notice="show($event)"
          @error="show($event, true)"
        />
      </template>
    </Card>
  </main>

  <main v-show="view === 'playlist'">
    <Card>
      <template #title>
        <h2>{{ t('playlist') }}</h2>
      </template>
      <template #content>
        <PlaylistView :runs="runs" @use-score="useSongScoreById" @new-song="useAsNewSong" @error="show($event, true)" />
      </template>
    </Card>
  </main>

  <main v-if="voicesPage" v-show="view === 'voices'" class="flex flex-col gap-4">
    <Card v-if="voiceInfo.voicesConfigured">
      <template #title>
        <h2>{{ t('voices') }}</h2>
      </template>
      <template #content>
        <VoicesPanel :active="view === 'voices'" :versions="versions" @error="show($event, true)" />
      </template>
    </Card>
    <Card v-if="voiceInfo.voicesConfigured">
      <template #title>
        <h2>{{ t('swap') }}</h2>
      </template>
      <template #content>
        <VoiceSwapPanel
          :active="view === 'voices'"
          :live="swaps"
          :can-separate="voiceInfo.conversionConfigured"
          @notice="show($event)"
          @error="show($event, true)"
        />
      </template>
    </Card>
    <Card v-if="voiceInfo.stemsConfigured">
      <template #title>
        <h2>{{ t('stems') }}</h2>
      </template>
      <template #content>
        <StemsPanel :active="view === 'voices'" :runs="runs" :live="stemSets" @error="show($event, true)" />
      </template>
    </Card>
  </main>

  <main v-if="logicOpened" v-show="view === 'logic'">
    <LogicPage :runs="runs" :stems="stemSets" :stems-configured="voiceInfo.stemsConfigured" />
  </main>

  <main v-if="harmonyOpened" v-show="view === 'harmony'">
    <HarmonyPage :runs="runs" />
  </main>

  <main v-if="instrumentsOpened" v-show="view === 'instruments'">
    <InstrumentsPage :active="view === 'instruments'" />
  </main>

  <main v-if="labOpened" v-show="view === 'lab'">
    <SpeechLab :active="view === 'lab'" :connected="connected" :live="speechTakes" @error="show($event, true)" />
  </main>
  <main v-if="logsOpened" v-show="view === 'logs'">
    <LogPage :active="view === 'logs'" />
  </main>

  <main v-if="templatesOpened" v-show="view === 'templates'">
    <Card>
      <template #title>
        <h2>{{ t('templates') }}</h2>
      </template>
      <template #content>
        <p class="muted mt-0">{{ t('templatesIntro') }}</p>
        <TemplatesPage
          :active="view === 'templates'"
          @apply="useTemplateSettings"
          @notice="show($event)"
          @error="show($event, true)"
        />
      </template>
    </Card>
  </main>

  <main v-if="storageOpened" v-show="view === 'storage'">
    <Card>
      <template #title>
        <h2>{{ t('storageTitle') }}</h2>
      </template>
      <template #content>
        <p class="muted mt-0">{{ t('storageIntro') }}</p>
        <StoragePage :active="view === 'storage'" @notice="show($event)" @error="show($event, true)" />
      </template>
    </Card>
  </main>

  <!-- Outside the pages, so switching between them does not stop the song. The spacer keeps it off the page's end. -->
  <div v-if="current" class="h-28" />
  <PlayerBar
    :has-score="!!current && !!librarySongs.get(current.songId)?.song.hasScore"
    :lyrics="(current && librarySongs.get(current.songId)?.run.lyrics) || ''"
    @use-score="useSongScoreById"
    @new-song="useAsNewSong"
    @error="show($event, true)"
  />
</template>

<style scoped>
.storage-link {
  color: inherit;
  text-decoration: underline dotted;
  text-underline-offset: 0.2em;
}

/* Negative margins undo #app's side padding (style.css), so the header is as wide as the player (72rem at most). */
.app-header {
  position: sticky;
  top: 0;
  z-index: 10;
  margin-right: calc(-1 * max(1rem, env(safe-area-inset-right)));
  margin-left: calc(-1 * max(1rem, env(safe-area-inset-left)));
  padding-top: env(safe-area-inset-top);
  background: var(--p-content-background);
  box-shadow: 0 4px 16px rgb(0 0 0 / 0.08);
}

.brand {
  margin-right: 0.75rem;
  font-size: 1.15rem;
  font-weight: 700;
  letter-spacing: -0.01em;
}
</style>
