<script setup lang="ts">
import Checkbox from 'primevue/checkbox'
import { computed, onBeforeUnmount, ref, useTemplateRef, watch } from 'vue'
import { addSwap, deleteSwap, listSwaps, listVoices, swapAudioUrl, swapSourceUrl } from '../api'
import { formatDateTime, formatDuration, t, versionProgress } from '../i18n'
import { playing as songPlaying, toggle as toggleSong } from '../player'
import type { ReferenceVoice, SwapState } from '../types'
import { machineField, ranOnNote, storedMachine } from '../machines'
import { octaveOptions, stepOptions, strengthOptions } from '../voiceChoices'
import MachinePicker from './MachinePicker.vue'

/**
 * An uploaded recording sung again with one of the reference voices: a bare vocal track straight through Seed-VC, a
 * whole song separated first and mixed back, like a version of a song. It waits in the voices' queue.
 */
const props = defineProps<{
  /** The page is open; the lists are only asked for then. */
  active: boolean
  /** Swaps in the works, and those finished since the page loaded, as the event stream reports them. */
  live: SwapState[]
  /** The server can separate, so a whole song can be uploaded. */
  canSeparate: boolean
}>()

const emit = defineEmits<{ error: [message: string]; notice: [message: string] }>()

const loaded = ref<SwapState[]>([])
const loadError = ref<string | null>(null)
const voices = ref<ReferenceVoice[]>([])
const voicesLoading = ref(false)
/** Deleted here; the server's `cancelled` event may come after the list was read again. */
const removed = ref(new Set<string>())

async function load(): Promise<void> {
  try {
    loaded.value = await listSwaps()
    loadError.value = null
  } catch (caught) {
    loadError.value = message(caught)
  }
}

/** Asked for each time the page opens: a voice added or deleted above should show here too. */
async function loadVoices(): Promise<void> {
  voicesLoading.value = true
  try {
    voices.value = await listVoices()
    if (!voices.value.some((v) => v.id === voice.value)) {
      voice.value = voices.value[0]?.id ?? null
    }
  } catch (caught) {
    emit('error', message(caught))
  } finally {
    voicesLoading.value = false
  }
}

watch(
  () => props.active,
  (active) => {
    if (active) {
      void load()
      void loadVoices()
    }
  },
  { immediate: true },
)

/** The live state wins: it is at least as new as the list. */
const swaps = computed(() => {
  const byId = new Map(loaded.value.map((swap) => [swap.id, swap]))
  for (const swap of props.live) {
    byId.set(swap.id, swap)
  }
  return [...byId.values()]
    .filter((swap) => swap.stage !== 'cancelled' && !removed.value.has(swap.id))
    .sort((a, b) => b.createdAt.localeCompare(a.createdAt))
})

// ---- Asking for a swap ------------------------------------------------------------------------------

const fileInput = useTemplateRef<HTMLInputElement>('fileInput')
const file = ref<File | null>(null)
const voice = ref<string | null>(null)
/** A bare vocal track is the default: it needs no separation and keeps exactly what was recorded. */
const whole = ref(false)
const octave = ref(0)
const strength = ref(0.7)
const steps = ref(50)
const keepReverb = ref(true)
const sending = ref(false)
/** The Mac that converts (and separates a whole song); shared with the library's voice dialog. */
const machine = storedMachine('voices')

const kinds = computed(() => [
  { value: false, label: t('swapKindVocals') },
  { value: true, label: t('swapKindSong'), disabled: !props.canSeparate },
])
const octaves = computed(octaveOptions)
const strengths = computed(strengthOptions)
const stepChoices = computed(stepOptions)

function chosen(event: Event): void {
  file.value = (event.target as HTMLInputElement).files?.[0] ?? null
}

async function send(): Promise<void> {
  if (!file.value || !voice.value || sending.value) {
    return
  }
  sending.value = true
  try {
    const swap = await addSwap(file.value, {
      voiceId: voice.value,
      semiToneShift: octave.value,
      strength: strength.value,
      diffusionSteps: steps.value,
      separate: whole.value && props.canSeparate,
      keepReverb: keepReverb.value,
      machine: machineField(machine.value),
    })
    loaded.value = [swap, ...loaded.value.filter((s) => s.id !== swap.id)]
    file.value = null
    if (fileInput.value) {
      fileInput.value.value = ''
    }
    emit('notice', t('swapQueued', { file: swap.fileName, voice: swap.voiceLabel }))
  } catch (caught) {
    emit('error', message(caught))
  } finally {
    sending.value = false
  }
}

