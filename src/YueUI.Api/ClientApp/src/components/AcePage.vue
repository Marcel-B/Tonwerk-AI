<script setup lang="ts">
import Checkbox from 'primevue/checkbox'
import Message from 'primevue/message'
import Panel from 'primevue/panel'
import { computed, ref, watch } from 'vue'
import { ApiError, composeAce, deleteAceJob, getAce } from '../api'
import { aceForm, aceKeys, aceLanguages, defaultAceForm, fromAceRequest, toAceRequest, usesLm } from '../ace'
import { formatDateTime, formatDuration, t } from '../i18n'
import { insertSectionTag, shout } from '../lyricsTags'
import type { AceInfo, AceJob, AceStage, AceTask, RunInfo } from '../types'
import { showSong, songHref } from '../view'
import { ranOnNote } from '../machines'
import FieldHelp from './FieldHelp.vue'
import MachinePicker from './MachinePicker.vue'
import OptionalNumberField from './OptionalNumberField.vue'
import SectionTags from './SectionTags.vue'
import StyleBlocks from './StyleBlocks.vue'
import TextActions from './TextActions.vue'

/**
 * ACE-Step, the second song model (Ace/AceComposer.cs): a style caption instead of YuE2's tags, lyrics the LM may
 * write itself, tempo, key, metre and length to fix or leave to it, and covers or repaints of a library song. Its
 * parameters differ from YuE2's too much to share the create page; the songs go into the same library.
 */
const props = defineProps<{
  /** Loaded when the page is shown, and again when the event stream reopens. */
  active: boolean
  connected: boolean
  /** Jobs as the event stream reports them, newer than what was loaded. */
  live: AceJob[]
  /** The library, for the song a cover or repaint starts from. */
  runs: RunInfo[]
}>()

const emit = defineEmits<{ error: [message: string]; notice: [message: string] }>()

const form = aceForm
const info = ref<AceInfo | null>(null)
const loadError = ref<string | null>(null)
let loading = false

