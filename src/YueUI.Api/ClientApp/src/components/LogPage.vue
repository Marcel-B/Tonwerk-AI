<script setup lang="ts">
import Message from 'primevue/message'
import { computed, ref, watch } from 'vue'
import { getLogReport, getLogs, type LogFilter } from '../api'
import { copyPending } from '../clipboard'
import { locale, t, type MessageKey } from '../i18n'
import { logSources, type LogLevel, type LogLine, type LogSource } from '../types'

/**
 * The collected log: the server's messages, the worker's output and the updater's log, filtered by source, level and
 * text. Its button copies a report for a chat, so a problem can be looked into without SSH on the Mac.
 */
const props = defineProps<{
  /** Loaded when the page is shown; the log does not stream, so coming back reloads it. */
  active: boolean
}>()

const sourceLabels: Record<LogSource, MessageKey> = {
  server: 'logsSourceServer',
  worker: 'logsSourceWorker',
  queue: 'logsSourceQueue',
  lyrics: 'logsSourceLyrics',
  voices: 'logsSourceVoices',
  speech: 'logsSourceSpeech',
  singing: 'logsSourceSinging',
  images: 'logsSourceImages',
  ace: 'logsSourceAce',
  video: 'logsSourceVideo',
  export: 'logsSourceExport',
  logic: 'logsSourceLogic',
  backup: 'logsSourceBackup',
  push: 'logsSourcePush',
  update: 'logsSourceUpdate',
}

const sourceOptions = computed(() => [
  { value: 'all', label: t('logsAllSources') },
  ...logSources.map((source) => ({ value: source, label: t(sourceLabels[source]) })),
])
const levelOptions = computed(() => [
  { value: 'all', label: t('logsLevelAll') },
  { value: 'warning', label: t('logsLevelWarning') },
  { value: 'error', label: t('logsLevelError') },
])

// 'all' rather than null: Select shows a null value as nothing chosen.
const source = ref<LogSource | 'all'>('all')
// Warnings and errors first: what is looked for here is nearly always a problem.
const level = ref<LogLevel | 'all'>('warning')
const text = ref('')

const lines = ref<LogLine[]>([])
const more = ref(false)
const loading = ref(false)
const error = ref<string | null>(null)
const expanded = ref(new Set<string>())

function filter(before?: string): LogFilter {
  return {
    source: source.value === 'all' ? null : source.value,
    level: level.value === 'all' ? null : level.value,
    text: text.value,
    before,
  }
}

/** Counts loads, so that an answer to an older filter does not overwrite a newer one. */
let generation = 0

async function load(older = false): Promise<void> {
  const mine = ++generation
  loading.value = true
  try {
    const page = await getLogs(filter(older ? lines.value.at(-1)?.time : undefined))
    if (mine !== generation) {
      return
    }
    lines.value = older ? [...lines.value, ...page.entries] : page.entries
    more.value = page.more
    error.value = null
    if (!older) {
      expanded.value = new Set()
    }
  } catch (caught) {
    if (mine === generation) {
      error.value = caught instanceof Error ? caught.message : String(caught)
    }
  } finally {
    if (mine === generation) {
      loading.value = false
    }
  }
}

watch(
  () => props.active,
  (active) => {
    if (active) {
      void load()
    }
  },
  { immediate: true },
)
watch([source, level], () => void load())
let typing: ReturnType<typeof setTimeout> | undefined
watch(text, () => {
  clearTimeout(typing)
  typing = setTimeout(() => void load(), 300)
})

function key(line: LogLine, index: number): string {
  return `${line.time}-${index}`
}

/** A message over several lines, or with an exception, shows its first line until opened. */
function hasDetails(line: LogLine): boolean {
  return line.message.includes('\n') || !!line.exception
}

function toggle(id: string): void {
  const next = new Set(expanded.value)
  if (!next.delete(id)) {
    next.add(id)
  }
  expanded.value = next
}

function firstLine(message: string): string {
  return message.split('\n', 1)[0] ?? ''
}

function stamp(iso: string): string {
  const date = new Date(iso)
  return Number.isNaN(date.getTime())
    ? iso
    : date.toLocaleString(locale.value, { dateStyle: 'short', timeStyle: 'medium' })
}

function severity(value: LogLevel): string {
  return value === 'error' ? 'danger' : value === 'warning' ? 'warn' : 'secondary'
}

