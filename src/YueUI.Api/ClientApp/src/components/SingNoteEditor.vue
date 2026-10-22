<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, useTemplateRef, watch } from 'vue'
import { t } from '../i18n'
import { defaultPatch, normalizePatch, playSynthNote } from '../logic/synth'
import { loadSounds, trackKey, trackSounds } from '../logic/synths'
import { isHold, isSilent, noteEnd, noteName, trimmed, type SingEditor, type SingNote } from '../singEditor'
import NumberField from './NumberField.vue'

/**
 * The singing page's piano roll (the app's `SingEditorView`): keys on the left, notes on a grid of beats with their
 * syllables on them and the pronunciation above, held notes joined to the note they go on from. A tap on a free place
 * sets a note, dragging moves one, its right edge stretches it; the chosen note's syllable is typed below.
 */
const props = defineProps<{ editor: SingEditor; title: string }>()

const rowHeight = 22
const keysWidth = 46
const zoom = ref(1)
const pointsPerBeat = computed(() => 56 * zoom.value)
const black = (pitch: number) => [1, 3, 6, 8, 10].includes(((pitch % 12) + 12) % 12)

/** Two octaves around the notes, at least C1 to C6 (Logic's names: C3 is note 60). */
const range = computed(() => {
  const pitches = props.editor.notes.map((note) => note.pitch)
  const low = Math.max(0, Math.min(36, (pitches.length ? Math.min(...pitches) : 60) - 4))
  const high = Math.min(127, Math.max(84, (pitches.length ? Math.max(...pitches) : 60) + 4))
  return { low, high }
})
const pitches = computed(() =>
  Array.from({ length: range.value.high - range.value.low + 1 }, (_, i) => range.value.high - i),
)
const gridWidth = computed(() => Math.max(32, props.editor.lengthInBeats + 8) * pointsPerBeat.value)
const gridHeight = computed(() => pitches.value.length * rowHeight)
const y = (pitch: number) => (range.value.high - pitch) * rowHeight
const x = (beat: number) => beat * pointsPerBeat.value
const beatLines = computed(() =>
  Array.from({ length: Math.floor(gridWidth.value / pointsPerBeat.value) + 1 }, (_, i) => i),
)

/** Lines joining a held note to the one it goes on from. */
const holdLines = computed(() => {
  const notes = props.editor.notes
  return notes.slice(1).flatMap((note, i) => {
    const before = notes[i]!
    return isHold(note)
      ? [
          {
            id: note.id,
            x1: x(noteEnd(before)),
            y1: y(before.pitch) + rowHeight / 2,
            x2: x(note.start),
            y2: y(note.pitch) + rowHeight / 2,
          },
        ]
      : []
  })
})

// ---- Pointer -------------------------------------------------------------------------------------------

const scroller = useTemplateRef<HTMLDivElement>('scroller')
const syllable = useTemplateRef<{ $el: HTMLInputElement }>('syllable')

/** A drag in progress: which note, whether its edge (stretch) or body (move), and where it started. */
const drag = ref<{ id: number; edge: boolean; x: number; y: number; dx: number; dy: number; moved: boolean } | null>(
  null,
)

function gridPoint(event: PointerEvent): { beat: number; pitch: number } {
  const box = (event.currentTarget as HTMLElement).getBoundingClientRect()
  return {
    beat: (event.clientX - box.left) / pointsPerBeat.value,
    pitch: range.value.high - Math.floor((event.clientY - box.top) / rowHeight),
  }
}

/** A tap on the free grid (not a scroll): a press and release close together. */
let press: { x: number; y: number } | null = null

function gridDown(event: PointerEvent): void {
  press = { x: event.clientX, y: event.clientY }
}

function gridUp(event: PointerEvent): void {
  if (!press || Math.hypot(event.clientX - press.x, event.clientY - press.y) > 6) {
    press = null
    return
  }
  press = null
  const point = gridPoint(event)
  props.editor.add(point.beat, point.pitch)
  focusSyllable()
}

function noteDown(event: PointerEvent, note: SingNote): void {
  event.stopPropagation()
  const target = event.currentTarget as HTMLElement
  const box = target.getBoundingClientRect()
  const edge = event.clientX > box.right - Math.min(12, box.width / 3)
  target.setPointerCapture(event.pointerId)
  drag.value = { id: note.id, edge, x: event.clientX, y: event.clientY, dx: 0, dy: 0, moved: false }
}

function noteMove(event: PointerEvent): void {
  const current = drag.value
  if (!current) {
    return
  }
  const dx = event.clientX - current.x
  const dy = event.clientY - current.y
  drag.value = { ...current, dx, dy, moved: current.moved || Math.hypot(dx, dy) > 3 }
}