async function load(): Promise<void> {
  if (loading) {
    return
  }
  loading = true
  try {
    info.value = await getAce()
    loadError.value = null
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

/** What was loaded with the live changes over it; a deleted job arrives as cancelled and leaves. */
const jobs = computed<AceJob[]>(() => {
  const byId = new Map((info.value?.jobs ?? []).map((job) => [job.id, job]))
  for (const job of props.live) {
    const known = byId.get(job.id)
    if (!known || known.updatedAt <= job.updatedAt) {
      byId.set(job.id, job)
    }
  }
  return [...byId.values()]
    .filter((job) => job.stage !== 'cancelled')
    .sort((a, b) => b.createdAt.localeCompare(a.createdAt))
})

const lm = computed(() => usesLm(form.value))

const taskOptions = computed(() =>
  (['text2music', 'cover', 'repaint'] as AceTask[]).map((task) => ({ label: t(`aceTask_${task}`), value: task })),
)

/** Every library song with audio, newest run first, as the source of a cover or repaint. */
const sourceOptions = computed(() =>
  props.runs.flatMap((run) =>
    run.songs
      .filter((song) => song.hasAudio)
      .map((song) => ({
        label: `${run.title || t('untitled')} · ${t('songN', { n: song.index })}`,
        value: song.id,
      })),
  ),
)

const auto = computed(() => t('aceAuto'))
const languageNames = computed(() => {
  try {
    return new Intl.DisplayNames([t('aceLocaleTag')], { type: 'language' })
  } catch {
    return null
  }
})
const languageOptions = computed(() => [
  { label: auto.value, value: '' },
  ...aceLanguages.map((code) => ({ label: languageNames.value?.of(code) ?? code, value: code as string })),
])
const keyOptions = computed(() => [
  { label: auto.value, value: '' },
  ...aceKeys.map((key) => ({ label: key, value: key })),
])
const metreOptions = computed(() => [
  { label: auto.value, value: null as number | null },
  ...[2, 3, 4, 6].map((beats) => ({ label: `${beats}/4`, value: beats as number | null })),
])
const batchOptions = [1, 2, 3, 4].map((n) => ({ label: String(n), value: n }))
const solverOptions = computed(() => [
  { label: auto.value, value: '' },
  ...['euler', 'sde', 'dpm3m', 'stork4'].map((solver) => ({ label: solver, value: solver })),
])

/** The server's default first: the largest LM, a turbo DiT (AceModelFiles sorts them so). */
function modelOptions(files: string[] | undefined) {
  return [
    { label: t('aceModelDefault'), value: '' },
    ...(files ?? []).map((file) => ({ label: file.replace(/\.gguf$/, ''), value: file })),
  ]
}
const lmOptions = computed(() => modelOptions(info.value?.lmModels))
const ditOptions = computed(() => modelOptions(info.value?.ditModels))
const adapterOptions = computed(() => [
  { label: t('aceAdapterNone'), value: '' },
  ...(info.value?.adapters ?? []).map((adapter) => ({ label: adapter, value: adapter })),
])

// A model or adapter removed from the folder since would be refused.
watch(info, (value) => {
  if (!value) {
    return
  }
  if (form.value.lmModel && !value.lmModels.includes(form.value.lmModel)) {
    form.value.lmModel = ''
  }
  if (form.value.ditModel && !value.ditModels.includes(form.value.ditModel)) {
    form.value.ditModel = ''
  }
  if (form.value.adapter && !value.adapters.includes(form.value.adapter)) {
    form.value.adapter = ''
  }
})

const blocksOpen = ref(false)
const sectionsOpen = ref(false)
const lyricsCaret = ref<{ text: string; start: number; at: number } | null>(null)

function rememberCaret(event: Event): void {
  const field = event.target as HTMLTextAreaElement
  lyricsCaret.value = { text: field.value, start: field.selectionStart, at: field.selectionEnd }
}

function insertTag(tag: string): void {
  const caret = lyricsCaret.value?.text === form.value.lyrics ? lyricsCaret.value.at : null
  const inserted = insertSectionTag(form.value.lyrics, tag, caret)
  form.value.lyrics = inserted.text
  lyricsCaret.value = { text: inserted.text, start: inserted.caret, at: inserted.caret }
}

/** Capitals for the selection (or the caret's line), kept selected so a second tap turns them back. */
function shoutLyrics(): void {
  const caret = lyricsCaret.value?.text === form.value.lyrics ? lyricsCaret.value : null
  if (caret === null) return
  const shouted = shout(form.value.lyrics, caret.start, caret.at)
  form.value.lyrics = shouted.text
  lyricsCaret.value = { text: shouted.text, start: shouted.start, at: shouted.end }
}

// ---- Advanced ----------------------------------------------------------------------------------------

const advancedKeys = [
  'lmModel',
  'ditModel',
  'steps',
  'guidance',
  'shift',
  'solver',
  'lmTemperature',
  'lmCfgScale',
  'lmTopP',
  'lmTopK',
  'negativePrompt',
  'enrichCaption',
  'adapter',
  'adapterScale',
] as const

const advancedChanged = computed(() => {
  const defaults = defaultAceForm()
  return advancedKeys.some((key) => form.value[key] !== defaults[key])
})
const advancedCollapsed = ref(!advancedChanged.value)

function resetAdvanced(): void {
  const defaults = defaultAceForm()
  for (const key of advancedKeys) {
    ;(form.value as unknown as Record<string, unknown>)[key] = defaults[key]
  }
}

function resetForm(): void {
  // The Mac is a choice of the page, not of the song.
  form.value = { ...defaultAceForm(), machine: form.value.machine }
  fieldErrors.value = {}
}

// ---- Sending -----------------------------------------------------------------------------------------

const sending = ref(false)
const fieldErrors = ref<Record<string, string[]>>({})

watch(fieldErrors, (errors) => {
  if (Object.keys(errors).some((key) => (advancedKeys as readonly string[]).includes(key))) {
    advancedCollapsed.value = false
  }
})

async function send(): Promise<void> {
  sending.value = true
  fieldErrors.value = {}
  try {
    const job = await composeAce(toAceRequest(form.value))
    if (info.value) {
      info.value = { ...info.value, jobs: [job, ...info.value.jobs.filter((other) => other.id !== job.id)] }
    }
    emit('notice', t('aceQueued'))
  } catch (caught) {
    if (caught instanceof ApiError) {
      fieldErrors.value = caught.errors
    }
    emit('error', message(caught))
  } finally {
    sending.value = false
  }
}

const canSend = computed(
  () =>
    !!info.value?.ready && form.value.caption.trim() !== '' && (lm.value || form.value.source !== '') && !sending.value,
)

async function removeJob(job: AceJob): Promise<void> {
  try {
    await deleteAceJob(job.id)
    if (info.value) {
      info.value = { ...info.value, jobs: info.value.jobs.filter((other) => other.id !== job.id) }
    }
  } catch (caught) {
    emit('error', message(caught))
  }
}

/** The job's request back in the form, with the seed it was made with, to vary one thing. */
function reuse(job: AceJob): void {
  form.value = fromAceRequest({ ...job.request, seed: job.seed }, form.value.machine)
  fieldErrors.value = {}
  window.scrollTo({ top: 0, behavior: 'smooth' })
}

const stageSeverity: Record<AceStage, 'secondary' | 'info' | 'success' | 'danger' | undefined> = {
  queued: 'secondary',
  loading: 'info',
  writing: 'info',
  composing: 'info',
  synthesizing: 'info',
  decoding: 'info',
  saving: 'info',
  done: 'success',
  failed: 'danger',
  cancelled: 'secondary',
}

function stageText(job: AceJob): string {
  const label = t(`aceStage_${job.stage}`)
  return job.finished || job.stage === 'queued' || job.stage === 'saving'
    ? label
    : `${label} ${Math.round(job.fraction * 100)} %`
}

/** The songs the job made, as the library lists them; one deleted since is left out. */
function songsOf(job: AceJob) {
  return props.runs.find((run) => run.id === job.run)?.songs ?? []
}

function errorOf(field: string): string | null {
  return fieldErrors.value[field]?.join(' ') ?? null
}

function message(caught: unknown): string {
  return caught instanceof Error ? caught.message : String(caught)
}
</script>

<template>
  <section class="flex flex-col gap-5">
    <div>
      <h2 class="mt-0 mb-1">{{ t('menuAce') }}</h2>
      <p class="muted m-0 text-sm">{{ t('aceIntro') }}</p>
    </div>
    <p v-if="loadError" class="danger m-0">{{ loadError }}</p>

    <Message v-if="info && !info.ready" severity="warn" :closable="false">
      <div class="flex flex-col gap-2">
        <span>{{ info.installed ? t('aceNoModels') : t('aceNotInstalled') }}</span>
        <code class="install">deploy/install-ace.sh</code>
        <span class="text-sm">{{ t('aceNotInstalledPath', { binaries: info.binaries, models: info.models }) }}</span>
      </div>
    </Message>

    <form class="grid grid-cols-1 items-start gap-4 md:grid-cols-2" @submit.prevent="send">
      <Card>
        <template #title>
          <div class="flex items-center justify-between">
            <span>{{ t('aceSong') }}</span>
            <Button icon="pi pi-trash" rounded text type="button" :aria-label="t('resetForm')" @click="resetForm" />
          </div>
        </template>
        <template #content>
          <div class="flex flex-col gap-4">
            <div>
              <SelectButton
                v-model="form.task"
                :options="taskOptions"
                option-label="label"
                option-value="value"
                :allow-empty="false"
                :aria-label="t('aceTask')"
              />
              <small class="muted mt-1 block">{{ t(`aceTaskHint_${form.task}`) }}</small>
            </div>

            <template v-if="!lm">
              <div class="flex flex-col gap-1">
                <label for="ace-source" class="muted text-sm">{{ t('aceSource') }}</label>
                <Select
                  id="ace-source"
                  :aria-label="t('aceSource')"
                  v-model="form.source"
                  :options="sourceOptions"
                  option-label="label"
                  option-value="value"
                  filter
                  :placeholder="t('aceSourcePick')"
                  fluid
                />
                <small v-if="errorOf('source')" class="danger">{{ errorOf('source') }}</small>
              </div>
              <div v-if="form.task === 'cover'" class="flex flex-col gap-1">
                <label for="ace-cover-strength" class="muted text-sm">{{ t('aceCoverStrength') }}</label>
                <OptionalNumberField
                  id="ace-cover-strength"
                  v-model="form.coverStrength"
                  :min="0"
                  :max="1"
                  :fraction-digits="2"
                  :placeholder="auto"
                />
                <small class="muted">{{ t('aceCoverStrengthHint') }}</small>
              </div>
              <div v-else class="grid grid-cols-2 gap-3">
                <div class="flex flex-col gap-1">
                  <label for="ace-repaint-start" class="muted text-sm">{{ t('aceRepaintStart') }}</label>
                  <OptionalNumberField
                    id="ace-repaint-start"
                    v-model="form.repaintStart"
                    :min="0"
                    :max="600"
                    :fraction-digits="1"
                    placeholder="0"
                  />
                </div>
                <div class="flex flex-col gap-1">
                  <label for="ace-repaint-end" class="muted text-sm">{{ t('aceRepaintEnd') }}</label>
                  <OptionalNumberField
                    id="ace-repaint-end"
                    v-model="form.repaintEnd"
                    :min="0"
                    :max="600"
                    :fraction-digits="1"
                    :placeholder="t('aceRepaintToEnd')"
                  />
                  <small v-if="errorOf('repaintEnd')" class="danger">{{ errorOf('repaintEnd') }}</small>
                </div>
              </div>
            </template>

            <div class="flex flex-col gap-1">
              <label for="ace-title" class="muted text-sm">{{ t('title') }}</label>
              <InputText
                id="ace-title"
                v-model="form.title"
                maxlength="120"
                :placeholder="t('aceTitlePlaceholder')"
                fluid
              />
            </div>

            <div class="flex flex-col gap-1">
              <label for="ace-caption" class="muted text-sm">{{ t('aceCaption') }}</label>
              <Textarea
                id="ace-caption"
                v-model="form.caption"
                rows="3"
                auto-resize
                maxlength="2000"
                :placeholder="t('aceCaptionPlaceholder')"
                fluid
                required
              />
              <TextActions v-model="form.caption" />
              <FieldHelp id="ace-caption-help" :hint="t('aceCaptionHint')" />
              <small v-if="errorOf('caption')" class="danger">{{ errorOf('caption') }}</small>
              <Button
                type="button"
                class="self-start"
                size="small"
                text
                :icon="blocksOpen ? 'pi pi-chevron-up' : 'pi pi-th-large'"
                :label="t('styleBlocks')"
                :aria-expanded="blocksOpen"
                aria-controls="ace-style-blocks"
                @click="blocksOpen = !blocksOpen"
              />
              <StyleBlocks
                v-if="blocksOpen"
                id="ace-style-blocks"
                v-model="form.caption"
                :instrumental="form.instrumental"
              />
            </div>

            <div class="flex items-center gap-2">
              <Checkbox v-model="form.instrumental" input-id="ace-instrumental" binary />
              <label for="ace-instrumental">{{ t('instrumental') }}</label>
            </div>

            <template v-if="!form.instrumental">
              <div class="flex flex-col gap-1">
                <label for="ace-lyrics" class="muted text-sm">{{ lm ? t('aceLyricsOptional') : t('lyrics') }}</label>
                <Textarea
                  id="ace-lyrics"
                  v-model="form.lyrics"
                  class="mono"
                  rows="10"
                  spellcheck="false"
                  maxlength="8000"
                  :placeholder="lm ? t('aceLyricsPlaceholder') : ''"
                  fluid
                  @blur="rememberCaret"
                  @select="rememberCaret"
                  @keyup="rememberCaret"
                  @click="rememberCaret"
                />
                <TextActions v-model="form.lyrics" />
                <small v-if="errorOf('lyrics')" class="danger">{{ errorOf('lyrics') }}</small>
                <Button
                  type="button"
                  class="self-start"
                  size="small"
                  text
                  :icon="sectionsOpen ? 'pi pi-chevron-up' : 'pi pi-bars'"
                  :label="t('sectionTags')"
                  :aria-expanded="sectionsOpen"
                  aria-controls="ace-section-tags"
                  @click="sectionsOpen = !sectionsOpen"
                />
                <SectionTags v-if="sectionsOpen" id="ace-section-tags" @pick="insertTag" @shout="shoutLyrics" />
              </div>
              <div class="flex flex-col gap-1">
                <label for="ace-language" class="muted text-sm">{{ t('aceLanguage') }}</label>
                <Select
                  id="ace-language"
                  :aria-label="t('aceLanguage')"
                  v-model="form.language"
                  :options="languageOptions"
                  :placeholder="auto"
                  option-label="label"
                  option-value="value"
                  filter
                  fluid
                />
              </div>
            </template>
          </div>
        </template>
      </Card>

      <div class="flex min-w-0 flex-col gap-4">
        <Card>
          <template #title>{{ t('aceMusic') }}</template>
          <template #content>
            <div class="flex flex-col gap-4">
              <p class="muted m-0 text-sm">{{ lm ? t('aceMusicHint') : t('aceMusicFromSource') }}</p>
              <div v-if="lm" class="grid grid-cols-2 gap-3">
                <div class="flex flex-col gap-1">
                  <label for="ace-duration" class="muted text-sm">{{ t('aceDuration') }}</label>
                  <OptionalNumberField
                    id="ace-duration"
                    v-model="form.duration"
                    :min="10"
                    :max="600"
                    :placeholder="auto"
                  />
                  <small v-if="errorOf('duration')" class="danger">{{ errorOf('duration') }}</small>
                </div>
                <div class="flex flex-col gap-1">
                  <label for="ace-bpm" class="muted text-sm">{{ t('aceBpm') }}</label>
                  <OptionalNumberField id="ace-bpm" v-model="form.bpm" :min="30" :max="300" :placeholder="auto" />
                </div>
                <div class="flex flex-col gap-1">
                  <label for="ace-key" class="muted text-sm">{{ t('aceKey') }}</label>
                  <Select
                    id="ace-key"
                    :aria-label="t('aceKey')"
                    v-model="form.key"
                    :options="keyOptions"
                    :placeholder="auto"
                    option-label="label"
                    option-value="value"
                    fluid
                  />
                </div>
                <div class="flex flex-col gap-1">
                  <label for="ace-metre" class="muted text-sm">{{ t('aceTimeSignature') }}</label>
                  <Select
                    id="ace-metre"
                    :aria-label="t('aceTimeSignature')"
                    v-model="form.timeSignature"
                    :options="metreOptions"
                    option-label="label"
                    option-value="value"
                    fluid
                  />
                </div>
              </div>
              <div class="grid grid-cols-2 gap-3">
                <div class="flex flex-col gap-1">
                  <label for="ace-seed" class="muted text-sm">{{ t('seed') }}</label>
                  <InputText
                    id="ace-seed"
                    v-model="form.seed"
                    inputmode="numeric"
                    pattern="[0-9]*"
                    :placeholder="t('seedPlaceholder')"
                    fluid
                  />
                  <small v-if="errorOf('seed')" class="danger">{{ errorOf('seed') }}</small>
                </div>
                <div class="flex flex-col gap-1">
                  <label class="muted text-sm">{{ t('aceBatch') }}</label>
                  <SelectButton
                    v-model="form.batch"
                    :options="batchOptions"
                    option-label="label"
                    option-value="value"
                    :allow-empty="false"
                    :aria-label="t('aceBatch')"
                  />
                </div>
              </div>
              <div class="flex flex-wrap items-center gap-3">
                <Button
                  type="submit"
                  :label="t('aceCompose')"
                  icon="pi pi-sparkles"
                  :loading="sending"
                  :disabled="!canSend"
                />
                <MachinePicker v-model="form.machine" engine="ace" :disabled="sending" />
                <span v-if="!lm && !form.source" class="muted text-sm">{{ t('aceSourcePick') }}</span>
              </div>
            </div>
          </template>
        </Card>

        <Panel v-model:collapsed="advancedCollapsed" toggleable>
          <template #header>{{ t('advanced') }}</template>
          <template #icons>
            <Tag v-if="advancedChanged" severity="warn">{{ t('advancedChanged') }}</Tag>
          </template>
          <div class="flex flex-col gap-4">
            <div class="flex items-start gap-2">
              <small class="muted flex-1">{{ t('aceAdvancedIntro') }}</small>
              <Button
                icon="pi pi-refresh"
                :label="advancedChanged ? t('advancedReset') : t('advancedAtDefaults')"
                size="small"
                severity="warn"
                type="button"
                :disabled="!advancedChanged"
                @click="resetAdvanced"
              />
            </div>

            <div class="grid grid-cols-1 gap-3 sm:grid-cols-2">
              <div v-if="lm" class="flex flex-col gap-1">
                <label for="ace-lm" class="muted text-sm">{{ t('aceLmModel') }}</label>
                <Select
                  id="ace-lm"
                  :aria-label="t('aceLmModel')"
                  v-model="form.lmModel"
                  :options="lmOptions"
                  :placeholder="t('aceModelDefault')"
                  option-label="label"
                  option-value="value"
                  fluid
                />
              </div>
              <div class="flex flex-col gap-1">
                <label for="ace-dit" class="muted text-sm">{{ t('aceDitModel') }}</label>
                <Select
                  id="ace-dit"
                  :aria-label="t('aceDitModel')"
                  v-model="form.ditModel"
                  :options="ditOptions"
                  :placeholder="t('aceModelDefault')"
                  option-label="label"
                  option-value="value"
                  fluid
                />
              </div>
            </div>
            <small class="muted">{{ t('aceModelsHint') }}</small>

            <h3 class="m-0 text-sm font-medium">{{ t('aceDiffusion') }}</h3>
            <div class="grid grid-cols-2 gap-3">
              <div class="flex flex-col gap-1">
                <label for="ace-steps" class="muted text-sm">{{ t('aceSteps') }}</label>
                <OptionalNumberField id="ace-steps" v-model="form.steps" :min="1" :max="200" :placeholder="auto" />
              </div>
              <div class="flex flex-col gap-1">
                <label for="ace-guidance" class="muted text-sm">{{ t('aceGuidance') }}</label>
                <OptionalNumberField
                  id="ace-guidance"
                  v-model="form.guidance"
                  :min="1"
                  :max="15"
                  :fraction-digits="1"
                  :placeholder="auto"
                />
              </div>
              <div class="flex flex-col gap-1">
                <label for="ace-shift" class="muted text-sm">{{ t('aceShift') }}</label>
                <OptionalNumberField
                  id="ace-shift"
                  v-model="form.shift"
                  :min="1"
                  :max="10"
                  :fraction-digits="1"
                  :placeholder="auto"
                />
              </div>
              <div class="flex flex-col gap-1">
                <label for="ace-solver" class="muted text-sm">{{ t('aceSolver') }}</label>
                <Select
                  id="ace-solver"
                  :aria-label="t('aceSolver')"
                  v-model="form.solver"
                  :options="solverOptions"
                  :placeholder="auto"
                  option-label="label"
                  option-value="value"
                  fluid
                />
              </div>
            </div>
            <small class="muted">{{ t('aceDiffusionHint') }}</small>

            <template v-if="lm">
              <h3 class="m-0 text-sm font-medium">{{ t('aceLm') }}</h3>
              <div class="grid grid-cols-2 gap-3">
                <div class="flex flex-col gap-1">
                  <label for="ace-lm-temperature" class="muted text-sm">{{ t('temperature') }}</label>
                  <OptionalNumberField
                    id="ace-lm-temperature"
                    v-model="form.lmTemperature"
                    :min="0.01"
                    :max="2"
                    :fraction-digits="2"
                    :placeholder="auto"
                  />
                </div>
                <div class="flex flex-col gap-1">
                  <label for="ace-lm-cfg-scale" class="muted text-sm">{{ t('aceLmCfg') }}</label>
                  <OptionalNumberField
                    id="ace-lm-cfg-scale"
                    v-model="form.lmCfgScale"
                    :min="1"
                    :max="10"
                    :fraction-digits="1"
                    :placeholder="auto"
                  />
                </div>
                <div class="flex flex-col gap-1">
                  <label for="ace-lm-top-p" class="muted text-sm">Top p</label>
                  <OptionalNumberField
                    id="ace-lm-top-p"
                    v-model="form.lmTopP"
                    :min="0.01"
                    :max="1"
                    :fraction-digits="2"
                    :placeholder="auto"
                  />
                </div>
                <div class="flex flex-col gap-1">
                  <label for="ace-lm-top-k" class="muted text-sm">Top k</label>
                  <OptionalNumberField
                    id="ace-lm-top-k"
                    v-model="form.lmTopK"
                    :min="0"
                    :max="1000"
                    :placeholder="auto"
                  />
                </div>
              </div>
              <div class="flex flex-col gap-1">
                <label for="ace-negative" class="muted text-sm">{{ t('aceNegative') }}</label>
                <InputText id="ace-negative" v-model="form.negativePrompt" maxlength="1000" fluid />
                <small class="muted">{{ t('aceNegativeHint') }}</small>
              </div>
              <div class="flex items-start gap-2">
                <Checkbox v-model="form.enrichCaption" input-id="ace-enrich" binary class="mt-0.5" />
                <label for="ace-enrich">
                  {{ t('aceEnrich') }}
                  <small class="muted block">{{ t('aceEnrichHint') }}</small>
                </label>
              </div>
            </template>

            <h3 class="m-0 text-sm font-medium">{{ t('aceAdapter') }}</h3>
            <div class="grid grid-cols-2 gap-3">
              <Select
                v-model="form.adapter"
                :options="adapterOptions"
                :placeholder="t('aceAdapterNone')"
                option-label="label"
                option-value="value"
                :aria-label="t('aceAdapter')"
                fluid
              />
              <OptionalNumberField
                v-if="form.adapter"
                v-model="form.adapterScale"
                :min="0"
                :max="2"
                :fraction-digits="2"
                placeholder="1"
                :aria-label="t('aceAdapterScale')"
              />
            </div>
            <small class="muted">{{ t('aceAdapterHint') }}</small>
          </div>
        </Panel>
      </div>
    </form>

    <div v-if="info">
      <h3 class="mt-0 mb-2 text-base">{{ t('aceJobs') }}</h3>
      <p v-if="jobs.length === 0" class="muted m-0">{{ t('aceJobsEmpty') }}</p>
      <ul class="m-0 flex list-none flex-col gap-3 p-0">
        <li v-for="job in jobs" :key="job.id" class="job flex flex-col gap-1 rounded-lg p-3">
          <div class="flex items-center gap-2">
            <div class="min-w-0 flex-1">
              <div class="truncate font-semibold">{{ job.title }}</div>
              <div class="muted truncate text-sm">
                {{ t(`aceTask_${job.request.task ?? 'text2music'}`) }} · {{ formatDateTime(job.createdAt)
                }}<template v-if="job.seconds !== null"> · {{ formatDuration(job.seconds) }}</template
                ><template v-if="ranOnNote(job.ranOn)"> · {{ ranOnNote(job.ranOn) }}</template>
              </div>
            </div>
            <Tag :severity="stageSeverity[job.stage]">
              <i v-if="!job.finished && job.stage !== 'queued'" class="pi pi-spin pi-spinner text-xs" />
              {{ stageText(job) }}
            </Tag>
            <Button
              v-if="job.finished"
              icon="pi pi-replay"
              text
              rounded
              size="small"
              type="button"
              v-tooltip="t('aceReuse')"
              :aria-label="t('aceReuse')"
              @click="reuse(job)"
            />
            <Button
              :icon="job.finished ? 'pi pi-trash' : 'pi pi-times'"
              text
              rounded
              size="small"
              type="button"
              :severity="job.finished ? 'danger' : 'secondary'"
              v-tooltip="job.finished ? t('aceJobRemove') : t('aceJobStop')"
              :aria-label="job.finished ? t('aceJobRemove') : t('aceJobStop')"
              @click="removeJob(job)"
            />
          </div>
          <p class="m-0 line-clamp-2 text-sm">{{ job.request.caption }}</p>
          <p v-if="job.stage === 'failed' && job.message" class="danger m-0 text-sm">{{ job.message }}</p>
          <div v-if="job.stage === 'done' && songsOf(job).length > 0" class="flex flex-wrap gap-2">
            <a
              v-for="song in songsOf(job)"
              :key="song.id"
              :href="songHref(song.id)"
              class="text-sm"
              @click.prevent="showSong(song.id)"
              >{{ t('songN', { n: song.index }) }} · {{ t('seed') }} {{ song.seed }}</a
            >
          </div>
        </li>
      </ul>
    </div>
  </section>
</template>

<style scoped>
.job {
  background: var(--surface-sunken);
}

.install {
  font-family: ui-monospace, monospace;
  user-select: all;
}

.mono {
  font-family: ui-monospace, monospace;
}
</style>
