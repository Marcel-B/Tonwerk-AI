<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useConfirm } from 'primevue/useconfirm'
import { audioUrl, coverUrl, deleteVersion, render, scoreUrl, songScore, versionAudioUrl } from '../api'
import { formatDateTime, formatDuration, t, versionProgress } from '../i18n'
import { current, play, playing, versionTrack } from '../player'
import { ratingOf } from '../ratings'
import type { RunInfo, SongInfo, VersionState } from '../types'
import { keyLabel, tempoTag, unclearBelow } from '../traits'
import { imageFileName, openViewer } from '../imageViewer'
import { openVideo } from '../video'
import { octaveOptions, stepOptions, strengthOptions } from '../voiceChoices'
import PlaylistToggle from './PlaylistToggle.vue'
import SongActions from './SongActions.vue'
import SongNote from './SongNote.vue'

/** One song of a run in the library: play, rating, downloads, its actions, note, versions and score. */
const props = defineProps<{
  run: RunInfo
  song: SongInfo
  /** The song's address was opened (`#/songs/<run>/songN`), so it is marked. */
  focused: boolean
  /** The worker is on the song: it cannot be rendered again or deleted meanwhile. */
  busy: boolean
  voices: boolean
  stems: boolean
  /** Versions as the event stream reports them, newer than the listing while they are in the works. */
  liveVersions: VersionState[]
}>()

/** The cover large, to download, with the text it was painted with when it was painted. */
function viewCover(updatedAt: string): void {
  const title = props.run.title || t('untitled')
  openViewer({
    src: coverUrl(props.song.id, updatedAt),
    fileName: imageFileName(title, 'Cover'),
    title,
    coverOf: props.song.id,
  })
}

/** Tempo and key as heard (or as the score asked), "92 BPM · A-Moll". */
const sound = computed(() => {
  const traits = props.song.traits
  const parts = [traits?.tempo ? tempoTag(traits.tempo) : null, traits?.key ? keyLabel(traits.key) : null]
  return parts.filter((part) => part !== null).join(' · ')
})
const soundHint = computed(() => (props.song.traits?.from === 'audio' ? t('songTraitsHeard') : t('songTraitsScore')))
/** How much of the lyrics Whisper understood, in percent; null for a song not timed or without lyrics. */
const clarity = computed(() =>
  props.song.traits?.clarity == null ? null : Math.round(props.song.traits.clarity * 100),
)
const clarityHint = computed(() =>
  t(props.song.traits?.clarityFrom === 'vocals' ? 'songClarityVocals' : 'songClarityMix', { n: clarity.value ?? 0 }),
)

const emit = defineEmits<{
  /** The list starts it, since the songs listed below it are what plays next. */
  play: []
  rate: [rating: number | null]
  /** `warnings` only when the score came from a MIDI file, which then replaces the song's own. */
  useScore: [abc: string, warnings?: string[]]
  newSong: []
  sing: []
  delete: []
  error: [message: string]
}>()

const confirm = useConfirm()

function failed(caught: unknown): void {
  emit('error', caught instanceof Error ? caught.message : String(caught))
}

const isPlaying = computed(() => current.value?.id === props.song.id && playing.value)

async function renderFull(): Promise<void> {
  try {
    await render(props.song.id, 'full')
  } catch (caught) {
    failed(caught)
  }
}

function openVideoOf(): void {
  const { run, song } = props
  openVideo({
    songId: song.id,
    title: run.title,
    style: run.style,
    cover: song.coverUpdatedAt ? coverUrl(song.id, song.coverUpdatedAt) : undefined,
  })
}

// ---- Score: fetched once when opened or used; a song's score does not change once it is written ----------

const score = ref<string | null>(null)

async function loadScore(): Promise<string> {
  score.value ??= await songScore(props.song.id)
  return score.value
}

async function useScore(): Promise<void> {
  try {
    emit('useScore', await loadScore())
  } catch (caught) {
    failed(caught)
  }
}

async function toggleScore(event: Event): Promise<void> {
  if ((event.target as HTMLDetailsElement).open && score.value === null) {
    try {
      await loadScore()
    } catch (caught) {
      failed(caught)
    }
  }
}

// ---- Note: closed by default, the button shows whether one is written ------------------------------------

const noteOpen = ref(false)
/** Saved here before the library's reload brings it. */
const savedNote = ref<string | null>(null)
const note = computed(() => savedNote.value ?? props.song.note ?? '')

// Once the server lists what was saved here, its copy (which another browser may change later) is the one shown.
watch(
  () => props.song.note,
  (listed) => {
    if (savedNote.value !== null && (listed ?? '') === savedNote.value) {
      savedNote.value = null
    }
  },
)

// ---- Versions: the song sung with another voice ------------------------------------------------------

