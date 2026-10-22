<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useConfirm } from 'primevue/useconfirm'
import { ApiError, draftLyrics, generate, getLyricsModels, listVoices, reviseLyrics } from '../api'
import { defaultFormState, toGenerateRequest, usesScore, type FormState } from '../form'
import { formatBytes, t } from '../i18n'
import { photoDataUrl } from '../photo'
import { insertSectionTag } from '../lyricsTags'
import { hasTag } from '../styleTags'
import type { LyricsModels, LyricsReasoning, LyricsState, ReferenceVoice } from '../types'
import { octaveOptions, stepOptions, strengthOptions } from '../voiceChoices'
import { machineField } from '../machines'
import FieldHelp from './FieldHelp.vue'
import MachinePicker from './MachinePicker.vue'
import SectionTags from './SectionTags.vue'
import StyleBlocks from './StyleBlocks.vue'
import TextActions from './TextActions.vue'
import Checkbox from 'primevue/checkbox'
import SelectButton from 'primevue/selectbutton'

const props = defineProps<{
  /** Jobs waiting in the server's queue; a draft among them is still on its way. */
  queuedIds: Set<string>
  /** The last lyrics draft from the event stream, from any browser. */
  lyricsDraft: LyricsState | null
  /** Songs can be sung with another voice (Seed-VC and the separator are set up). */
  voices: boolean
}>()
const form = defineModel<FormState>({ required: true })
/** Shared with the advanced parameters, which show the errors of their own fields. */
const fieldErrors = defineModel<Record<string, string[]>>('errors', { required: true })

const sending = ref(false)
const blocksOpen = ref(false)
const sectionsOpen = ref(false)
/**
 * Where the caret last was in the lyrics, with the text it belongs to: tapping a tag blurs the field first, and a
 * draft or "use everything" replacing the text makes the position meaningless, so a tag then goes to the end.
 */
const lyricsCaret = ref<{ text: string; at: number } | null>(null)

function rememberCaret(event: Event): void {
  const field = event.target as HTMLTextAreaElement
  lyricsCaret.value = { text: field.value, at: field.selectionEnd }
}

function insertTag(tag: string): void {
  const caret = lyricsCaret.value?.text === form.value.lyrics ? lyricsCaret.value.at : null
  const inserted = insertSectionTag(form.value.lyrics, tag, caret)
  form.value.lyrics = inserted.text
  // Not focused again: on a phone that would raise the keyboard after every tag. The next tag follows this one.
  lyricsCaret.value = { text: inserted.text, at: inserted.caret }
}
const message = ref<{ text: string; error: boolean } | null>(null)

// The API refuses this too; saying so before sending saves a round trip from the phone.
const scoreWithoutPlanning = computed(
  () => usesScore(form.value) && form.value.cot === 'off' && !form.value.instrumental,
)

async function submit(): Promise<void> {
  sending.value = true
  message.value = null
  fieldErrors.value = {}
  try {
    const request = toGenerateRequest(form.value)
    // A voice kept in the form from when the server still had voices would be refused.
    if (!props.voices || voiceList.value === null) {
      request.voice = null
    }
    const waiting = await generate(request)
    message.value = { text: t(waiting ? 'waiting' : 'queued'), error: false }
  } catch (caught) {
    if (caught instanceof ApiError) {
      fieldErrors.value = caught.errors
    }
    message.value = { text: errorText(caught), error: true }
  } finally {
    sending.value = false
  }
}

const confirm = useConfirm()
const starting = ref(false)
const draftMessage = ref<{ text: string; error: boolean } | null>(null)
/**
 * The draft this browser asked for, waiting or being written. Another one can be asked meanwhile, e.g. from another
 * browser: it waits in the queue.
 */
const drafting = computed(() => {
  const id = form.value.lyricsDraftId
  return (
    starting.value ||
    (id !== '' && (props.queuedIds.has(id) || (props.lyricsDraft?.id === id && props.lyricsDraft.stage === 'writing')))
  )
})
/** Said while the draft waits for the memory, so that the spinner does not look stuck. */
const draftWaiting = computed(() => form.value.lyricsDraftId !== '' && props.queuedIds.has(form.value.lyricsDraftId))

