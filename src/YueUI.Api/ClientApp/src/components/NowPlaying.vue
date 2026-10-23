<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, useTemplateRef, watch } from 'vue'
import Slider from 'primevue/slider'
import { drawCover, exportTargetFor } from '../export'
import { t } from '../i18n'
import {
  current,
  duration,
  elapsed,
  expanded,
  hasNext,
  hasPrevious,
  listen,
  next,
  playerSource,
  playing,
  previous,
  seek,
  toggle,
} from '../player'
import { getTiming, requestTiming } from '../api'
import { rate, ratingOf } from '../ratings'
import { hasDirections, shownAt, shownLines, shownStart, type ShownLine } from '../timing'
import type { LyricsTiming } from '../types'
import { readBands, reducedMotion, setVisual, visuals } from '../spectrum'
import { coverColour } from '../video'
import PlaylistToggle from './PlaylistToggle.vue'
import SongMenu from './SongMenu.vue'
import SpectrumBars from './SpectrumBars.vue'

/**
 * The player over the whole screen, as Spotify and Apple Music have it: the cover large on a blurred copy of itself,
 * the progress bar and the buttons, and on request the lyrics, a large analyzer and glows in the cover's colour that
 * move with the music. It only shows what player.ts plays; the audio element stays in PlayerBar, so opening and
 * closing never interrupts the song.
 */
const props = defineProps<{ lyrics: string; hasScore: boolean }>()

const emit = defineEmits<{ error: [message: string]; useScore: [songId: string]; newSong: [songId: string] }>()

// ---- Cover ---------------------------------------------------------------------------------------------------------

/** A song without a cover of its own gets the drawn one the export would put in, made here and never stored. */
const drawn = ref<{ songId: string; url: string } | null>(null)
const cover = computed(
  () => current.value?.cover ?? (drawn.value?.songId === current.value?.songId ? drawn.value?.url : undefined),
)
/** The cover's most vivid hue, for the analyzer and the glows; Tonwerk's emerald until the cover is loaded. */
const colour = ref('#34d399')

watch(
  () => (current.value && !current.value.cover ? current.value.songId : null),
  async (songId) => {
    if (!songId || drawn.value?.songId === songId) {
      return
    }
    const blob = await drawCover(exportTargetFor(songId, current.value?.title ?? ''))
    if (current.value?.songId !== songId) {
      return
    }
    if (drawn.value) {
      URL.revokeObjectURL(drawn.value.url)
    }
    drawn.value = { songId, url: URL.createObjectURL(blob) }
  },
  { immediate: true },
)

function coverLoaded(event: Event): void {
  try {
    colour.value = coverColour(event.target as HTMLImageElement)
  } catch {
    // A cover the canvas may not read keeps the colour it had.
  }
}

// ---- Progress ------------------------------------------------------------------------------------------------------

/** While the thumb is dragged it shows where it is, not where the song is, and the song jumps when it is let go. */
const scrubbing = ref<number | null>(null)
const shownTime = computed(() => scrubbing.value ?? elapsed.value)

/**
 * Slider's `change` comes with every step of a drag as well as for a tap on the bar or a key; only the latter seek at
 * once, a drag on its end (`slideend`), so the song does not stutter along under the finger.
 */
const progress = useTemplateRef<HTMLElement>('progress')

function scrub(value: number | number[]): void {
  scrubbing.value = Array.isArray(value) ? (value[0] ?? 0) : value
  if (!progress.value?.querySelector('[data-p-sliding="true"]')) {
    scrubbed()
  }
}

function scrubbed(): void {
  if (scrubbing.value !== null) {
    seek(scrubbing.value)
    scrubbing.value = null
  }
}

function clock(seconds: number): string {
  const whole = Math.max(0, Math.floor(seconds))
  return `${Math.floor(whole / 60)}:${String(whole % 60).padStart(2, '0')}`
}

// ---- Lyrics --------------------------------------------------------------------------------------------------------

/**
 * YuE2's lyrics with their section tags ([Verse], [Chorus], …) as headings of their own; directions in round brackets
 * only on request.
 */
