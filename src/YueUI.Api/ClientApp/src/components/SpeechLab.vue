<script setup lang="ts">
import Checkbox from 'primevue/checkbox'
import Message from 'primevue/message'
import RadioButton from 'primevue/radiobutton'
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import {
  addSpeechVoice,
  deleteSpeechTake,
  deleteSpeechVoice,
  getSpeech,
  speak,
  speechTakeAudioUrl,
  speechVoiceAudioUrl,
} from '../api'
import { formatDateTime, formatDuration, locale, t, tMaybe } from '../i18n'
import type { SpeechInfo, SpeechModelInfo, SpeechTake, SpeechVoice } from '../types'
import { machineField, ranOnNote, storedMachine } from '../machines'
import MachinePicker from './MachinePicker.vue'
import SpeechCleanPanel from './SpeechCleanPanel.vue'
import TextActions from './TextActions.vue'

/**
 * The speech lab: record a voice, have a text spoken by local text-to-speech models with it, and compare what they
 * make of it. For finding a model that could speak a podcast; nothing here reaches the song library.
 */
const props = defineProps<{
  /** Loaded when the page is shown, and again when the event stream reopens (a take may have finished meanwhile). */
  active: boolean
  connected: boolean
  /** Takes as the event stream reports them, newer than what was loaded. */
  live: SpeechTake[]
}>()

const emit = defineEmits<{ error: [message: string] }>()

const info = ref<SpeechInfo | null>(null)
const loadError = ref<string | null>(null)
let loading = false

