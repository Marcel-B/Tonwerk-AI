<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import Dialog from 'primevue/dialog'
import Checkbox from 'primevue/checkbox'
import { deleteImage, getImageInfo, imageUrl, listImages, paintImage, takeImage } from '../api'
import { t } from '../i18n'
import { imageTarget, loadImageSettings, saveImageSettings, suggestPrompt } from '../images'
import { machineField, ranOnNote, storedMachine } from '../machines'
import type { ImageInfo, ImageState } from '../types'
import MachinePicker from './MachinePicker.vue'

/**
 * App.vue holds the one dialog for painted covers; the library opens it through `openImages`. A description goes to
 * the Mac, which paints with FLUX.2 Klein when the memory is free; the candidates are listed here (and in the queue
 * while in the works), and "Use as cover" makes one the song's cover.
 */
const props = defineProps<{
  /** Pictures in the works and finished while the page was open, from the event stream. */
  live: ImageState[]
}>()

const emit = defineEmits<{ error: [message: string]; notice: [message: string] }>()

const stored = loadImageSettings()
const withTitle = ref(stored.withTitle)
const model = ref<string | null>(stored.model)
const prompt = ref('')
/** The Mac that paints; empty lets the server choose one that has the model. */
const machine = storedMachine('images')
const info = ref<ImageInfo | null>(null)
const loaded = ref<ImageState[]>([])
const sending = ref(false)
const taking = ref<string | null>(null)

/** Whether the description is still the suggestion, so that the title switch may rewrite it. */
const suggested = ref(true)

function suggestion(): string {
  const target = imageTarget.value
  return target ? suggestPrompt(target.title, target.style, withTitle.value) : ''
}

watch(
  imageTarget,
  async (target) => {
    if (!target) {
      return
    }
    prompt.value = suggestion()
    suggested.value = true
    loaded.value = []
    try {
      const [about, images] = await Promise.all([getImageInfo(), listImages(target.songId)])
      if (imageTarget.value !== target) {
        return
      }
      info.value = about
      loaded.value = images
      if (!about.models.some((m) => m.id === model.value)) {
        model.value = about.models[0]?.id ?? null
      }
    } catch (caught) {
      emit('error', caught instanceof Error ? caught.message : String(caught))
    }
  },
  { immediate: true },
)

watch(withTitle, () => {
  if (suggested.value) {
    prompt.value = suggestion()
  }
})

watch([model, withTitle], () => saveImageSettings({ model: model.value, withTitle: withTitle.value }))

function edited(): void {
  suggested.value = prompt.value === suggestion()
}

function reset(): void {
  prompt.value = suggestion()
  suggested.value = true
}

const modelOptions = computed(() => (info.value?.models ?? []).map((m) => ({ value: m.id, label: m.label })))
const chosen = computed(() => info.value?.models.find((m) => m.id === model.value) ?? null)

/** What the server listed, with newer states from the event stream laid over it; deleted ones leave. */
const images = computed(() => {
  const songId = imageTarget.value?.songId
  const byId = new Map(loaded.value.map((image) => [image.id, image]))
  for (const image of props.live.filter((i) => i.songId === songId)) {
    const known = byId.get(image.id)
    if (!known || image.updatedAt >= known.updatedAt) {
      byId.set(image.id, image)
    }
  }
  return [...byId.values()]
    .filter((image) => image.stage !== 'cancelled')
    .sort((a, b) => b.createdAt.localeCompare(a.createdAt))
})

async function start(): Promise<void> {
  const target = imageTarget.value
  if (!target || !model.value || sending.value || prompt.value.trim() === '') {
    return
  }
  sending.value = true
  try {
    const image = await paintImage(target.songId, {
      prompt: prompt.value,
      model: model.value,
      machine: machineField(machine.value),
    })
    if (imageTarget.value === target) {
      loaded.value = [image, ...loaded.value.filter((i) => i.id !== image.id)]
    }
  } catch (caught) {
    emit('error', t('imageFailedToStart', { message: caught instanceof Error ? caught.message : String(caught) }))
  } finally {
    sending.value = false
  }
}

async function take(image: ImageState): Promise<void> {
  taking.value = image.id
  try {
    await takeImage(image.id)
    emit('notice', t('imageTaken'))
  } catch (caught) {
    emit('error', caught instanceof Error ? caught.message : String(caught))
  } finally {
    taking.value = null
  }
}

async function remove(image: ImageState): Promise<void> {
  try {
    await deleteImage(image.id)
    loaded.value = loaded.value.filter((i) => i.id !== image.id)
  } catch (caught) {
    emit('error', caught instanceof Error ? caught.message : String(caught))
  }
}

function stage(image: ImageState): string {
  return image.stage === 'painting'
    ? t('imageStage_painting', { percent: Math.round(image.fraction * 100) })
    : image.stage === 'loading'
      ? t('imageStage_loading')
      : t('imageStage_queued')
}

function close(): void {
  imageTarget.value = null
}
</script>

