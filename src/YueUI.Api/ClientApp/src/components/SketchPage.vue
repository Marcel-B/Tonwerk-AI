<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import Message from 'primevue/message'
import Select from 'primevue/select'
import { useConfirm } from 'primevue/useconfirm'
import { ApiError, deleteSketch, getSketch, getSketches, getSketchInfo, planSketch, sketchFileUrl } from '../api'
import { formatDuration, locale, t, type MessageKey } from '../i18n'
import { aceForm, fromAceRequest } from '../ace'
import { navigate, openMidiOn } from '../view'
import type { SketchInfo, SketchResult, SketchStage, SketchSummary, SketchWarning, SongRequest } from '../types'

/**
 * A written song sketch planned by Claude: the sketch in, after a few minutes a whole score, and from it a MIDI draft
 * to download, the score for the Logic page and the song for the create form. The server keeps the sketches, so the
 * page only asks: on showing, and every few seconds while one is being written.
 */
const props = defineProps<{ active: boolean }>()
const emit = defineEmits<{ use: [request: SongRequest]; notice: [message: string]; error: [message: string] }>()

const confirm = useConfirm()
const info = ref<SketchInfo | null>(null)
const sketches = ref<SketchSummary[]>([])
const selected = ref<SketchResult | null>(null)
const text = ref('')
const wishes = ref('')
const sending = ref(false)
const fileInput = ref<HTMLInputElement | null>(null)
let poll: ReturnType<typeof setTimeout> | undefined

const working = computed(() => sketches.value.some((s) => s.stage === 'queued' || s.stage === 'writing'))
const keyFile = '~/.config/tonwerk/anthropic.key'

/** The Claude model chosen on this browser; empty for the server's default, so a new default reaches it. */
const modelKey = 'yue-ui.sketchModel'
const chosen = ref(readModel())
const model = computed({
  get: () => (info.value?.models.some((m) => m.id === chosen.value) ? chosen.value : (info.value?.model ?? '')),
  set: (value: string) => {
    chosen.value = value === info.value?.model ? '' : value
    try {
      localStorage.setItem(modelKey, chosen.value)
    } catch {
      // Only a convenience.
    }
  },
})
const modelOptions = computed(() =>
  (info.value?.models ?? []).map((m) => ({
    value: m.id,
    label: m.id === info.value?.model ? t('sketchModelDefault', { name: m.name }) : m.name,
  })),
)

function readModel(): string {
  try {
    return localStorage.getItem(modelKey) ?? ''
  } catch {
    return ''
  }
}

/** A model the server no longer offers goes to the default rather than to a refusal. */
function offered(id: string | null | undefined): string | null {
  return id && info.value?.models.some((m) => m.id === id) ? id : null
}

function message(caught: unknown): string {
  return caught instanceof ApiError && caught.status === 0
    ? t('errorNetwork')
    : caught instanceof Error
      ? caught.message
      : String(caught)
}

async function refresh(): Promise<void> {
  try {
    sketches.value = await getSketches()
    const shown = selected.value
    const summary = shown ? sketches.value.find((s) => s.id === shown.job.id) : undefined
    if (shown && !summary) {
      selected.value = null
    } else if (shown && summary && summary.stage !== shown.job.stage) {
      selected.value = await getSketch(summary.id)
    } else if (!shown && sketches.value[0]) {
      selected.value = await getSketch(sketches.value[0].id)
    }
  } catch (caught) {
    emit('error', message(caught))
  }
  schedule()
}

function schedule(): void {
  clearTimeout(poll)
  if (props.active && working.value) {
    poll = setTimeout(() => void refresh(), 4000)
  }
}

watch(
  () => props.active,
  (active) => {
    if (active) {
      getSketchInfo()
        .then((value) => (info.value = value))
        .catch(() => undefined)
      void refresh()
    } else {
      clearTimeout(poll)
    }
  },
  { immediate: true },
)
onBeforeUnmount(() => clearTimeout(poll))

async function pick(event: Event): Promise<void> {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  if (file) {
    text.value = await file.text()
  }
  input.value = ''
}

async function send(source = text.value, instruction = wishes.value, withModel = model.value): Promise<void> {
  if (!source.trim() || sending.value) {
    return
  }
  sending.value = true
  try {
    const queued = await planSketch(source, instruction.trim(), offered(withModel))
    sketches.value = [queued, ...sketches.value.filter((s) => s.id !== queued.id)]
    selected.value = await getSketch(queued.id)
    schedule()
  } catch (caught) {
    emit('error', message(caught))
  } finally {
    sending.value = false
  }
}

async function show(summary: SketchSummary): Promise<void> {
  try {
    selected.value = await getSketch(summary.id)
  } catch (caught) {
    emit('error', message(caught))
  }
}

