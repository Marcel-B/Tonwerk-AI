<script setup lang="ts">
import Checkbox from 'primevue/checkbox'
import Message from 'primevue/message'
import { computed, onBeforeUnmount, reactive, ref, watch } from 'vue'
import {
  addSingingVoice,
  alignSinging,
  deleteSingingTake,
  deleteSingingVoice,
  getSinging,
  readSingingMelody,
  sing as singNotes,
  singingVoiceAudioUrl,
  singTakeAudioUrl,
  singTakeNotes,
} from '../api'
import { formatDuration, t } from '../i18n'
import { machineField, ranOnNote, storedMachine } from '../machines'
import { SingEditor } from '../singEditor'
import {
  heardNote,
  mergeTakes,
  singAlignment,
  singFinished,
  singStage,
  singStats,
  suggestedPhrase,
  voiceRange,
} from '../singing'
import type { RunInfo, SingingInfo, SingingVoiceInfo, SingingVoiceMade, SingTake } from '../types'
import MachinePicker from './MachinePicker.vue'
import SingNoteEditor from './SingNoteEditor.vue'

/**
 * Singen: a melody and its words sung by SoulX-Singer on the server (the app's singing page). The melody lives in the
 * note editor: drawn, or read from a MIDI file, a score.abc, a library song or an earlier take; each note gets a
 * syllable, or a text is spread over them.
 */
const props = defineProps<{
  active: boolean
  connected: boolean
  /** Takes as the event stream reports them, newer than what was loaded. */
  live: SingTake[]
  /** The library, for a song's score to sing. */
  runs: RunInfo[]
}>()

const emit = defineEmits<{ error: [message: string] }>()

const info = ref<SingingInfo | null>(null)
const loadError = ref<string | null>(null)
let loading = false

const editor = reactive(new SingEditor()) as SingEditor
const title = ref('')
const lyrics = ref('')
const voiceId = ref<string | null>(null)
const pitchShift = ref(0)
const autoShift = ref(false)
const machine = storedMachine('singing')
const sending = ref(false)
/** A melody being read or words being spread. */
const busy = ref(false)

const installed = computed(() => info.value?.installed === true)

async function load(): Promise<void> {
  if (loading) {
    return
  }
  loading = true
  try {
    const loaded = await getSinging()
    info.value = loaded
    loadError.value = null
    if (!loaded.voices.some((voice) => voice.id === voiceId.value)) {
      voiceId.value = loaded.voices[0]?.id ?? null
    }
  } catch (caught) {
    loadError.value = message(caught)
  } finally {
    loading = false
  }
}

watch(
  () => [props.active, props.connected] as const,
  ([active, connected]) => {
    if (active && connected) {
      void load()
    }
  },
  { immediate: true },
)

const takes = computed(() => mergeTakes(info.value?.takes ?? [], props.live))

/** Notes with words, or notes and a text to spread over them. */
const canSing = computed(
  () =>
    editor.notes.length > 0 &&
    (editor.hasLyrics || lyrics.value.trim() !== '') &&
    voiceId.value !== null &&
    !sending.value &&
    installed.value &&
    info.value?.modelDownloaded === true,
)

// ---- Melody --------------------------------------------------------------------------------------------

const melodyInput = ref<HTMLInputElement | null>(null)

/** Library songs with a score, newest runs first, as the Logic page offers them. */
const songOptions = computed(() =>
  props.runs.flatMap((run) =>
    run.songs
      .filter((song) => song.hasScore)
      .map((song) => ({ value: song.id, label: `${run.title || t('untitled')} · ${t('songN', { n: song.index })}` })),
  ),
)
const chosenSong = ref<string | null>(null)

async function openMelody(source: { file: File } | { song: string }): Promise<void> {
  busy.value = true
  try {
    const melody = await readSingingMelody(source)
    editor.load(melody)
    title.value = melody.title
    if (melody.lyrics) {
      lyrics.value = melody.lyrics
    }
    // A song's lyrics go on its notes when they carry none.
    if (!editor.hasLyrics && lyrics.value.trim() && installed.value) {
      await spreadWords()
    }
  } catch (caught) {
    emit('error', message(caught))
  } finally {
    busy.value = false
  }
}

function melodyChosen(event: Event): void {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  input.value = ''
  if (file) {
    void openMelody({ file })
  }
}

