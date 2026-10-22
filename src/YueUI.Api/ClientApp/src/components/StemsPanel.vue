<script setup lang="ts">
import Checkbox from 'primevue/checkbox'
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import {
  deleteStems,
  listStemModels,
  listStems,
  separateFile,
  separateSong,
  stemAudioUrl,
  stemToMidi,
  type MidiTrack,
} from '../api'
import { loadMidiInfo, midiInfo, midiSettings } from '../audioMidi'
import { formatDateTime, formatDuration, hasMessage, t } from '../i18n'
import { machineField, ranOnNote, storedMachine } from '../machines'
import MachinePicker from './MachinePicker.vue'
import { playing as songPlaying, toggle as toggleSong } from '../player'
import type { RunInfo, StemFile, StemModel, StemSetState } from '../types'
import { showSong, songHref, stemSong } from '../view'
import MidiOptions from './MidiOptions.vue'
import MidiResult from './MidiResult.vue'
import NumberField from './NumberField.vue'
import TabRecordButton from './TabRecordButton.vue'
import WaveformView from './WaveformView.vue'

/**
 * Songs split into their stems: pick a song (or a file of one's own) and a model, and the stems are listed below once they are
 * made, each with its waveform to listen to and download. The separation waits in the same queue as the voices,
 * since it needs the same memory.
 */
const props = defineProps<{
  /** The page is open; the lists are only asked for then. */
  active: boolean
  runs: RunInfo[]
  /** Sets in the works, and those finished since the page loaded, as the event stream reports them. */
  live: StemSetState[]
}>()

const emit = defineEmits<{ error: [message: string] }>()

const loaded = ref<StemSetState[]>([])
const loadError = ref<string | null>(null)
const models = ref<StemModel[]>([])
/** Deleted here; the server's `cancelled` event may come after the list was read again. */
const removed = ref(new Set<string>())

async function load(): Promise<void> {
  try {
    loaded.value = await listStems()
    loadError.value = null
  } catch (caught) {
    loadError.value = message(caught)
  }
}

async function loadModels(): Promise<void> {
  try {
    models.value = await listStemModels()
    if (!models.value.some((m) => m.id === model.value)) {
      model.value = models.value.find((m) => m.isDefault)?.id ?? models.value[0]?.id ?? null
    }
  } catch (caught) {
    emit('error', message(caught))
  }
}

watch(
  () => props.active,
  (active) => {
    if (active) {
      void load()
      void loadMidiInfo()
      if (models.value.length === 0) {
        void loadModels()
      }
    }
  },
  { immediate: true },
)
// A deleted song takes its stems along on the server.
watch(
  () => props.runs,
  () => {
    if (props.active) {
      void load()
    }
  },
)

/** The live state wins: it is at least as new as the list. */
const sets = computed(() => {
  const byId = new Map(loaded.value.map((set) => [set.id, set]))
  for (const set of props.live) {
    byId.set(set.id, set)
  }
  return [...byId.values()]
    .filter((set) => set.stage !== 'cancelled' && !removed.value.has(set.id))
    .sort((a, b) => b.createdAt.localeCompare(a.createdAt))
})

// ---- Asking for stems -------------------------------------------------------------------------------

/** A song of the library, or a file from the device: anything ffmpeg reads, a mix from elsewhere included. */
type Source = 'song' | 'file'
const source = ref<Source>('song')
const sourceOptions = computed(() => [
  { value: 'song', label: t('stemsSourceSong') },
  { value: 'file', label: t('stemsSourceFile') },
])

const song = ref<string | null>(stemSong.value)
watch(stemSong, (value) => {
  if (value) {
    song.value = value
    source.value = 'song'
  }
})

const file = ref<File | null>(null)
const fileInput = ref<HTMLInputElement | null>(null)
/** Read by the browser, for the estimate; null until known or where it cannot read the file. */
const fileSeconds = ref<number | null>(null)

function pick(event: Event): void {
  take((event.target as HTMLInputElement).files?.[0] ?? null)
}

/** A recorded tab counts as a chosen file. */
function tabRecorded(recorded: File): void {
  if (fileInput.value) {
    fileInput.value.value = ''
  }
  take(recorded)
}

