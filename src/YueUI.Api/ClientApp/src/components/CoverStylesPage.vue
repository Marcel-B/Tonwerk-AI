<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useConfirm } from 'primevue/useconfirm'
import {
  addCoverStyleImage,
  coverStyleImageUrl,
  createCoverStyle,
  deleteCoverStyle,
  deleteCoverStyleImage,
  getImageInfo,
  listCoverStyles,
  setCoverStyleImageRole,
  updateCoverStyle,
} from '../api'
import { lookedRoles } from '../coverStyles'
import { t } from '../i18n'
import { referenceJpeg } from '../photo'
import type { CoverStyle, ImageLoraChoice, ImageModelInfo, ReferenceRole } from '../types'
import CoverLoras from './CoverLoras.vue'

/**
 * The cover styles: name, the sentence for the description, the model, LoRAs and up to four reference pictures with
 * their roles. The painting dialog chooses one; a candidate can be kept in the chosen style from there.
 */
const props = defineProps<{ active: boolean }>()
const emit = defineEmits<{ notice: [message: string]; error: [message: string] }>()
const confirm = useConfirm()

const styles = ref<CoverStyle[]>([])
const models = ref<ImageModelInfo[]>([])
const newName = ref('')
const busy = ref<string | null>(null)

/** The edits not yet saved, per style. */
interface Draft {
  name: string
  prompt: string
  model: string | null
  loras: ImageLoraChoice[]
}
const drafts = ref<Record<number, Draft>>({})

function failed(caught: unknown): void {
  emit('error', caught instanceof Error ? caught.message : String(caught))
}

function draftOf(style: CoverStyle): Draft {
  return { name: style.name, prompt: style.prompt ?? '', model: style.model, loras: style.loras.map((l) => ({ ...l })) }
}

function replace(style: CoverStyle): void {
  styles.value = styles.value.map((s) => (s.id === style.id ? style : s))
}

async function load(): Promise<void> {
  try {
    const [list, info] = await Promise.all([listCoverStyles(), getImageInfo()])
    styles.value = list
    models.value = info.models
    drafts.value = Object.fromEntries(list.map((s) => [s.id, draftOf(s)]))
  } catch (caught) {
    failed(caught)
  }
}

// Read again each time the page is shown: the dialog may have kept a picture in a style meanwhile.
watch(
  () => props.active,
  (active) => {
    if (active) {
      void load()
    }
  },
  { immediate: true },
)

const modelOptions = computed(() => [
  { value: null, label: t('coverStyleModelAny') },
  ...models.value.map((m) => ({ value: m.id, label: m.label })),
])
const roleOptions = computed(() =>
  [...lookedRoles, 'start' as const].map((role) => ({ value: role, label: t(`coverReferenceRole_${role}`) })),
)

async function run(key: string, work: () => Promise<void>): Promise<void> {
  busy.value = key
  try {
    await work()
  } catch (caught) {
    failed(caught)
  } finally {
    busy.value = null
  }
}

function create(): Promise<void> {
  return run('new', async () => {
    const style = await createCoverStyle({ name: newName.value.trim() })
    styles.value = [...styles.value, style].sort((a, b) => a.name.localeCompare(b.name))
    drafts.value[style.id] = draftOf(style)
    newName.value = ''
  })
}

function save(style: CoverStyle): Promise<void> {
  const draft = drafts.value[style.id]!
  return run(`save${style.id}`, async () => {
    const saved = await updateCoverStyle(style.id, {
      name: draft.name.trim(),
      prompt: draft.prompt,
      model: draft.model ?? '',
      loras: draft.loras,
    })
    replace(saved)
    drafts.value[style.id] = draftOf(saved)
    emit('notice', t('coverStyleSaved'))
  })
}

function remove(style: CoverStyle): void {
  confirm.require({
    header: t('confirmDelete'),
    message: t('confirmDeleteCoverStyle', { name: style.name }),
    icon: 'pi pi-trash',
    rejectProps: { label: t('keep'), severity: 'secondary', outlined: true },
    acceptProps: { label: t('delete'), severity: 'danger' },
    accept: () =>
      run(`delete${style.id}`, async () => {
        await deleteCoverStyle(style.id)
        styles.value = styles.value.filter((s) => s.id !== style.id)
      }),
  })
}

const inputs = ref<Record<number, HTMLInputElement | null>>({})

function picked(style: CoverStyle, event: Event): Promise<void> {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  input.value = ''
  if (!file) {
    return Promise.resolve()
  }
  return run(`image${style.id}`, async () =>
    replace(await addCoverStyleImage(style.id, { file: await referenceJpeg(file) }, 'style')),
  )
}

