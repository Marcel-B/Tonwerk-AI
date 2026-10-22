<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import Slider from 'primevue/slider'
import { deleteImageLora, fetchImageLora, listImageLoras, updateImageLora, uploadImageLora } from '../api'
import { t } from '../i18n'
import type { ImageLora, ImageLoraChoice, ImageLoraList, ImageModelInfo } from '../types'

/**
 * The image LoRAs under the cover blocks: those made for the chosen model as chips, a strength for each chosen one,
 * and, folded away, the library itself (upload, the model a LoRA fits, its trigger words, Krea's own to fetch). A LoRA
 * fits one model only, so the dialog offers only the chosen model's; one without a known model waits in the library
 * until someone says which it is.
 */
const props = defineProps<{
  model: ImageModelInfo | null
  models: ImageModelInfo[]
}>()
const chosen = defineModel<ImageLoraChoice[]>({ required: true })
const emit = defineEmits<{ error: [message: string] }>()

/** As `ImageLoraLibrary.MaxPerPicture`. */
const maxPerPicture = 3

const list = ref<ImageLoraList | null>(null)
const managing = ref(false)
const busy = ref<string | null>(null)
const picker = ref<HTMLInputElement | null>(null)

function failed(caught: unknown): void {
  emit('error', caught instanceof Error ? caught.message : String(caught))
}

async function load(): Promise<void> {
  try {
    list.value = await listImageLoras()
  } catch (caught) {
    failed(caught)
  }
}

onMounted(load)

const takesLoras = computed(() => props.model?.loras === true)
const fitting = computed(() => (list.value?.loras ?? []).filter((lora) => lora.model === props.model?.id))
const unassigned = computed(() => (list.value?.loras ?? []).filter((lora) => !lora.model).length)
const modelOptions = computed(() => [
  { value: null, label: t('imageLoraModelUnknown') },
  ...props.models.filter((m) => m.loras).map((m) => ({ value: m.id, label: m.label })),
])

// Another model, or a LoRA gone or moved to another model: what no longer fits leaves the choice.
watch([fitting, takesLoras], () => {
  const kept = chosen.value.filter((c) => takesLoras.value && fitting.value.some((lora) => lora.name === c.name))
  if (kept.length !== chosen.value.length) {
    chosen.value = kept
  }
})

function isChosen(name: string): boolean {
  return chosen.value.some((c) => c.name === name)
}

function toggle(lora: ImageLora): void {
  if (isChosen(lora.name)) {
    chosen.value = chosen.value.filter((c) => c.name !== lora.name)
  } else if (chosen.value.length < maxPerPicture) {
    chosen.value = [...chosen.value, { name: lora.name, strength: lora.strength }]
  }
}

function setStrength(name: string, strength: number | number[]): void {
  const value = (Array.isArray(strength) ? strength[0] : strength) ?? 1
  chosen.value = chosen.value.map((c) => (c.name === name ? { ...c, strength: value } : c))
}

function labelOf(name: string): string {
  return list.value?.loras.find((lora) => lora.name === name)?.label ?? name
}

function replace(lora: ImageLora): void {
  if (list.value) {
    list.value = {
      ...list.value,
      loras: [...list.value.loras.filter((l) => l.name !== lora.name), lora].sort((a, b) =>
        a.name.localeCompare(b.name, undefined, { sensitivity: 'base' }),
      ),
    }
  }
}

async function change(lora: ImageLora, update: { model?: string | null; trigger?: string | null }): Promise<void> {
  try {
    // The server reads null as "leave it"; an empty text clears.
    replace(await updateImageLora(lora.name, { ...update, model: update.model === null ? '' : update.model }))
  } catch (caught) {
    failed(caught)
  }
}

async function remove(lora: ImageLora): Promise<void> {
  busy.value = lora.name
  try {
    await deleteImageLora(lora.name)
    await load()
  } catch (caught) {
    failed(caught)
  } finally {
    busy.value = null
  }
}

async function fetchSuggestion(name: string): Promise<void> {
  busy.value = name
  try {
    await fetchImageLora(name)
    await load()
  } catch (caught) {
    failed(caught)
  } finally {
    busy.value = null
  }
}

async function upload(event: Event): Promise<void> {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  input.value = ''
  if (!file) {
    return
  }
  busy.value = 'upload'
  try {
    replace(await uploadImageLora(file, takesLoras.value ? (props.model?.id ?? null) : null))
  } catch (caught) {
    failed(caught)
  } finally {
    busy.value = null
  }
}

function size(bytes: number): string {
  return bytes >= 1 << 30 ? `${(bytes / (1 << 30)).toFixed(1)} GB` : `${Math.round(bytes / (1 << 20))} MB`
}
</script>