const copied = ref(false)
const copying = ref(false)
/** The report, shown to copy by hand when the clipboard refused it. */
const manualReport = ref<string | null>(null)

async function copyReport(): Promise<void> {
  copying.value = true
  copied.value = false
  manualReport.value = null
  const report = getLogReport()
  try {
    if (await copyPending(report)) {
      copied.value = true
      setTimeout(() => (copied.value = false), 6000)
    } else {
      manualReport.value = await report
    }
  } catch (caught) {
    error.value = caught instanceof Error ? caught.message : String(caught)
  } finally {
    copying.value = false
  }
}
</script>

<template>
  <section class="flex flex-col gap-4">
    <div class="flex flex-wrap items-start justify-between gap-3">
      <div class="min-w-0">
        <h2 class="mt-0 mb-1">{{ t('menuLogs') }}</h2>
        <p class="muted m-0 text-sm">{{ t('logsIntro') }}</p>
      </div>
      <Button :label="t('logsCopyReport')" icon="pi pi-copy" :loading="copying" @click="copyReport" />
    </div>

    <Message v-if="copied" severity="success" :closable="false">{{ t('logsReportCopied') }}</Message>
    <div v-if="manualReport !== null" class="flex flex-col gap-2">
      <span class="text-sm">{{ t('logsReportManual') }}</span>
      <Textarea
        :model-value="manualReport"
        readonly
        rows="10"
        fluid
        class="report"
        @focus="($event.target as HTMLTextAreaElement).select()"
      />
    </div>

    <!-- On a phone the two choices share a row and the search gets the next one; on a wider screen all in one. -->
    <div class="grid grid-cols-2 gap-2 sm:flex">
      <Select
        v-model="source"
        :options="sourceOptions"
        option-label="label"
        option-value="value"
        :aria-label="t('logsSource')"
        class="min-w-0 sm:min-w-48"
      />
      <Select
        v-model="level"
        :options="levelOptions"
        option-label="label"
        option-value="value"
        :aria-label="t('logsLevel')"
        class="min-w-0 sm:min-w-48"
      />
      <div class="col-span-2 flex flex-1 gap-2">
        <InputText
          v-model="text"
          type="search"
          :placeholder="t('logsSearch')"
          :aria-label="t('logsSearch')"
          class="min-w-0 flex-1"
        />
        <Button
          icon="pi pi-refresh"
          severity="secondary"
          text
          :loading="loading"
          :aria-label="t('logsRefresh')"
          v-tooltip.bottom="t('logsRefresh')"
          @click="load()"
        />
      </div>
    </div>

    <p v-if="error" class="danger m-0">{{ error }}</p>
    <p v-else-if="!loading && lines.length === 0" class="muted m-0">{{ t('logsEmpty') }}</p>

    <ol class="m-0 flex list-none flex-col p-0">
      <li v-for="(line, index) in lines" :key="key(line, index)" class="line">
        <div class="flex flex-wrap items-baseline gap-x-2 gap-y-1">
          <span class="muted text-xs tabular-nums">{{ stamp(line.time) }}</span>
          <Tag :value="t(sourceLabels[line.source] ?? 'logsSourceServer')" severity="secondary" class="text-xs" />
          <Tag v-if="line.level !== 'info'" :value="line.level" :severity="severity(line.level)" class="text-xs" />
        </div>
        <pre v-if="expanded.has(key(line, index))" class="message"
          >{{ line.message }}{{ line.exception ? '\n' + line.exception : '' }}</pre>
        <div v-else class="message">{{ firstLine(line.message) }}</div>
        <Button
          v-if="hasDetails(line)"
          :label="t('logsDetails')"
          :icon="expanded.has(key(line, index)) ? 'pi pi-chevron-up' : 'pi pi-chevron-down'"
          size="small"
          text
          class="self-start"
          @click="toggle(key(line, index))"
        />
      </li>
    </ol>

    <Button
      v-if="more"
      :label="t('logsOlder')"
      severity="secondary"
      outlined
      :loading="loading"
      class="self-center"
      @click="load(true)"
    />
  </section>
</template>

<style scoped>
.line {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
  padding: 0.6rem 0;
  border-bottom: 1px solid var(--border);
}

.message {
  margin: 0;
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
  font-size: 0.8125rem;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.report :deep(textarea),
.report {
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
  font-size: 0.8125rem;
}
</style>