/** The draft this browser asked for lands in the field when it arrives, also after a reload or a locked phone. */
function take(draft: LyricsState | null): void {
  if (!draft?.finished || draft.id !== form.value.lyricsDraftId) {
    return
  }
  form.value.lyricsDraftId = ''
  const before = revisingFrom.value
  revisingFrom.value = null
  if (before !== null) {
    if (draft.stage === 'done' && draft.lyrics) {
      form.value.lyrics = draft.lyrics
      undo.value = { before, after: draft.lyrics }
      instruction.value = ''
      reviseMessage.value = { text: t('lyricsRevised'), error: false }
    } else {
      reviseMessage.value = { text: t('errorGeneric', { message: draft.message ?? '' }), error: true }
    }
    return
  }
  if (draft.stage === 'done' && draft.lyrics) {
    form.value.lyrics = draft.lyrics
    // YuE2 pronounces by the style's language tag; German words sung as English are the likeliest surprise.
    const untagged = form.value.lyricsLanguage === 'german' && !hasTag(form.value.style, 'German')
    draftMessage.value = { text: t(untagged ? 'lyricsDraftedGerman' : 'lyricsDrafted'), error: false }
  } else {
    draftMessage.value = { text: t('errorGeneric', { message: draft.message ?? '' }), error: true }
  }
}

/**
 * The lyrics as they were before the revision on its way; null for a new draft. Only in memory: after a reload the
 * revision still lands in the field, like a draft, just without the way back.
 */
const revisingFrom = ref<string | null>(null)
/** What to change, e.g. "make the chorus catchier". */
const instruction = ref('')
const reviseMessage = ref<{ text: string; error: boolean } | null>(null)
/** The text before the last revision, offered back while the field still holds what the revision wrote. */
const undo = ref<{ before: string; after: string } | null>(null)
const canUndo = computed(() => undo.value !== null && undo.value.after === form.value.lyrics)

watch(() => props.lyricsDraft, take, { immediate: true })

/** Changes the lyrics in the field as instructed, rather than rolling a whole new draft. */
async function revise(): Promise<void> {
  const text = instruction.value.trim()
  if (!text || drafting.value) {
    return
  }
  starting.value = true
  reviseMessage.value = null
  draftMessage.value = null
  revisingFrom.value = form.value.lyrics
  try {
    const started = await reviseLyrics(
      form.value.lyrics,
      text,
      form.value.lyricsIdea.trim(),
      form.value.style.trim(),
      form.value.lyricsLanguage,
      form.value.lyricsModel || null,
      reasoning(),
      machineField(form.value.lyricsMachine),
    )
    form.value.lyricsDraftId = started.id
    take(props.lyricsDraft)
  } catch (caught) {
    revisingFrom.value = null
    reviseMessage.value = { text: errorText(caught), error: true }
  } finally {
    starting.value = false
  }
}

function undoRevision(): void {
  if (undo.value) {
    form.value.lyrics = undo.value.before
    undo.value = null
    reviseMessage.value = null
  }
}

/** A draft replaces the lyrics field; lyrics someone wrote by hand should not vanish without a question. */
function askDraft(): void {
  if (form.value.lyrics.trim() === '') {
    void draft()
    return
  }
  confirm.require({
    header: t('replaceLyrics'),
    message: t('confirmReplaceLyrics'),
    icon: 'pi pi-pencil',
    rejectProps: { label: t('keep'), severity: 'secondary', outlined: true },
    acceptProps: { label: t('replace') },
    accept: () => void draft(),
  })
}

async function draft(): Promise<void> {
  starting.value = true
  draftMessage.value = null
  reviseMessage.value = null
  revisingFrom.value = null
  try {
    const started = await draftLyrics(
      form.value.lyricsIdea.trim(),
      form.value.style.trim(),
      form.value.lyricsLanguage,
      form.value.lyricsModel || null,
      photo.value,
      reasoning(),
      machineField(form.value.lyricsMachine),
    )
    form.value.lyricsDraftId = started.id
    // A quick failure (LM Studio missing) can arrive as an event before this answer.
    take(props.lyricsDraft)
  } catch (caught) {
    draftMessage.value = { text: errorText(caught), error: true }
  } finally {
    starting.value = false
  }
}

/**
 * The photo the next draft is about, scaled down (see photo.ts). Only in memory, not in the saved form: a few hundred KB
 * of base64 do not belong in localStorage.
 */
const photo = ref<string | null>(null)
const photoInput = ref<HTMLInputElement | null>(null)