const lyricLines = computed(() => shownLines(props.lyrics, visuals.value.nowPlayingDirections))
const directionsFound = computed(() => hasDirections(props.lyrics))
const hasLyrics = computed(() => lyricLines.value.some((line) => line.text && !line.section))
const showLyrics = computed(() => visuals.value.nowPlayingLyrics)

/**
 * When each line is sung (Whisper, on the server): the line being sung is lit and kept in view, and a tap on a line
 * jumps there. A song without timing is asked for once and looked at again while the lyrics are open; until then the
 * lyrics stand as they are.
 */
const timing = ref<{ songId: string; value: LyricsTiming } | null>(null)
const currentTiming = computed(() => (timing.value?.songId === current.value?.songId ? timing.value!.value : null))
const activeLine = computed(() => shownAt(lyricLines.value, currentTiming.value, elapsed.value))
let timingPoll: number | undefined

async function loadTiming(songId: string, ask: boolean): Promise<void> {
  window.clearTimeout(timingPoll)
  try {
    let answer = await getTiming(songId)
    if (!answer.timing && ask && answer.info.installed && answer.info.state === 'none') {
      answer = await requestTiming(songId)
    }
    if (current.value?.songId !== songId) {
      return
    }
    if (answer.timing) {
      timing.value = { songId, value: answer.timing }
    } else if (answer.info.state === 'queued' || answer.info.state === 'listening') {
      timingPoll = window.setTimeout(() => void loadTiming(songId, false), 10_000)
    }
  } catch {
    // Without timing the lyrics just stand; nothing to tell.
  }
}

watch(
  () => (showLyrics.value && hasLyrics.value && current.value ? current.value.songId : null),
  (songId) => {
    window.clearTimeout(timingPoll)
    if (songId && timing.value?.songId !== songId) {
      void loadTiming(songId, true)
    }
  },
  { immediate: true },
)
onBeforeUnmount(() => window.clearTimeout(timingPoll))

const lyricsPanel = useTemplateRef<HTMLElement>('lyricsPanel')
/** A finger scrolling the lyrics wins over following the song for a few seconds. */
let scrolledByHand = 0

function handScrolled(): void {
  scrolledByHand = Date.now()
}

watch(activeLine, async (index) => {
  if (index < 0 || Date.now() - scrolledByHand < 4000) {
    return
  }
  const line = lyricsPanel.value?.querySelector<HTMLElement>(`[data-shown="${index}"]`)
  line?.scrollIntoView({ block: 'center', behavior: reducedMotion.value ? 'auto' : 'smooth' })
})

function seekLine(line: ShownLine): void {
  const start = shownStart(line, currentTiming.value)
  if (start !== null) {
    scrolledByHand = 0
    seek(start)
  }
}

// ---- Switches ------------------------------------------------------------------------------------------------------

/**
 * Analyzer and glows need the audio routed through Web Audio, which happens only inside a tap (iOS); the switch is
 * that tap.
 */
function switchVisual(key: 'nowPlayingAnalyzer' | 'nowPlayingEffects' | 'nowPlayingLyrics'): void {
  setVisual(key, !visuals.value[key])
  listen()
}

const effectsOn = computed(() => visuals.value.nowPlayingEffects && !reducedMotion.value)

async function rateCurrent(rating: number | null | undefined): Promise<void> {
  if (!current.value) {
    return
  }
  try {
    await rate(current.value.songId, rating ?? null)
  } catch (caught) {
    emit('error', caught instanceof Error ? caught.message : String(caught))
  }
}

// ---- Effects -------------------------------------------------------------------------------------------------------

const coverBox = useTemplateRef<HTMLElement>('coverBox')
const glows = useTemplateRef<HTMLDivElement[]>('glows')
const bands = new Float32Array(24)
const levels = [0, 0, 0]
const RANGES: [from: number, to: number][] = [
  [0, 6],
  [6, 15],
  [15, 24],
]
let frame = 0

/**
 * The cover beats with the bass and three glows swell with bass, mids and highs, as AudioBackground.vue does for the
 * page. Only transform and opacity change per frame, which the compositor handles without repainting.
 */