/** The song's versions, each in its latest state: the listing's, or the event stream's while one is in the works. */
const versions = computed(() => {
  const live = new Map(props.liveVersions.filter((v) => v.songId === props.song.id).map((v) => [v.id, v]))
  const listed = props.song.versions.map((v) => live.get(v.id) ?? v)
  const known = new Set(listed.map((v) => v.id))
  // Asked for since the library loaded; it reloads when a version is queued, but the event may come first.
  const added = [...live.values()].filter((v) => !known.has(v.id) && v.stage !== 'cancelled')
  return [...listed, ...added]
})

function isPlayingVersion(version: VersionState): boolean {
  return current.value?.id === `${props.song.id}@${version.id}` && playing.value
}

/** Stops one in the works right away; a finished one is audio of its own, so that asks first. */
function removeVersion(version: VersionState): void {
  const remove = async () => {
    try {
      await deleteVersion(props.song.id, version.id)
    } catch (caught) {
      failed(caught)
    }
  }
  if (!version.finished) {
    void remove()
    return
  }
  confirm.require({
    header: t('confirmDelete'),
    message: t('confirmDeleteVersion', {
      voice: version.voiceLabel,
      song: t('songN', { n: props.song.index }),
      title: props.run.title || t('untitled'),
    }),
    icon: 'pi pi-trash',
    rejectProps: { label: t('keep'), severity: 'secondary', outlined: true },
    acceptProps: { label: t('delete'), severity: 'danger' },
    accept: remove,
  })
}

const octaves = computed(octaveOptions)
const strengths = computed(strengthOptions)
const stepChoices = computed(stepOptions)

/**
 * What the version was made with, so versions of one song can be told apart later: the choices of the dialog in its
 * words where they match one, else the number, then when and with which separation model.
 */
function versionSettings(version: VersionState): string {
  const shift = version.semiToneShift
  const octave = octaves.value.find((o) => o.value === shift)
  const strength = strengths.value.find((s) => s.value === version.strength)?.label ?? String(version.strength)
  const quality =
    stepChoices.value.find((s) => s.value === version.diffusionSteps)?.label ?? String(version.diffusionSteps)
  return [
    octave?.label ?? t('semitones', { n: shift > 0 ? `+${shift}` : String(shift).replace('-', '−') }),
    t('settingStrength', { value: strength }),
    t('settingSteps', { value: quality }),
    version.keepReverb ? t('withReverb') : t('withoutReverb'),
    ...(version.stemModel ? [t('stemModel', { model: version.stemModel })] : []),
    formatDateTime(version.createdAt),
  ].join(' · ')
}

const severityByQuality: Record<string, string> = {
  draft: 'warning',
  full: 'success',
}
</script>

