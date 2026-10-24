<script setup lang="ts">
import Slider from 'primevue/slider'
import { computed, onBeforeUnmount, ref, useTemplateRef, watch } from 'vue'
import Select from 'primevue/select'
import { addVoice, addVoiceFromSinging, deleteVoice, getSinging, listVoices, voiceAudioUrl } from '../api'
import { formatDateTime, formatDuration, locale, t, versionProgress } from '../i18n'
import { peaksOf } from '../waveform'
import NumberField from './NumberField.vue'
import WaveformView from './WaveformView.vue'
import type { ReferenceVoice, SingingVoiceInfo, VersionState } from '../types'
import { showSong } from '../view'

/**
 * The reference voices, the ones a song can be sung with ("Sing with a voice" in the library),
 * and the versions in the works. The voices live only in the service, as in yue-to-logic-pro, so both apps share one
 * collection.
 */
const props = defineProps<{
  /** Shown only when a page is open, so the service is not asked while nobody looks. */
  active: boolean
  /** Versions in the works and those finished since the page loaded. */
  versions: VersionState[]
}>()

const emit = defineEmits<{
  error: [message: string]
  /** A voice was added or deleted, so other lists of them (the swap's picker) read them again. */
  changed: []
}>()

const voices = ref<ReferenceVoice[] | null>(null)
const loading = ref(false)
const loadError = ref<string | null>(null)

async function load(): Promise<void> {
  loading.value = true
  try {
    voices.value = await listVoices()
    loadError.value = null
  } catch (caught) {
    loadError.value = message(caught)
  } finally {
    loading.value = false
  }
}

watch(
  () => props.active,
  (active) => {
    if (active && !loading.value) {
      void load()
      void loadSinging()
    }
  },
  { immediate: true },
)

// ---- Upload ----------------------------------------------------------------------------------------

const fileInput = useTemplateRef<HTMLInputElement>('fileInput')
const label = ref('')
const file = ref<File | null>(null)
const adding = ref(false)

function chosen(event: Event): void {
  file.value = (event.target as HTMLInputElement).files?.[0] ?? null
  // The file's name is a good start for the voice's name.
  if (file.value && !label.value.trim()) {
    label.value = file.value.name.replace(/\.[^.]+$/, '')
  }
  setPreview(file.value)
}

async function add(): Promise<void> {
  if (!label.value.trim() || !file.value || adding.value || tooShort.value) {
    return
  }
  adding.value = true
  try {
    const [start, end] = trimmed.value
    await addVoice(label.value.trim(), file.value, start, end)
    emit('changed')
    label.value = ''
    file.value = null
    setPreview(null)
    if (fileInput.value) {
      fileInput.value.value = ''
    }
    await load()
  } catch (caught) {
    emit('error', message(caught))
  } finally {
    adding.value = false
  }
}

// ---- From the singing page -------------------------------------------------------------------------

/**
 * The singing page's own voices, recorded there with the words they sing. Their phrase is a reference Seed-VC takes as
 * it is, so one can be copied over instead of recorded again; the copy is a voice of its own here, kept when the
 * singing voice is deleted.
 */
const singingVoices = ref<SingingVoiceInfo[]>([])
const singingChoice = ref<string | null>(null)
const takingOver = ref(false)

async function loadSinging(): Promise<void> {
  try {
    singingVoices.value = (await getSinging()).voices.filter((v) => v.own)
    if (!singingVoices.value.some((v) => v.id === singingChoice.value)) {
      singingChoice.value = singingVoices.value[0]?.id ?? null
    }
  } catch {
    // Without the singing page there is nothing to take over; the rest of the page works as before.
    singingVoices.value = []
  }
}

/** Marked by name, since the copy has an id of its own: taking one over twice is allowed but rarely meant. */
const singingOptions = computed(() =>
  singingVoices.value.map((v) => ({
    id: v.id,
    label: voices.value?.some((r) => r.label === v.label) ? `${v.label} (${t('voiceFromSingingTaken')})` : v.label,
  })),
)

async function takeOver(): Promise<void> {
  if (!singingChoice.value || takingOver.value) {
    return
  }
  takingOver.value = true
  try {
    await addVoiceFromSinging(singingChoice.value)
    await load()
    emit('changed')
  } catch (caught) {
    emit('error', message(caught))
  } finally {
    takingOver.value = false
  }
}

