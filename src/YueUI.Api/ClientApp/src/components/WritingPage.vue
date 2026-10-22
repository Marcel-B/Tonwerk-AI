<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import Message from 'primevue/message'
import Select from 'primevue/select'
import { useConfirm } from 'primevue/useconfirm'
import {
  ApiError,
  createWritingChat,
  deleteWriting,
  getLyricsModels,
  getWriting,
  getWritingChat,
  releaseWriting,
  renameWriting,
  retryWriting,
  sendWriting,
  stopWriting,
} from '../api'
import { copyText } from '../clipboard'
import { t, type MessageKey } from '../i18n'
import { liveWriting } from '../writing'
import type { LyricsModel, LyricsReasoning, WritingChat, WritingOverview, WritingReply } from '../types'

/**
 * Conversations with a model in LM Studio for ideas, rhymes, variants and lyrics. The server keeps the conversations
 * and writes one answer at a time; the answer grows in `writing` events (`liveWriting`), the whole text so far each
 * time, so a reload or a locked phone picks it up again.
 */
const props = defineProps<{ active: boolean }>()
const emit = defineEmits<{ use: [lyrics: string]; error: [message: string] }>()

const confirm = useConfirm()
const overview = ref<WritingOverview | null>(null)
const chat = ref<WritingChat | null>(null)
const models = ref<LyricsModel[]>([])
const text = ref('')
const sending = ref(false)
const renaming = ref(false)
const title = ref('')
const field = ref<{ $el: HTMLTextAreaElement } | null>(null)

/** The model chosen on this browser; empty for the chat's own or the server's default. */
const modelKey = 'yue-ui.writingModel'
const chosen = ref(readModel())
const model = computed({
  get: () => {
    if (models.value.some((m) => m.id === chosen.value)) return chosen.value
    const own = chat.value?.model
    if (own && models.value.some((m) => m.id === own)) return own
    return overview.value?.model ?? chosen.value
  },
  set: (value: string) => {
    chosen.value = value
    try {
      localStorage.setItem(modelKey, value)
    } catch {
      // Only a convenience.
    }
  },
})
/** How much a thinking model thinks first, per browser; empty leaves it to LM Studio. */
const reasoningKey = 'yue-ui.writingReasoning'
const reasoningValues: LyricsReasoning[] = ['off', 'low', 'medium', 'high', 'on']
const reasoning = ref<LyricsReasoning | ''>(readReasoning())
watch(reasoning, (value) => {
  try {
    localStorage.setItem(reasoningKey, value)
  } catch {
    // Only a convenience.
  }
})
const reasoningOptions = computed(() => [
  { value: '', label: t('lyricsReasoningDefault') },
  { value: 'off', label: t('lyricsReasoningOff') },
  { value: 'low', label: t('lyricsReasoningLow') },
  { value: 'medium', label: t('lyricsReasoningMedium') },
  { value: 'high', label: t('lyricsReasoningHigh') },
  { value: 'on', label: t('lyricsReasoningOn') },
])

function readReasoning(): LyricsReasoning | '' {
  try {
    const stored = localStorage.getItem(reasoningKey) as LyricsReasoning | null
    return stored && reasoningValues.includes(stored) ? stored : ''
  } catch {
    return ''
  }
}

const modelOptions = computed(() =>
  models.value.map((m) => {
    const name = m.role ? `${m.name} · ${t(`lyricsModelRole_${m.role}`)}` : m.name
    return { value: m.id, label: m.id === overview.value?.model ? t('writingModelDefault', { name }) : name }
  }),
)
const chatOptions = computed(() =>
  (overview.value?.chats ?? []).map((c) => ({ value: c.id, label: c.title || t('writingUntitled') })),
)
const chatId = computed({
  get: () => chat.value?.id ?? null,
  set: (id: string | null) => {
    if (id) void open(id)
  },
})

/** The answer being written in whichever chat. */
const anyReply = computed(() => liveWriting.value ?? overview.value?.reply ?? null)
const replying = computed(() => !!anyReply.value && !anyReply.value.finished)
/** The answer of the open chat, while it is being written or when it failed. */
const reply = computed(() => {
  const value = anyReply.value
  return value && value.chatId === chat.value?.id && (!value.finished || value.stage === 'failed') ? value : null
})
const messages = computed(() => chat.value?.messages ?? [])
const lastAnswer = computed(() => {
  const last = messages.value.at(-1)
  return last?.role === 'assistant' ? messages.value.length - 1 : -1
})

const starters = ['titles', 'rhymes', 'variants', 'images', 'lyrics'] as const

function readModel(): string {
  try {
    return localStorage.getItem(modelKey) ?? ''
  } catch {
    return ''
  }
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
    overview.value = await getWriting()
    const id = chat.value?.id ?? overview.value.chats[0]?.id
    chat.value = id ? await getWritingChat(id) : null
  } catch (caught) {
    emit('error', message(caught))
  }
}