watch(chosenSong, (song) => {
  if (song) {
    void openMelody({ song })
    chosenSong.value = null
  }
})

function clearMelody(): void {
  editor.clear()
  title.value = ''
}

/** The editor's melody as a MIDI file with its words, saved through a link. */
function saveMidi(): void {
  const name = title.value.trim()
  const blob = new Blob([editor.midi(name).slice().buffer], { type: 'audio/midi' })
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = `${(name || 'Tonwerk').replace(/[\\/:*?"<>|]+/g, '_')}.mid`
  link.click()
  setTimeout(() => URL.revokeObjectURL(url), 1000)
}

// ---- Words ---------------------------------------------------------------------------------------------

/** The text laid on the notes by the server's rules, replacing the words on them. */
async function spreadWords(): Promise<void> {
  if (editor.notes.length === 0) {
    return
  }
  busy.value = true
  try {
    const notes = editor.sung(true).map(({ lyric: _, ...note }) => note)
    editor.spread(await alignSinging(notes, lyrics.value))
  } catch (caught) {
    emit('error', message(caught))
  } finally {
    busy.value = false
  }
}

/** The pronunciation of the words on the notes, asked again a moment after they change. */
let phonemeTimer: ReturnType<typeof setTimeout> | undefined
watch(
  () => editor.revision,
  () => {
    clearTimeout(phonemeTimer)
    phonemeTimer = setTimeout(() => void readPhonemes(), 700)
  },
)

async function readPhonemes(): Promise<void> {
  if (!installed.value || !editor.hasLyrics) {
    editor.forgetPhonemes()
    return
  }
  const ids = editor.notes.map((note) => note.id)
  try {
    const answer = await alignSinging(editor.sung(true))
    if (ids.join() === editor.notes.map((note) => note.id).join()) {
      editor.read(answer, ids)
    }
  } catch {
    // Only a help while typing; the take itself says what went wrong.
  }
}

// ---- Singing -------------------------------------------------------------------------------------------

async function sing(): Promise<void> {
  if (!canSing.value || !voiceId.value) {
    return
  }
  sending.value = true
  try {
    const take = await singNotes({
      notes: editor.sung(),
      tempoBpm: editor.tempo,
      voice: voiceId.value,
      lyrics: editor.hasLyrics ? undefined : lyrics.value.trim(),
      pitchShift: autoShift.value ? 0 : pitchShift.value,
      autoShift: autoShift.value,
      title: title.value.trim() || undefined,
      machine: machineField(machine.value),
    })
    if (info.value) {
      info.value = { ...info.value, takes: [take, ...info.value.takes] }
    }
  } catch (caught) {
    emit('error', message(caught))
  } finally {
    sending.value = false
  }
}

/** An earlier take's notes and words back into the editor, to change and sing again. */
async function openTake(take: SingTake): Promise<void> {
  busy.value = true
  try {
    const melody = await singTakeNotes(take.id)
    editor.load(melody)
    title.value = melody.title
    lyrics.value = melody.lyrics
  } catch (caught) {
    emit('error', message(caught))
  } finally {
    busy.value = false
  }
}

async function removeTake(take: SingTake): Promise<void> {
  try {
    if (listening.value === take.id) {
      stopListening()
    }
    await deleteSingingTake(take.id)
    if (info.value) {
      info.value = { ...info.value, takes: info.value.takes.filter((other) => other.id !== take.id) }
    }
  } catch (caught) {
    emit('error', message(caught))
  }
}

// ---- Own voices ----------------------------------------------------------------------------------------

const ownVoices = computed(() => (info.value?.voices ?? []).filter((voice) => voice.own))
const voiceName = ref('')
const voiceLyrics = ref(suggestedPhrase)
const recorded = ref<Blob | null>(null)
const recordedName = ref('recording')
const recordedUrl = ref<string | null>(null)
const recording = ref(false)
const recordingSeconds = ref(0)
const savingVoice = ref(false)
/** What the server heard in the last recording made into a voice. */
const heard = ref<SingingVoiceMade | null>(null)

/** The server keeps 30 seconds at most, of which a phrase of up to 12. */
const maxRecordingSeconds = 30
let recorder: MediaRecorder | null = null
let stream: MediaStream | null = null
let timer: ReturnType<typeof setInterval> | undefined

const canRecord =
  typeof navigator !== 'undefined' && !!navigator.mediaDevices?.getUserMedia && typeof MediaRecorder !== 'undefined'