function step(): void {
  readBands(playerSource.analysers(), bands)
  RANGES.forEach(([from, to], index) => {
    let sum = 0
    for (let band = from; band < to; band++) {
      sum += bands[band]!
    }
    const level = sum / (to - from)
    levels[index] = level > levels[index]! ? levels[index]! * 0.5 + level * 0.5 : levels[index]! * 0.9 + level * 0.1
  })
  if (coverBox.value) {
    coverBox.value.style.transform = `scale(${1 + levels[0]! * 0.05})`
  }
  glows.value?.forEach((glow, index) => {
    const level = levels[index]!
    glow.style.transform = `translate(-50%, -50%) scale(${0.6 + level * 0.9})`
    glow.style.opacity = String(0.1 + level * 0.9)
  })
  frame = requestAnimationFrame(step)
}

function updateEffects(): void {
  const run = effectsOn.value && playing.value && document.visibilityState === 'visible'
  if (run && frame === 0) {
    frame = requestAnimationFrame(step)
  } else if (!run && frame !== 0) {
    cancelAnimationFrame(frame)
    frame = 0
    if (coverBox.value) {
      coverBox.value.style.transform = ''
    }
  }
}

watch([effectsOn, playing], updateEffects)

// ---- Opening and closing -------------------------------------------------------------------------------------------

/** The phone's back gesture closes the player rather than leaving the page; opening it added a step to go back. */
function shut(): void {
  if ((history.state as { nowPlaying?: boolean } | null)?.nowPlaying) {
    history.back()
  } else {
    expanded.value = false
  }
}

function onPopState(): void {
  expanded.value = false
}

/** Going to another page (the song menu's "go to song", "use score", …) closes the player too. */
function onHashChange(): void {
  expanded.value = false
}

function onKey(event: KeyboardEvent): void {
  if (event.key === 'Escape') {
    shut()
  }
}

/** Pulled down by its top or its cover, the player closes, as a sheet on the phone does. */
const pull = ref(0)
let pullFrom: number | null = null

function pullStart(event: TouchEvent): void {
  pullFrom = event.touches.length === 1 ? event.touches[0]!.clientY : null
}

function pullMove(event: TouchEvent): void {
  if (pullFrom !== null) {
    pull.value = Math.max(0, event.touches[0]!.clientY - pullFrom)
  }
}

function pullEnd(): void {
  if (pull.value > 120) {
    shut()
  }
  pull.value = 0
  pullFrom = null
}

let scrolledTo = 0

onMounted(() => {
  history.pushState({ ...(history.state as object | null), nowPlaying: true }, '')
  window.addEventListener('popstate', onPopState)
  window.addEventListener('hashchange', onHashChange)
  window.addEventListener('keydown', onKey)
  document.addEventListener('visibilitychange', updateEffects)
  // No overflow: hidden on the page to keep it still: on iOS, toggling it left the fixed mini player stuck halfway up
  // the screen afterwards. The page may scroll behind the opaque player; closing returns it to where it was.
  scrolledTo = window.scrollY
  updateEffects()
})

onBeforeUnmount(() => {
  cancelAnimationFrame(frame)
  window.removeEventListener('popstate', onPopState)
  window.removeEventListener('hashchange', onHashChange)
  window.removeEventListener('keydown', onKey)
  document.removeEventListener('visibilitychange', updateEffects)
  window.scrollTo(0, scrolledTo)
  if (drawn.value) {
    URL.revokeObjectURL(drawn.value.url)
  }
})
</script>