<template>
  <Dialog
    :visible="imageTarget !== null"
    modal
    :header="t('imageDialogTitle', { title: imageTarget?.title || t('untitled') })"
    :draggable="false"
    :style="{ width: 'min(34rem, calc(100vw - 2rem))' }"
    @update:visible="(open: boolean) => !open && close()"
  >
    <p v-if="info && !info.installed" class="m-0 mb-3 muted">{{ t('imageNotInstalled') }}</p>
    <form class="flex flex-col gap-3" @submit.prevent="start">
      <label class="flex flex-col gap-1">
        <span class="flex items-center justify-between gap-2">
          <span class="font-medium">{{ t('imagePrompt') }}</span>
          <Button
            v-if="!suggested"
            :label="t('imagePromptReset')"
            icon="pi pi-undo"
            text
            size="small"
            type="button"
            @click="reset"
          />
        </span>
        <Textarea v-model="prompt" rows="4" auto-resize fluid maxlength="2000" @input="edited" />
        <small class="muted">{{ t(info?.translates ? 'imagePromptHintTranslated' : 'imagePromptHint') }}</small>
      </label>
      <label class="flex items-center gap-2">
        <Checkbox v-model="withTitle" binary />
        <span>{{ t('imageWithTitle') }}</span>
      </label>
      <div v-if="modelOptions.length > 1" class="flex flex-col gap-1">
        <!-- A list rather than buttons side by side: five models do not fit a phone's width. -->
        <Select
          v-model="model"
          :options="modelOptions"
          option-label="label"
          option-value="value"
          :aria-label="t('imageModel')"
          fluid
        />
      </div>
      <div v-if="chosen" class="flex flex-col gap-1 text-sm">
        <span :class="chosen.commercial ? 'muted' : 'danger'">{{
          t(chosen.commercial ? 'imageModelCommercial' : 'imageModelPrivate', { license: chosen.license })
        }}</span>
        <span v-if="!chosen.downloaded" class="muted">{{ t('imageModelDownload', { gb: chosen.downloadGb }) }}</span>
        <span v-if="!chosen.tokenFound" class="danger">{{ t('imageModelToken') }}</span>
        <span v-if="chosen.available === false" class="danger">{{ chosen.unavailable ?? t('imageModelNowhere') }}</span>
        <span v-else-if="chosen.unavailable" class="muted">{{
          t('imageModelHelper', { reason: chosen.unavailable })
        }}</span>
        <span v-if="withTitle && !chosen.lettering" class="muted">{{ t('imageModelNoLettering') }}</span>
      </div>
      <small class="muted">{{ t('imageHint', { gb: chosen?.memoryGb ?? 9 }) }}</small>
      <div class="flex flex-wrap items-center justify-end gap-2">
        <MachinePicker v-model="machine" :engine="`images:${model ?? ''}`" :disabled="sending" class="mr-auto" />
        <Button
          type="submit"
          :label="t('imageStart')"
          icon="pi pi-palette"
          :loading="sending"
          :disabled="!model || chosen?.available === false || prompt.trim() === ''"
        />
      </div>
    </form>

    <div v-if="images.length > 0" class="mt-4">
      <h3 class="m-0 mb-2 text-sm font-medium text-muted-color">{{ t('imageList') }}</h3>
      <ul class="m-0 p-0 list-none grid grid-cols-2 gap-3">
        <li v-for="image in images" :key="image.id" class="flex flex-col gap-1 min-w-0">
          <div class="picture">
            <img v-if="image.stage === 'done'" :src="imageUrl(image.id)" :alt="image.prompt" loading="lazy" />
            <div v-else-if="image.stage === 'failed'" class="state">
              <i class="pi pi-exclamation-triangle danger" aria-hidden="true" />
              <small class="danger">{{ t('imageFailed', { message: image.message ?? '' }) }}</small>
            </div>
            <div v-else class="state">
              <i v-if="image.stage !== 'queued'" class="pi pi-spin pi-spinner" aria-hidden="true" />
              <Tag :severity="image.stage === 'queued' ? 'secondary' : undefined">{{ stage(image) }}</Tag>
            </div>
          </div>
          <span class="text-xs text-muted-color truncate" :title="image.paintedPrompt ?? image.prompt">{{
            [image.modelLabel, t('imageSeed', { seed: image.seed }), ranOnNote(image.ranOn)].filter(Boolean).join(' · ')
          }}</span>
          <div class="flex items-center gap-1">
            <Button
              v-if="image.stage === 'done'"
              :label="t('imageTake')"
              icon="pi pi-check"
              size="small"
              class="flex-1"
              :loading="taking === image.id"
              @click="take(image)"
            />
            <span v-else class="flex-1" />
            <Button
              icon="pi pi-trash"
              text
              rounded
              size="small"
              severity="danger"
              :aria-label="image.finished ? t('imageDelete') : t('queueCancel')"
              @click="remove(image)"
            />
          </div>
        </li>
      </ul>
    </div>
  </Dialog>
</template>

<style scoped>
.picture {
  position: relative;
  aspect-ratio: 1;
  border-radius: 0.5rem;
  overflow: hidden;
  background: var(--surface-sunken);
}
.picture img {
  display: block;
  width: 100%;
  height: 100%;
  object-fit: cover;
}
.state {
  position: absolute;
  inset: 0;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 0.5rem;
  padding: 0.5rem;
  text-align: center;
}
</style>