// ---- Trimming --------------------------------------------------------------------------------------

/**
 * The chosen file, played in the browser, so the part to keep can be found by ear: a recording often starts with
 * talking or silence, and only 25 seconds are kept. Only the numbers go to the server, the whole file
 * with them; the server cuts it.
 */
const preview = useTemplateRef<HTMLAudioElement>('preview')
const previewUrl = ref<string | null>(null)
/** Known once the browser has read the file's header; without it (a format it cannot play) there is no trimming. */
const duration = ref<number | null>(null)
/** Start and end in seconds, as the slider's range. */
const clip = ref<[number, number]>([0, 0])
/** The file's loudness outline, drawn above the slider; null while decoding or when the browser cannot. */
const peaks = ref<number[] | null>(null)
/** Where playback stands, for the waveform's line. */
const position = ref(0)
/** Set while "play the part" runs, so playback stops at the end. */
const playingClip = ref(false)

/** The server's limits (Seed-VC's and ChangeMyVoice's alike): it keeps 25 seconds from the start and refuses a part shorter than 3. */
const keptSeconds = 25
const minSeconds = 3

onBeforeUnmount(() => setPreview(null))

function setPreview(chosen: File | null): void {
  if (previewUrl.value) {
    URL.revokeObjectURL(previewUrl.value)
  }
  previewUrl.value = chosen ? URL.createObjectURL(chosen) : null
  duration.value = null
  playingClip.value = false
  peaks.value = null
  position.value = 0
  if (chosen) {
    // One bar per few pixels of a phone's width is enough to find the singing and the silence.
    void peaksOf(chosen, 200).then((result) => {
      if (file.value === chosen) {
        peaks.value = result
      }
    })
  }
}

function metadata(): void {
  const seconds = preview.value?.duration ?? NaN
  duration.value = Number.isFinite(seconds) && seconds > 0 ? round(seconds) : null
  if (duration.value !== null) {
    clip.value = [0, Math.min(duration.value, keptSeconds)]
  }
}

const start = computed({
  get: () => clip.value[0],
  set: (value: number) => (clip.value = [Math.min(value, clip.value[1]), clip.value[1]]),
})
const end = computed({
  get: () => clip.value[1],
  set: (value: number) => (clip.value = [clip.value[0], Math.max(value, clip.value[0])]),
})

const clipSeconds = computed(() => round(clip.value[1] - clip.value[0]))
const tooShort = computed(() => duration.value !== null && clipSeconds.value < minSeconds)

/** What is sent: only what differs from the whole file, so an untouched choice is the plain upload it was before. */
const trimmed = computed<[number | undefined, number | undefined]>(() =>
  duration.value === null
    ? [undefined, undefined]
    : [clip.value[0] > 0 ? clip.value[0] : undefined, clip.value[1] < duration.value ? clip.value[1] : undefined],
)

function here(which: 'start' | 'end'): void {
  const position = round(preview.value?.currentTime ?? 0)
  if (which === 'start') {
    start.value = position
  } else {
    end.value = position
  }
}

function playClip(): void {
  const element = preview.value
  if (!element) {
    return
  }
  element.currentTime = clip.value[0]
  playingClip.value = true
  // Inside the click, as iOS wants it.
  void element.play().catch(() => (playingClip.value = false))
}

function seek(seconds: number): void {
  if (preview.value) {
    preview.value.currentTime = seconds
    position.value = seconds
  }
}

function timeUpdate(): void {
  const element = preview.value
  position.value = element?.currentTime ?? 0
  if (playingClip.value && element && element.currentTime >= clip.value[1]) {
    element.pause()
    playingClip.value = false
  }
}

function round(seconds: number): number {
  return Math.round(seconds * 10) / 10
}

function formatSeconds(seconds: number): string {
  return seconds.toLocaleString(locale.value, { maximumFractionDigits: 1 })
}

/** Refused (409) while a job of the service still waits for the voice. */
async function remove(voice: ReferenceVoice): Promise<void> {
  if (!window.confirm(t('voiceDeleteConfirm', { label: voice.label }))) {
    return
  }
  try {
    stop()
    await deleteVoice(voice.id)
    await load()
    emit('changed')
  } catch (caught) {
    emit('error', message(caught))
  }
}