function remove(result: SketchResult): void {
  confirm.require({
    header: t('confirmDelete'),
    message: t('sketchDeleteConfirm', { title: result.job.title || t('sketchUntitled') }),
    icon: 'pi pi-trash',
    rejectProps: { label: t('keep'), severity: 'secondary', outlined: true },
    acceptProps: { label: t('delete'), severity: 'danger' },
    accept: async () => {
      try {
        await deleteSketch(result.job.id)
        selected.value = null
        await refresh()
      } catch (caught) {
        emit('error', message(caught))
      }
    },
  })
}

function useSong(result: SketchResult): void {
  if (result.request) {
    emit('use', result.request)
    emit('notice', t('sketchApplied', { title: result.request.title || t('sketchUntitled') }))
  }
}

/** What ACE-Step can take of the plan (lyrics, style, tempo, key, metre, length) on its page; the melody stays here. */
function useAce(result: SketchResult): void {
  if (result.ace) {
    aceForm.value = fromAceRequest(result.ace, aceForm.value.machine)
    navigate('ace')
    emit('notice', t('sketchAceApplied', { title: result.ace.title || t('sketchUntitled') }))
  }
}

function toLogic(result: SketchResult): void {
  if (result.abc) {
    const name = `${result.job.title || 'score'}.abc`
    openMidiOn('logic', new File([result.abc], name, { type: 'text/plain' }))
  }
}

function stageSeverity(stage: SketchStage): string {
  return stage === 'done' ? 'success' : stage === 'failed' ? 'danger' : 'info'
}

function summaryLine(sketch: { bars?: number | null; seconds?: number | null }): string {
  return sketch.bars && sketch.seconds
    ? t('sketchSummary', { bars: sketch.bars, duration: formatDuration(sketch.seconds) })
    : ''
}

function warningText(warning: SketchWarning): string {
  return t(`sketchWarning_${warning.code}` as MessageKey, { bar: warning.bar ?? '', detail: warning.detail ?? '' })
}

function created(at: string): string {
  return new Date(at).toLocaleString(locale.value, { dateStyle: 'short', timeStyle: 'short' })
}

/** A section's chords bar by bar, the way the sketch writes them: `| Dm | Bbmaj7 | F C/E |`. */
function chordLine(chords: string[]): string {
  return `| ${chords.join(' | ')} |`
}
</script>