async function pickPhoto(event: Event): Promise<void> {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  // Cleared so that picking the same photo again after removing it fires a change.
  input.value = ''
  if (!file) {
    return
  }
  draftMessage.value = null
  try {
    photo.value = await photoDataUrl(file)
  } catch {
    draftMessage.value = { text: t('photoUnreadable'), error: true }
  }
}

const canDraft = computed(() => form.value.lyricsIdea.trim() !== '' || photo.value !== null)

/** LM Studio says which models can see; one that cannot would refuse the photo only after loading for a while. */
const blind = computed(() => {
  const chosen = lyricsModels.value?.models.find((m) => m.id === lyricsModel.value)
  return photo.value !== null && chosen?.vision === false
})

/** Null until LM Studio answered; without it the picker stays hidden and drafts use the configured model. */
const lyricsModels = ref<LyricsModels | null>(null)

/** The models of the chosen Mac's LM Studio (a helper's may have others), read again when the choice changes. */
async function loadLyricsModels(): Promise<void> {
  const machine = form.value.lyricsMachine
  try {
    const models = await getLyricsModels(machineField(machine))
    if (machine !== form.value.lyricsMachine) {
      return
    }
    // A model deleted in LM Studio since it was picked: back to the default rather than a draft that fails.
    if (form.value.lyricsModel && !models.models.some((m) => m.id === form.value.lyricsModel)) {
      form.value.lyricsModel = ''
    }
    lyricsModels.value = models
  } catch {
    // LM Studio is not there; a draft says so with its own message.
    if (machine === form.value.lyricsMachine) {
      lyricsModels.value = null
    }
  }
}

onMounted(loadLyricsModels)
watch(() => form.value.lyricsMachine, loadLyricsModels)

/** Null until the voice service answered; without it the choice stays hidden and songs keep their own voice. */
const voiceList = ref<ReferenceVoice[] | null>(null)

watch(
  () => props.voices,
  async (configured) => {
    if (!configured || voiceList.value !== null) {
      return
    }
    try {
      const voices = await listVoices()
      // A voice deleted since it was picked: back to none rather than a song that is refused.
      if (form.value.voiceId && !voices.some((v) => v.id === form.value.voiceId)) {
        form.value.voiceId = ''
      }
      voiceList.value = voices
    } catch {
      // The voice service is not there; the library says so when a voice is asked for there.
    }
  },
  { immediate: true },
)

// A template may name a voice deleted since it was saved.
watch(
  () => form.value.voiceId,
  (id) => {
    if (id && voiceList.value !== null && !voiceList.value.some((v) => v.id === id)) {
      form.value.voiceId = ''
    }
  },
)

const voiceOptions = computed(() => [
  { value: '', label: t('voiceAfterNone') },
  ...(voiceList.value ?? []).map((v) => ({ value: v.id, label: v.label })),
])
const octaves = computed(octaveOptions)
const strengths = computed(strengthOptions)
const stepChoices = computed(stepOptions)

// The size is shown because on 24 GB it decides whether a model fits beside the open apps.
const lyricsModelOptions = computed(() =>
  (lyricsModels.value?.models ?? []).map((m) => {
    const name = m.id === lyricsModels.value?.default ? t('lyricsModelDefault', { name: m.name }) : m.name
    const details = [
      m.cloud ? t('lyricsModelCloud') : '',
      m.needsGb ? t('lyricsModelNeeds', { gb: m.needsGb }) : m.role ? t(`lyricsModelRole_${m.role}`) : '',
      m.sizeBytes ? formatBytes(m.sizeBytes) : '',
      m.vision ? t('lyricsModelVision') : '',
      m.loaded ? t('lyricsModelLoaded') : '',
    ]
    return { value: m.id, label: [name, ...details.filter((d) => d)].join(' · ') }
  }),
)

/** Claude writes beside a running song, so the form need not say the draft waits for it. */
const reasoningOptions = computed(() => [
  { value: '', label: t('lyricsReasoningDefault') },
  { value: 'off', label: t('lyricsReasoningOff') },
  { value: 'low', label: t('lyricsReasoningLow') },
  { value: 'medium', label: t('lyricsReasoningMedium') },
  { value: 'high', label: t('lyricsReasoningHigh') },
  { value: 'on', label: t('lyricsReasoningOn') },
])

/** Claude has its own effort on the server; a value kept from before an option was dropped goes out as none. */
function reasoning(): LyricsReasoning | null {
  const value = form.value.lyricsReasoning
  return !cloudModel.value && value !== '' && reasoningOptions.value.some((o) => o.value === value) ? value : null
}