// ---- Listening -------------------------------------------------------------------------------------

/**
 * One audio element of its own for the recordings, apart from the song player: a reference is a short check of
 * what the model hears, not something to queue.
 */
const audio = new Audio()
audio.preload = 'none'
const listening = ref<string | null>(null)
audio.addEventListener('ended', () => (listening.value = null))
audio.addEventListener('pause', () => (listening.value = null))
onBeforeUnmount(stop)

function listen(voice: ReferenceVoice): void {
  if (listening.value === voice.id) {
    stop()
    return
  }
  audio.src = voiceAudioUrl(voice.id)
  // Inside the click, as iOS wants it.
  void audio.play().catch(() => (listening.value = null))
  listening.value = voice.id
}

function stop(): void {
  audio.pause()
  listening.value = null
}

function message(caught: unknown): string {
  return caught instanceof Error ? caught.message : String(caught)
}

const stageSeverity: Record<string, string | undefined> = {
  done: 'success',
  failed: 'danger',
  cancelled: 'secondary',
}
</script>

<template>
  <section class="flex flex-col gap-4">
    <p class="muted m-0 text-sm">{{ t('voicesIntro') }}</p>

    <div v-if="versions.length > 0">
      <h3 class="mt-0 mb-2 text-base">{{ t('versionsInWork') }}</h3>
      <ul class="m-0 flex list-none flex-col gap-2 p-0">
        <li v-for="version in versions" :key="version.id" class="flex flex-wrap items-center gap-x-3 gap-y-1">
          <a
            :href="`#/songs/${version.songId}`"
            class="text-primary no-underline"
            @click.prevent="showSong(version.songId)"
            >{{ version.title || t('untitled') }}</a
          >
          <span class="muted text-sm">{{ version.voiceLabel }}</span>
          <Tag :severity="stageSeverity[version.stage]" class="ml-auto">
            {{ t(`versionStage_${version.stage}`) }}
            {{ versionProgress(version) }}
          </Tag>
          <span v-if="version.stage === 'failed' && version.message" class="danger basis-full text-sm">{{
            version.message
          }}</span>
        </li>
      </ul>
    </div>

    <h3 v-if="versions.length > 0" class="m-0 text-base">{{ t('voiceCollection') }}</h3>
    <p v-if="loadError" class="danger m-0">{{ t('voicesError', { message: loadError }) }}</p>
    <p v-else-if="voices && voices.length === 0" class="muted m-0">{{ t('voicesEmpty') }}</p>

    <ul v-if="voices && voices.length > 0" class="m-0 flex list-none flex-col gap-1 p-0">
      <li v-for="voice in voices" :key="voice.id" class="flex items-center gap-3">
        <Button
          :icon="listening === voice.id ? 'pi pi-pause' : 'pi pi-play'"
          rounded
          outlined
          size="small"
          :aria-label="listening === voice.id ? t('pause') : t('voiceListen')"
          @click="listen(voice)"
        />
        <div class="min-w-0 flex-1">
          <div class="truncate font-semibold">{{ voice.label }}</div>
          <div class="muted text-sm">
            {{ formatDuration(voice.seconds)
            }}<template v-if="voice.createdAt"> · {{ formatDateTime(voice.createdAt) }}</template>
          </div>
        </div>
        <Button
          icon="pi pi-trash"
          text
          rounded
          size="small"
          severity="danger"
          v-tooltip="t('voiceDelete')"
          :aria-label="t('voiceDelete')"
          @click="remove(voice)"
        />
      </li>
    </ul>

    <div v-if="singingVoices.length > 0" class="flex flex-wrap items-end gap-3 border-t border-surface pt-4">
      <div class="flex min-w-0 flex-[1_1_12rem] flex-col gap-1">
        <label for="voice-from-singing" class="muted text-sm">{{ t('voiceFromSinging') }}</label>
        <Select
          v-model="singingChoice"
          input-id="voice-from-singing"
          :options="singingOptions"
          option-label="label"
          option-value="id"
          :disabled="takingOver"
          fluid
        />
      </div>
      <Button
        :label="t('voiceFromSingingAdd')"
        icon="pi pi-copy"
        :loading="takingOver"
        :disabled="takingOver || !singingChoice"
        @click="takeOver"
      />
      <p class="muted m-0 basis-full text-sm">{{ t('voiceFromSingingHint') }}</p>
    </div>

    <form class="flex flex-wrap items-end gap-3 border-t border-surface pt-4" @submit.prevent="add">
      <div class="flex min-w-0 flex-[1_1_12rem] flex-col gap-1">
        <label for="voice-name" class="muted text-sm">{{ t('voiceName') }}</label>
        <InputText id="voice-name" v-model="label" required spellcheck="false" :disabled="adding" fluid />
      </div>
      <div class="flex min-w-0 flex-[1_1_16rem] flex-col gap-1">
        <label for="voice-file" class="muted text-sm">{{ t('voiceFile') }}</label>
        <!-- A native file input: PrimeVue's FileUpload brings its own upload flow, while this only picks a file. -->
        <input
          id="voice-file"
          ref="fileInput"
          class="max-w-full text-sm"
          type="file"
          accept="audio/*,.wav,.mp3,.flac,.m4a,.aac,.ogg,.opus"
          required
          :disabled="adding"
          @change="chosen"
        />
      </div>
      <div v-if="previewUrl" class="flex min-w-0 basis-full flex-col gap-3">
        <!-- The browser's own controls: seeking by finger works on the phone without anything built here. -->
        <audio
          ref="preview"
          :src="previewUrl"
          controls
          preload="metadata"
          class="w-full"
          @loadedmetadata="metadata"
          @timeupdate="timeUpdate"
          @pause="playingClip = false"
        ></audio>
        <template v-if="duration !== null">
          <div v-if="peaks" class="px-2">
            <WaveformView
              :peaks="peaks"
              :duration="duration"
              :start="clip[0]"
              :end="clip[1]"
              :position="position"
              @seek="seek"
            />
          </div>
          <Slider v-model="clip" range :min="0" :max="duration" :step="0.1" :disabled="adding" class="mx-2" />
          <div class="flex flex-wrap items-end gap-3">
            <div class="flex w-32 flex-col gap-1">
              <label for="voice-start" class="muted text-sm">{{ t('voiceTrimStart') }}</label>
              <div class="flex items-center gap-1">
                <NumberField id="voice-start" v-model="start" :min="0" :max="duration" :fraction-digits="1" />
                <Button
                  icon="pi pi-map-marker"
                  text
                  rounded
                  size="small"
                  v-tooltip="t('voiceTrimStartHere')"
                  :aria-label="t('voiceTrimStartHere')"
                  @click="here('start')"
                />
              </div>
            </div>
            <div class="flex w-32 flex-col gap-1">
              <label for="voice-end" class="muted text-sm">{{ t('voiceTrimEnd') }}</label>
              <div class="flex items-center gap-1">
                <NumberField id="voice-end" v-model="end" :min="0" :max="duration" :fraction-digits="1" />
                <Button
                  icon="pi pi-map-marker"
                  text
                  rounded
                  size="small"
                  v-tooltip="t('voiceTrimEndHere')"
                  :aria-label="t('voiceTrimEndHere')"
                  @click="here('end')"
                />
              </div>
            </div>
            <Button :label="t('voiceTrimPlay')" icon="pi pi-play" outlined size="small" @click="playClip" />
          </div>
          <p class="m-0 text-sm" :class="tooShort ? 'danger' : 'muted'">
            {{ t('voiceTrimLength', { seconds: formatSeconds(clipSeconds) })
            }}<template v-if="tooShort">, {{ t('voiceTrimTooShort') }}</template
            ><template v-else-if="clipSeconds > keptSeconds">, {{ t('voiceTrimTooLong') }}</template>
          </p>
        </template>
      </div>
      <Button
        type="submit"
        :label="adding ? t('voiceAdding') : t('voiceAdd')"
        icon="pi pi-upload"
        :loading="adding"
        :disabled="adding || !label.trim() || !file || tooShort"
      />
    </form>
    <p class="muted m-0 text-sm">{{ t('voiceFileHint') }}</p>
  </section>
</template>