function noteUp(note: SingNote): void {
  const current = drag.value
  drag.value = null
  if (!current) {
    return
  }
  props.editor.selection = note.id
  if (!current.moved) {
    focusSyllable()
    return
  }
  if (current.edge) {
    props.editor.resize(note.id, current.dx / pointsPerBeat.value)
  } else {
    props.editor.move(note.id, current.dx / pointsPerBeat.value, -Math.round(current.dy / rowHeight))
  }
}

/** Where a note is drawn while dragged: snapped, so it lands where it is shown. */
function noteStyle(note: SingNote): Record<string, string> {
  let left = x(note.start)
  let top = y(note.pitch)
  let width = Math.max(8, note.length * pointsPerBeat.value)
  const current = drag.value
  if (current?.id === note.id && current.moved) {
    if (current.edge) {
      width = Math.max(8, width + current.dx)
    } else {
      const beats = props.editor.round(current.dx / pointsPerBeat.value)
      left = Math.max(0, left + beats * pointsPerBeat.value)
      top += Math.round(current.dy / rowHeight) * rowHeight
    }
  }
  return { left: `${left}px`, top: `${top + 1}px`, width: `${width}px`, height: `${rowHeight - 2}px` }
}

// ---- Inspector -----------------------------------------------------------------------------------------

const typed = ref('')
watch(
  () => [props.editor.selection, props.editor.selected?.lyric] as const,
  () => {
    typed.value = props.editor.selected?.lyric ?? ''
  },
)

function focusSyllable(): void {
  void nextTick(() => {
    const field = syllable.value?.$el
    field?.focus()
    field?.select()
  })
}

/** On to the next note, to type a line syllable by syllable. */
function submitSyllable(): void {
  const note = props.editor.selected
  if (!note) {
    return
  }
  const next = props.editor.type(typed.value, note.id)
  if (next !== null) {
    props.editor.selection = next
    focusSyllable()
  }
}

function step(by: number): void {
  const selected = props.editor.selection
  props.editor.selection = props.editor.neighbour(selected, by) ?? selected
}

function keydown(event: KeyboardEvent): void {
  const note = props.editor.selected
  if ((event.metaKey || event.ctrlKey) && event.key === 'z') {
    event.preventDefault()
    props.editor.undo()
  } else if (note && (event.metaKey || event.ctrlKey) && (event.key === 'Backspace' || event.key === 'Delete')) {
    event.preventDefault()
    props.editor.delete(note.id)
  }
}

// ---- Scrolling to the notes ----------------------------------------------------------------------------

/** The row the notes sit around, scrolled to when the editor appears or gets a melody. */
function centre(): void {
  const box = scroller.value
  if (!box) {
    return
  }
  const sorted = props.editor.notes.map((note) => note.pitch).sort((a, b) => a - b)
  const pitch = sorted.length ? sorted[Math.floor(sorted.length / 2)]! : 64
  box.scrollTop = Math.max(0, y(pitch) - box.clientHeight / 2)
}

watch(
  () => props.editor.notes.length === 0,
  () => void nextTick(centre),
)
watch(scroller, (box) => box && centre())

// ---- Hearing it ----------------------------------------------------------------------------------------

let context: AudioContext | null = null
let sources: AudioScheduledSourceNode[] = []
let frame = 0
const playing = ref(false)
const position = ref(0)

async function play(): Promise<void> {
  if (playing.value) {
    stop()
    return
  }
  if (props.editor.notes.length === 0) {
    return
  }
  // Made inside the click, which iOS requires; the sounds load meanwhile and play the default where they fail.
  context ??= new AudioContext()
  void context.resume()
  await loadSounds()
  const stored = trackSounds.value[trackKey('Vocal')]?.patch
  const patch = stored === undefined ? defaultPatch('Melody') : normalizePatch(stored, 'Melody')
  const output = context.createGain()
  output.gain.value = 0.8
  output.connect(context.destination)
  const begin = context.currentTime + 0.1
  for (const note of props.editor.notes) {
    playSynthNote(
      context,
      output,
      patch,
      {
        pitch: note.pitch,
        level: isSilent(note) ? 0.5 : 0.8,
        start: begin + props.editor.seconds(note.start),
        end: begin + props.editor.seconds(noteEnd(note)),
      },
      (source) => sources.push(source),
    )
  }
  playing.value = true
  const length = props.editor.seconds(props.editor.lengthInBeats) + 0.5
  const tick = () => {
    const elapsed = context!.currentTime - begin
    position.value = Math.max(0, elapsed)
    if (elapsed > length) {
      stop()
    } else {
      frame = requestAnimationFrame(tick)
    }
  }
  frame = requestAnimationFrame(tick)
}

function stop(): void {
  cancelAnimationFrame(frame)
  for (const source of sources) {
    try {
      source.stop()
    } catch {
      // Already ended.
    }
  }
  sources = []
  playing.value = false
}

