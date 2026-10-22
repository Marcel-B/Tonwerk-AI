<script setup lang="ts">
import RadioButton from 'primevue/radiobutton'
import { ref, watch } from 'vue'
import { cleanSpeech, speechTakeAudioUrl, speechTakeSourceUrl } from '../api'
import { formatDuration, locale, t, tMaybe } from '../i18n'
import { machineField, ranOnNote, storedMachine } from '../machines'
import MachinePicker from './MachinePicker.vue'
import TabRecordButton from './TabRecordButton.vue'
import type { SpeechInfo, SpeechModelInfo, SpeechTake } from '../types'

/**
 * Sprache säubern: a speech recording (a talk, an interview, a voice memo) cleaned of noise by an enhancement model.
 * Part of the speech lab, since the models load through the same mlx-audio and wait in the same line as the takes.
 */
const props = defineProps<{
  info: SpeechInfo
  /** The lab's takes that clean a recording, newest first. */
  cleanups: SpeechTake[]
  /** The lab's one player: the id of what plays. */
  listening: string | null
}>()

const emit = defineEmits<{
  listen: [id: string, url: string]
  added: [take: SpeechTake]
  remove: [take: SpeechTake]
  error: [message: string]
}>()

const model = ref<string | null>(props.info.cleanModels[0]?.id ?? null)
watch(
  () => props.info.cleanModels,
  (models) => {
    if (!models.some((m) => m.id === model.value)) {
      model.value = models[0]?.id ?? null
    }
  },
)

/** The lab's Mac, the same as for its takes. */
const machine = storedMachine('speech')
const file = ref<File | null>(null)
const input = ref<HTMLInputElement | null>(null)
const sending = ref(false)
/** The file came from a tab recording, which the file input cannot show. */
const fromTab = ref(false)

function chosen(event: Event): void {
  file.value = (event.target as HTMLInputElement).files?.[0] ?? null
  fromTab.value = false
}

/** A recorded tab takes the file input's place; the input is cleared so it does not claim another file. */
function tabRecorded(recorded: File): void {
  file.value = recorded
  fromTab.value = true
  if (input.value) {
    input.value.value = ''
  }
}

async function send(): Promise<void> {
  if (!file.value || !model.value || sending.value) {
    return
  }
  sending.value = true
  try {
    emit('added', await cleanSpeech(file.value, file.value.name, model.value, machineField(machine.value)))
    file.value = null
    fromTab.value = false
    if (input.value) {
      input.value.value = ''
    }
  } catch (caught) {
    emit('error', caught instanceof Error ? caught.message : String(caught))
  } finally {
    sending.value = false
  }
}

const stageSeverity: Record<string, string | undefined> = { done: 'success', failed: 'danger', cancelled: 'secondary' }

function stage(take: SpeechTake): string {
  if (take.stage === 'cleaning' && take.progress !== null) {
    return `${t('labStage_cleaning')} ${Math.round(take.progress * 100)} %`
  }
  return t(`labStage_${take.stage}`)
}

function modelNote(model: SpeechModelInfo): string {
  return tMaybe(`cleanNote_${model.id}`) ?? model.note
}

function size(gb: number): string {
  return gb < 1
    ? `${Math.max(1, Math.round(gb * 1000)).toLocaleString(locale.value)} MB`
    : `${gb.toLocaleString(locale.value, { maximumFractionDigits: 1 })} GB`
}
</script>

<template>
  <Card>
    <template #title>{{ t('cleanTitle') }}</template>
    <template #content>
      <div class="flex flex-col gap-4">
        <p class="muted m-0 text-sm">{{ t('cleanIntro') }}</p>
        <form class="flex flex-col gap-3" @submit.prevent="send">
          <input
            ref="input"
            class="max-w-full text-sm"
            type="file"
            accept="audio/*,video/*,.wav,.mp3,.flac,.m4a,.aac,.ogg,.opus,.webm,.mp4,.mov"
            :aria-label="t('cleanFile')"
            :disabled="sending"
            @change="chosen"
          />
          <div class="flex min-w-0 flex-wrap items-center gap-2">
            <TabRecordButton :disabled="sending" @recorded="tabRecorded" @error="emit('error', $event)" />
            <span v-if="file && fromTab" class="muted min-w-0 truncate text-sm">{{ file.name }}</span>
          </div>
          <fieldset class="m-0 flex flex-col gap-2 border-0 p-0">
            <legend class="muted mb-2 p-0 text-sm">{{ t('cleanModel') }}</legend>
            <div v-for="option in info.cleanModels" :key="option.id" class="flex items-start gap-3">
              <RadioButton
                v-model="model"
                :input-id="`clean-model-${option.id}`"
                name="clean-model"
                :value="option.id"
                class="mt-0.5"
              />
              <label :for="`clean-model-${option.id}`" class="min-w-0 flex-1">
                <span class="font-semibold">{{ option.label }}</span>
                <span class="muted text-sm">
                  · {{ option.license }} ·
                  {{ option.downloaded ? t('labDownloaded') : t('cleanDownload', { size: size(option.downloadGb) }) }}
                </span>
                <span class="muted block text-sm">{{ modelNote(option) }}</span>
              </label>
            </div>
          </fieldset>
          <div class="flex flex-wrap items-center gap-3">
            <Button
              type="submit"
              :label="sending ? t('cleanUploading') : t('cleanStart')"
              icon="pi pi-filter"
              :loading="sending"
              :disabled="!info.installed || !info.ffmpegInstalled || !file || !model"
            />
            <MachinePicker v-model="machine" engine="speech" :disabled="sending" />
            <span v-if="!info.ffmpegInstalled" class="danger text-sm">{{ t('labNoFfmpeg') }}</span>
          </div>
        </form>

        <ul v-if="cleanups.length > 0" class="m-0 flex list-none flex-col gap-3 p-0">
          <li v-for="take in cleanups" :key="take.id" class="cleanup flex flex-col gap-1 rounded-lg p-3">
            <div class="flex items-center gap-2">
              <Button
                v-if="take.stage === 'done'"
                :icon="listening === take.id ? 'pi pi-pause' : 'pi pi-play'"
                rounded
                size="small"
                :aria-label="t('cleanListen')"
                @click="emit('listen', take.id, speechTakeAudioUrl(take.id))"
              />
              <div class="min-w-0 flex-1">
                <div class="truncate font-semibold">{{ take.source }}</div>
                <div class="muted truncate text-sm">
                  {{ take.modelLabel
                  }}<template v-if="take.seconds !== null"> · {{ formatDuration(take.seconds) }}</template
                  ><template v-if="ranOnNote(take.ranOn)"> · {{ ranOnNote(take.ranOn) }}</template>
                </div>
              </div>
              <Tag :severity="stageSeverity[take.stage]">{{ stage(take) }}</Tag>
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
                @click="emit('remove', take)"
              />
            </div>
            <Button
              v-if="take.stage === 'done'"
              :label="listening === `${take.id}:source` ? t('cleanOriginalStop') : t('cleanOriginal')"
              :icon="listening === `${take.id}:source` ? 'pi pi-pause' : 'pi pi-history'"
              text
              size="small"
              class="self-start"
              @click="emit('listen', `${take.id}:source`, speechTakeSourceUrl(take.id))"
            />
            <p v-if="take.stage === 'failed' && take.message" class="danger m-0 text-sm">{{ take.message }}</p>
          </li>
        </ul>
      </div>
    </template>
  </Card>
</template>

<style scoped>
.cleanup {
  background: var(--surface-sunken);
}
</style>