/** WebM/Opus in Chrome and Firefox, MP4/AAC in Safari; ffmpeg on the server reads either. */
function recordingType(): { mimeType?: string; extension: string } {
  for (const [mimeType, extension] of [
    ['audio/webm;codecs=opus', 'webm'],
    ['audio/mp4', 'm4a'],
    ['audio/ogg;codecs=opus', 'ogg'],
  ] as const) {
    if (MediaRecorder.isTypeSupported(mimeType)) {
      return { mimeType, extension }
    }
  }
  return { extension: 'webm' }
}

async function startRecording(): Promise<void> {
  if (!canRecord) {
    emit('error', t('labMicUnsupported'))
    return
  }
  stopListening()
  try {
    // Without the phone's call processing, which changes the voice SoulX-Singer copies.
    stream = await navigator.mediaDevices.getUserMedia({
      audio: { echoCancellation: false, noiseSuppression: false, autoGainControl: false },
    })
  } catch (caught) {
    emit('error', t('labMicDenied', { message: message(caught) }))
    return
  }
  const type = recordingType()
  const chunks: Blob[] = []
  recorder = new MediaRecorder(stream, type.mimeType ? { mimeType: type.mimeType } : undefined)
  recorder.addEventListener('dataavailable', (event) => chunks.push(event.data))
  recorder.addEventListener('stop', () => {
    setRecorded(
      new Blob(chunks, { type: recorder?.mimeType || type.mimeType || 'audio/webm' }),
      `recording.${type.extension}`,
    )
    releaseMicrophone()
  })
  recorder.start()
  recording.value = true
  recordingSeconds.value = 0
  const started = Date.now()
  timer = setInterval(() => {
    recordingSeconds.value = Math.floor((Date.now() - started) / 1000)
    if (recordingSeconds.value >= maxRecordingSeconds) {
      stopRecording()
    }
  }, 250)
}

function stopRecording(): void {
  clearInterval(timer)
  recording.value = false
  if (recorder?.state === 'recording') {
    recorder.stop()
  }
}

function releaseMicrophone(): void {
  stream?.getTracks().forEach((track) => track.stop())
  stream = null
  recorder = null
}

function voiceChosen(event: Event): void {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  if (file) {
    setRecorded(file, file.name)
    if (!voiceName.value.trim()) {
      voiceName.value = file.name.replace(/\.[^.]+$/, '')
    }
  }
}

function setRecorded(blob: Blob | null, name = 'recording'): void {
  if (recordedUrl.value) {
    URL.revokeObjectURL(recordedUrl.value)
  }
  recorded.value = blob
  recordedName.value = name
  recordedUrl.value = blob ? URL.createObjectURL(blob) : null
}

const canSaveVoice = computed(
  () =>
    recorded.value !== null &&
    !recording.value &&
    !savingVoice.value &&
    installed.value &&
    voiceName.value.trim() !== '' &&
    voiceLyrics.value.trim() !== '',
)

async function saveVoice(): Promise<void> {
  if (!recorded.value || !canSaveVoice.value) {
    return
  }
  savingVoice.value = true
  try {
    const created = await addSingingVoice(
      voiceName.value.trim(),
      voiceLyrics.value.trim(),
      recorded.value,
      recordedName.value,
    )
    setRecorded(null)
    voiceName.value = ''
    heard.value = created.heard
    await load()
    voiceId.value = created.voice.id
  } catch (caught) {
    emit('error', message(caught))
  } finally {
    savingVoice.value = false
  }
}

async function removeVoice(voice: SingingVoiceInfo): Promise<void> {
  if (!window.confirm(t('singVoiceDeleteConfirm', { label: voice.label }))) {
    return
  }
  try {
    if (listening.value === `voice:${voice.id}`) {
      stopListening()
    }
    await deleteSingingVoice(voice.id)
    if (voiceId.value === voice.id) {
      voiceId.value = null
    }
    heard.value = null
    await load()
  } catch (caught) {
    emit('error', message(caught))
  }
}

// ---- Listening -----------------------------------------------------------------------------------------

/** One element for voices and takes, apart from the song player, as in the speech lab. */
const audio = new Audio()
audio.preload = 'none'
const listening = ref<string | null>(null)
audio.addEventListener('ended', () => (listening.value = null))
audio.addEventListener('pause', () => (listening.value = null))