async function remove(swap: SwapState): Promise<void> {
  const question = swap.finished ? 'swapDeleteConfirm' : 'swapCancelConfirm'
  if (!window.confirm(t(question, { file: swap.fileName, voice: swap.voiceLabel }))) {
    return
  }
  try {
    if (listening.value?.swap === swap.id) {
      stop()
    }
    await deleteSwap(swap.id)
    removed.value = new Set([...removed.value, swap.id])
  } catch (caught) {
    emit('error', message(caught))
  }
}

// ---- Listening --------------------------------------------------------------------------------------

type Which = 'result' | 'original'

/**
 * One audio element apart from the song player, as for the stems. Switching between result and original keeps the
 * time, so both can be compared at the same line.
 */
const audio = new Audio()
audio.preload = 'none'
const listening = ref<{ swap: string; which: Which } | null>(null)
const position = ref(0)
const duration = ref(0)
const paused = ref(true)

audio.addEventListener('pause', () => (paused.value = true))
audio.addEventListener('play', () => (paused.value = false))
audio.addEventListener('ended', () => (listening.value = null))
audio.addEventListener('timeupdate', () => (position.value = audio.currentTime))
audio.addEventListener('loadedmetadata', () => (duration.value = Number.isFinite(audio.duration) ? audio.duration : 0))
onBeforeUnmount(stop)

function isPlaying(swap: SwapState, which: Which): boolean {
  return listening.value?.swap === swap.id && listening.value.which === which && !paused.value
}

/** Inside the click, as iOS wants it. */
function toggle(swap: SwapState, which: Which): void {
  if (listening.value?.swap === swap.id && listening.value.which === which) {
    if (audio.paused) {
      void audio.play().catch(() => undefined)
    } else {
      audio.pause()
    }
    return
  }
  // Two songs at once help nobody.
  if (songPlaying.value) {
    toggleSong()
  }
  const at = listening.value?.swap === swap.id ? audio.currentTime : 0
  listening.value = { swap: swap.id, which }
  audio.src = which === 'result' ? swapAudioUrl(swap.id) : swapSourceUrl(swap.id)
  audio.currentTime = at
  position.value = at
  void audio.play().catch(() => (listening.value = null))
}

function stop(): void {
  audio.pause()
  listening.value = null
}

// ---- Labels -----------------------------------------------------------------------------------------

/** What it was made with, in the form's words, so swaps of one file can be told apart. */
function settings(swap: SwapState): string {
  const octave = octaves.value.find((o) => o.value === swap.semiToneShift)
  return [
    swap.separate ? t('swapKindSong') : t('swapKindVocals'),
    octave?.label ?? t('semitones', { n: swap.semiToneShift }),
    t('settingStrength', { value: strengths.value.find((s) => s.value === swap.strength)?.label ?? swap.strength }),
    t('settingSteps', {
      value: stepChoices.value.find((s) => s.value === swap.diffusionSteps)?.label ?? swap.diffusionSteps,
    }),
    ...(swap.separate ? [swap.keepReverb ? t('withReverb') : t('withoutReverb')] : []),
    formatDateTime(swap.createdAt),
    ...(ranOnNote(swap.ranOn) ? [ranOnNote(swap.ranOn)!] : []),
  ].join(' · ')
}

function message(caught: unknown): string {
  return caught instanceof Error ? caught.message : String(caught)
}

const stageSeverity: Record<string, string | undefined> = { failed: 'danger' }
</script>