function take(chosenFile: File | null): void {
  file.value = chosenFile
  fileSeconds.value = null
  if (!file.value) {
    return
  }
  const chosen = file.value
  const url = URL.createObjectURL(chosen)
  const probe = new Audio()
  probe.preload = 'metadata'
  const done = (): void => {
    if (file.value === chosen && Number.isFinite(probe.duration)) {
      fileSeconds.value = probe.duration
    }
    URL.revokeObjectURL(url)
  }
  probe.addEventListener('loadedmetadata', done, { once: true })
  probe.addEventListener('error', () => URL.revokeObjectURL(url), { once: true })
  probe.src = url
}

const ready = computed(() => (source.value === 'song' ? !!song.value : !!file.value))

const songOptions = computed(() =>
  props.runs.flatMap((run) =>
    run.songs
      .filter((s) => s.hasAudio)
      .map((s) => ({
        value: s.id,
        label: `${run.title || t('untitled')} · ${t('songN', { n: s.index })}`,
        seconds: s.seconds,
      })),
  ),
)

const modelKey = 'yue-ui.stemModel'
const model = ref<string | null>(readModel())
watch(model, (value) => {
  try {
    if (value) {
      localStorage.setItem(modelKey, value)
    }
  } catch {
    // Only a convenience.
  }
})

function readModel(): string | null {
  try {
    return localStorage.getItem(modelKey)
  } catch {
    return null
  }
}

const chosenModel = computed(() => models.value.find((m) => m.id === model.value) ?? null)
const dereverb = ref(false)
const separating = ref(false)
/** The Mac that separates; empty lets the server choose. */
const machine = storedMachine('stems')

/** What the model makes and roughly how long it takes for the chosen song. */
const modelHint = computed(() => {
  const chosen = chosenModel.value
  if (!chosen) {
    return ''
  }
  const parts: string[] = []
  if (chosen.stems?.length) {
    parts.push(chosen.stems.map(stemLabel).join(', '))
  }
  const seconds =
    source.value === 'song' ? songOptions.value.find((o) => o.value === song.value)?.seconds : fileSeconds.value
  if (chosen.realtimeFactor && seconds) {
    parts.push(t('stemsEstimate', { duration: formatDuration(seconds / chosen.realtimeFactor) }))
  }
  return parts.join(' · ')
})

async function separate(): Promise<void> {
  if (!ready.value || separating.value) {
    return
  }
  separating.value = true
  try {
    const set =
      source.value === 'song'
        ? await separateSong(song.value!, model.value, dereverb.value, machineField(machine.value))
        : await separateFile(file.value!, model.value, dereverb.value, machineField(machine.value))
    loaded.value = [set, ...loaded.value.filter((s) => s.id !== set.id)]
    if (source.value === 'file') {
      file.value = null
      fileSeconds.value = null
      if (fileInput.value) {
        fileInput.value.value = ''
      }
    }
  } catch (caught) {
    emit('error', message(caught))
  } finally {
    separating.value = false
  }
}

async function remove(set: StemSetState): Promise<void> {
  const question = set.finished ? 'stemsDeleteConfirm' : 'stemsCancelConfirm'
  if (!window.confirm(t(question, { name: setName(set) }))) {
    return
  }
  try {
    if (listening.value?.set === set.id) {
      stop()
    }
    await deleteStems(set.id)
    removed.value = new Set([...removed.value, set.id])
  } catch (caught) {
    emit('error', message(caught))
  }
}

// ---- Listening --------------------------------------------------------------------------------------

/**
 * One audio element for the stems, apart from the song player, like the reference voices: a stem is something to
 * compare, not to queue. Switching to another stem of the same song keeps the time, so the parts can be heard one
 * after the other at the same bar.
 */
const audio = new Audio()
audio.preload = 'none'
const listening = ref<{ set: string; name: string } | null>(null)
const position = ref(0)
let frame = 0

audio.addEventListener('pause', () => cancelAnimationFrame(frame))
audio.addEventListener('ended', () => (listening.value = null))
audio.addEventListener('play', follow)
onBeforeUnmount(stop)