watch(
  () => props.active,
  (active) => {
    if (!active) return
    // LM Studio's models only: Claude has no part here, and the large ones need a large Mac.
    getLyricsModels()
      .then((listed) => (models.value = listed.models.filter((m) => !m.cloud && !m.needsGb)))
      .catch(() => undefined)
    void refresh()
  },
  { immediate: true },
)

// A finished answer is in the chat now; the event carried only its text.
watch(liveWriting, (value, before) => {
  if (value?.finished && !before?.finished && props.active) {
    void refresh()
  }
})

async function open(id: string): Promise<void> {
  try {
    chat.value = await getWritingChat(id)
    renaming.value = false
  } catch (caught) {
    emit('error', message(caught))
  }
}

async function newChat(): Promise<void> {
  try {
    chat.value = await createWritingChat()
    overview.value = await getWriting()
    renaming.value = false
  } catch (caught) {
    emit('error', message(caught))
  }
}

function chosenModel(): string | null {
  return model.value || null
}

async function send(): Promise<void> {
  const value = text.value.trim()
  if (!value || sending.value || replying.value) return
  sending.value = true
  try {
    if (!chat.value) {
      chat.value = await createWritingChat()
    }
    const id = chat.value.id
    liveWriting.value = await sendWriting(id, value, chosenModel(), reasoning.value || null)
    text.value = ''
    chat.value = await getWritingChat(id)
    overview.value = await getWriting()
  } catch (caught) {
    emit('error', message(caught))
  } finally {
    sending.value = false
  }
}

async function retry(): Promise<void> {
  const id = chat.value?.id
  if (!id || replying.value) return
  try {
    liveWriting.value = await retryWriting(id, chosenModel(), reasoning.value || null)
    chat.value = await getWritingChat(id)
  } catch (caught) {
    emit('error', message(caught))
  }
}

async function stop(): Promise<void> {
  const id = anyReply.value?.chatId
  if (id) {
    await stopWriting(id).catch((caught) => emit('error', message(caught)))
  }
}

function keydown(event: KeyboardEvent): void {
  if (event.key === 'Enter' && (event.metaKey || event.ctrlKey)) {
    event.preventDefault()
    void send()
  }
}

async function start(starter: (typeof starters)[number]): Promise<void> {
  text.value = t(`writingStarter_${starter}Text` as MessageKey)
  await nextTick()
  const element = field.value?.$el
  if (element) {
    element.focus()
    // Inside the quotes for the rhymes, else at the end.
    const quote = text.value.indexOf('“')
    const at = starter === 'rhymes' && quote >= 0 ? quote : text.value.length
    element.setSelectionRange(at, at)
  }
}

function beginRename(): void {
  title.value = chat.value?.title ?? ''
  renaming.value = true
}

async function rename(): Promise<void> {
  const id = chat.value?.id
  if (!id || !title.value.trim()) return
  try {
    chat.value = await renameWriting(id, title.value.trim())
    overview.value = await getWriting()
    renaming.value = false
  } catch (caught) {
    emit('error', message(caught))
  }
}

function remove(): void {
  const current = chat.value
  if (!current) return
  confirm.require({
    header: t('confirmDelete'),
    message: t('writingDeleteConfirm', { title: current.title || t('writingUntitled') }),
    icon: 'pi pi-trash',
    rejectProps: { label: t('keep'), severity: 'secondary', outlined: true },
    acceptProps: { label: t('delete'), severity: 'danger' },
    accept: async () => {
      try {
        await deleteWriting(current.id)
        chat.value = null
        await refresh()
      } catch (caught) {
        emit('error', message(caught))
      }
    },
  })
}

async function release(): Promise<void> {
  try {
    await releaseWriting()
    overview.value = await getWriting()
  } catch (caught) {
    emit('error', message(caught))
  }
}

async function copy(value: string): Promise<void> {
  await copyText(value)
}

function stageText(value: WritingReply): string {
  if (value.stage === 'failed') return value.message || t('writingStage_failed')
  if (value.stage === 'loading') return t('writingStage_loading', { model: value.model })
  if (value.stage === 'writing') return t(value.thinking ? 'writingStage_thinking' : 'writingStage_writing')
  return value.stage === 'waiting' ? t('writingStage_waiting') : ''
}
</script>