async function load(): Promise<void> {
  if (loading) {
    return
  }
  loading = true
  try {
    info.value = await getSpeech()
    loadError.value = null
    // Models picked before stay picked; the first visit starts with the smallest one that clones.
    if (picked.value.length === 0 && info.value.models.length > 0) {
      picked.value = [info.value.models[0]!.id]
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

/** What was loaded, with the live changes laid over it; a deleted take arrives as cancelled and leaves. */
const takes = computed<SpeechTake[]>(() => {
  const byId = new Map((info.value?.takes ?? []).map((take) => [take.id, take]))
  for (const take of props.live) {
    const known = byId.get(take.id)
    if (!known || known.updatedAt <= take.updatedAt) {
      byId.set(take.id, take)
    }
  }
  return [...byId.values()]
    .filter((take) => take.stage !== 'cancelled')
    .sort((a, b) => b.createdAt.localeCompare(a.createdAt))
})

/** Spoken takes and cleaned recordings share the lab's line, but not its list. */
const spoken = computed(() => takes.value.filter((take) => take.source === null))
const cleanups = computed(() => takes.value.filter((take) => take.source !== null))

function added(take: SpeechTake): void {
  if (info.value) {
    info.value = { ...info.value, takes: [take, ...info.value.takes] }
  }
}

// ---- Voices ------------------------------------------------------------------------------------------

/** The recorded voice the models clone; null for each model's own. */
const voiceId = ref<string | null>(null)
const voiceOptions = computed(() => [
  { label: t('labOwnVoice'), value: null as string | null },
  ...(info.value?.voices ?? []).map((voice) => ({ label: voice.label, value: voice.id as string | null })),
])
watch(info, (value) => {
  if (voiceId.value && !value?.voices.some((voice) => voice.id === voiceId.value)) {
    voiceId.value = null
  }
})

const voiceName = ref('')
const readAloud = ref(t('labReadAloudDefault'))
/** The recording or chosen file, until it is saved. */
const recorded = ref<Blob | null>(null)
const recordedName = ref('recording')
const recordedUrl = ref<string | null>(null)
const recording = ref(false)
const recordingSeconds = ref(0)
const savingVoice = ref(false)

/** Enough for any model; the server keeps 30 seconds at most. */
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
  try {
    // Without the phone's call processing: noise suppression and gain control change the timbre the model copies.
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

function chosen(event: Event): void {
  const file = (event.target as HTMLInputElement).files?.[0]
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

async function saveVoice(): Promise<void> {
  if (!recorded.value || !voiceName.value.trim() || savingVoice.value) {
    return
  }
  savingVoice.value = true
  try {
    const voice = await addSpeechVoice(
      voiceName.value.trim(),
      readAloud.value.trim(),
      recorded.value,
      recordedName.value,
    )
    setRecorded(null)
    voiceName.value = ''
    await load()
    voiceId.value = voice.id
  } catch (caught) {
    emit('error', message(caught))
  } finally {
    savingVoice.value = false
  }
}

async function removeVoice(voice: SpeechVoice): Promise<void> {
  if (!window.confirm(t('labVoiceDeleteConfirm', { label: voice.label }))) {
    return
  }
  try {
    stopListening()
    await deleteSpeechVoice(voice.id)
    await load()
  } catch (caught) {
    emit('error', message(caught))
  }
}

onBeforeUnmount(() => {
  stopRecording()
  releaseMicrophone()
  setRecorded(null)
  stopListening()
})

// ---- Speaking ----------------------------------------------------------------------------------------

const text = ref(t('labTextDefault'))
// Texts left as they were follow the language switch; typed ones stay.
let textDefault = text.value
let readDefault = readAloud.value
watch(locale, () => {
  if (text.value === textDefault) {
    text.value = textDefault = t('labTextDefault')
  }
  if (readAloud.value === readDefault) {
    readAloud.value = readDefault = t('labReadAloudDefault')
  }
})
const picked = ref<string[]>([])
const sending = ref(false)
/** The Mac that speaks; shared with the cleanups, since both are the lab's models. */
const machine = storedMachine('speech')

async function send(): Promise<void> {
  if (!text.value.trim() || picked.value.length === 0 || sending.value) {
    return
  }
  sending.value = true
  try {
    const added = await speak(text.value.trim(), voiceId.value, picked.value, machineField(machine.value))
    if (info.value) {
      info.value = { ...info.value, takes: [...added, ...info.value.takes] }
    }
  } catch (caught) {
    emit('error', message(caught))
  } finally {
    sending.value = false
  }
}

async function removeTake(take: SpeechTake): Promise<void> {
  try {
    if (listening.value?.startsWith(take.id)) {
      stopListening()
    }
    await deleteSpeechTake(take.id)
    if (info.value) {
      info.value = { ...info.value, takes: info.value.takes.filter((other) => other.id !== take.id) }
    }
  } catch (caught) {
    emit('error', message(caught))
  }
}

// ---- Listening ---------------------------------------------------------------------------------------

/** One element for recordings and takes, apart from the song player: a take is a short check, not a song to queue. */
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

// ---- Helpers -----------------------------------------------------------------------------------------

const stageSeverity: Record<string, string | undefined> = { done: 'success', failed: 'danger', cancelled: 'secondary' }

function number(value: number | null, digits = 1): string {
  return value === null ? '–' : value.toLocaleString(locale.value, { maximumFractionDigits: digits })
}

/** The page's own words for the offered models; a model added in the configuration brings its note in English. */
function modelNote(model: SpeechModelInfo): string {
  return tMaybe(`labNote_${model.id}`) ?? model.note
}

function message(caught: unknown): string {
  return caught instanceof Error ? caught.message : String(caught)
}
</script>

<template>
  <section class="flex flex-col gap-5">
    <div>
      <h2 class="mt-0 mb-1">{{ t('menuLab') }}</h2>
      <p class="muted m-0 text-sm">{{ t('labIntro') }}</p>
    </div>
    <p v-if="loadError" class="danger m-0">{{ loadError }}</p>

    <Message v-if="info && !info.installed" severity="warn" :closable="false">
      <div class="flex flex-col gap-2">
        <span>{{ t('labNotInstalled') }}</span>
        <code class="install">deploy/install-speech.sh</code>
        <span class="text-sm">{{ t('labNotInstalledPath', { path: info.python }) }}</span>
      </div>
    </Message>

    <div v-if="info" class="grid grid-cols-1 items-start gap-5 md:grid-cols-2">
      <!-- The voice: pick one, or record a new one. -->
      <Card>
        <template #title>{{ t('labVoice') }}</template>
        <template #content>
          <div class="flex flex-col gap-4">
            <ul class="m-0 flex list-none flex-col gap-1 p-0">
              <li v-for="option in voiceOptions" :key="option.value ?? 'own'" class="flex items-center gap-3">
                <RadioButton
                  v-model="voiceId"
                  :input-id="`lab-voice-${option.value ?? 'own'}`"
                  name="lab-voice"
                  :value="option.value"
                />
                <label :for="`lab-voice-${option.value ?? 'own'}`" class="min-w-0 flex-1">
                  <span class="block truncate" :class="{ 'font-semibold': voiceId === option.value }">{{
                    option.label
                  }}</span>
                  <span v-if="option.value" class="muted block text-sm">
                    {{ formatDuration(info.voices.find((v) => v.id === option.value)!.seconds) }} ·
                    {{ formatDateTime(info.voices.find((v) => v.id === option.value)!.createdAt) }}
                  </span>
                </label>
                <template v-if="option.value">
                  <Button
                    :icon="listening === option.value ? 'pi pi-pause' : 'pi pi-play'"
                    rounded
                    outlined
                    size="small"
                    :aria-label="t('voiceListen')"
                    @click="listen(option.value, speechVoiceAudioUrl(option.value))"
                  />
                  <Button
                    icon="pi pi-trash"
                    text
                    rounded
                    size="small"
                    severity="danger"
                    v-tooltip="t('labVoiceDelete')"
                    :aria-label="t('labVoiceDelete')"
                    @click="removeVoice(info.voices.find((v) => v.id === option.value)!)"
                  />
                </template>
              </li>
            </ul>

            <form class="flex flex-col gap-3 border-t border-surface pt-4" @submit.prevent="saveVoice">
              <h3 class="m-0 text-base">{{ t('labRecordTitle') }}</h3>
              <p v-if="!info.ffmpegInstalled" class="danger m-0 text-sm">{{ t('labNoFfmpeg') }}</p>
              <div class="flex flex-col gap-1">
                <label for="lab-read" class="muted text-sm">{{ t('labReadAloud') }}</label>
                <Textarea id="lab-read" v-model="readAloud" rows="3" auto-resize fluid />
                <span class="muted text-sm">{{ t('labReadAloudHint') }}</span>
              </div>
              <div class="flex flex-wrap items-center gap-3">
                <Button
                  v-if="!recording"
                  :label="recorded ? t('labRecordAgain') : t('labRecord')"
                  icon="pi pi-microphone"
                  :outlined="!!recorded"
                  :disabled="savingVoice"
                  @click="startRecording"
                />
                <Button
                  v-else
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
                <label for="lab-file" class="muted">{{ t('labOrFile') }}</label>
                <input
                  id="lab-file"
                  class="max-w-full"
                  type="file"
                  accept="audio/*,.wav,.mp3,.flac,.m4a,.aac,.ogg,.opus,.webm"
                  :disabled="recording || savingVoice"
                  @change="chosen"
                />
              </div>
              <audio v-if="recordedUrl" :src="recordedUrl" controls preload="metadata" class="w-full"></audio>
              <div v-if="recorded" class="flex flex-wrap items-end gap-3">
                <div class="flex min-w-0 flex-[1_1_10rem] flex-col gap-1">
                  <label for="lab-voice-name" class="muted text-sm">{{ t('voiceName') }}</label>
                  <InputText id="lab-voice-name" v-model="voiceName" required spellcheck="false" fluid />
                </div>
                <Button
                  type="submit"
                  :label="savingVoice ? t('labSavingVoice') : t('labSaveVoice')"
                  icon="pi pi-save"
                  :loading="savingVoice"
                  :disabled="!voiceName.trim() || !info.ffmpegInstalled"
                />
              </div>
            </form>
          </div>
        </template>
      </Card>

      <!-- The text and the models to speak it. -->
      <Card>
        <template #title>{{ t('labText') }}</template>
        <template #content>
          <form class="flex flex-col gap-4" @submit.prevent="send">
            <div>
              <Textarea v-model="text" rows="6" auto-resize fluid :aria-label="t('labText')" />
              <TextActions v-model="text" />
            </div>
            <fieldset class="m-0 flex flex-col gap-3 border-0 p-0">
              <legend class="muted mb-2 p-0 text-sm">{{ t('labModels') }}</legend>
              <div v-for="model in info.models" :key="model.id" class="flex items-start gap-3">
                <Checkbox v-model="picked" :input-id="`lab-model-${model.id}`" :value="model.id" class="mt-0.5" />
                <label :for="`lab-model-${model.id}`" class="min-w-0 flex-1">
                  <span class="font-semibold">{{ model.label }}</span>
                  <span class="muted text-sm">
                    · {{ model.license }} ·
                    {{ model.downloaded ? t('labDownloaded') : t('labDownload', { size: number(model.downloadGb) }) }}
                  </span>
                  <span class="muted block text-sm">{{ modelNote(model) }}</span>
                </label>
              </div>
            </fieldset>
            <div class="flex flex-wrap items-center gap-3">
              <Button
                type="submit"
                :label="t('labSpeak')"
                icon="pi pi-volume-up"
                :loading="sending"
                :disabled="!info.installed || !text.trim() || picked.length === 0"
              />
              <MachinePicker v-model="machine" engine="speech" :disabled="sending" />
              <span v-if="picked.length === 0" class="muted text-sm">{{ t('labPickModel') }}</span>
              <span v-else-if="info.models.some((m) => picked.includes(m.id) && !m.downloaded)" class="muted text-sm">{{
                t('labSpeakHint')
              }}</span>
            </div>
          </form>
        </template>
      </Card>
    </div>

    <div v-if="info">
      <h3 class="mt-0 mb-2 text-base">{{ t('labTakes') }}</h3>
      <p v-if="spoken.length === 0" class="muted m-0">{{ t('labTakesEmpty') }}</p>
      <ul class="m-0 flex list-none flex-col gap-3 p-0">
        <li v-for="take in spoken" :key="take.id" class="take flex flex-col gap-1 rounded-lg p-3">
          <!-- One line on a phone too: the play button, what spoke, its state and the actions. -->
          <div class="flex items-center gap-2">
            <Button
              v-if="take.stage === 'done'"
              :icon="listening === take.id ? 'pi pi-pause' : 'pi pi-play'"
              rounded
              size="small"
              :aria-label="t('voiceListen')"
              @click="listen(take.id, speechTakeAudioUrl(take.id))"
            />
            <div class="min-w-0 flex-1">
              <div class="truncate font-semibold">{{ take.modelLabel }}</div>
              <div class="muted truncate text-sm">
                {{ take.voiceLabel ?? t('labOwnVoice')
                }}<template v-if="take.seconds !== null"> · {{ formatDuration(take.seconds) }}</template
                ><template v-if="ranOnNote(take.ranOn)"> · {{ ranOnNote(take.ranOn) }}</template>
              </div>
            </div>
            <Tag :severity="stageSeverity[take.stage]">{{ t(`labStage_${take.stage}`) }}</Tag>
            <Button
              v-if="take.stage === 'done'"
              as="a"
              :href="speechTakeAudioUrl(take.id, true)"
              icon="pi pi-download"
              text
              rounded
              size="small"
              v-tooltip="t('labTakeDownload')"
              :aria-label="t('labTakeDownload')"
            />
            <Button
              :icon="take.finished ? 'pi pi-trash' : 'pi pi-times'"
              text
              rounded
              size="small"
              :severity="take.finished ? 'danger' : 'secondary'"
              v-tooltip="take.finished ? t('labTakeDelete') : t('labTakeStop')"
              :aria-label="take.finished ? t('labTakeDelete') : t('labTakeStop')"
              @click="removeTake(take)"
            />
          </div>
          <p class="m-0 line-clamp-2 text-sm">{{ take.text }}</p>
          <p v-if="take.stage === 'done'" class="muted m-0 text-sm">
            {{
              t('labStats', {
                load: number(take.loadSeconds),
                speak: number(take.speakSeconds),
                memory: number(take.peakMemoryGb),
              })
            }}
          </p>
          <p v-if="take.stage === 'failed' && take.message" class="danger m-0 text-sm">{{ take.message }}</p>
          <Button
            v-if="take.text !== text"
            :label="t('labUseText')"
            text
            size="small"
            class="self-start"
            @click="text = take.text"
          />
        </li>
      </ul>
    </div>

    <SpeechCleanPanel
      v-if="info && info.cleanModels.length > 0"
      :info="info"
      :cleanups="cleanups"
      :listening="listening"
      @listen="listen"
      @added="added"
      @remove="removeTake"
      @error="(text) => emit('error', text)"
    />
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