watch(
  () => props.editor.revision,
  () => playing.value && stop(),
)
onBeforeUnmount(() => {
  stop()
  void context?.close()
})

const tempo = computed({
  get: () => props.editor.tempo,
  set: (value: number) => props.editor.setTempo(value),
})
const grids = [
  { value: 1, label: '1/4' },
  { value: 0.5, label: '1/8' },
  { value: 0.25, label: '1/16' },
  { value: 0.125, label: '1/32' },
]

defineExpose({ stop })
</script>

<template>
  <div class="flex flex-col gap-2" @keydown="keydown">
    <div class="flex flex-wrap items-center gap-2">
      <Button
        :label="playing ? t('singStop') : t('singPreview')"
        :icon="playing ? 'pi pi-stop' : 'pi pi-play'"
        size="small"
        outlined
        :disabled="editor.notes.length === 0"
        @click="play"
      />
      <Button
        icon="pi pi-undo"
        size="small"
        text
        :disabled="!editor.canUndo"
        :aria-label="t('singUndo')"
        v-tooltip="t('singUndo')"
        @click="editor.undo()"
      />
      <span class="flex items-center gap-1 text-sm">
        <NumberField v-model="tempo" :min="20" :max="400" class="w-16" :aria-label="t('midiTempo')" />
        <span class="muted">BPM</span>
      </span>
      <Select
        v-model="editor.grid"
        :options="grids"
        option-value="value"
        option-label="label"
        size="small"
        :aria-label="t('singGrid')"
        :title="t('singGrid')"
      />
      <Button
        icon="pi pi-search-minus"
        size="small"
        text
        :aria-label="t('singZoomOut')"
        @click="zoom = Math.max(0.4, zoom / 1.4)"
      />
      <Button
        icon="pi pi-search-plus"
        size="small"
        text
        :aria-label="t('singZoomIn')"
        @click="zoom = Math.min(4, zoom * 1.4)"
      />
    </div>

    <div ref="scroller" class="roll" data-testid="sing-roll">
      <div class="inner" :style="{ width: `${keysWidth + gridWidth}px`, height: `${gridHeight}px` }">
        <div class="keys" :style="{ width: `${keysWidth}px` }">
          <div
            v-for="pitch in pitches"
            :key="pitch"
            :class="['key', { black: black(pitch) }]"
            :style="{ height: `${rowHeight}px` }"
          >
            <span v-if="pitch % 12 === 0">{{ noteName(pitch) }}</span>
          </div>
        </div>
        <div
          class="grid-area"
          :style="{ left: `${keysWidth}px`, width: `${gridWidth}px`, height: `${gridHeight}px` }"
          @pointerdown="gridDown"
          @pointerup="gridUp"
        >
          <svg :width="gridWidth" :height="gridHeight" class="lines" aria-hidden="true">
            <template v-for="pitch in pitches" :key="pitch">
              <rect v-if="black(pitch)" x="0" :y="y(pitch)" :width="gridWidth" :height="rowHeight" class="row-black" />
              <rect
                v-if="pitch % 12 === 0"
                x="0"
                :y="y(pitch) + rowHeight - 0.5"
                :width="gridWidth"
                height="0.5"
                class="octave"
              />
            </template>
            <template v-for="beat in beatLines" :key="beat">
              <rect
                :x="x(beat)"
                y="0"
                :width="beat % 4 === 0 ? 1 : 0.5"
                :height="gridHeight"
                :class="beat % 4 === 0 ? 'bar' : 'beat'"
              />
              <text v-if="beat % 4 === 0" :x="x(beat) + 3" y="11" class="bar-number">{{ beat / 4 + 1 }}</text>
            </template>
            <line
              v-for="line in holdLines"
              :key="line.id"
              :x1="line.x1"
              :y1="line.y1"
              :x2="line.x2"
              :y2="line.y2"
              class="hold"
            />
            <rect
              v-if="playing"
              :x="x(editor.beats(position))"
              y="0"
              width="1.5"
              :height="gridHeight"
              class="playhead"
            />
          </svg>
          <div
            v-for="note in editor.notes"
            :key="note.id"
            :class="['note', { selected: editor.selection === note.id, silent: isSilent(note) }]"
            :style="noteStyle(note)"
            :title="noteName(note.pitch)"
            @pointerdown="noteDown($event, note)"
            @pointermove="noteMove"
            @pointerup="noteUp(note)"
            @pointercancel="drag = null"
          >
            <span class="syllable">{{ isHold(note) ? '–' : isSilent(note) ? '·' : trimmed(note) }}</span>
            <span v-if="editor.phonemes[note.id] && !isHold(note)" class="phonemes">{{
              editor.phonemes[note.id]
            }}</span>
          </div>
        </div>
      </div>
    </div>

    <div v-if="editor.selected" class="flex flex-wrap items-center gap-2">
      <Button icon="pi pi-chevron-left" size="small" text :aria-label="t('singNotePrevious')" @click="step(-1)" />
      <span class="mono min-w-9 text-sm">{{ noteName(editor.selected.pitch) }}</span>
      <InputText
        ref="syllable"
        v-model="typed"
        size="small"
        class="w-40"
        autocapitalize="off"
        autocomplete="off"
        spellcheck="false"
        :placeholder="t('singSyllable')"
        :aria-label="t('singSyllable')"
        @keydown.enter.prevent="submitSyllable"
        @blur="editor.selected && typed !== editor.selected.lyric && editor.type(typed, editor.selected.id)"
      />
      <Button
        :label="t('singHold')"
        :icon="isHold(editor.selected) ? 'pi pi-link' : 'pi pi-link'"
        size="small"
        :outlined="!isHold(editor.selected)"
        v-tooltip="t('singHoldHint')"
        @click="editor.toggleHold(editor.selected.id)"
      />
      <span v-if="editor.phonemes[editor.selected.id]" class="mono muted truncate text-xs">{{
        editor.phonemes[editor.selected.id]
      }}</span>
      <span class="flex-1" />
      <Button
        icon="pi pi-trash"
        size="small"
        text
        severity="danger"
        :aria-label="t('singNoteDelete')"
        v-tooltip="t('singNoteDelete')"
        @click="editor.delete(editor.selected.id)"
      />
      <Button icon="pi pi-chevron-right" size="small" text :aria-label="t('singNoteNext')" @click="step(1)" />
    </div>
    <small v-else class="muted">{{
      editor.notes.length === 0 ? t('singEditorEmpty') : t('singEditorCount', { n: editor.notes.length })
    }}</small>
  </div>