function listen(id: string, url: string): void {
  if (listening.value === id) {
    stopListening()
    return
  }
  audio.src = url
  // Inside the click, as iOS wants it.
  void audio.play().catch(() => (listening.value = null))
  listening.value = id
}

function stopListening(): void {
  audio.pause()
  listening.value = null
}

onBeforeUnmount(() => {
  clearTimeout(phonemeTimer)
  stopRecording()
  releaseMicrophone()
  setRecorded(null)
  stopListening()
})

const voiceOptions = computed(() =>
  (info.value?.voices ?? []).map((voice) => ({ value: voice.id, label: voice.label })),
)
const stageSeverity: Record<string, string | undefined> = { done: 'success', failed: 'danger', cancelled: 'secondary' }

function voiceDetail(voice: SingingVoiceInfo): string {
  return [
    voice.text ? `„${voice.text}“` : null,
    voice.seconds !== null && voice.seconds !== undefined ? formatDuration(voice.seconds) : null,
    voiceRange(voice),
  ]
    .filter(Boolean)
    .join(' · ')
}

function takeDetail(take: SingTake): string {
  return [
    take.voiceLabel,
    take.seconds !== null && take.seconds !== undefined ? formatDuration(take.seconds) : null,
    ranOnNote(take.ranOn),
  ]
    .filter(Boolean)
    .join(' · ')
}

function message(caught: unknown): string {
  return caught instanceof Error ? caught.message : String(caught)
}
</script>