<template>
  <div class="flex flex-col gap-2">
    <span class="flex items-center justify-between gap-2">
      <span class="font-medium">{{ t('imageLoras') }}</span>
      <Button
        :label="t(managing ? 'imageLorasDone' : 'imageLorasManage')"
        :icon="managing ? 'pi pi-chevron-up' : 'pi pi-cog'"
        text
        size="small"
        type="button"
        @click="managing = !managing"
      />
    </span>
    <small v-if="model && !takesLoras" class="muted">{{ t('imageLorasNotForModel', { model: model.label }) }}</small>
    <template v-else-if="list">
      <small v-if="fitting.length === 0" class="muted">{{ t('imageLorasNone', { model: model?.label ?? '' }) }}</small>
      <div v-else class="flex flex-wrap gap-2" role="group">
        <Button
          v-for="lora in fitting"
          :key="lora.name"
          type="button"
          size="small"
          rounded
          :label="lora.label"
          :icon="isChosen(lora.name) ? 'pi pi-check' : undefined"
          :outlined="!isChosen(lora.name)"
          :severity="isChosen(lora.name) ? undefined : 'secondary'"
          :aria-pressed="isChosen(lora.name)"
          :disabled="!isChosen(lora.name) && chosen.length >= maxPerPicture"
          :title="lora.trigger ? t('imageLoraTrigger', { trigger: lora.trigger }) : undefined"
          @click="toggle(lora)"
        />
      </div>
      <div v-for="choice in chosen" :key="choice.name" class="strength">
        <span class="truncate text-sm">{{ labelOf(choice.name) }}</span>
        <Slider
          :model-value="choice.strength"
          :min="0"
          :max="2"
          :step="0.05"
          :aria-label="t('imageLoraStrength', { lora: labelOf(choice.name) })"
          @update:model-value="(value: number | number[]) => setStrength(choice.name, value)"
        />
        <span class="text-sm tabular-nums">{{ choice.strength.toFixed(2) }}</span>
      </div>
      <small v-if="unassigned > 0 && !managing" class="muted">{{
        t('imageLorasUnassigned', { count: unassigned })
      }}</small>
    </template>

    <div v-if="managing && list" class="manage flex flex-col gap-3">
      <small class="muted">{{ t('imageLorasFolder', { folder: list.folder }) }}</small>
      <ul v-if="list.loras.length > 0" class="m-0 p-0 list-none flex flex-col gap-3">
        <li v-for="lora in list.loras" :key="lora.name" class="flex flex-col gap-1">
          <span class="flex items-center gap-2">
            <span class="font-medium truncate flex-1" :title="lora.name">{{ lora.label }}</span>
            <small class="muted">{{ size(lora.sizeBytes) }}</small>
            <Button
              icon="pi pi-trash"
              text
              rounded
              size="small"
              severity="danger"
              type="button"
              :loading="busy === lora.name"
              :aria-label="t('imageLoraDelete', { lora: lora.label })"
              @click="remove(lora)"
            />
          </span>
          <div class="flex flex-wrap gap-2">
            <Select
              :model-value="lora.model"
              :options="modelOptions"
              option-label="label"
              option-value="value"
              size="small"
              :aria-label="t('imageLoraModel')"
              class="min-w-40"
              @update:model-value="(value: string | null) => change(lora, { model: value })"
            />
            <InputText
              :model-value="lora.trigger ?? ''"
              size="small"
              class="flex-1 min-w-40"
              :placeholder="t('imageLoraTriggerPlaceholder')"
              :aria-label="t('imageLoraTriggerLabel')"
              @change="(event: Event) => change(lora, { trigger: (event.target as HTMLInputElement).value })"
            />
          </div>
        </li>
      </ul>
      <div>
        <input ref="picker" type="file" accept=".safetensors" class="hidden" @change="upload" />
        <Button
          :label="t('imageLoraUpload')"
          icon="pi pi-upload"
          size="small"
          outlined
          type="button"
          :loading="busy === 'upload'"
          @click="picker?.click()"
        />
      </div>
      <div v-if="list.suggestions.length > 0" class="flex flex-col gap-2">
        <span class="text-sm font-medium">{{ t('imageLoraSuggestions') }}</span>
        <small class="muted">{{ t('imageLoraSuggestionsHint') }}</small>
        <div v-for="suggestion in list.suggestions" :key="suggestion.name" class="flex items-center gap-2">
          <span class="flex-1 min-w-0 flex flex-col">
            <span class="text-sm truncate">{{ suggestion.label }}</span>
            <small class="muted truncate">{{ suggestion.trigger }}</small>
          </span>
          <Button
            :label="t('imageLoraFetch')"
            icon="pi pi-download"
            size="small"
            text
            type="button"
            :loading="busy === suggestion.name || list.fetching.includes(suggestion.name)"
            @click="fetchSuggestion(suggestion.name)"
          />
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.strength {
  display: grid;
  grid-template-columns: minmax(0, 8rem) 1fr 2.5rem;
  align-items: center;
  gap: 0.75rem;
}
.manage {
  padding: 0.75rem;
  border-radius: 0.5rem;
  background: var(--surface-sunken);
}
</style>