<template>
  <li :class="['song', { 'focused bg-emphasis': focused }]">
    <div class="flex flex-wrap gap-x-3 gap-y-1 items-center">
      <button
        v-if="song.coverUpdatedAt"
        type="button"
        class="cover"
        :aria-label="t('viewerOpen')"
        :title="t('viewerOpen')"
        @click="viewCover(song.coverUpdatedAt)"
      >
        <img
          :src="coverUrl(song.id, song.coverUpdatedAt)"
          alt=""
          loading="lazy"
          class="h-10 w-10 rounded-md object-cover"
        />
      </button>
      <Button
        v-if="song.hasAudio"
        :icon="isPlaying ? 'pi pi-pause' : 'pi pi-play'"
        rounded
        :outlined="current?.id !== song.id"
        size="small"
        :aria-label="isPlaying ? t('pause') : t('play')"
        @click="emit('play')"
      />
      <div>
        <strong>{{ t('songN', { n: song.index }) }}</strong>
      </div>
      <div v-if="song.seconds" class="muted">{{ formatDuration(song.seconds) }}</div>
      <Tag v-if="song.engine === 'ace'" severity="contrast" :title="t('aceBadgeHint')">{{ t('aceBadge') }}</Tag>
      <Tag v-if="song.quality" :severity="severityByQuality[song.quality]">
        {{ song.quality === 'draft' ? t('qualityDraft') : t('qualityFull') }}
      </Tag>
      <!-- Here rather than among the actions, which a phone hides in its menu. -->
      <Button
        v-if="song.videoFormats?.length"
        icon="pi pi-video"
        text
        rounded
        size="small"
        v-tooltip="t('songHasVideo')"
        :aria-label="t('songHasVideo')"
        @click="openVideoOf"
      />
      <div v-if="song.seed !== null" class="muted seed">#{{ song.seed }}</div>
      <div v-if="sound" class="muted text-sm" v-tooltip.top="soundHint">{{ sound }}</div>
      <div
        v-if="clarity !== null"
        class="text-sm"
        :class="clarity < unclearBelow * 100 ? 'unclear' : 'muted'"
        v-tooltip.top="clarityHint"
      >
        {{ t('songClarity', { n: clarity }) }}
      </div>
      <Rating
        :model-value="ratingOf(song.id)"
        :aria-label="t('rating')"
        class="ml-auto"
        @update:model-value="emit('rate', $event ?? null)"
      />
    </div>
    <div class="flex justify-between">
      <Button
        v-if="song.canRender && song.quality !== 'full'"
        :label="busy ? t('rendering') : t('renderFull')"
        :loading="busy"
        text
        size="small"
        :disabled="busy"
        class="whitespace-nowrap"
        @click="renderFull"
      />
      <div class="ml-auto flex items-center justify-end">
        <Button as="a" text v-if="song.hasAudio" size="small" :href="audioUrl(song.id, true)">{{
          t('download')
        }}</Button>
        <Button as="a" text v-if="song.hasScore" size="small" :href="scoreUrl(song.id)">{{ t('score') }}</Button>
        <!-- Filled while a note is written, so it shows with the note closed. -->
        <Button
          icon="pi pi-comment"
          :text="!note"
          size="small"
          rounded
          :severity="note ? undefined : 'secondary'"
          v-tooltip="note ? t('songNoteHas') : t('songNote')"
          :aria-label="note ? t('songNoteHas') : t('songNote')"
          :aria-expanded="noteOpen"
          @click="noteOpen = !noteOpen"
        />
        <PlaylistToggle v-if="song.hasAudio" :song-id="song.id" size="small" @error="emit('error', $event)" />
        <SongActions
          :run="run"
          :song="song"
          :busy="busy"
          :voices="voices"
          :stems="stems"
          @use-score="useScore"
          @use-midi="(abc, warnings) => emit('useScore', abc, warnings)"
          @new-song="emit('newSong')"
          @sing="emit('sing')"
          @delete="emit('delete')"
          @error="emit('error', $event)"
        />
      </div>
    </div>
    <SongNote
      v-if="noteOpen"
      :song-id="song.id"
      :note="note"
      @saved="savedNote = $event"
      @error="emit('error', $event)"
    />
    <ul v-if="versions.length > 0" class="versions">
      <li v-for="version in versions" :key="version.id" class="flex flex-wrap items-center gap-x-2">
        <Button
          v-if="version.stage === 'done'"
          :icon="isPlayingVersion(version) ? 'pi pi-pause' : 'pi pi-play'"
          rounded
          text
          size="small"
          :aria-label="isPlayingVersion(version) ? t('pause') : t('play')"
          @click="play(versionTrack(run, song, version))"
        />
        <span class="pi pi-user muted px-2" v-else aria-hidden="true" />
        <span>{{ version.voiceLabel }}</span>
        <Tag v-if="version.stage !== 'done'" :severity="version.stage === 'failed' ? 'danger' : undefined">
          {{ t(`versionStage_${version.stage}`) }}
          {{ versionProgress(version) }}
        </Tag>
        <div class="ml-auto flex">
          <Button
            v-if="version.stage === 'done'"
            as="a"
            text
            size="small"
            :href="versionAudioUrl(song.id, version.id, true)"
            >{{ t('download') }}</Button
          >
          <Button
            :icon="version.finished ? 'pi pi-trash' : 'pi pi-times'"
            text
            rounded
            size="small"
            :severity="version.finished ? 'danger' : 'secondary'"
            v-tooltip="version.finished ? t('deleteVersion') : t('cancel')"
            :aria-label="version.finished ? t('deleteVersion') : t('cancel')"
            @click="removeVersion(version)"
          />
        </div>
        <span class="muted basis-full pb-1 text-xs">{{ versionSettings(version) }}</span>
        <span v-if="version.stage === 'failed' && version.message" class="danger basis-full text-sm">
          {{ version.message }}
        </span>
      </li>
    </ul>
    <!-- One player for the whole page (PlayerBar.vue), so the song keeps playing on the other pages. -->
    <span v-if="!song.hasAudio" class="muted">{{ t('noAudio') }}</span>
    <details v-if="song.hasScore" class="score" @toggle="toggleScore">
      <summary>{{ t('showScore') }}</summary>
      <pre>{{ score ?? '…' }}</pre>
    </details>
  </li>
</template>

<style scoped>
.cover {
  display: block;
  padding: 0;
  border: 0;
  background: none;
  cursor: zoom-in;
}
.song {
  display: flex;
  flex-direction: column;
  gap: 0.4rem;
}

.focused {
  margin: -0.5rem;
  padding: 0.5rem;
  border-radius: var(--radius-small);
}

.versions {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
  margin: 0;
  padding: 0 0 0 0.75rem;
  border-left: 2px solid var(--border);
  font-size: 0.9rem;
  list-style: none;
}

.score {
  font-size: 0.9rem;
}

.score summary {
  cursor: pointer;
  color: var(--accent);
}

.score pre {
  max-height: 20rem;
  margin: 0.5rem 0 0;
  padding: 0.5rem 0.75rem;
  overflow: auto;
  border-radius: var(--radius-small);
  background: var(--surface-sunken);
  font-family: var(--font-mono);
  font-size: 0.8rem;
  white-space: pre-wrap;
}

.seed {
  font-family: var(--font-mono);
  font-size: 0.8rem;
}

.unclear {
  color: var(--warning-text);
}
</style>