<template>
  <section class="flex flex-col gap-5">
    <div>
      <h2 class="mt-0 mb-1">{{ t('menuSing') }}</h2>
      <p class="muted m-0 text-sm">{{ t('singPageIntro') }}</p>
    </div>
    <p v-if="loadError" class="danger m-0">{{ loadError }}</p>

    <Message v-if="info && (!info.installed || !info.modelDownloaded)" severity="warn" :closable="false">
      <div class="flex flex-col gap-2">
        <span>{{ t('singNotInstalled') }}</span>
        <code class="install">deploy/install-singing.sh</code>
        <span class="text-sm">{{ t('singNotInstalledPath', { python: info.python, model: info.model }) }}</span>
      </div>
    </Message>

    <template v-if="info">
      <Card>
        <template #title>
          <div class="flex flex-wrap items-center gap-2">
            <span class="flex-1">{{ t('singMelody') }}</span>
            <i v-if="busy" class="pi pi-spin pi-spinner text-sm" aria-hidden="true" />
          </div>
        </template>
        <template #content>
          <div class="flex flex-col gap-3">
            <div class="flex flex-wrap items-center gap-2">
              <Button :label="t('singNew')" icon="pi pi-file" size="small" text @click="clearMelody" />
              <input
                ref="melodyInput"
                type="file"
                accept=".mid,.midi,.abc,.txt,audio/midi"
                class="hidden"
                @change="melodyChosen"
              />
              <Button
                :label="t('singOpenFile')"
                icon="pi pi-folder-open"
                size="small"
                text
                :disabled="busy"
                @click="melodyInput?.click()"
              />
              <Select
                v-model="chosenSong"
                :options="songOptions"
                option-value="value"
                option-label="label"
                filter
                :filter-placeholder="t('search')"
                :placeholder="t('singFromLibrary')"
                :empty-message="t('singNoScoreSongs')"
                :disabled="busy"
                size="small"
                class="min-w-0 flex-[1_1_12rem]"
              />
              <Button
                :label="t('singSaveMidi')"
                icon="pi pi-download"
                size="small"
                text
                :disabled="editor.notes.length === 0"
                @click="saveMidi"
              />
            </div>
            <InputText v-model="title" :placeholder="t('singTitle')" :aria-label="t('singTitle')" fluid />
            <SingNoteEditor :editor="editor" :title="title" />
            <small class="muted">{{ t('singEditorHelp') }}</small>
          </div>
        </template>
      </Card>

      <Card>
        <template #title>{{ t('singWordsVoice') }}</template>
        <template #content>
          <form class="flex flex-col gap-3" @submit.prevent="sing">
            <Textarea
              v-model="lyrics"
              rows="4"
              auto-resize
              fluid
              :aria-label="t('singWords')"
              :placeholder="t('singWords')"
            />
            <div class="flex flex-wrap items-center gap-3">
              <Button
                type="button"
                :label="t('singSpread')"
                icon="pi pi-align-left"
                size="small"
                outlined
                :disabled="editor.notes.length === 0 || lyrics.trim() === '' || !installed || busy"
                @click="spreadWords"
              />
              <span v-if="editor.leftover > 0" class="danger text-sm">{{
                t('singLeftover', { n: editor.leftover })
              }}</span>
            </div>
            <small class="muted">{{ t('singSpreadHint') }}</small>
            <div class="flex flex-wrap items-center gap-3">
              <Select
                v-model="voiceId"
                :options="voiceOptions"
                option-value="value"
                option-label="label"
                :aria-label="t('voice')"
                :title="t('voice')"
                class="min-w-0 flex-[1_1_12rem]"
              />
              <label class="flex items-center gap-2 text-sm">
                <Checkbox v-model="autoShift" binary />
                {{ t('singAutoShift') }}
              </label>
              <span v-if="!autoShift" class="flex items-center gap-1 text-sm">
                <Button
                  type="button"
                  icon="pi pi-minus"
                  size="small"
                  text
                  :aria-label="t('singShiftDown')"
                  :disabled="pitchShift <= -24"
                  @click="pitchShift--"
                />
                <span class="min-w-24 text-center">{{
                  pitchShift === 0
                    ? t('singShiftNone')
                    : t('semitones', { n: pitchShift > 0 ? `+${pitchShift}` : pitchShift })
                }}</span>
                <Button
                  type="button"
                  icon="pi pi-plus"
                  size="small"
                  text
                  :aria-label="t('singShiftUp')"
                  :disabled="pitchShift >= 24"
                  @click="pitchShift++"
                />
              </span>
            </div>
            <div class="flex flex-wrap items-center gap-3">
              <Button
                type="submit"
                :label="t('sing')"
                icon="pi pi-microphone"
                :loading="sending"
                :disabled="!canSing"
              />
              <MachinePicker v-model="machine" engine="singing" :disabled="sending" />
              <span v-if="editor.notes.length === 0" class="muted text-sm">{{ t('singNeedsNotes') }}</span>
            </div>
          </form>
        </template>
      </Card>

      <Card>
        <template #title>{{ t('singOwnVoices') }}</template>
        <template #content>
          <div class="flex flex-col gap-3">
            <p class="muted m-0 text-sm">{{ t('singOwnVoicesIntro') }}</p>
            <ul v-if="ownVoices.length > 0" class="m-0 flex list-none flex-col gap-2 p-0">
              <li v-for="voice in ownVoices" :key="voice.id" class="flex items-center gap-2">
                <Button
                  :icon="listening === `voice:${voice.id}` ? 'pi pi-pause' : 'pi pi-play'"
                  rounded
                  outlined
                  size="small"
                  :aria-label="t('voiceListen')"
                  @click="listen(`voice:${voice.id}`, singingVoiceAudioUrl(voice.id))"
                />
                <div class="min-w-0 flex-1">
                  <div class="truncate font-semibold">{{ voice.label }}</div>
                  <div class="muted line-clamp-2 text-sm">{{ voiceDetail(voice) }}</div>
                </div>
                <Button
                  icon="pi pi-trash"
                  text
                  rounded
                  size="small"
                  severity="danger"
                  :aria-label="t('singVoiceDelete')"
                  v-tooltip="t('singVoiceDelete')"
                  @click="removeVoice(voice)"
                />
              </li>
            </ul>
            <p v-if="heard" :class="['m-0 text-sm', heard.leftover > 0 ? 'danger' : 'muted']">{{ heardNote(heard) }}</p>

            <form class="flex flex-col gap-3 border-t border-surface pt-3" @submit.prevent="saveVoice">
              <h3 class="m-0 text-base">{{ t('singNewVoice') }}</h3>
              <div class="flex flex-col gap-1">
                <label for="sing-voice-lyrics" class="muted text-sm">{{ t('singVoiceLyrics') }}</label>
                <Textarea id="sing-voice-lyrics" v-model="voiceLyrics" rows="2" auto-resize fluid />
                <span class="muted text-sm">{{ t('singVoiceTips') }}</span>
              </div>
              <div class="flex flex-wrap items-center gap-3">
                <Button
                  v-if="!recording"
                  type="button"
                  :label="recorded ? t('labRecordAgain') : t('labRecord')"
                  icon="pi pi-microphone"
                  :outlined="!!recorded"
                  :disabled="savingVoice"
                  @click="startRecording"
                />
                <Button
                  v-else
                  type="button"
                  :label="t('labStop')"
                  icon="pi pi-stop-circle"
                  severity="danger"
                  @click="stopRecording"
                />
                <span v-if="recording" class="danger text-sm">{{
                  t('labRecording', { seconds: recordingSeconds })
                }}</span>
              </div>
              <div class="flex flex-wrap items-center gap-2 text-sm">
                <label for="sing-voice-file" class="muted">{{ t('labOrFile') }}</label>
                <input
                  id="sing-voice-file"
                  class="max-w-full"
                  type="file"
                  accept="audio/*,video/*,.wav,.mp3,.flac,.m4a,.aac,.ogg,.opus,.webm"
                  :disabled="recording || savingVoice"
                  @change="voiceChosen"
                />
              </div>
              <audio v-if="recordedUrl" :src="recordedUrl" controls preload="metadata" class="w-full"></audio>
              <div v-if="recorded" class="flex flex-wrap items-end gap-3">
                <div class="flex min-w-0 flex-[1_1_10rem] flex-col gap-1">
                  <label for="sing-voice-name" class="muted text-sm">{{ t('voiceName') }}</label>
                  <InputText id="sing-voice-name" v-model="voiceName" required spellcheck="false" fluid />
                </div>
                <Button
                  type="submit"
                  :label="t('singVoiceSave')"
                  icon="pi pi-save"
                  :loading="savingVoice"
                  :disabled="!canSaveVoice"
                />
              </div>
            </form>
          </div>
        </template>
      </Card>

      <div>
        <h3 class="mt-0 mb-2 text-base">{{ t('singTakes') }}</h3>
        <p v-if="takes.length === 0" class="muted m-0">{{ t('singTakesEmpty') }}</p>
        <ul class="m-0 flex list-none flex-col gap-3 p-0">
          <li v-for="take in takes" :key="take.id" class="take flex flex-col gap-1 rounded-lg p-3">
            <div class="flex items-center gap-2">
              <Button
                v-if="take.stage === 'done'"
                :icon="listening === take.id ? 'pi pi-pause' : 'pi pi-play'"
                rounded
                size="small"
                :aria-label="t('voiceListen')"
                @click="listen(take.id, singTakeAudioUrl(take.id))"
              />
              <div class="min-w-0 flex-1">
                <div class="truncate font-semibold">{{ take.title }}</div>
                <div class="muted truncate text-sm">{{ takeDetail(take) }}</div>
              </div>
              <Tag :severity="stageSeverity[take.stage]">
                <i v-if="!singFinished(take) && take.stage !== 'queued'" class="pi pi-spin pi-spinner text-xs" />
                {{ singStage(take) }}
              </Tag>
              <Button
                v-if="take.stage === 'done'"
                as="a"
                :href="singTakeAudioUrl(take.id, true)"
                icon="pi pi-download"
                text
                rounded
                size="small"
                v-tooltip="t('singTakeDownload')"
                :aria-label="t('singTakeDownload')"
              />
              <Button
                :icon="singFinished(take) ? 'pi pi-trash' : 'pi pi-times'"
                text
                rounded
                size="small"
                :severity="singFinished(take) ? 'danger' : 'secondary'"
                v-tooltip="singFinished(take) ? t('singTakeDelete') : t('queueCancel')"
                :aria-label="singFinished(take) ? t('singTakeDelete') : t('queueCancel')"
                @click="removeTake(take)"
              />
            </div>
            <p v-if="singAlignment(take)" class="muted m-0 text-sm">{{ singAlignment(take) }}</p>
            <p v-if="take.stage === 'done'" class="muted m-0 text-sm">{{ singStats(take) }}</p>
            <p v-if="take.stage === 'failed' && take.message" class="danger m-0 text-sm">{{ take.message }}</p>
            <Button
              v-if="singFinished(take)"
              :label="t('singOpenTake')"
              icon="pi pi-pencil"
              text
              size="small"
              class="self-start"
              @click="openTake(take)"
            />
          </li>
        </ul>
      </div>
    </template>
  </section>
</template>

<style scoped>
.take {
  background: var(--surface-sunken);
}

.install {
  font-family: ui-monospace, monospace;
  user-select: all;
}
</style>