<template>
  <div class="flex flex-col gap-4">
    <div class="flex flex-wrap items-end gap-2">
      <div v-if="chatOptions.length" class="flex min-w-48 flex-1 flex-col gap-1">
        <label for="writing-chat" class="text-sm font-semibold">{{ t('writingChat') }}</label>
        <Select
          v-model="chatId"
          input-id="writing-chat"
          :options="chatOptions"
          option-value="value"
          option-label="label"
          size="small"
          fluid
        />
      </div>
      <div v-if="modelOptions.length > 1" class="flex min-w-48 flex-1 flex-col gap-1">
        <label for="writing-model" class="text-sm font-semibold">{{ t('writingModel') }}</label>
        <Select
          v-model="model"
          input-id="writing-model"
          :options="modelOptions"
          option-value="value"
          option-label="label"
          size="small"
          fluid
        />
      </div>
      <div v-if="models.length" class="flex min-w-40 flex-col gap-1">
        <label for="writing-reasoning" class="text-sm font-semibold">{{ t('writingReasoning') }}</label>
        <Select
          v-model="reasoning"
          input-id="writing-reasoning"
          :options="reasoningOptions"
          option-value="value"
          option-label="label"
          size="small"
          fluid
        />
      </div>
      <Button
        icon="pi pi-plus"
        :label="t('writingNewChat')"
        severity="secondary"
        outlined
        size="small"
        @click="newChat"
      />
    </div>

    <div v-if="chat" class="flex flex-wrap items-center gap-2">
      <form v-if="renaming" class="flex flex-1 gap-2" @submit.prevent="rename">
        <InputText v-model="title" size="small" fluid :aria-label="t('writingRename')" />
        <Button type="submit" icon="pi pi-check" size="small" :aria-label="t('writingRename')" />
        <Button
          type="button"
          icon="pi pi-times"
          size="small"
          severity="secondary"
          text
          :aria-label="t('cancel')"
          @click="renaming = false"
        />
      </form>
      <template v-else>
        <Button
          icon="pi pi-pen-to-square"
          :label="t('writingRename')"
          severity="secondary"
          text
          size="small"
          @click="beginRename"
        />
        <Button icon="pi pi-trash" :label="t('writingDelete')" severity="danger" text size="small" @click="remove" />
      </template>
      <Button
        v-if="overview?.loaded && !replying"
        icon="pi pi-eject"
        :label="t('writingRelease', { model: overview.loaded })"
        severity="secondary"
        text
        size="small"
        @click="release"
      />
    </div>

    <Message v-if="models.length === 0 && overview" severity="warn" role="note">{{ t('writingNoModels') }}</Message>

    <div class="flex flex-col gap-3">
      <div v-for="(entry, index) in messages" :key="index" class="bubble" :class="entry.role">
        <div class="whitespace-pre-wrap break-words">{{ entry.text }}</div>
        <div v-if="entry.role === 'assistant'" class="mt-1 flex flex-wrap items-center gap-1">
          <span class="muted mr-auto text-xs">
            {{ entry.model }}<template v-if="entry.stopped"> · {{ t('writingStopped') }}</template>
          </span>
          <Button
            icon="pi pi-copy"
            :aria-label="t('writingCopy')"
            severity="secondary"
            text
            size="small"
            @click="copy(entry.text)"
          />
          <Button
            icon="pi pi-file-edit"
            :label="t('writingUse')"
            severity="secondary"
            text
            size="small"
            @click="emit('use', entry.text)"
          />
          <Button
            v-if="index === lastAnswer && !replying"
            icon="pi pi-refresh"
            :aria-label="t('writingRetry')"
            severity="secondary"
            text
            size="small"
            @click="retry"
          />
        </div>
      </div>

      <div v-if="reply" class="bubble assistant">
        <div v-if="reply.text" class="whitespace-pre-wrap break-words">{{ reply.text }}</div>
        <Message v-if="reply.stage === 'failed'" severity="error" class="mt-1">{{ stageText(reply) }}</Message>
        <p v-else class="muted m-0 mt-1 text-sm"><i class="pi pi-spin pi-spinner mr-2" />{{ stageText(reply) }}</p>
      </div>
    </div>

    <div v-if="messages.length === 0 && !reply" class="flex flex-col gap-2">
      <p class="muted m-0 text-sm">{{ t('writingEmpty') }}</p>
      <div class="flex flex-wrap gap-2">
        <Button
          v-for="starter in starters"
          :key="starter"
          :label="t(`writingStarter_${starter}`)"
          severity="secondary"
          outlined
          rounded
          size="small"
          @click="start(starter)"
        />
      </div>
    </div>

    <form class="flex flex-col gap-2" @submit.prevent="send">
      <Textarea
        ref="field"
        v-model="text"
        rows="3"
        auto-resize
        fluid
        :placeholder="t('writingPlaceholder')"
        @keydown="keydown"
      />
      <div class="flex gap-2">
        <Button
          v-if="replying"
          type="button"
          icon="pi pi-stop"
          :label="t('writingStop')"
          severity="secondary"
          @click="stop"
        />
        <Button
          v-else
          type="submit"
          icon="pi pi-send"
          :label="t('writingSend')"
          :loading="sending"
          :disabled="!text.trim() || models.length === 0"
        />
      </div>
    </form>
  </div>
</template>

<style scoped>
.bubble {
  border-radius: 0.75rem;
  padding: 0.6rem 0.8rem;
  max-width: 92%;
}
.bubble.user {
  align-self: flex-end;
  background: var(--accent);
  color: var(--p-primary-contrast-color);
}
.bubble.assistant {
  align-self: flex-start;
  background: var(--surface-sunken);
}
</style>