<template>
  <div class="flex flex-col gap-5">
    <Message v-if="info && !info.configured" severity="warn" role="note">{{
      t('sketchNoKey', { path: keyFile })
    }}</Message>
    <p v-else-if="info" class="muted m-0 text-sm">{{ t('sketchCloud') }}</p>

    <form class="flex flex-col gap-3" @submit.prevent="send()">
      <div class="flex flex-col gap-1">
        <div class="flex items-center justify-between gap-2">
          <label for="sketch-text" class="text-sm font-semibold">{{ t('sketchText') }}</label>
          <input ref="fileInput" type="file" accept=".md,.markdown,.txt,text/*" class="hidden" @change="pick" />
          <Button
            type="button"
            icon="pi pi-folder-open"
            :label="t('sketchFile')"
            severity="secondary"
            text
            size="small"
            @click="fileInput?.click()"
          />
        </div>
        <Textarea id="sketch-text" v-model="text" rows="8" fluid class="font-mono text-sm" />
      </div>
      <div class="flex flex-col gap-1">
        <label for="sketch-wishes" class="text-sm font-semibold">{{ t('sketchWishes') }}</label>
        <InputText id="sketch-wishes" v-model="wishes" :placeholder="t('sketchWishesPlaceholder')" fluid />
      </div>
      <div v-if="modelOptions.length > 1" class="flex flex-col gap-1">
        <label for="sketch-model" class="text-sm font-semibold">{{ t('sketchModel') }}</label>
        <Select
          v-model="model"
          input-id="sketch-model"
          :options="modelOptions"
          option-value="value"
          option-label="label"
          size="small"
          fluid
        />
      </div>
      <div>
        <Button
          type="submit"
          icon="pi pi-sparkles"
          :label="t('sketchPlan')"
          :loading="sending"
          :disabled="!text.trim() || info?.configured === false"
        />
      </div>
    </form>

    <section>
      <h3 class="mb-2 mt-0 text-base">{{ t('sketchList') }}</h3>
      <p v-if="sketches.length === 0" class="muted m-0">{{ t('sketchEmpty') }}</p>
      <ul class="m-0 flex list-none flex-col gap-1 p-0">
        <li v-for="sketch in sketches" :key="sketch.id">
          <button
            type="button"
            class="sketch-row"
            :class="{ chosen: selected?.job.id === sketch.id }"
            @click="show(sketch)"
          >
            <span class="min-w-0 flex-1 truncate font-semibold">{{ sketch.title || t('sketchUntitled') }}</span>
            <span class="muted shrink-0 text-sm">{{ summaryLine(sketch) || created(sketch.createdAt) }}</span>
            <Tag :value="t(`sketchStage_${sketch.stage}`)" :severity="stageSeverity(sketch.stage)" />
          </button>
        </li>
      </ul>
    </section>

    <section v-if="selected" class="flex flex-col gap-3">
      <div class="flex flex-wrap items-baseline gap-x-3 gap-y-1">
        <h3 class="m-0 text-lg">{{ selected.job.plan?.title || selected.job.title || t('sketchUntitled') }}</h3>
        <span v-if="selected.job.plan" class="muted text-sm">
          {{
            t('sketchFrame', {
              tempo: selected.job.plan.tempo,
              key: selected.job.plan.key,
              beats: selected.job.plan.beatsPerBar,
            })
          }}
          · {{ summaryLine(selected) }}
        </span>
      </div>

      <Message v-if="selected.job.stage === 'failed'" severity="error">{{ selected.job.message }}</Message>
      <p v-else-if="!selected.job.finished" class="muted m-0">
        <i class="pi pi-spin pi-spinner mr-2" />{{ t(`sketchStage_${selected.job.stage}`) }}
      </p>

      <template v-if="selected.job.plan && selected.request">
        <div class="flex flex-wrap gap-2">
          <Button icon="pi pi-sparkles" :label="t('sketchUseSong')" @click="useSong(selected)" />
          <Button
            v-if="selected.ace"
            icon="pi pi-sparkles"
            :label="t('sketchUseAce')"
            severity="secondary"
            outlined
            @click="useAce(selected)"
          />
          <a :href="sketchFileUrl(selected.job.id, 'midi')" download class="no-underline">
            <Button icon="pi pi-download" :label="t('sketchMidi')" severity="secondary" outlined tabindex="-1" />
          </a>
          <a :href="sketchFileUrl(selected.job.id, 'score')" download class="no-underline">
            <Button icon="pi pi-file" :label="t('sketchScore')" severity="secondary" outlined tabindex="-1" />
          </a>
          <Button icon="pi pi-box" :label="t('sketchLogic')" severity="secondary" outlined @click="toLogic(selected)" />
        </div>

        <div>
          <h4 class="mb-1 mt-0 text-sm">{{ t('sketchStyle') }}</h4>
          <p class="m-0">{{ selected.job.plan.style }}</p>
        </div>

        <div v-if="selected.job.plan.notes.length">
          <h4 class="mb-1 mt-0 text-sm">{{ t('sketchNotes') }}</h4>
          <ul class="m-0 pl-5">
            <li v-for="(note, index) in selected.job.plan.notes" :key="index">{{ note }}</li>
          </ul>
        </div>

        <div v-if="selected.warnings?.length">
          <h4 class="mb-1 mt-0 text-sm">{{ t('sketchWarnings') }}</h4>
          <ul class="muted m-0 pl-5 text-sm">
            <li v-for="(warning, index) in selected.warnings" :key="index">{{ warningText(warning) }}</li>
          </ul>
        </div>

        <div>
          <h4 class="mb-1 mt-0 text-sm">{{ t('sketchSections') }}</h4>
          <div class="flex flex-col gap-2">
            <div v-for="(section, index) in selected.job.plan.sections" :key="index" class="section">
              <div class="flex items-baseline justify-between gap-2">
                <span class="font-semibold">{{ section.name }}</span>
                <span class="muted text-xs">{{ section.bars.length }} · {{ section.parts.join(', ') }}</span>
              </div>
              <div class="chords font-mono text-xs">{{ chordLine(section.bars.map((b) => b.chords)) }}</div>
              <div v-if="section.lyrics.length" class="muted mt-1 text-sm whitespace-pre-line">
                {{ section.lyrics.join('\n') }}
              </div>
            </div>
          </div>
        </div>
      </template>

      <div class="flex flex-wrap gap-2">
        <Button
          v-if="selected.job.finished"
          icon="pi pi-refresh"
          :label="t('sketchAgain')"
          severity="secondary"
          text
          :disabled="info?.configured === false"
          @click="send(selected.job.source, selected.job.instruction ?? '', selected.job.model ?? model)"
        />
        <Button icon="pi pi-trash" :label="t('sketchDelete')" severity="danger" text @click="remove(selected)" />
      </div>
      <p v-if="selected.job.inputTokens && selected.job.outputTokens" class="muted m-0 text-xs">
        {{ selected.job.model }} ·
        {{ t('sketchTokens', { input: selected.job.inputTokens, output: selected.job.outputTokens }) }}
      </p>
    </section>
  </div>
</template>

<style scoped>
.sketch-row {
  display: flex;
  width: 100%;
  align-items: center;
  gap: 0.75rem;
  padding: 0.5rem 0.75rem;
  border: 1px solid var(--border);
  border-radius: 0.5rem;
  background: transparent;
  color: inherit;
  font: inherit;
  text-align: left;
  cursor: pointer;
}

.sketch-row.chosen {
  border-color: var(--accent);
}

.section {
  padding: 0.5rem 0.75rem;
  border-radius: 0.5rem;
  background: var(--surface-sunken);
}

/* Long chord rows wrap at the bar lines' spaces rather than widening the page. */
.chords {
  overflow-wrap: anywhere;
}
</style>