/** The position line moves with every frame while it plays; timeupdate comes only a few times a second. */
function follow(): void {
  position.value = audio.currentTime
  if (!audio.paused) {
    frame = requestAnimationFrame(follow)
  }
}

function isPlaying(set: StemSetState, stem: StemFile): boolean {
  return listening.value?.set === set.id && listening.value.name === stem.name && !paused.value
}

const paused = ref(true)
audio.addEventListener('pause', () => (paused.value = true))
audio.addEventListener('play', () => (paused.value = false))

function toggle(set: StemSetState, stem: StemFile): void {
  if (listening.value?.set === set.id && listening.value.name === stem.name) {
    if (audio.paused) {
      void audio.play().catch(() => undefined)
    } else {
      audio.pause()
    }
    return
  }
  const at = listening.value?.set === set.id ? audio.currentTime : 0
  start(set, stem, at)
}

/** Inside the click, as iOS wants it. */
function start(set: StemSetState, stem: StemFile, at: number): void {
  // Two songs at once help nobody.
  if (songPlaying.value) {
    toggleSong()
  }
  listening.value = { set: set.id, name: stem.name }
  audio.src = stemAudioUrl(set.id, stem.name)
  audio.currentTime = at
  position.value = at
  void audio.play().catch(() => (listening.value = null))
}

function seek(set: StemSetState, stem: StemFile, seconds: number): void {
  if (listening.value?.set === set.id && listening.value.name === stem.name) {
    audio.currentTime = seconds
    position.value = seconds
  } else {
    start(set, stem, seconds)
  }
}

function stop(): void {
  audio.pause()
  listening.value = null
}

function stemPosition(set: StemSetState, stem: StemFile): number {
  return listening.value?.set === set.id && listening.value.name === stem.name ? position.value : 0
}

// ---- MIDI -------------------------------------------------------------------------------------------

/** Made in this visit, by set and stem; a MIDI file takes seconds, so it is made again rather than stored. */
const midi = ref<Record<string, MidiTrack>>({})
const midiErrors = ref<Record<string, string>>({})
const converting = ref<string | null>(null)
const anyDone = computed(() => sets.value.some((set) => set.stage === 'done'))
/** An upload has no score to take a tempo from; as on the transcription page, without one the file is at 120 bpm. */
const anyUploadDone = computed(() => sets.value.some((set) => set.stage === 'done' && set.upload))
const tempo = ref<number>(midiSettings.value.tempo ?? 120)
const tempoKnown = ref(midiSettings.value.tempo != null)

function midiKey(set: StemSetState, stem: StemFile): string {
  return `${set.id}/${stem.name}`
}

async function toMidi(set: StemSetState, stem: StemFile): Promise<void> {
  const key = midiKey(set, stem)
  if (converting.value) {
    return
  }
  converting.value = key
  delete midiErrors.value[key]
  try {
    let uploadTempo: number | null = null
    if (set.upload) {
      midiSettings.value.tempo = tempoKnown.value ? tempo.value : null
      uploadTempo = midiSettings.value.tempo
    }
    midi.value[key] = await stemToMidi(set.id, stem.name, midiSettings.value, uploadTempo)
  } catch (caught) {
    delete midi.value[key]
    midiErrors.value[key] = message(caught)
  } finally {
    converting.value = null
  }
}

// ---- Labels -----------------------------------------------------------------------------------------

/** StemMyWav's file names, as people call them; an unknown one as it is. */
function stemLabel(name: string): string {
  const key = `stem_${name.toLowerCase()}`
  return hasMessage(key) ? t(key) : name
}

function modelName(id: string): string {
  return models.value.find((m) => m.id === id)?.name ?? id
}

function songName(set: StemSetState): string {
  return t('songN', { n: set.songId.slice(set.songId.lastIndexOf('/') + 5) })
}

/** How the list and the questions name a set: run title and song, or the uploaded file. */
function setName(set: StemSetState): string {
  return set.upload ? set.title : `${set.title || t('untitled')} · ${songName(set)}`
}

function message(caught: unknown): string {
  return caught instanceof Error ? caught.message : String(caught)
}

const stageSeverity: Record<string, string | undefined> = { done: 'success', failed: 'danger' }
</script>