<template>
  <div
    v-if="current"
    class="now-playing"
    :class="{ lyrics: showLyrics }"
    role="dialog"
    aria-modal="true"
    :aria-label="t('nowPlaying')"
    :style="{ '--cover-colour': colour, transform: pull ? `translateY(${pull}px)` : undefined }"
  >
    <div class="backdrop" aria-hidden="true">
      <img v-if="cover" :src="cover" alt="" />
      <div v-for="index in 3" v-show="effectsOn" :key="index" ref="glows" class="glow" :class="`glow-${index}`" />
    </div>

    <div class="top" @touchstart.passive="pullStart" @touchmove.passive="pullMove" @touchend="pullEnd">
      <Button icon="pi pi-chevron-down" text rounded :aria-label="t('closeNowPlaying')" @click="shut" />
      <div class="min-w-0 flex-1 truncate text-center text-sm opacity-80">{{ current.detail }}</div>
      <SongMenu
        :song-id="current.songId"
        :has-score="hasScore"
        @use-score="emit('useScore', $event)"
        @new-song="emit('newSong', $event)"
      />
    </div>

    <div class="stage">
      <div class="cover-area" @touchstart.passive="pullStart" @touchmove.passive="pullMove" @touchend="pullEnd">
        <div ref="coverBox" class="cover-box">
          <img v-if="cover" :src="cover" alt="" class="cover" @load="coverLoaded" />
          <div v-else class="cover cover-empty"><i class="pi pi-music" aria-hidden="true" /></div>
        </div>
      </div>

      <div
        v-if="showLyrics"
        ref="lyricsPanel"
        :class="['lyrics-panel', { timed: !!currentTiming }]"
        @touchmove.passive="handScrolled"
        @wheel.passive="handScrolled"
      >
        <p v-if="!hasLyrics" class="opacity-70">{{ t('nowPlayingInstrumental') }}</p>
        <template v-else>
          <template v-for="(line, index) in lyricLines" :key="index">
            <div v-if="line.section" class="section">{{ line.text.slice(1, -1) }}</div>
            <p v-else-if="line.direction" class="direction">{{ line.text.slice(1, -1) }}</p>
            <p
              v-else-if="line.text"
              :class="['line', { active: index === activeLine }]"
              :data-shown="index"
              @click="currentTiming && line.timed >= 0 && seekLine(line)"
            >
              {{ line.text }}
            </p>
            <div v-else class="h-3" />
          </template>
        </template>
      </div>
    </div>

    <div class="controls">
      <div class="flex items-center gap-3">
        <img v-if="showLyrics && cover" :src="cover" alt="" class="thumb" />
        <div class="min-w-0 flex-1">
          <div class="truncate text-xl font-bold">{{ current.title }}</div>
          <Rating
            :model-value="ratingOf(current.songId)"
            :aria-label="t('rating')"
            class="mt-1"
            @update:model-value="rateCurrent"
          />
        </div>
        <PlaylistToggle :song-id="current.songId" @error="emit('error', $event)" />
      </div>

      <div v-if="visuals.nowPlayingAnalyzer" class="analyzer">
        <SpectrumBars :key="colour" :sources="[playerSource]" :bars="40" peaks />
      </div>

      <div ref="progress" class="mt-3">
        <Slider
          :model-value="shownTime"
          :min="0"
          :max="duration || 1"
          :step="0.1"
          :disabled="!duration"
          :aria-label="t('nowPlayingPosition')"
          @update:model-value="scrub"
          @slideend="scrubbed"
        />
        <div class="mt-2 flex justify-between text-xs tabular-nums opacity-70">
          <span>{{ clock(shownTime) }}</span>
          <span>{{ duration ? `−${clock(duration - shownTime)}` : '' }}</span>
        </div>
      </div>

      <div class="mt-1 flex items-center justify-center gap-6">
        <Button
          icon="pi pi-step-backward"
          text
          rounded
          size="large"
          :disabled="!hasPrevious && !playing"
          :aria-label="t('previousTrack')"
          @click="previous()"
        />
        <Button
          :icon="playing ? 'pi pi-pause' : 'pi pi-play'"
          rounded
          class="play"
          :aria-label="playing ? t('pause') : t('play')"
          @click="toggle"
        />
        <Button
          icon="pi pi-step-forward"
          text
          rounded
          size="large"
          :disabled="!hasNext"
          :aria-label="t('nextTrack')"
          @click="next()"
        />
      </div>

      <div class="switches">
        <Button
          icon="pi pi-align-left"
          :label="t('nowPlayingLyrics')"
          text
          size="small"
          :class="{ on: showLyrics }"
          :aria-pressed="showLyrics"
          @click="switchVisual('nowPlayingLyrics')"
        />
        <Button
          v-if="showLyrics && directionsFound"
          icon="pi pi-megaphone"
          :label="t('nowPlayingDirections')"
          text
          size="small"
          :class="{ on: visuals.nowPlayingDirections }"
          :aria-pressed="visuals.nowPlayingDirections"
          :title="t('nowPlayingDirectionsHint')"
          @click="setVisual('nowPlayingDirections', !visuals.nowPlayingDirections)"
        />
        <Button
          icon="pi pi-chart-bar"
          :label="t('nowPlayingAnalyzer')"
          text
          size="small"
          :class="{ on: visuals.nowPlayingAnalyzer }"
          :aria-pressed="visuals.nowPlayingAnalyzer"
          @click="switchVisual('nowPlayingAnalyzer')"
        />
        <Button
          icon="pi pi-sparkles"
          :label="t('nowPlayingEffects')"
          text
          size="small"
          :class="{ on: visuals.nowPlayingEffects }"
          :aria-pressed="visuals.nowPlayingEffects"
          :title="reducedMotion ? t('nowPlayingEffectsReduced') : undefined"
          @click="switchVisual('nowPlayingEffects')"
        />
      </div>
    </div>
  </div>
