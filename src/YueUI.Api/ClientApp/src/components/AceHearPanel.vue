<script setup lang="ts">
import Panel from 'primevue/panel'
import { computed, onBeforeUnmount, ref } from 'vue'
import { understandAce } from '../api'
import { t } from '../i18n'
import type { AceHeard, RunInfo } from '../types'

/**
 * "Fill in from a recording" on the ACE-Step page: a hummed idea, a file or a library song goes to
 * `POST /api/ace/understand`, and what ACE-Step heard comes back for the form. Nothing is kept on the server.
 */
const props = defineProps<{
  runs: RunInfo[]
  /** The form's LM, so it hears with the one it will compose with; empty for the server's default. */
  lmModel: string
  disabled: boolean
}>()

const emit = defineEmits<{ heard: [heard: AceHeard]; error: [message: string] }>()

const collapsed = ref(true)
const listening = ref(false)
const recording = ref(false)
const recordingSeconds = ref(0)
const song = ref('')
const fileInput = ref<HTMLInputElement | null>(null)

/** A hummed idea needs seconds; a minute leaves room to sing a verse. */
const maxRecordingSeconds = 60

let recorder: MediaRecorder | null = null
let stream: MediaStream | null = null
let timer: ReturnType<typeof setInterval> | undefined

const canRecord =
  typeof navigator !== 'undefined' && !!navigator.mediaDevices?.getUserMedia && typeof MediaRecorder !== 'undefined'

const songOptions = computed(() =>
  props.runs.flatMap((run) =>
    run.songs
      .filter((s) => s.hasAudio)
      .map((s) => ({ label: `${run.title || t('untitled')} · ${t('songN', { n: s.index })}`, value: s.id })),
  ),
)

async function hear(source: { file: Blob; name: string } | { song: string }): Promise<void> {
  listening.value = true
  try {
    emit('heard', await understandAce(source, props.lmModel || undefined))
  } catch (caught) {
    emit('error', caught instanceof Error ? caught.message : String(caught))
  } finally {
    listening.value = false
  }
}

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

async function toggleRecording(): Promise<void> {
  if (recording.value) {
    stopRecording()
    return
  }
  if (!canRecord) {
    emit('error', t('labMicUnsupported'))
    return
  }
  try {
    stream = await navigator.mediaDevices.getUserMedia({ audio: true })
  } catch (caught) {
    emit('error', t('labMicDenied', { message: caught instanceof Error ? caught.message : String(caught) }))
    return
  }
  const type = recordingType()
  const chunks: Blob[] = []
  recorder = new MediaRecorder(stream, type.mimeType ? { mimeType: type.mimeType } : undefined)
  recorder.addEventListener('dataavailable', (event) => chunks.push(event.data))
  recorder.addEventListener('stop', () => {
    const blob = new Blob(chunks, { type: recorder?.mimeType || type.mimeType || 'audio/webm' })
    releaseMicrophone()
    void hear({ file: blob, name: `idee.${type.extension}` })
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
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  input.value = ''
  if (file) {
    void hear({ file, name: file.name })
  }
}

onBeforeUnmount(() => {
  clearInterval(timer)
  releaseMicrophone()
})
</script>

<template>
  <Panel v-model:collapsed="collapsed" toggleable>
    <template #header>
      <span class="flex items-center gap-2"><i class="pi pi-microphone" aria-hidden="true" />{{ t('aceHear') }}</span>
    </template>
    <div class="flex flex-col gap-3">
      <small class="muted">{{ disabled ? t('aceHearUnavailable') : t('aceHearHint') }}</small>
      <div class="flex flex-wrap items-center gap-2">
        <Button
          type="button"
          :icon="recording ? 'pi pi-stop-circle' : 'pi pi-microphone'"
          :label="recording ? t('aceHearStop', { seconds: recordingSeconds }) : t('aceHearRecord')"
          :severity="recording ? 'danger' : undefined"
          :disabled="disabled || listening"
          @click="toggleRecording"
        />
        <Button
          type="button"
          icon="pi pi-upload"
          :label="t('aceHearFile')"
          severity="secondary"
          :disabled="disabled || listening || recording"
          @click="fileInput?.click()"
        />
        <input ref="fileInput" type="file" accept="audio/*" class="hidden" @change="chosen" />
      </div>
      <div class="flex flex-wrap items-end gap-2">
        <div class="flex min-w-0 flex-1 flex-col gap-1">
          <label for="ace-hear-song" class="muted text-sm">{{ t('aceHearSong') }}</label>
          <Select
            id="ace-hear-song"
            v-model="song"
            :options="songOptions"
            option-label="label"
            option-value="value"
            filter
            :placeholder="t('aceHearSongPick')"
            fluid
          />
        </div>
        <Button
          type="button"
          icon="pi pi-headphones"
          :label="t('aceHearListen')"
          severity="secondary"
          :disabled="disabled || listening || recording || !song"
          @click="hear({ song })"
        />
      </div>
      <span v-if="listening" class="muted flex items-center gap-2 text-sm">
        <i class="pi pi-spin pi-spinner" aria-hidden="true" />{{ t('aceHearListening') }}
      </span>
    </div>
  </Panel>
</template>