function setRole(style: CoverStyle, imageId: string, role: ReferenceRole): Promise<void> {
  return run(`role${imageId}`, async () => replace(await setCoverStyleImageRole(style.id, imageId, role)))
}

function removeImage(style: CoverStyle, imageId: string): Promise<void> {
  return run(`role${imageId}`, async () => replace(await deleteCoverStyleImage(style.id, imageId)))
}

function modelOf(draft: Draft): ImageModelInfo | null {
  return models.value.find((m) => m.id === draft.model) ?? null
}

function changed(style: CoverStyle): boolean {
  return JSON.stringify(drafts.value[style.id]) !== JSON.stringify(draftOf(style))
}
</script>

<template>
  <div class="flex flex-col gap-4">
    <form class="flex gap-2" @submit.prevent="create">
      <InputText
        v-model="newName"
        :placeholder="t('coverStyleNew')"
        :aria-label="t('coverStyleName')"
        maxlength="80"
        fluid
      />
      <Button
        type="submit"
        :label="t('coverStyleCreate')"
        icon="pi pi-plus"
        :loading="busy === 'new'"
        :disabled="newName.trim() === ''"
      />
    </form>
    <p v-if="styles.length === 0" class="muted m-0">{{ t('coverStylesEmpty') }}</p>
    <section v-for="style in styles" :key="style.id" class="style flex flex-col gap-3">
      <template v-if="drafts[style.id]">
        <div class="flex items-center gap-2">
          <InputText
            v-model="drafts[style.id]!.name"
            :aria-label="t('coverStyleName')"
            maxlength="80"
            class="font-medium"
            fluid
          />
          <Button
            icon="pi pi-trash"
            text
            rounded
            severity="danger"
            :aria-label="t('coverStyleDelete')"
            :loading="busy === `delete${style.id}`"
            @click="remove(style)"
          />
        </div>
        <label class="flex flex-col gap-1">
          <span class="text-sm">{{ t('coverStylePrompt') }}</span>
          <Textarea v-model="drafts[style.id]!.prompt" rows="2" auto-resize fluid maxlength="600" />
          <small class="muted">{{ t('coverStylePromptHint') }}</small>
        </label>
        <Select
          v-model="drafts[style.id]!.model"
          :options="modelOptions"
          option-label="label"
          option-value="value"
          :aria-label="t('coverStyleModel')"
          fluid
        />
        <CoverLoras
          v-if="modelOf(drafts[style.id]!)"
          v-model="drafts[style.id]!.loras"
          :model="modelOf(drafts[style.id]!)"
          :models="models"
          @error="failed"
        />
        <div class="flex justify-end">
          <Button
            :label="t('coverStyleSave')"
            icon="pi pi-check"
            size="small"
            :disabled="!changed(style) || drafts[style.id]!.name.trim() === ''"
            :loading="busy === `save${style.id}`"
            @click="save(style)"
          />
        </div>
        <span class="text-sm font-medium">{{ t('coverStyleImages', { count: style.images.length }) }}</span>
        <ul class="m-0 p-0 list-none grid grid-cols-4 gap-2">
          <li v-for="image in style.images" :key="image.id" class="flex flex-col gap-1 min-w-0">
            <div class="thumb">
              <img :src="coverStyleImageUrl(style.id, image.id)" alt="" loading="lazy" />
              <Button
                icon="pi pi-times"
                rounded
                size="small"
                severity="secondary"
                class="remove"
                :aria-label="t('coverReferenceRemove')"
                @click="removeImage(style, image.id)"
              />
            </div>
            <Select
              :model-value="image.role"
              :options="roleOptions"
              option-label="label"
              option-value="value"
              size="small"
              fluid
              @update:model-value="(role: ReferenceRole) => setRole(style, image.id, role)"
            />
          </li>
        </ul>
        <div v-if="style.images.length < 4">
          <Button
            :label="t('coverStyleAddImage')"
            icon="pi pi-image"
            size="small"
            outlined
            :loading="busy === `image${style.id}`"
            @click="inputs[style.id]?.click()"
          />
          <input
            :ref="(el) => (inputs[style.id] = el as HTMLInputElement | null)"
            type="file"
            accept="image/*"
            class="hidden"
            @change="(event) => picked(style, event)"
          />
        </div>
      </template>
    </section>
  </div>
</template>

<style scoped>
.style {
  border: 1px solid var(--p-content-border-color);
  border-radius: 0.75rem;
  padding: 0.75rem;
}
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
</style>