</template>

<style scoped>
/* Dark whatever the page's theme, as the music apps' players are: the colours come from the cover, and white text
   reads on every cover once it is blurred and darkened. PrimeVue's own colours are set through its tokens. */
.now-playing {
  --p-button-text-primary-color: #fff;
  --p-button-text-primary-hover-background: rgb(255 255 255 / 0.1);
  --p-button-text-primary-active-background: rgb(255 255 255 / 0.18);
  --p-button-text-secondary-color: #fff;
  --p-button-text-secondary-hover-background: rgb(255 255 255 / 0.1);
  --p-button-text-secondary-active-background: rgb(255 255 255 / 0.18);
  --p-rating-icon-color: rgb(255 255 255 / 0.45);
  --p-rating-icon-hover-color: #fff;
  --p-rating-icon-active-color: #fff;
  --p-slider-track-background: rgb(255 255 255 / 0.25);
  --p-slider-range-background: #fff;
  --p-slider-handle-background: #fff;
  --p-slider-handle-content-background: #fff;
  --p-slider-handle-hover-background: #fff;
  --accent: var(--cover-colour);
  --text-muted: rgb(255 255 255 / 0.55);

  position: fixed;
  inset: 0;
  z-index: 50;
  display: flex;
  flex-direction: column;
  overflow: hidden;
  padding: env(safe-area-inset-top) max(1rem, env(safe-area-inset-right)) max(1rem, env(safe-area-inset-bottom))
    max(1rem, env(safe-area-inset-left));
  color: #fff;
  background: #0b0d12;
  color-scheme: dark;
  overscroll-behavior: contain;
  transition: transform 0.15s ease-out;
}

.backdrop {
  position: absolute;
  inset: 0;
  z-index: -1;
  overflow: hidden;
  pointer-events: none;
}

/* Drawn once and then only composited: a still image, blurred, costs nothing per frame. */
.backdrop img {
  position: absolute;
  inset: -20%;
  width: 140%;
  height: 140%;
  object-fit: cover;
  filter: blur(60px) saturate(1.4) brightness(0.45);
}

.glow {
  position: absolute;
  width: 80vmax;
  height: 80vmax;
  border-radius: 50%;
  opacity: 0.1;
  transform: translate(-50%, -50%) scale(0.6);
  will-change: transform, opacity;
}

.glow-1 {
  top: 80%;
  left: 25%;
  background: radial-gradient(circle, color-mix(in srgb, var(--cover-colour) 55%, transparent), transparent 65%);
}