<template>
  <section class="flex flex-col gap-4">
    <p class="muted m-0 text-sm">{{ t('swapIntro') }}</p>

    <form class="flex flex-col gap-3" @submit.prevent="send">
      <div class="flex flex-wrap items-end gap-3">
        <div class="flex min-w-0 flex-[1_1_16rem] flex-col gap-1">
          <label for="swap-file" class="muted text-sm">{{ t('swapFile') }}</label>
          <!-- A native file input, as for the voices: PrimeVue's FileUpload brings its own upload flow. -->
          <input
            id="swap-file"
            ref="fileInput"
            class="max-w-full text-sm"
            type="file"
            accept="audio/*,.wav,.mp3,.flac,.m4a,.aac,.ogg,.opus"
            required
            :disabled="sending"
            @change="chosen"
          />
        </div>
        <div class="flex min-w-0 flex-[1_1_12rem] flex-col gap-1">
          <label for="swap-voice" class="muted text-sm">{{ t('voice') }}</label>
          <Select
            v-model="voice"
            input-id="swap-voice"
            :options="voices"
            option-label="label"
            option-value="id"
            :loading="voicesLoading"
            :placeholder="voices.length === 0 && !voicesLoading ? t('voicesEmpty') : undefined"
            fluid
          />
        </div>
      </div>
      <div class="flex flex-wrap gap-x-4 gap-y-3">
        <div class="flex flex-col gap-1">
          <span class="muted text-sm">{{ t('swapKind') }}</span>
          <SelectButton
            v-model="whole"
            :options="kinds"
            option-label="label"
            option-value="value"
            option-disabled="disabled"
            :allow-empty="false"
            :aria-label="t('swapKind')"
          />
        </div>
        <div class="flex flex-col gap-1">
          <span class="muted text-sm">{{ t('octave') }}</span>
          <SelectButton
            v-model="octave"
            :options="octaves"
            option-label="label"
            option-value="value"
            :allow-empty="false"
            :aria-label="t('octave')"
          />
        </div>
        <div class="flex flex-col gap-1">
          <span class="muted text-sm">{{ t('strength') }}</span>
          <SelectButton
            v-model="strength"
            :options="strengths"
            option-label="label"
            option-value="value"
            :allow-empty="false"
            :aria-label="t('strength')"
          />
        </div>
        <div class="flex flex-col gap-1">
          <span class="muted text-sm">{{ t('steps') }}</span>
          <SelectButton
            v-model="steps"
            :options="stepChoices"
            option-label="label"
            option-value="value"
            :allow-empty="false"
            :aria-label="t('steps')"
          />
        </div>
      </div>
      <p v-if="!canSeparate" class="muted m-0 text-sm">{{ t('swapNoSeparator') }}</p>
      <div class="flex flex-wrap items-center justify-between gap-3">
        <div v-if="whole" class="flex items-center gap-2">
          <Checkbox v-model="keepReverb" input-id="swap-reverb" binary />
          <label for="swap-reverb" class="text-sm">{{ t('keepReverb') }}</label>
        </div>
        <MachinePicker v-model="machine" engine="voices" :disabled="sending" />
        <Button
          type="submit"
          class="ml-auto"
          :label="t('swapStart')"
          icon="pi pi-user-edit"
          :loading="sending"
          :disabled="!file || !voice || sending"
        />
      </div>
    </form>

    <p v-if="loadError" class="danger m-0">{{ t('swapError', { message: loadError }) }}</p>
    <p v-else-if="swaps.length === 0" class="muted m-0">{{ t('swapEmpty') }}</p>

    <ul class="m-0 flex list-none flex-col gap-3 p-0">
      <li v-for="swap in swaps" :key="swap.id" class="flex flex-col gap-2 border-t border-surface pt-3">
        <div class="flex items-center gap-x-3">
          <div class="min-w-0 flex-1">
            <div class="truncate font-semibold">{{ swap.fileName }} · {{ swap.voiceLabel }}</div>
            <div class="muted text-sm">{{ settings(swap) }}</div>
            <Tag v-if="swap.stage !== 'done'" :severity="stageSeverity[swap.stage]" class="mt-1">
              <i v-if="!swap.finished && swap.stage !== 'queued'" class="pi pi-spin pi-spinner text-xs" />
              {{ t(`versionStage_${swap.stage}`) }} {{ versionProgress(swap) }}
            </Tag>
          </div>
          <Button
            icon="pi pi-trash"
            text
            rounded
            size="small"
            severity="danger"
            class="shrink-0"
            v-tooltip="swap.finished ? t('swapDelete') : t('swapCancel')"
            :aria-label="swap.finished ? t('swapDelete') : t('swapCancel')"
            @click="remove(swap)"
          />
        </div>
        <p v-if="swap.stage === 'failed' && swap.message" class="danger m-0 text-sm">{{ swap.message }}</p>
        <div v-if="swap.stage === 'done'" class="flex flex-wrap items-center gap-2">
          <Button
            :icon="isPlaying(swap, 'result') ? 'pi pi-pause' : 'pi pi-play'"
            :label="t('swapResult')"
            size="small"
            :outlined="listening?.swap !== swap.id || listening.which !== 'result'"
            :aria-label="isPlaying(swap, 'result') ? t('pause') : t('swapListen', { which: t('swapResult') })"
            @click="toggle(swap, 'result')"
          />
          <Button
            :icon="isPlaying(swap, 'original') ? 'pi pi-pause' : 'pi pi-play'"
            :label="t('swapOriginal')"
            size="small"
            severity="secondary"
            :outlined="listening?.swap !== swap.id || listening.which !== 'original'"
            :aria-label="isPlaying(swap, 'original') ? t('pause') : t('swapListen', { which: t('swapOriginal') })"
            @click="toggle(swap, 'original')"
          />
          <span v-if="listening?.swap === swap.id" class="muted text-sm tabular-nums">
            {{ formatDuration(position) }}<template v-if="duration > 0"> / {{ formatDuration(duration) }}</template>
          </span>
          <Button
            as="a"
            :href="swapAudioUrl(swap.id, true)"
            icon="pi pi-download"
            text
            rounded
            size="small"
            class="ml-auto"
            v-tooltip="t('swapDownload')"
            :aria-label="t('swapDownload')"
          />
        </div>
      </li>
    </ul>
  </section>
</template>