<template>
  <section class="flex flex-col gap-4">
    <p class="muted m-0 text-sm">{{ t('stemsIntro') }}</p>

    <form class="flex flex-wrap items-end gap-3" @submit.prevent="separate">
      <div class="flex w-full">
        <SelectButton
          v-model="source"
          :options="sourceOptions"
          option-label="label"
          option-value="value"
          :allow-empty="false"
          :aria-label="t('stemsSource')"
          size="small"
        />
      </div>
      <div v-if="source === 'file'" class="flex min-w-0 flex-[1_1_16rem] flex-col gap-1">
        <label for="stems-file" class="muted text-sm">{{ t('stemsFile') }}</label>
        <!-- A native file input, as for the voices: PrimeVue's FileUpload brings its own upload flow. -->
        <input
          id="stems-file"
          ref="fileInput"
          type="file"
          accept="audio/*,.wav,.mp3,.flac,.m4a,.aac,.ogg,.opus,.aif,.aiff"
          class="hidden"
          @change="pick"
        />
        <div class="flex min-w-0 items-center gap-2">
          <Button
            type="button"
            icon="pi pi-folder-open"
            :label="t('chooseRecording')"
            severity="secondary"
            outlined
            class="shrink-0"
            :disabled="separating"
            @click="fileInput?.click()"
          />
          <span class="muted min-w-0 truncate text-sm">{{ file?.name ?? t('noRecording') }}</span>
        </div>
        <TabRecordButton :disabled="separating" @recorded="tabRecorded" @error="emit('error', $event)" />
      </div>
      <div v-else class="flex min-w-0 flex-[1_1_16rem] flex-col gap-1">
        <label for="stems-song" class="muted text-sm">{{ t('stemsSong') }}</label>
        <Select
          v-model="song"
          input-id="stems-song"
          :options="songOptions"
          option-label="label"
          option-value="value"
          filter
          :placeholder="t('stemsSongPick')"
          :empty-message="t('stemsNoSongs')"
          fluid
        />
      </div>
      <div class="flex min-w-0 flex-[1_1_12rem] flex-col gap-1">
        <label for="stems-model" class="muted text-sm">{{ t('stemsModel') }}</label>
        <Select v-model="model" input-id="stems-model" :options="models" option-label="name" option-value="id" fluid />
      </div>
      <div class="flex items-center gap-2 py-2">
        <Checkbox v-model="dereverb" input-id="stems-dereverb" binary />
        <label for="stems-dereverb" class="text-sm">{{ t('stemsDereverb') }}</label>
      </div>
      <MachinePicker v-model="machine" engine="stems" :disabled="separating" />
      <Button
        type="submit"
        :label="t('stemsSeparate')"
        icon="pi pi-sliders-v"
        :loading="separating"
        :disabled="!ready || separating"
      />
    </form>
    <p v-if="modelHint" class="muted -mt-2 mb-0 text-sm">{{ modelHint }}</p>

    <p v-if="loadError" class="danger m-0">{{ t('stemsError', { message: loadError }) }}</p>
    <p v-else-if="sets.length === 0" class="muted m-0">{{ t('stemsEmpty') }}</p>

    <div v-if="midiInfo?.installed && anyDone" class="flex flex-col gap-1">
      <span class="muted text-sm">{{ t('stemsMidi') }}</span>
      <MidiOptions id-prefix="stems-midi" />
      <div v-if="anyUploadDone" class="flex flex-wrap items-center gap-x-3 gap-y-2">
        <div class="flex items-center gap-2">
          <Checkbox v-model="tempoKnown" input-id="stems-midi-tempo-known" binary />
          <label for="stems-midi-tempo-known" class="text-sm">{{ t('stemsMidiTempo') }}</label>
        </div>
        <NumberField v-if="tempoKnown" v-model="tempo" :min="20" :max="300" class="w-20" :aria-label="t('midiTempo')" />
        <span v-if="tempoKnown" class="muted text-sm">bpm</span>
        <span v-else-if="midiSettings.quantize" class="danger text-sm">{{ t('midiQuantizeNeedsTempo') }}</span>
      </div>
    </div>

    <ul class="m-0 flex list-none flex-col gap-4 p-0">
      <li v-for="set in sets" :key="set.id" class="flex flex-col gap-2 border-t border-surface pt-3">
        <div class="flex flex-wrap items-center gap-x-3 gap-y-1">
          <div class="min-w-0 flex-1">
            <span v-if="set.upload" class="flex min-w-0 items-center gap-1 font-semibold">
              <i class="pi pi-file muted text-xs" :aria-label="t('stemsSourceFile')" />
              <span class="truncate">{{ set.title }}</span>
            </span>
            <a
              v-else
              :href="songHref(set.songId)"
              class="font-semibold text-primary no-underline"
              @click.prevent="showSong(set.songId)"
              >{{ setName(set) }}</a
            >
            <div class="muted truncate text-sm">
              {{ modelName(set.model) }}<template v-if="set.dereverb"> · {{ t('stemsDereverbShort') }}</template> ·
              {{ formatDateTime(set.createdAt)
              }}<template v-if="ranOnNote(set.ranOn)"> · {{ ranOnNote(set.ranOn) }}</template>
            </div>
          </div>
          <Tag v-if="set.stage !== 'done'" :severity="stageSeverity[set.stage]">
            <i v-if="set.stage === 'separating'" class="pi pi-spin pi-spinner text-xs" />
            {{ t(`stemsStage_${set.stage}`) }}
          </Tag>
          <Button
            icon="pi pi-trash"
            text
            rounded
            size="small"
            severity="danger"
            v-tooltip="set.finished ? t('stemsDelete') : t('stemsCancel')"
            :aria-label="set.finished ? t('stemsDelete') : t('stemsCancel')"
            @click="remove(set)"
          />
        </div>
        <p v-if="set.stage === 'failed' && set.message" class="danger m-0 text-sm">{{ set.message }}</p>

        <div v-for="stem in set.stems" :key="stem.name" class="flex flex-col gap-1">
          <div class="flex items-center gap-2">
            <Button
              :icon="isPlaying(set, stem) ? 'pi pi-pause' : 'pi pi-play'"
              rounded
              outlined
              size="small"
              :aria-label="isPlaying(set, stem) ? t('pause') : t('stemsListen', { stem: stemLabel(stem.name) })"
              @click="toggle(set, stem)"
            />
            <div class="min-w-0 flex-1">
              <div class="flex items-baseline gap-2 text-sm">
                <span class="truncate font-semibold">{{ stemLabel(stem.name) }}</span>
                <span v-if="stem.seconds > 0" class="muted ml-auto shrink-0 tabular-nums">
                  <template v-if="listening?.set === set.id && listening.name === stem.name"
                    >{{ formatDuration(position) }} /
                  </template>
                  {{ formatDuration(stem.seconds) }}
                </span>
              </div>
              <WaveformView
                v-if="stem.peaks && stem.seconds > 0"
                :peaks="stem.peaks"
                :duration="stem.seconds"
                :start="0"
                :end="stemPosition(set, stem)"
                :position="stemPosition(set, stem)"
                compact
                @seek="seek(set, stem, $event)"
              />
            </div>
            <Button
              as="a"
              :href="stemAudioUrl(set.id, stem.name, true)"
              icon="pi pi-download"
              text
              rounded
              size="small"
              v-tooltip="t('stemsDownload')"
              :aria-label="t('stemsDownload')"
            />
            <Button
              v-if="midiInfo?.installed"
              icon="pi pi-wave-pulse"
              text
              rounded
              size="small"
              :loading="converting === midiKey(set, stem)"
              :disabled="converting !== null"
              v-tooltip="t('stemsToMidi')"
              :aria-label="t('stemsToMidi')"
              @click="toMidi(set, stem)"
            />
          </div>
          <MidiResult v-if="midi[midiKey(set, stem)]" :track="midi[midiKey(set, stem)]!" class="pl-10" />
          <p v-if="midiErrors[midiKey(set, stem)]" class="danger m-0 pl-10 text-sm">
            {{ midiErrors[midiKey(set, stem)] }}
          </p>
        </div>
      </li>
    </ul>
  </section>
</template>