</template>

<style scoped>
.roll {
  position: relative;
  height: 330px;
  overflow: auto;
  border-radius: 0.5rem;
  background: var(--surface-sunken);
  touch-action: pan-x pan-y;
  overscroll-behavior: contain;
}
.inner {
  position: relative;
}
.keys {
  position: sticky;
  left: 0;
  z-index: 2;
  background: var(--surface-sunken);
}
.key {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  padding-right: 4px;
  font-size: 0.65rem;
  font-weight: 600;
  color: var(--text-muted, var(--p-text-muted-color));
  background: color-mix(in srgb, var(--p-text-color) 6%, transparent);
  border-bottom: 0.5px solid color-mix(in srgb, var(--p-text-muted-color) 25%, transparent);
}
.key.black {
  background: color-mix(in srgb, var(--p-text-color) 55%, transparent);
}
.grid-area {
  position: absolute;
  top: 0;
  cursor: crosshair;
}
.lines {
  position: absolute;
  inset: 0;
}
.row-black {
  fill: color-mix(in srgb, var(--p-text-color) 5%, transparent);
}
.octave {
  fill: color-mix(in srgb, var(--p-text-muted-color) 35%, transparent);
}
.bar {
  fill: color-mix(in srgb, var(--p-text-muted-color) 40%, transparent);
}
.beat {
  fill: color-mix(in srgb, var(--p-text-muted-color) 15%, transparent);
}
.bar-number {
  font-size: 10px;
  fill: var(--p-text-muted-color);
}
.hold {
  stroke: var(--accent);
  stroke-width: 1.5;
  opacity: 0.8;
}
.playhead {
  fill: var(--p-text-color);
}
.note {
  position: absolute;
  display: flex;
  align-items: center;
  border-radius: 4px;
  background: color-mix(in srgb, var(--accent) 70%, transparent);
  cursor: grab;
  touch-action: none;
  user-select: none;
}
.note::after {
  /* The edge that stretches the note. */
  content: '';
  position: absolute;
  top: 0;
  right: 0;
  bottom: 0;
  width: min(12px, 33%);
  cursor: ew-resize;
}
.note.selected {
  background: var(--accent);
  outline: 1.5px solid var(--p-text-color);
  z-index: 1;
}
.note.silent {
  background: color-mix(in srgb, var(--p-text-muted-color) 25%, transparent);
}
.syllable {
  padding: 0 4px;
  overflow: hidden;
  white-space: nowrap;
  font-size: 0.75rem;
  font-weight: 600;
  color: #000;
}
.note.silent .syllable {
  color: var(--p-text-muted-color);
}
.phonemes {
  position: absolute;
  top: -12px;
  left: 0;
  white-space: nowrap;
  font-family: ui-monospace, monospace;
  font-size: 9px;
  color: var(--p-text-muted-color);
  pointer-events: none;
}
.mono {
  font-family: ui-monospace, monospace;
}
</style>
