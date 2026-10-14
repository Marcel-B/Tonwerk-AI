<script setup lang="ts">
import { onBeforeUnmount, ref } from 'vue'
import { formatDuration, t } from '../i18n'
import { NoTabAudio, recordTab, tabAudioSupported, type TabRecording } from '../tabAudio'

/**
 * Records what another tab plays and hands it over as a file, as if it had been chosen. Shown only in desktop Chromium,
 * the one browser that shares a tab's sound.
 */
const props = defineProps<{ disabled?: boolean }>()

const emit = defineEmits<{
  recorded: [file: File]
  error: [message: string]
}>()

const supported = tabAudioSupported()
const running = ref<TabRecording | null>(null)
const seconds = ref(0)
let timer: ReturnType<typeof setInterval> | undefined

async function start(): Promise<void> {
  let recording: TabRecording
  try {
    recording = await recordTab()
  } catch (caught) {
    if (caught instanceof NoTabAudio) {
      emit('error', t('tabRecordNoAudio'))
    } else if (!(caught instanceof DOMException && caught.name === 'NotAllowedError')) {
      // Cancelling Chrome's picker is a NotAllowedError, and needs no message.
      emit('error', t('tabRecordFailed', { message: caught instanceof Error ? caught.message : String(caught) }))
    }
    return
  }
  running.value = recording
  seconds.value = 0
  const started = Date.now()
  timer = setInterval(() => (seconds.value = Math.floor((Date.now() - started) / 1000)), 500)
  try {
    const file = await recording.done
    if (file.size > 0) {
      emit('recorded', file)
    } else {
      emit('error', t('tabRecordEmpty'))
    }
  } catch (caught) {
    emit('error', t('tabRecordFailed', { message: caught instanceof Error ? caught.message : String(caught) }))
  } finally {
    clearInterval(timer)
    running.value = null
  }
}

onBeforeUnmount(() => running.value?.stop())
</script>

<template>
  <div v-if="supported" class="flex min-w-0 flex-wrap items-center gap-2">
    <Button
      v-if="!running"
      type="button"
      icon="pi pi-desktop"
      :label="t('tabRecord')"
      severity="secondary"
      outlined
      :disabled="props.disabled"
      v-tooltip="t('tabRecordHint')"
      @click="start"
    />
    <Button
      v-else
      type="button"
      icon="pi pi-stop-circle"
      :label="t('tabRecordStop', { time: formatDuration(seconds) })"
      severity="danger"
      @click="running.stop()"
    />
  </div>
</template>