.glow-2 {
  top: 30%;
  left: 85%;
  background: radial-gradient(
    circle,
    color-mix(in srgb, color-mix(in srgb, var(--cover-colour) 60%, #fff) 40%, transparent),
    transparent 65%
  );
}

.glow-3 {
  top: 15%;
  left: 20%;
  background: radial-gradient(
    circle,
    color-mix(in srgb, color-mix(in srgb, var(--cover-colour) 60%, #8b5cf6) 40%, transparent),
    transparent 65%
  );
}

.top {
  display: flex;
  flex: none;
  align-items: center;
  gap: 0.5rem;
  width: 100%;
  max-width: 64rem;
  margin: 0 auto;
  padding-top: 0.5rem;
}

.stage {
  display: flex;
  flex: 1;
  justify-content: center;
  width: 100%;
  max-width: 64rem;
  min-height: 0;
  margin: 0 auto;
}

.cover-area {
  display: flex;
  flex: 1;
  align-items: center;
  justify-content: center;
  min-width: 0;
  padding: 1rem 0;
}

.cover-box {
  /* As large as the space allows, square, never wider than a phone's screen is useful for. */
  width: min(100%, 28rem, 50vh);
  aspect-ratio: 1;
  transition: transform 0.08s linear;
  will-change: transform;
}

.cover {
  width: 100%;
  height: 100%;
  border-radius: 0.75rem;
  object-fit: cover;
  box-shadow: 0 20px 60px rgb(0 0 0 / 0.5);
}

.cover-empty {
  display: flex;
  align-items: center;
  justify-content: center;
  font-size: 4rem;
  background: rgb(255 255 255 / 0.08);
}

.lyrics-panel {
  flex: 1;
  min-width: 0;
  overflow-y: auto;
  padding: 1rem 0 3rem;
  overscroll-behavior: contain;
  mask-image: linear-gradient(transparent, #000 1.5rem, #000 calc(100% - 3rem), transparent);
}

.line {
  margin: 0 0 0.35rem;
  font-size: 1.4rem;
  font-weight: 700;
  line-height: 1.3;
}

/* With timing, the sung line stands out and the others step back, as in Apple Music. */
.timed .line {
  opacity: 0.45;
  cursor: pointer;
  transition: opacity 0.3s;
}

.timed .line.active {
  opacity: 1;
}

.direction {
  margin: 0 0 0.35rem;
  font-size: 0.95rem;
  font-style: italic;
  line-height: 1.3;
  opacity: 0.55;
}

.section {
  margin: 1rem 0 0.4rem;
  font-size: 0.75rem;
  font-weight: 600;
  letter-spacing: 0.08em;
  text-transform: uppercase;
  color: var(--cover-colour);
}

.thumb {
  width: 3rem;
  height: 3rem;
  flex: none;
  border-radius: 0.375rem;
  object-fit: cover;
}

.controls {
  flex: none;
  width: 100%;
  max-width: 28rem;
  margin: 0 auto;
}

.analyzer {
  height: 4rem;
  margin-top: 0.75rem;
}

.play {
  width: 4rem;
  height: 4rem;
  --p-button-primary-background: #fff;
  --p-button-primary-border-color: #fff;
  --p-button-primary-color: #0b0d12;
  --p-button-primary-hover-background: rgb(255 255 255 / 0.85);
  --p-button-primary-hover-border-color: transparent;
  --p-button-primary-hover-color: #0b0d12;
  --p-button-primary-active-background: rgb(255 255 255 / 0.75);
  --p-button-primary-active-color: #0b0d12;
}

.play :deep(.p-button-icon) {
  font-size: 1.5rem;
}

.switches {
  display: flex;
  justify-content: space-between;
  margin-top: 0.75rem;
}

.switches :deep(.p-button) {
  opacity: 0.55;
}

/* Through the token, so the colour holds in the hover and pressed states too, which set it from the token. */
.switches :deep(.p-button.on) {
  --p-button-text-primary-color: var(--cover-colour);
  opacity: 1;
}

/* On a phone the lyrics take the cover's place; the cover is then the small one next to the title. */
.lyrics .cover-area {
  display: none;
}

/* With room beside it, the cover stays and the lyrics run next to it. */
@media (min-width: 900px) and (min-height: 500px) {
  .lyrics .cover-area {
    display: flex;
  }

  .lyrics .lyrics-panel {
    padding-left: 2rem;
  }

  .lyrics .thumb {
    display: none;
  }
}

/* A phone held sideways: cover beside the controls, so neither is squeezed. */
@media (orientation: landscape) and (max-height: 500px) {
  .now-playing {
    display: grid;
    grid-template-rows: auto 1fr;
    grid-template-columns: 1fr 1fr;
    column-gap: 1.5rem;
  }

  .top {
    grid-column: 1 / -1;
  }

  .controls {
    align-self: center;
  }

  .analyzer {
    display: none;
  }
}

@media (prefers-reduced-motion: reduce) {
  .now-playing,
  .cover-box {
    transition: none;
  }
}
</style>
