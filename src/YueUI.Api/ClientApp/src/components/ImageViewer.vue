<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import Dialog from 'primevue/dialog'
import { getCoverPainting } from '../api'
import { copyText } from '../clipboard'
import { t } from '../i18n'
import { viewedImage } from '../imageViewer'
import type { PaintingDetails } from '../types'

/**
 * The picture large, a download and the text it was painted with: the description as written, the English the model
 * read (Claude's translation, FLUX 3's upsampler) and the whole text sent, LoRA triggers and reference sentences included.
 */
const emit = defineEmits<{ error: [message: string] }>()

const painting = ref<PaintingDetails | null>(null)
const loading = ref(false)
const copied = ref<string | null>(null)

watch(
  viewedImage,
  async (image) => {
    copied.value = null
    painting.value = image?.painting ?? null
    if (!image?.coverOf) {
      return
    }
    loading.value = true
    try {
      painting.value = await getCoverPainting(image.coverOf)
    } catch (caught) {
      emit('error', caught instanceof Error ? caught.message : String(caught))
    } finally {
      loading.value = false
    }
  },
  { immediate: true },
)

/** The texts worth copying, each once: a translation equal to the description, or a sent text equal to it, is left out. */
const texts = computed(() => {
  const p = painting.value
  if (!p) {
    return []
  }
  const english = p.paintedPrompt && p.paintedPrompt !== p.prompt ? p.paintedPrompt : null
  const sent = p.sentPrompt && p.sentPrompt !== (english ?? p.prompt) ? p.sentPrompt : null
  return [
    { key: 'prompt', label: t('viewerPrompt'), text: p.prompt },
    ...(english ? [{ key: 'english', label: t('viewerEnglish'), text: english }] : []),
    ...(sent ? [{ key: 'sent', label: t('viewerSent'), text: sent }] : []),
  ]
})

const details = computed(() => {
  const p = painting.value
  if (!p) {
    return ''
  }
  return [
    p.modelLabel,
    ...(p.loras ?? []).map((lora) => `${lora.label} ${lora.strength.toFixed(2)}`),
    ...(p.references ?? []).map((r) => `${t(`coverReferenceRole_${r.role}`)}: ${r.label}`),
    t('imageSeed', { seed: p.seed }),
  ].join(' · ')
})

async function copy(key: string, text: string): Promise<void> {
  copied.value = (await copyText(text)) ? key : null
}
</script>

<template>
  <Dialog
    :visible="viewedImage !== null"
    modal
    dismissable-mask
    :header="viewedImage?.title"
    :draggable="false"
    :style="{ width: 'min(60rem, calc(100vw - 1rem))' }"
    @update:visible="(open: boolean) => !open && (viewedImage = null)"
  >
    <div v-if="viewedImage" class="flex flex-col gap-3">
      <img :src="viewedImage.src" :alt="viewedImage.title" class="picture" />
      <div class="flex justify-end">
        <Button
          as="a"
          :href="viewedImage.src"
          :download="viewedImage.fileName"
          :label="t('viewerDownload')"
          icon="pi pi-download"
          size="small"
          outlined
        />
      </div>
      <p v-if="loading" class="m-0 muted"><i class="pi pi-spin pi-spinner" aria-hidden="true" /></p>
      <template v-else-if="painting">
        <small class="muted">{{ details }}</small>
        <div v-for="item in texts" :key="item.key" class="flex flex-col gap-1">
          <span class="flex items-center justify-between gap-2">
            <span class="text-sm font-medium">{{ item.label }}</span>
            <Button
              :label="copied === item.key ? t('viewerCopied') : t('viewerCopy')"
              :icon="copied === item.key ? 'pi pi-check' : 'pi pi-copy'"
              text
              size="small"
              @click="copy(item.key, item.text)"
            />
          </span>
          <p class="m-0 text-sm prompt">{{ item.text }}</p>
        </div>
      </template>
      <small v-else-if="viewedImage.coverOf" class="muted">{{ t('viewerNoPainting') }}</small>
    </div>
  </Dialog>
</template>

<style scoped>
.picture {
  display: block;
  width: 100%;
  max-height: 70vh;
  object-fit: contain;
  border-radius: 0.5rem;
  background: var(--surface-sunken);
}
.prompt {
  white-space: pre-wrap;
  user-select: text;
  padding: 0.5rem 0.625rem;
  border-radius: 0.375rem;
  background: var(--surface-sunken);
}
</style>
