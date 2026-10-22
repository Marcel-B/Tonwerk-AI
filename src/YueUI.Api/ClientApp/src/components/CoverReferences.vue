<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import Slider from 'primevue/slider'
import { coverStyleImageUrl, listCoverStyles, uploadImageReference } from '../api'
import { fitReferences, lookedRoles, type ReferenceItem } from '../coverStyles'
import { t } from '../i18n'
import { referenceJpeg } from '../photo'
import type { CoverStyle, ImageModelInfo, ReferenceRole } from '../types'

/**
 * The painting dialog's reference pictures and cover style: a style brings its pictures (and, through `style`, its
 * text, model and LoRAs); photos, the song's cover and earlier candidates can be added for one picture. What the
 * chosen model takes is `fitReferences`'s.
 */
const props = defineProps<{
  model: ImageModelInfo | null
  /** The song's own cover, when it has one. */
  cover?: string
}>()

const items = defineModel<ReferenceItem[]>({ required: true })
const strength = defineModel<number>('strength', { required: true })
const styleId = defineModel<number | null>('styleId', { required: true })

const emit = defineEmits<{ style: [style: CoverStyle | null]; error: [message: string] }>()

const styles = ref<CoverStyle[]>([])
const uploading = ref(false)
const fileInput = ref<HTMLInputElement | null>(null)

onMounted(async () => {
  try {
    styles.value = await listCoverStyles()
  } catch (caught) {
    emit('error', caught instanceof Error ? caught.message : String(caught))
  }
})

const styleOptions = computed(() => [
  { value: null, label: t('coverStyleNone') },
  ...styles.value.map((s) => ({ value: s.id, label: s.name })),
])

function choose(id: number | null): void {
  styleId.value = id
  emit('style', styles.value.find((s) => s.id === id) ?? null)
}

const sent = computed(() => fitReferences(items.value, props.model, strength.value).length)
const looked = computed(() => props.model?.references ?? 0)

const hint = computed(() => {
  const model = props.model?.label ?? ''
  return looked.value > 0
    ? t('coverReferencesLooked', { model, count: looked.value })
    : props.model?.startImage
      ? t('coverReferencesStart', { model })
      : t('coverReferencesNone', { model })
})

const roleOptions = computed(() => lookedRoles.map((role) => ({ value: role, label: t(`coverReferenceRole_${role}`) })))

function setRole(index: number, role: ReferenceRole): void {
  items.value = items.value.map((item, n) => (n === index ? { ...item, choice: { ...item.choice, role } } : item))
}

function remove(index: number): void {
  const [gone] = items.value.slice(index, index + 1)
  if (gone?.preview.startsWith('blob:')) {
    URL.revokeObjectURL(gone.preview)
  }
  items.value = items.value.filter((_, n) => n !== index)
}

async function picked(event: Event): Promise<void> {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  input.value = ''
  if (!file) {
    return
  }
  uploading.value = true
  try {
    const picture = await referenceJpeg(file)
    const id = await uploadImageReference(picture)
    items.value = [
      ...items.value,
      { choice: { source: 'upload', id, role: 'style' }, preview: URL.createObjectURL(picture), label: file.name },
    ]
  } catch (caught) {
    emit('error', caught instanceof Error ? caught.message : String(caught))
  } finally {
    uploading.value = false
  }
}

function addCover(): void {
  if (props.cover && !items.value.some((i) => i.choice.source === 'cover')) {
    items.value = [
      ...items.value,
      { choice: { source: 'cover', role: 'style' }, preview: props.cover, label: t('coverReferenceCover') },
    ]
  }
}

/** The style list read again, after a picture was kept in one. */
async function reload(): Promise<void> {
  styles.value = await listCoverStyles()
}

defineExpose({ reload, styles, styleImageUrl: coverStyleImageUrl })
</script>

<template>
  <section class="flex flex-col gap-2">
    <div class="flex items-center gap-2">
      <span class="font-medium">{{ t('coverReferences') }}</span>
      <Select
        v-if="styles.length > 0"
        :model-value="styleId"
        :options="styleOptions"
        option-label="label"
        option-value="value"
        :aria-label="t('coverStyle')"
        size="small"
        class="ml-auto min-w-0"
        @update:model-value="choose"
      />
    </div>
    <small v-if="model" class="muted">{{ hint }}</small>
    <ul v-if="items.length > 0" class="m-0 p-0 list-none grid grid-cols-4 gap-2">
      <li
        v-for="(item, index) in items"
        :key="index"
        class="flex flex-col gap-1 min-w-0"
        :class="{ unused: index >= sent }"
      >
        <div class="thumb">
          <img :src="item.preview" :alt="item.label" loading="lazy" />
          <Button
            icon="pi pi-times"
            rounded
            size="small"
            severity="secondary"
            class="remove"
            :aria-label="t('coverReferenceRemove')"
            @click="remove(index)"
          />
        </div>
        <Select
          v-if="looked > 0"
          :model-value="item.choice.role === 'start' ? 'style' : item.choice.role"
          :options="roleOptions"
          option-label="label"
          option-value="value"
          size="small"
          fluid
          :aria-label="t('coverStyle')"
          @update:model-value="(role: ReferenceRole) => setRole(index, role)"
        />
        <small v-else-if="index === 0 && model?.startImage" class="text-center">{{
          t('coverReferenceRole_start')
        }}</small>
      </li>
    </ul>
    <small v-if="model && items.length > sent && sent > 0" class="muted">{{
      t('coverReferencesUnused', { model: model.label })
    }}</small>
    <label v-if="model?.startImage && items.length > 0" class="flex items-center gap-3">
      <span class="text-sm shrink-0">{{ t('coverStartStrength') }}</span>
      <Slider v-model="strength" :min="0.1" :max="0.9" :step="0.05" class="flex-1" />
      <span class="text-sm tabular-nums w-10 text-right">{{ strength.toFixed(2) }}</span>
    </label>
    <div class="flex flex-wrap gap-2">
      <Button
        :label="t('coverReferencePhoto')"
        icon="pi pi-image"
        size="small"
        outlined
        type="button"
        :loading="uploading"
        @click="fileInput?.click()"
      />
      <Button
        v-if="cover"
        :label="t('coverReferenceCover')"
        icon="pi pi-clone"
        size="small"
        outlined
        type="button"
        :disabled="items.some((i) => i.choice.source === 'cover')"
        @click="addCover"
      />
      <input ref="fileInput" type="file" accept="image/*" class="hidden" @change="picked" />
    </div>
  </section>
</template>

<style scoped>
.thumb {
  position: relative;
  aspect-ratio: 1;
  border-radius: 0.375rem;
  overflow: hidden;
  background: var(--surface-sunken);
}
.thumb img {
  display: block;
  width: 100%;
  height: 100%;
  object-fit: cover;
}
.remove {
  position: absolute;
  top: 0.125rem;
  right: 0.125rem;
  width: 1.5rem;
  height: 1.5rem;
}
.unused {
  opacity: 0.45;
}
</style>