const cloudModel = computed(() => lyricsModels.value?.models.find((m) => m.id === lyricsModel.value)?.cloud === true)

/** The default is kept as "none chosen", so that a new default on the server reaches this form too. */
const lyricsModel = computed({
  get: () => form.value.lyricsModel || lyricsModels.value?.default || '',
  set: (value: string) => {
    form.value.lyricsModel = value === lyricsModels.value?.default ? '' : value
  },
})

function errorText(caught: unknown): string {
  if (caught instanceof ApiError && caught.status === 0) {
    return t('errorNetwork')
  }
  return t('errorGeneric', { message: caught instanceof Error ? caught.message : String(caught) })
}

function reset(): void {
  form.value = defaultFormState()
  message.value = null
  fieldErrors.value = {}
}

// App's trash button resets the form; the message lives here, so it calls this instead of the model.
defineExpose({ reset })

const lyricsLanguageOptions = [
  { value: 'english', label: t('lyricsLanguageEnglish') },
  { value: 'german', label: t('lyricsLanguageGerman') },
]

const qualityOptions = [
  { value: 'draft', label: t('qualityDraft') },
  {
    value: 'full',
    label: t('qualityFull'),
  },
]
// Up to the API's MaxBatch; the worker composes four at a time and works through the rest one after another.
const batchOptions = Array.from({ length: 10 }, (_, i) => ({ value: i + 1, label: String(i + 1) }))
</script>

