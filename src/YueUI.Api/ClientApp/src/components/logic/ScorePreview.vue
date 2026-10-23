<script lang="ts">
/** Which tracks are muted and soloed, by name. */
export interface Listening {
  muted: string[]
  solo: string[]
}
</script>

<script setup lang="ts">
import Checkbox from 'primevue/checkbox'
import Panel from 'primevue/panel'
import Slider from 'primevue/slider'
import { computed, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import { t } from '../../logic/i18n'
import { drumTestNote, effectiveRouting, instrumentOf } from '../../logic/instruments'
import { loadRecordingSettings, loadRoutings, saveRecordingSettings, saveRoutings } from '../../logic/options'
import { decodeRecording, RecordingPlayer, type Recording } from '../../logic/recording'
import {
  contentHeight,
  draw,
  GUTTER_WIDTH,
  lanesOf,
  ticksAtX,
  totalWidth,
  trackColour,
  WAVE_HEIGHT,
  type Waveform,
  xAtTicks,
} from '../../logic/pianoRoll'
import {
  AUDIO_OUTPUT,
  createPlayer,
  defaultRoutings,
  listMidiPorts,
  midiAlreadyAllowed,
  midiSupported,
  midiUsable,
  onMidiPortsChanged,
  OutputPool,
  rescanMidi,
  scheduleOf,
  testTone,
  type MidiPort,
  type Player,
  type Routing,
} from '../../logic/midiPlayer'
import { playableVoices } from '../../logic/score'
import { clonePatch, normalizePatch, type SynthPatch } from '../../logic/synth'
import {
  loadSounds,
  mixer,
  namedSounds,
  resetTrackSound,
  setTrackSound,
  trackKey,
  trackMix,
  trackSounds,
} from '../../logic/synths'
import type { Assignments, Instrument, ScoreDocument, VoiceTrack } from '../../logic/types'
import { registerSource, setVisual, visuals, type SpectrumSource } from '../../spectrum'
import SpectrumBars from '../SpectrumBars.vue'
import MixerPanel, { type MixerTrack } from './MixerPanel.vue'

const props = defineProps<{
  score: ScoreDocument
  includeChords: boolean
  stale: boolean
  /** The MIDI instrument library, edited on the instruments page. */
  instruments: Instrument[]
  /** Track name → instrument id, kept on the server by the app. */
  assignments: Assignments
  /** The song's recording: the URL of a library song's audio.flac, or the FLAC brought along; null without one. */
  recording: string | File | null
  /** Tracks a session had muted and soloed, laid over the routing whenever a score arrives; null leaves it as stored. */
  listening?: Listening | null
}>()

const emit = defineEmits<{
  /** The user picked an instrument for a track, or none. */
  assign: [track: string, instrumentId: number | null]
  manageInstruments: []
  /** Mute or solo changed, so a session can keep them. */
  listening: [value: Listening]
}>()

const canvas = ref<HTMLCanvasElement | null>(null)
const viewport = ref<HTMLDivElement | null>(null)
const large = ref(false)
const pxPerBar = ref(32)
const viewportWidth = ref(0)
const playhead = ref<number | null>(null)
const playing = ref(false)
const ports = ref<MidiPort[]>([])
const note = ref<string | null>(null)
const canAskForMidi = ref(false)

// Kept out of Vue's deep reactivity: thousands of notes that nothing renders from directly.
const voices = shallowRef(playableVoices(props.score, props.includeChords))
const lanes = shallowRef(lanesOf(voices.value))

// ---- The recording ----------------------------------------------------------------------------------

const recordingSettings = ref(loadRecordingSettings())
watch(recordingSettings, (value) => saveRecordingSettings(value), { deep: true })
const decoded = shallowRef<Recording | null>(null)
const recordingState = ref<'idle' | 'loading' | 'ready' | 'failed'>('idle')
/** Where the loaded recording came from, so a new score of the same song does not decode it again. */
let loadedFrom: string | File | null = null
let loadToken = 0
let recordingContext: AudioContext | null = null
let recordingPlayer: RecordingPlayer | null = null
const showWave = computed(() => recordingSettings.value.show && decoded.value !== null)
const waveHeight = computed(() => (showWave.value ? WAVE_HEIGHT : 0))

/**
 * Fetches and decodes the recording once it is to be shown. A library song's FLAC is tens of megabytes, which is why
 * nothing is loaded while the recording is switched off.
 */
async function loadRecording(): Promise<void> {
  const from = props.recording
  if (!from || !recordingSettings.value.show || from === loadedFrom) {
    return
  }
  const token = ++loadToken
  forgetRecording()
  loadedFrom = from
  recordingState.value = 'loading'
  try {
    const file = typeof from === 'string' ? await fetchRecording(from) : from
    recordingContext ??= new AudioContext()
    const result = await decodeRecording(file, recordingContext)
    if (token !== loadToken) {
      return
    }
    decoded.value = result
    recordingState.value = result ? 'ready' : 'failed'
    if (result) {
      recordingPlayer = new RecordingPlayer(recordingContext, result.buffer, recordingSettings.value.volume)
      if (playing.value) {
        startRecording(playhead.value ?? 0)
      }
    }
  } catch {
    if (token === loadToken) {
      recordingState.value = 'failed'
    }
  }
}

async function fetchRecording(url: string): Promise<Blob> {
  const response = await fetch(url)
  if (!response.ok) {
    throw new Error(`HTTP ${response.status}`)
  }
  return response.blob()
}

function forgetRecording(): void {
  recordingPlayer?.close()
  recordingPlayer = null
  decoded.value = null
  loadedFrom = null
  recordingState.value = 'idle'
}

/** The recording's first sample sits where the count-in ends, so the audio lines up with the notes behind it. */
function startRecording(fromTicks: number): void {
  if (recordingSettings.value.show) {
    recordingPlayer?.start((fromTicks - (props.score.countInTicks ?? 0)) * secondsPerTick.value)
  }
}

watch(
  () => [props.recording, recordingSettings.value.show] as const,
  ([from, show]) => {
    if (!from) {
      loadToken++
      forgetRecording()
    } else if (show) {
      void loadRecording()
    } else {
      recordingPlayer?.stop()
    }
  },
)

watch(
  () => recordingSettings.value.volume,
  (volume) => recordingPlayer?.setVolume(volume),
)

const waveform = computed<Waveform | null>(() =>
  showWave.value && decoded.value
    ? {
        peaks: decoded.value.peaks,
        secondsPerPeak: decoded.value.secondsPerPeak,
        startTicks: props.score.countInTicks ?? 0,
        secondsPerTick: secondsPerTick.value,
      }
    : null,
)

// The waveform takes a lane of its own above the tracks, which moves every lane down.
watch(waveHeight, (height) => {
  lanes.value = lanesOf(voices.value, height)
  requestAnimationFrame(render)
})

const trackIds = computed(() => voices.value.map((voice) => voice.id))
/**
 * Per track whether it is muted, and the channel it would have without an instrument. Ports are no longer chosen by
 * hand: a track sounds in the browser, or on the port and channel of its instrument, configured on the instruments
 * page. A port stored by an older version is ignored.
 */
const routings = ref<Routing[]>(loadRoutings(trackIds.value, defaultRoutings(voices.value)))
/** Tracks on solo; while any is, only those play. Saved only with a session: a solo is otherwise for listening now. */
const solos = ref<boolean[]>([])

function applyListening(): void {
  const listening = props.listening
  if (listening) {
    const muted = new Set(listening.muted)
    const solo = new Set(listening.solo)
    routings.value = routings.value.map((routing, index) => ({ ...routing, muted: muted.has(trackIds.value[index]!) }))
    solos.value = trackIds.value.map((id) => solo.has(id))
  }
}
applyListening()
/**
 * Only browsers with Web MIDI (Chromium, Firefox) drive MIDI ports; elsewhere the choice offers browser sounds
 * only, and a track that has an instrument (chosen in another browser) keeps it, since it also decides the MIDI file's channel and the Logic project's
 * hardware, but sounds in the browser.
 */
const canDrivePorts = midiUsable()
/** What the player uses: the instrument's port and channel where a track has one, the browser elsewhere. */
const effective = computed(() =>
  routings.value.map((routing, index) =>
    effectiveRouting(
      { ...routing, output: AUDIO_OUTPUT },
      instrumentOf(trackIds.value[index]!, props.assignments, props.instruments),
      ports.value,
    ),
  ),
)

/**
 * Every track's sound on the browser synthesizer, checked once per change rather than per note: the saved sound it
 * was given, so an edit on the instruments page reaches it; the copy it keeps when that sound was deleted or it has
 * one of its own; the default of its kind otherwise. The pool looks it up for every note.
 */
const patches = computed(
  () =>
    new Map<string, SynthPatch>(
      voices.value.map((voice) => {
        const sound = trackSounds.value[trackKey(voice.id)]
        const preset = sound?.preset?.toUpperCase()
        const named = preset ? namedSounds.value.find((entry) => entry.name.toUpperCase() === preset) : undefined
        return [trackKey(voice.id), normalizePatch(named?.patch ?? sound?.patch, voice.kind)]
      }),
    ),
)
const pool = new OutputPool(null, (track) => patches.value.get(trackKey(track)) ?? null, {
  volume: (track) => trackMix(track).volume,
  pan: (track) => trackMix(track).pan,
  master: () => mixer.value.master,
})
// Fader and pan are heard at once; the schedule stays as it is.
watch(mixer, () => pool.mixChanged(), { deep: true })

/** The routing the player uses, with mute and solo applied; a muted track's notes are not scheduled at all. */
const audible = computed(() => {
  const anySolo = solos.value.some(Boolean)
  return effective.value.map((entry, index) => ({
    ...entry.routing,
    muted: entry.routing.muted || (anySolo && !solos.value[index]),
  }))
})
const mixerTracks = computed<MixerTrack[]>(() =>
  trackIds.value.map((id, index) => ({
    id,
    inBrowser: effective.value[index]?.routing.output === AUDIO_OUTPUT,
    muted: routings.value[index]?.muted ?? false,
    solo: solos.value[index] ?? false,
  })),
)

function setMute(index: number, muted: boolean): void {
  const routing = routings.value[index]
  if (routing) {
    routing.muted = muted
  }
}

function setSolo(index: number, solo: boolean): void {
  const next = [...solos.value]
  next[index] = solo
  solos.value = next
}
/** The middle of a track's range, so a test note sounds where the track plays. */
function registerOf(voice: VoiceTrack | undefined): number {
  const pitches = (voice?.notes ?? []).map((entry) => entry.noteNumber).sort((a, b) => a - b)
  return pitches.length > 0 ? pitches[Math.floor(pitches.length / 2)]! : 60
}

/**
 * What a track plays, as one choice: `sound:` for the default sound of its kind (the noise kit for drums),
 * `sound:<name>` for a saved sound, `own` for a sound of its own made in an earlier version's sound dialog, and
 * `midi:<id>` for an instrument.
 */
function choiceOf(index: number): string {
  const instrument = effective.value[index]?.instrument
  if (instrument) {
    return `midi:${instrument.id}`
  }
  const sound = trackSounds.value[trackKey(trackIds.value[index]!)]
  if (!sound) {
    return 'sound:'
  }
  const preset = sound.preset?.toUpperCase()
  const named = namedSounds.value.find((entry) => entry.name.toUpperCase() === preset)
  return named ? `sound:${named.name}` : 'own'
}

interface ChoiceGroup {
  label: string
  items: { label: string; value: string }[]
}

function choicesFor(index: number): ChoiceGroup[] {
  const drums = voices.value[index]?.kind === 'Drums'
  const browser = [
    { label: t(drums ? 'previewDrumKit' : 'synthReset'), value: 'sound:' },
    ...(choiceOf(index) === 'own' ? [{ label: t('previewOwnSound'), value: 'own' }] : []),
    ...(drums ? [] : namedSounds.value.map((sound) => ({ label: sound.name, value: `sound:${sound.name}` }))),
  ]
  // Without Web MIDI only an instrument the track already has is listed, so the choice does not look empty.
  const current = effective.value[index]?.instrument
  const instruments = canDrivePorts ? props.instruments : current ? [current] : []
  return [
    { label: t('previewBrowserSounds'), items: browser },
    ...(instruments.length > 0
      ? [
          {
            label: t('instrumentsTitle'),
            items: instruments.map((instrument) => ({ label: instrument.name, value: `midi:${instrument.id}` })),
          },
        ]
      : []),
  ]
}

function choose(index: number, value: string): void {
  const track = trackIds.value[index]!
  if (value.startsWith('midi:')) {
    assign(track, Number(value.slice(5)))
    return
  }
  if (effective.value[index]?.instrument) {
    assign(track, null)
  }
  if (value === 'own') {
    return
  }
  const name = value.slice(6)
  const sound = namedSounds.value.find((entry) => entry.name === name)
  if (sound) {
    // The track follows the saved sound (see `patches`); the copy is what it keeps should that be deleted.
    setTrackSound(track, clonePatch(normalizePatch(sound.patch, voices.value[index]?.kind)), sound.name)
  } else if (trackSounds.value[trackKey(track)]) {
    void resetTrackSound(track)
  }
}
let player: Player | null = null
let frame = 0
let scrollTicks = 0

const secondsPerTick = computed(() => 60 / props.score.tempoBpm / props.score.ticksPerQuarterNote)
const contentWidth = computed(() => totalWidth(props.score, pxPerBar.value) + GUTTER_WIDTH)
const height = computed(() => contentHeight(lanes.value, waveHeight.value))

function render(): void {
  if (canvas.value) {
    draw(canvas.value, {
      score: props.score,
      lanes: lanes.value,
      pxPerBar: pxPerBar.value,
      scrollTicks,
      playhead: playhead.value,
      waveform: waveform.value,
      waveformLabel: t('previewRecording'),
    })
  }
}

function onScroll(): void {
  if (viewport.value) {
    scrollTicks = ticksAtX(props.score, viewport.value.scrollLeft, pxPerBar.value)
    render()
  }
}

/** Keeps the playhead in view while playing, without fighting a scroll the user just made. */
function follow(ticks: number): void {
  const element = viewport.value
  if (!element) {
    return
  }
  const x = xAtTicks(props.score, ticks, pxPerBar.value)
  const visible = element.clientWidth - GUTTER_WIDTH
  if (x < element.scrollLeft || x > element.scrollLeft + visible - 40) {
    element.scrollLeft = Math.max(0, x - visible * 0.2)
  }
}

function step(): void {
  const seconds = player?.position() ?? null
  if (seconds === null) {
    playing.value = false
    render()
    return
  }
  playhead.value = seconds / secondsPerTick.value
  follow(playhead.value)
  render()
  frame = requestAnimationFrame(step)
}

function stop(): void {
  cancelAnimationFrame(frame)
  player?.stop()
  recordingPlayer?.stop()
  playing.value = false
  render()
}

/** Throws the player away, so the next play picks up changed routing or a new score. */
function release(): void {
  cancelAnimationFrame(frame)
  player?.stop()
  recordingPlayer?.stop()
  player = null
  playing.value = false
}

function play(fromTicks = playhead.value ?? 0): void {
  if (!player) {
    player = createPlayer(scheduleOf(props.score, voices.value, audible.value), pool, () => {
      recordingPlayer?.stop()
      playing.value = false
      playhead.value = null
      render()
    })
  }
  player.play(fromTicks * secondsPerTick.value)
  startRecording(fromTicks)
  playing.value = true
  cancelAnimationFrame(frame)
  frame = requestAnimationFrame(step)
}

function toggle(): void {
  if (playing.value) {
    stop()
  } else {
    play()
  }
}

/** Back to bar 1, scrolled home; playback carries on from there if it was running. */
function rewind(): void {
  playhead.value = 0
  viewport.value?.scrollTo({ left: 0 })
  if (playing.value) {
    play(0)
  } else {
    render()
  }
}

/** Lifts every key on every output; the way out when an instrument hangs on a note. */
function panic(): void {
  stop()
  pool.silence()
}

/** A click on the roll moves the playhead there, and keeps playing if it was. */
function seek(event: MouseEvent): void {
  const element = viewport.value
  if (!element) {
    return
  }
  const bounds = element.getBoundingClientRect()
  const x = event.clientX - bounds.left - GUTTER_WIDTH + element.scrollLeft
  // Below the lanes is the horizontal scrollbar; dragging it should not move the playhead.
  if (x < 0 || event.clientY - bounds.top > height.value) {
    return
  }
  playhead.value = Math.min(props.score.lengthTicks, ticksAtX(props.score, x, pxPerBar.value))
  if (playing.value) {
    play(playhead.value)
  } else {
    render()
  }
}

/** The canvas only covers what is on screen, so its width follows the viewport rather than the song. */
const observer = new ResizeObserver(() => {
  viewportWidth.value = viewport.value?.clientWidth ?? 0
  requestAnimationFrame(render)
})

async function loadPorts(): Promise<void> {
  const found = await listMidiPorts()
  pool.setAccess(found.access)
  ports.value = found.ports
  unreadable.value = found.unreadable
  restartFirefox.value = found.restartFirefox
  canAskForMidi.value = found.access === null && found.reason !== 'unsupported'
  note.value =
    found.reason === 'unsupported' ? t('midiUnsupported') : found.reason === 'denied' ? t('midiDenied') : null

  // Ports come and go while the page is open, and a rescan elsewhere on the page makes a new access.
  stopWatchingPorts ??= found.access ? onMidiPortsChanged(() => void refreshPorts()) : null
}

let stopWatchingPorts: (() => void) | null = null

async function refreshPorts(): Promise<void> {
  const found = await listMidiPorts()
  pool.setAccess(found.access)
  ports.value = found.ports
  unreadable.value = found.unreadable
  restartFirefox.value = found.restartFirefox
}

const rescanning = ref(false)
const unreadable = ref(false)
const restartFirefox = ref(false)

/** A device switched on after the page asked is only found by asking again in Firefox; see rescanMidi. */
async function rescan(): Promise<void> {
  rescanning.value = true
  try {
    await rescanMidi()
  } catch {
    // The access the page had stays; loadPorts below says if there is none.
  } finally {
    rescanning.value = false
  }
  await loadPorts()
}

onMounted(async () => {
  void loadSounds()
  void loadRecording()
  if (viewport.value) {
    observer.observe(viewport.value)
    viewportWidth.value = viewport.value.clientWidth
  }
  // The canvas takes its width from a style Vue has not applied yet on this tick.
  requestAnimationFrame(render)

  if (!midiSupported()) {
    note.value = t('midiUnsupported')
  } else if (await midiAlreadyAllowed()) {
    await loadPorts()
  } else {
    canAskForMidi.value = true
  }
})

/**
 * The synth and the recording play on two audio contexts, so the analyzer and the background get one analyser of each.
 * Notes sent to MIDI ports are not heard here and so not shown.
 */
const spectrumSource: SpectrumSource = {
  analysers: () => [...pool.analysers(), ...(recordingPlayer ? [recordingPlayer.analyser] : [])],
  playing,
}
const unregisterSource = registerSource(spectrumSource)

onBeforeUnmount(() => {
  unregisterSource()
  observer.disconnect()
  release()
  pool.close()
  stopWatchingPorts?.()
  recordingPlayer?.close()
  void recordingContext?.close().catch(() => {})
  window.removeEventListener('pagehide', stop)
})

// Hardware would keep sounding if the page went away mid-note.
window.addEventListener('pagehide', stop)

watch(
  () => [props.score, props.includeChords] as const,
  ([score, includeChords]) => {
    release()
    playhead.value = null
    voices.value = playableVoices(score, includeChords)
    lanes.value = lanesOf(voices.value, waveHeight.value)
    routings.value = loadRoutings(trackIds.value, defaultRoutings(voices.value))
    solos.value = []
    applyListening()
    scrollTicks = 0
    if (viewport.value) {
      viewport.value.scrollLeft = 0
    }
    render()
  },
)

watch(routings, (value) => saveRoutings(trackIds.value, value), { deep: true })
watch(
  [routings, solos],
  () =>
    emit('listening', {
      muted: trackIds.value.filter((_, index) => routings.value[index]?.muted),
      solo: trackIds.value.filter((_, index) => solos.value[index]),
    }),
  { deep: true, immediate: true },
)

// Switched on while playing, the recording joins at the playhead.
watch(
  () => recordingSettings.value.show,
  (show) => {
    if (show && playing.value) {
      startRecording(playhead.value ?? 0)
    }
  },
)

// Re-routing - by hand, by instrument or by a port coming or going - rebuilds the schedule; playback picks up
// where it was rather than jumping back to the start. Compared as text, so a refreshed port list alone changes nothing.
watch(
  () => JSON.stringify(audible.value),
  () => {
    const resume = playing.value ? (playhead.value ?? 0) : null
    release()
    if (resume !== null) {
      play(resume)
    }
  },
)

function assign(track: string, instrumentId: number | null): void {
  emit('assign', track, instrumentId)
}

// Zooming keeps the bar at the left edge in place; the raw scroll offset would otherwise jump to a
// different part of the song every time the scale changes.
watch(pxPerBar, (value) => {
  const anchor = scrollTicks
  requestAnimationFrame(() => {
    if (viewport.value) {
      viewport.value.scrollLeft = Math.max(0, xAtTicks(props.score, anchor, value))
    }
    onScroll()
  })
})

watch([large, viewportWidth], () => requestAnimationFrame(onScroll))
</script>

<template>
  <!--
    min-w-0: a grid item will not shrink below its content by default, and the content here is the whole song - at
    204 bars well over 6000 pixels, which would drag the entire page wide. This keeps the card inside the column.
  -->
  <Card class="min-w-0" :class="{ 'fixed inset-4 z-20 overflow-auto shadow-2xl': large }">
    <template #title>
      <div class="flex flex-wrap items-center justify-between gap-3">
        <h2 class="m-0">{{ t('previewTitle') }}</h2>
        <div class="flex flex-wrap items-center gap-2">
          <Button
            size="small"
            :icon="playing ? 'pi pi-stop' : 'pi pi-play'"
            :label="playing ? t('previewStop') : t('previewPlay')"
            :aria-pressed="playing"
            @click="toggle"
          />
          <Button
            size="small"
            severity="secondary"
            outlined
            icon="pi pi-step-backward"
            :label="t('previewRewind')"
            :title="t('previewRewindTitle')"
            :disabled="!playing && !playhead"
            @click="rewind"
          />
          <Button
            size="small"
            severity="secondary"
            outlined
            :label="t('previewPanic')"
            :title="t('previewPanicTitle')"
            @click="panic"
          />
          <div class="w-32 px-2" :title="t('previewZoom')">
            <Slider
              :model-value="pxPerBar"
              :min="6"
              :max="120"
              :step="2"
              :aria-label="t('previewZoom')"
              @update:model-value="pxPerBar = $event as number"
            />
          </div>
          <Button
            size="small"
            severity="secondary"
            outlined
            :icon="large ? 'pi pi-window-minimize' : 'pi pi-window-maximize'"
            :label="large ? t('previewSmaller') : t('previewLarger')"
            :aria-pressed="large"
            @click="large = !large"
          />
        </div>
      </div>
    </template>

    <template #content>
      <p v-if="stale" class="hint warning mt-0">{{ t('stale') }}</p>

      <div class="mb-2 flex flex-wrap items-center gap-x-4 gap-y-2 text-sm">
        <div v-if="recording" class="flex items-center gap-2">
          <Checkbox v-model="recordingSettings.show" binary input-id="preview-recording" />
          <label for="preview-recording" class="cursor-pointer">{{ t('previewRecordingShow') }}</label>
        </div>
        <div v-if="recordingSettings.show && recordingState === 'ready'" class="flex items-center gap-2">
          <span id="preview-recording-volume" class="text-muted-color">{{ t('previewRecordingVolume') }}</span>
          <Slider
            :model-value="Math.round(recordingSettings.volume * 100)"
            :min="0"
            :max="100"
            aria-labelledby="preview-recording-volume"
            class="w-28"
            @update:model-value="recordingSettings.volume = ($event as number) / 100"
          />
        </div>
        <span v-if="recordingSettings.show && recordingState === 'loading'" class="muted">
          <i class="pi pi-spin pi-spinner mr-1" aria-hidden="true" />{{ t('previewRecordingLoading') }}
        </span>
        <span v-if="recordingSettings.show && recordingState === 'failed'" class="text-(--warning-text)">
          {{ t('previewRecordingFailed') }}
        </span>
        <div class="flex items-center gap-2">
          <Checkbox
            :model-value="visuals.logic"
            binary
            input-id="preview-analyzer"
            @update:model-value="setVisual('logic', $event)"
          />
          <label for="preview-analyzer" class="cursor-pointer">{{ t('previewAnalyzer') }}</label>
        </div>
      </div>

      <!-- Above the roll rather than beside it: the roll needs the width, the analyzer only a strip of height. -->
      <div v-if="visuals.logic" class="analyzer mb-2">
        <SpectrumBars :sources="[spectrumSource]" :bars="64" peaks />
      </div>

      <div ref="viewport" class="viewport" @scroll.passive="onScroll" @click="seek">
        <div :style="{ width: `${contentWidth}px`, height: `${height}px` }">
          <canvas ref="canvas" :style="{ width: `${viewportWidth}px`, height: `${height}px` }" />
        </div>
      </div>

      <!-- The panel's content sits in a grid (for its collapse animation), whose item would grow to the table's width. -->
      <Panel :header="t('previewRouting')" toggleable class="mt-3" :pt="{ contentWrapper: { class: 'min-w-0' } }">
        <div class="mb-2 flex flex-wrap items-center gap-3">
          <Button
            v-if="canAskForMidi"
            size="small"
            severity="secondary"
            outlined
            :label="t('previewFindMidi')"
            @click="loadPorts"
          />
          <Button
            v-else-if="canDrivePorts && note === null"
            size="small"
            severity="secondary"
            outlined
            icon="pi pi-refresh"
            :label="t('midiRescan')"
            :loading="rescanning"
            @click="rescan"
          />
          <Button
            size="small"
            severity="secondary"
            outlined
            :label="t('instrumentsManage')"
            @click="emit('manageInstruments')"
          />
        </div>

        <p v-if="unreadable" class="hint mt-0 mb-2 text-sm text-(--warning-text)">{{ t('midiUnreadable') }}</p>
        <p v-else-if="restartFirefox" class="hint mt-0 mb-2 text-sm text-(--warning-text)">
          {{ t('midiRestartFirefox') }}
        </p>
        <div class="relative overflow-x-auto">
          <table class="w-full border-collapse text-sm">
            <thead>
              <tr class="text-left text-xs text-muted-color">
                <th class="py-1.5 pr-2 font-medium">{{ t('previewTrack') }}</th>
                <th class="py-1.5 pr-2 font-medium">{{ t('previewPlaysOn') }}</th>
                <th class="py-1.5 font-medium">
                  <span class="sr-only">{{ t('previewTest') }}</span>
                </th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="(routing, index) in routings" :key="trackIds[index]" :class="{ 'opacity-50': routing.muted }">
                <td class="py-1 pr-2 align-middle">
                  <div class="flex items-center gap-2 whitespace-nowrap">
                    <Checkbox
                      v-model="routing.muted"
                      binary
                      :true-value="false"
                      :false-value="true"
                      :input-id="`preview-track-${index}`"
                    />
                    <span class="swatch" :style="{ background: trackColour(index) }" />
                    <label :for="`preview-track-${index}`" class="cursor-pointer">{{ trackIds[index] }}</label>
                  </div>
                </td>
                <td class="py-1 pr-2 align-middle">
                  <Select
                    :model-value="choiceOf(index)"
                    :options="choicesFor(index)"
                    option-label="label"
                    option-value="value"
                    option-group-label="label"
                    option-group-children="items"
                    :aria-label="`${t('previewPlaysOn')} ${trackIds[index]}`"
                    size="small"
                    class="w-full min-w-40"
                    @update:model-value="choose(index, $event)"
                  />
                  <!-- Without Web MIDI no port is ever found, so naming one that is missing would be misleading. -->
                  <div
                    v-if="canDrivePorts && effective[index]?.instrument && !effective[index]?.port"
                    class="mt-0.5 text-xs text-(--warning-text)"
                  >
                    {{ t('previewInstrumentMissing', { port: effective[index]?.instrument?.port ?? '' }) }}
                  </div>
                </td>
                <td class="py-1 align-middle">
                  <Button
                    size="small"
                    severity="secondary"
                    outlined
                    :label="t('previewTest')"
                    @click="
                      testTone(
                        pool,
                        effective[index]!.routing,
                        trackIds[index]!,
                        voices[index]?.kind === 'Drums',
                        voices[index]?.kind === 'Drums'
                          ? drumTestNote(trackIds[index]!, effective[index]!.instrument)
                          : registerOf(voices[index]),
                      )
                    "
                  />
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </Panel>

      <Panel :header="t('mixerTitle')" toggleable class="mt-3" :pt="{ contentWrapper: { class: 'min-w-0' } }">
        <MixerPanel :tracks="mixerTracks" :pool="pool" @mute="setMute" @solo="setSolo" />
        <p class="muted hint mb-0">{{ t('mixerHint') }}</p>
      </Panel>

      <p class="muted hint">{{ note ?? t('previewHint') }}</p>
    </template>
  </Card>
</template>

<style scoped>
/*
 * The content element carries the scrollable width; the canvas sticks to the left edge of the scrollport and
 * is only as wide as what is visible, so a long song costs no extra pixels. The piano roll reads its colours
 * (--text, --border, --accent, ...) from the canvas's computed style, which inherits them from :root.
 */
.analyzer {
  height: 5rem;
  padding: 0.375rem;
  border-radius: var(--radius-small);
  background: var(--surface-sunken);
}

.viewport {
  max-width: 100%;
  overflow-x: auto;
  overflow-y: hidden;
  border: 1px solid var(--border);
  border-radius: var(--radius-small);
  cursor: crosshair;
}

.viewport canvas {
  position: sticky;
  left: 0;
  display: block;
}

.swatch {
  flex: none;
  width: 0.75rem;
  height: 0.75rem;
  border-radius: 3px;
}
</style>