<template>
  <form @submit.prevent="submit">
    <FloatLabel variant="on">
      <InputText
        fluid
        id="gen-title"
        v-model="form.title"
        type="text"
        maxlength="120"
        :placeholder="t('titlePlaceholder')"
        aria-describedby="gen-title-help"
      />
      <label for="gen-title">{{ t('title') }}</label>
      <FieldHelp id="gen-title-help" :hint="t('titleHint')" />
    </FloatLabel>

    <FloatLabel variant="on" class="mt-6">
      <Textarea
        fluid
        id="gen-style"
        v-model="form.style"
        rows="3"
        required
        :placeholder="t('stylePlaceholder')"
        aria-describedby="gen-style-help"
      />
      <label for="gen-style">{{ t('style') }}</label>
    </FloatLabel>
    <TextActions v-model="form.style" />
    <FieldHelp id="gen-style-help" :hint="t('styleHint')" :more="t('styleMore')" />
    <small v-if="fieldErrors.style" class="danger">{{ fieldErrors.style.join(' ') }}</small>
    <Button
      type="button"
      class="mt-1"
      size="small"
      text
      :icon="blocksOpen ? 'pi pi-chevron-up' : 'pi pi-th-large'"
      :label="t('styleBlocks')"
      :aria-expanded="blocksOpen"
      aria-controls="gen-style-blocks"
      @click="blocksOpen = !blocksOpen"
    />
    <StyleBlocks v-if="blocksOpen" id="gen-style-blocks" v-model="form.style" :instrumental="form.instrumental" />

    <div class="mt-6 flex items-start gap-2">
      <FloatLabel variant="on" class="min-w-0 flex-1">
        <InputText
          fluid
          id="gen-lyrics-idea"
          v-model="form.lyricsIdea"
          type="text"
          maxlength="1000"
          :placeholder="t('lyricsIdeaPlaceholder')"
          aria-describedby="gen-lyrics-idea-help"
        />
        <label for="gen-lyrics-idea">{{ t('lyricsIdea') }}</label>
      </FloatLabel>
      <SelectButton
        v-model="form.lyricsLanguage"
        option-value="value"
        option-label="label"
        :options="lyricsLanguageOptions"
        :allow-empty="false"
        :aria-label="t('lyricsLanguage')"
        class="shrink-0"
      />
      <!-- Without "capture" a phone offers both the camera and its photo library. -->
      <input ref="photoInput" type="file" accept="image/*" class="hidden" @change="pickPhoto" />
      <Button
        type="button"
        icon="pi pi-camera"
        text
        size="large"
        :disabled="drafting"
        :aria-label="t('photoPick')"
        v-tooltip.bottom="t('photoPick')"
        @click="photoInput?.click()"
      />
      <Button
        type="button"
        icon="pi pi-sparkles"
        text
        size="large"
        :loading="drafting"
        :disabled="drafting || !canDraft || blind"
        @click="askDraft"
      />
    </div>
    <div class="mt-2 flex flex-wrap gap-2">
      <Select
        v-if="lyricsModelOptions.length > 1"
        v-model="lyricsModel"
        :options="lyricsModelOptions"
        option-value="value"
        option-label="label"
        :disabled="drafting"
        :aria-label="t('lyricsModel')"
        :title="t('lyricsModel')"
        size="small"
        class="min-w-0 flex-[2_1_14rem]"
      />
      <Select
        v-if="!cloudModel"
        v-model="form.lyricsReasoning"
        :options="reasoningOptions"
        option-value="value"
        option-label="label"
        :disabled="drafting"
        :aria-label="t('lyricsReasoning')"
        :title="t('lyricsReasoning')"
        size="small"
        class="min-w-0 flex-[1_1_10rem]"
      />
      <MachinePicker v-model="form.lyricsMachine" engine="lyrics" :disabled="drafting" class="flex-[1_1_10rem]" />
    </div>
    <small v-if="cloudModel" class="muted mt-1 block">{{ t('lyricsCloudHint') }}</small>
    <div v-if="photo" class="mt-2 flex items-center gap-2">
      <img :src="photo" :alt="t('photoAlt')" class="h-16 w-16 rounded-md object-cover" />
      <small class="muted min-w-0 flex-1">{{ blind ? t('photoBlindModel') : t('photoHint') }}</small>
      <Button
        type="button"
        icon="pi pi-times"
        text
        severity="secondary"
        :disabled="drafting"
        :aria-label="t('photoRemove')"
        @click="photo = null"
      />
    </div>
    <FieldHelp id="gen-lyrics-idea-help" :hint="t('lyricsIdeaHint')" :more="t('lyricsIdeaMore')" />
    <small v-if="draftMessage" :class="['block', draftMessage.error ? 'danger' : 'muted']" role="status">{{
      draftMessage.text
    }}</small>
    <small v-else-if="draftWaiting" class="block muted" role="status">{{ t('lyricsWaiting') }}</small>

    <FloatLabel variant="on" class="mt-6">
      <Textarea
        fluid
        id="gen-lyrics"
        v-model="form.lyrics"
        class="mono"
        rows="12"
        :required="!form.instrumental"
        spellcheck="false"
        :placeholder="t('lyricsPlaceholder')"
        aria-describedby="gen-lyrics-help"
        @blur="rememberCaret"
        @select="rememberCaret"
        @keyup="rememberCaret"
        @click="rememberCaret"
      />
      <label for="gen-lyrics">{{ form.instrumental ? t('lyricsOptional') : t('lyrics') }}</label>
      <TextActions v-model="form.lyrics" />
      <FieldHelp id="gen-lyrics-help" :hint="t('lyricsHint')" :more="t('lyricsMore')" />
      <small v-if="fieldErrors.lyrics" class="danger">{{ fieldErrors.lyrics.join(' ') }}</small>
    </FloatLabel>
    <Button
      type="button"
      class="mt-1"
      size="small"
      text
      :icon="sectionsOpen ? 'pi pi-chevron-up' : 'pi pi-bars'"
      :label="t('sectionTags')"
      :aria-expanded="sectionsOpen"
      aria-controls="gen-section-tags"
      @click="sectionsOpen = !sectionsOpen"
    />
    <SectionTags v-if="sectionsOpen" id="gen-section-tags" @pick="insertTag" />
    <!-- Enter here revises; it must not submit the form and start a song. -->
    <div v-if="form.lyrics.trim() !== '' && !form.instrumental" class="mt-2 flex items-center gap-2">
      <InputText
        v-model="instruction"
        type="text"
        maxlength="1000"
        enterkeyhint="send"
        :placeholder="t('revisePlaceholder')"
        :aria-label="t('revise')"
        :disabled="drafting"
        class="min-w-0 flex-1"
        @keydown.enter.prevent="revise"
      />
      <Button
        type="button"
        icon="pi pi-sync"
        text
        size="large"
        :loading="drafting"
        :disabled="drafting || instruction.trim() === ''"
        :aria-label="t('revise')"
        v-tooltip.bottom="t('revise')"
        @click="revise"
      />
    </div>
    <div v-if="reviseMessage" class="flex flex-wrap items-center gap-x-2" role="status">
      <small :class="reviseMessage.error ? 'danger' : 'muted'">{{ reviseMessage.text }}</small>
      <Button v-if="canUndo" type="button" :label="t('undoRevision')" text size="small" @click="undoRevision" />
    </div>

    <div class="flex items-center gap-2 mt-6">
      <Checkbox id="instrumental" binary v-model="form.instrumental" aria-describedby="gen-instrumental-help" />
      <label for="instrumental">{{ t('instrumental') }}</label>
    </div>
    <FieldHelp id="gen-instrumental-help" :hint="t('instrumentalHint')" :more="t('instrumentalMore')" />
    <Divider />
    <div class="grid grid-cols-2 gap-3">
      <div>
        <div>
          <label class="text-sm muted mb-0">{{ t('quality') }}</label
          ><br />
          <SelectButton v-model="form.quality" option-value="value" option-label="label" :options="qualityOptions" />
        </div>
        <FieldHelp id="gen-quality-help" :hint="t('qualityHint')" :more="t('qualityMore')" />
      </div>

      <div>
        <FloatLabel variant="on" class="min-w-0 flex-1 mt-5">
          <Select
            id="gen-batch"
            v-model.number="form.batch"
            option-label="label"
            option-value="value"
            :options="batchOptions"
            aria-describedby="gen-batch-help"
          />
          <label for="gen-batch">{{ t('batch') }}</label>
        </FloatLabel>
        <FieldHelp id="gen-batch-help" :hint="t('batchHint')" :more="t('batchMore')" />
        <small v-if="fieldErrors.batch" class="danger">{{ fieldErrors.batch.join(' ') }}</small>
      </div>

      <!-- Out here, not among the advanced parameters: it stays in effect when those are switched off. -->
      <div class="col-span-2">
        <FloatLabel variant="on" class="mt-2 w-full">
          <InputText
            fluid
            id="gen-seed"
            v-model="form.seed"
            type="text"
            inputmode="numeric"
            pattern="[0-9]*"
            :placeholder="t('seedPlaceholder')"
            aria-describedby="gen-seed-help"
          />
          <label for="gen-seed">{{ t('seed') }}</label>
        </FloatLabel>
        <FieldHelp id="gen-seed-help" :hint="t('seedHint')" :more="t('seedMore')" />
        <small v-if="fieldErrors.seed" class="danger">{{ fieldErrors.seed.join(' ') }}</small>
      </div>
    </div>

    <template v-if="voices && voiceList && voiceList.length > 0">
      <FloatLabel variant="on" class="mt-5">
        <Select
          id="gen-voice"
          v-model="form.voiceId"
          option-label="label"
          option-value="value"
          :options="voiceOptions"
          fluid
          aria-describedby="gen-voice-help"
        />
        <label for="gen-voice">{{ t('voiceAfter') }}</label>
      </FloatLabel>
      <div v-if="form.voiceId" class="mt-2 flex flex-wrap gap-2">
        <SelectButton
          v-model="form.voiceShift"
          :options="octaves"
          option-label="label"
          option-value="value"
          :allow-empty="false"
          :aria-label="t('octave')"
          size="small"
        />
        <SelectButton
          v-model="form.voiceStrength"
          :options="strengths"
          option-label="label"
          option-value="value"
          :allow-empty="false"
          :aria-label="t('strength')"
          size="small"
        />
        <SelectButton
          v-model="form.voiceSteps"
          :options="stepChoices"
          option-label="label"
          option-value="value"
          :allow-empty="false"
          :aria-label="t('steps')"
          size="small"
        />
      </div>
      <FieldHelp id="gen-voice-help" :hint="t('voiceAfterHint')" />
      <small v-if="fieldErrors['voice.voiceId']" class="danger">{{ fieldErrors['voice.voiceId'].join(' ') }}</small>
    </template>

    <Divider />
    <div>
      <Button
        type="submit"
        class="w-full"
        :label="sending ? t('generating') : t('generate')"
        severity="primary"
        :loading="sending || drafting || scoreWithoutPlanning"
        :disabled="sending || drafting || scoreWithoutPlanning"
      />
      <!-- The score lives with the advanced parameters, which may be in another column or collapsed. -->
      <small v-if="scoreWithoutPlanning" class="danger block">{{ t('abcNeedsPlanning') }}</small>
      <span v-if="message" :class="message.error ? 'danger' : 'muted'" role="status">{{ message.text }}</span>
    </div>
  </form>
</template>

<style scoped></style>
