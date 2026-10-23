<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import Checkbox from 'primevue/checkbox'
import { useConfirm } from 'primevue/useconfirm'
import InputGroup from 'primevue/inputgroup'
import InputGroupAddon from 'primevue/inputgroupaddon'
import Message from 'primevue/message'
import {
  ApiError,
  assignInstrument,
  convertScore,
  createSession,
  deleteSession,
  exportLogicProject,
  exportMusicXml,
  type LogicProgress,
  listAssignments,
  listSessions,
  LogicExportError,
  type ScoreSource,
  sessionFile,
  setSessionAudio,
  type StemParts,
  type StoredSession,
  updateSession,
} from '../logic/api'
import { t } from '../logic/i18n'
import { instrumentsForExport, withDrumNotes, withInstrumentChannels } from '../logic/instruments'
import { instruments, reloadInstruments } from '../logic/instrumentLibrary'
import { navigate } from '../view'
import { deletePreset, loadPresets, savePreset, type Preset } from '../logic/presets'
import { clearFormState, defaultFormState, loadFormState, saveFormState, toConversionOptions } from '../logic/options'
import { baseName, download, playableVoices } from '../logic/score'
import {
  lastSession,
  readSettings,
  rememberSession,
  sessionForm,
  sessionOfSong,
  settingsOf,
  type PageState,
  type SessionSettings,
} from '../logic/sessions'
import type { SynthPatch } from '../logic/synth'
import { applyMixer, loadSounds, mixer, resetTrackSound, setTrackSound, trackKey, trackSounds } from '../logic/synths'
import type { Assignments, ConversionResult, Diagnostic } from '../logic/types'
import { audioUrl, listStems } from '../api'
import { loadMidiInfo, midiInfo } from '../audioMidi'
import { locale } from '../i18n'
import type { RunInfo, SongInfo, StemSetState } from '../types'
import { handedMidi, logicSong, openOnSingPage, openStems, replaceLogicSong } from '../view'
import { singHandOff } from '../singing'
import FileDropZone from './logic/FileDropZone.vue'
import OptionsForm from './logic/OptionsForm.vue'
import ResultView from './logic/ResultView.vue'
import ScorePreview, { type Listening } from './logic/ScorePreview.vue'
import SessionBar from './logic/SessionBar.vue'

/**
 * yue-to-logic-pro's page inside YuE UI: a score becomes a MIDI file and, with its audio, a Logic Pro project, with
 * every arrangement option, the instrument library and a MIDI preview. The score is a song of the library, which the
 * server reads itself, or a score.abc brought along, as on the original page.
 */
const props = defineProps<{
  runs: RunInfo[]
  /** Stem sets in the works, as the event stream reports them; laid over the list loaded here. */
  stems: StemSetState[]
  /** Whether this server can separate stems at all; without it there is no button to ask for them. */
  stemsConfigured: boolean
}>()

type SourceMode = 'library' | 'upload'
/** A song of the library is the usual source here; a score.abc from elsewhere is the exception. */
const mode = ref<SourceMode>('library')
const modes = computed(() => [
  { label: t('sourceLibrary'), value: 'library' },
  { label: t('sourceUpload'), value: 'upload' },
])

// ---- Library song -----------------------------------------------------------------------------------

/** Every song that has a score, labelled "<title> · songN"; one without a score has nothing to convert. */
const songOptions = computed(() =>
  props.runs.flatMap((run) =>
    run.songs
      .filter((song) => song.hasScore)
      .map((song) => ({ label: `${run.title || t('untitled')} · ${t('songN', { n: song.index })}`, value: song.id })),
  ),
)
const librarySongs = computed(
  () => new Map(props.runs.flatMap((run) => run.songs.map((song) => [song.id, { run, song }] as const))),
)
const songId = ref<string | null>(logicSong.value)
const chosen = computed<{ run: RunInfo; song: SongInfo } | null>(() =>
  songId.value ? (librarySongs.value.get(songId.value) ?? null) : null,
)
/** A song the address names that the library does not list (deleted, or the library is not loaded yet). */
const songMissing = computed(() => songId.value !== null && props.runs.length > 0 && !chosen.value)

/** A file name out of a title: the characters a file system refuses would otherwise reach the download. */
function fileNameOf(title: string): string {
  return title.replace(/[\\/:*?"<>|]+/g, '-').trim() || 'score'
}

function pickSong(id: string | null): void {
  songId.value = id
  replaceLogicSong(id)
  forgetResult()
  const found = id ? librarySongs.value.get(id) : undefined
  if (found) {
    outputName.value = fileNameOf(found.run.title)
  }
  followSong(id)
}

/** A song opened from the library is converted as soon as the page can, so its preview is there to adjust. */
let convertWhenReady = songId.value !== null

// An address with a song (a link from the library) picks it, also while the page is already open.
watch(logicSong, (id) => {
  if (id && id !== songId.value) {
    mode.value = 'library'
    pickSong(id)
    convertWhenReady = true
  }
})

// The library arrives after the page: a song picked by address gets its name once its title is known.
watch(chosen, (found, before) => {
  if (found && !before && outputName.value === 'score') {
    outputName.value = fileNameOf(found.run.title)
  }
})

// ---- Uploaded files ---------------------------------------------------------------------------------

const file = ref<File | null>(null)
const audio = ref<File | null>(null)
/** Counts the files chosen, so a session knows the upload it belongs to even when the same file is chosen again. */
const uploadSerial = ref(0)

function selectFile(selected: File): void {
  file.value = selected
  uploadSerial.value++
  outputName.value = baseName(selected.name)
  forgetResult()
  detach()
}

function selectAudio(selected: File | null): void {
  audio.value = selected
  logicError.value = null
  logicWarnings.value = []
  void keepAudio(selected)
}

/** What the server is asked to convert, or null while the chosen way has nothing to offer. */
const source = computed<ScoreSource | null>(() => {
  if (mode.value === 'library') {
    return chosen.value ? { song: chosen.value.song.id } : null
  }
  return file.value ? { file: file.value, audio: audio.value } : null
})
/** The recording the preview shows and plays along: the library song's audio.flac, or the FLAC brought along. */
const recording = computed<string | File | null>(() =>
  mode.value === 'library' ? (chosen.value?.song.hasAudio ? audioUrl(chosen.value.song.id) : null) : audio.value,
)
/** A library song brings its own audio.flac, which the server takes from the song's folder. */
const hasAudio = computed(() => (mode.value === 'library' ? !!chosen.value?.song.hasAudio : audio.value !== null))

watch(mode, () => forgetResult())

// ---- Stems ------------------------------------------------------------------------------------------

/**
 * The template has two audio tracks for the vocals beside the mix, which yue-to-logic-pro's page filled from
 * StemMyWav; here a set made on the voices page fills them. The list is loaded when a song is picked, the live
 * sets from the event stream win, since they are at least as new.
 */
const loadedStems = ref<StemSetState[]>([])
const stemSetId = ref<string | null>(null)
const vocalStems = new Set(['vocals', 'vocals_dry'])

const songStems = computed(() => {
  const id = chosen.value?.song.id
  if (!id) {
    return []
  }
  const sets = new Map(loadedStems.value.map((set) => [set.id, set]))
  for (const set of props.stems) {
    sets.set(set.id, set)
  }
  return [...sets.values()].filter((set) => set.songId === id)
})
/** Finished sets with vocals, newest first: only those have anything for the project's vocal tracks. */
const usableStems = computed(() =>
  songStems.value
    .filter((set) => set.stage === 'done' && set.stems.some((stem) => vocalStems.has(stem.name)))
    .sort((a, b) => b.createdAt.localeCompare(a.createdAt)),
)
const stemsRunning = computed(() => songStems.value.some((set) => !set.finished))
const stemOptions = computed(() => [
  { label: t('stemsNone'), value: null },
  ...usableStems.value.map((set) => ({
    label: t('stemsSet', {
      model: set.model,
      date: new Date(set.createdAt).toLocaleString(locale.value, { dateStyle: 'short', timeStyle: 'short' }),
    }),
    value: set.id,
  })),
])

async function loadStems(): Promise<void> {
  try {
    loadedStems.value = await listStems()
  } catch {
    // Without the list the export simply has no stems to offer.
    loadedStems.value = []
  }
}

watch(
  () => chosen.value?.song.id,
  (id) => {
    stemSetId.value = null
    if (id) {
      void loadStems()
    }
  },
  { immediate: true },
)

/** The set a session being opened had chosen (null for none), taken once the song's sets are known. */
let wantedStems: string | null | undefined = undefined

function takeWantedStems(sets: StemSetState[]): boolean {
  if (wantedStems === undefined || sets.length === 0) {
    return false
  }
  const wanted = wantedStems
  stemSetId.value = wanted === null ? null : sets.some((set) => set.id === wanted) ? wanted : sets[0]!.id
  wantedStems = undefined
  return true
}

// The newest set is taken, also one that finishes while the page is open; one deleted meanwhile is let go.
watch(usableStems, (sets, before) => {
  if (takeWantedStems(sets)) {
    return
  }
  const known = new Set((before ?? []).map((set) => set.id))
  const fresh = sets.find((set) => !known.has(set.id))
  if (fresh) {
    stemSetId.value = fresh.id
  } else if (stemSetId.value !== null && !sets.some((set) => set.id === stemSetId.value)) {
    stemSetId.value = null
  }
})

// ---- Bass and drums from stems ----------------------------------------------------------------------

/**
 * The bass (and, as a sketch, kick, snare and hi-hat) heard in a stem instead of the ones generated from the chords.
 * The set picked for the vocals is taken where it has the stem, else the newest that does: only the four- and
 * six-stem models (htdemucs, …) separate a bass and drums. Whether to is remembered per browser.
 */
const PARTS_KEY = 'yue-ui.logicStemParts'
function loadPartChoice(): { bass: boolean; drums: boolean } {
  try {
    const stored = JSON.parse(localStorage.getItem(PARTS_KEY) ?? '{}') as { bass?: unknown; drums?: unknown }
    return { bass: stored.bass === true, drums: stored.drums === true }
  } catch {
    return { bass: false, drums: false }
  }
}
const partChoice = ref(loadPartChoice())
watch(
  partChoice,
  (value) => {
    try {
      localStorage.setItem(PARTS_KEY, JSON.stringify(value))
    } catch {
      // Then it is forgotten with the page.
    }
    if (result.value) {
      stale.value = true
    }
  },
  { deep: true },
)

function setWith(stem: string): StemSetState | null {
  const sets = songStems.value
    .filter((set) => set.stage === 'done' && set.stems.some((file) => file.name === stem))
    .sort((a, b) => b.createdAt.localeCompare(a.createdAt))
  return sets.find((set) => set.id === stemSetId.value) ?? sets[0] ?? null
}
const bassSet = computed(() => setWith('bass'))
const drumSet = computed(() => setWith('drums'))

/** What goes along with a conversion; only for a library song, and only where Basic Pitch is installed. */
function stemParts(): StemParts | null {
  if (mode.value !== 'library' || !midiInfo.value?.installed) {
    return null
  }
  const bass = partChoice.value.bass ? (bassSet.value?.id ?? null) : null
  const drums = partChoice.value.drums ? (drumSet.value?.id ?? null) : null
  return bass || drums ? { bass, drums } : null
}

void loadMidiInfo()

function createStems(): void {
  if (chosen.value) {
    openStems(chosen.value.song.id)
  }
}

// Waits for the library as well, which may arrive after the page when it is opened by address.
watch(
  source,
  (from) => {
    if (convertWhenReady && from && mode.value === 'library') {
      convertWhenReady = false
      void convert()
    }
  },
  { flush: 'post' },
)

function forgetResult(): void {
  pending?.abort()
  pending = null
  busy.value = false
  result.value = null
  stale.value = false
  error.value = null
  logicError.value = null
  logicWarnings.value = []
  musicXmlError.value = null
}

// ---- Instruments ------------------------------------------------------------------------------------

/**
 * Which track plays which instrument. This is configuration, not playback: it decides the channel a track is
 * written on and the hardware the Logic project addresses, both of which the server produces. It therefore
 * applies in every browser - only sending the preview to a MIDI port needs Web MIDI, which Safari lacks.
 */
const assignments = ref<Assignments>({})
const instrumentsError = ref<string | null>(null)
const instrumentsLoaded = loadInstruments()

async function loadInstruments(): Promise<void> {
  try {
    ;[, assignments.value] = await Promise.all([reloadInstruments(), listAssignments()])
  } catch (caught) {
    // Without the library everything else still works; the routing table then shows ports and channels only.
    instrumentsError.value = t('instrumentsError', {
      message: caught instanceof Error ? caught.message : String(caught),
    })
  }
}

/** The library is kept on its own page, where the instruments are also played. */
function openInstruments(): void {
  navigate('instruments')
}

/**
 * A vocal track to the singing page, with the library song's lyrics (only what is sung); an uploaded score brings no
 * words, so its notes go alone.
 */
function singTrack(track: string): void {
  const score = result.value?.score
  if (!score) {
    return
  }
  const song = mode.value === 'library' ? chosen.value : null
  const name = song?.run.title || score.title || (file.value ? baseName(file.value.name) : '') || t('untitled')
  openOnSingPage(singHandOff(score, track, song?.run.lyrics ?? '', `${name} · ${track}`))
}

// A track whose instrument was deleted on the instruments page loses its assignment, as on the server.
watch(instruments, (list) => {
  const ids = new Set(list.map((instrument) => instrument.id))
  assignments.value = Object.fromEntries(Object.entries(assignments.value).filter(([, id]) => ids.has(id)))
})

/** Shown at once and sent to the server; if that fails the previous choice comes back. */
async function assign(track: string, instrumentId: number | null): Promise<void> {
  const before = { ...assignments.value }
  const next = { ...assignments.value }
  if (instrumentId === null) {
    delete next[track]
  } else {
    next[track] = instrumentId
  }
  assignments.value = next
  instrumentsError.value = null
  try {
    await assignInstrument(track, instrumentId)
  } catch (caught) {
    assignments.value = before
    instrumentsError.value =
      caught instanceof ApiError && caught.status === 0
        ? t('networkError')
        : t('instrumentsError', { message: caught instanceof Error ? caught.message : String(caught) })
  }
}

/** The channel a track plays on depends on its instrument, so the downloads are only current for the assignment they were made with. */
watch(assignments, () => {
  if (result.value) {
    stale.value = true
  }
})

/**
 * The drums are generated on the notes of the drum machine their tracks play, so a preview made before a track got
 * its machine (or before the assignments arrived from the server, which a song opened from the library outruns)
 * would send General MIDI's notes to it: wrong drums. A change in those notes therefore converts again at once,
 * unlike other changes, which only mark the result stale.
 */
const drumNotesKey = computed(() =>
  JSON.stringify(
    withDrumNotes(toConversionOptions(form.value), assignments.value, instruments.value).arrangement.drums?.notes ??
      null,
  ),
)
let convertedDrumNotes: string | null = null
// Only the assignments and the library trigger it; a form change keeps marking the result stale as before.
watch([assignments, instruments], () => {
  if (result.value && drumNotesKey.value !== convertedDrumNotes) {
    void convert()
  }
})

// ---- Presets ----------------------------------------------------------------------------------------

/** The presets, kept on the server like the instruments; loaded once, and again after every change. */
const presets = ref<Preset[]>([])
/** The preset the form currently shows; empty once a preset is saved under a new name or none is chosen. */
const presetName = ref('')
const newPresetName = ref('')
const presetsBusy = ref(false)
const presetsError = ref<string | null>(null)
void withPresets(() => loadPresets())

/** Runs a change of the presets and shows what came back; the form itself is unaffected by a failure. */
async function withPresets(change: () => Promise<Preset[]>): Promise<boolean> {
  presetsBusy.value = true
  presetsError.value = null
  try {
    presets.value = await change()
    return true
  } catch (caught) {
    presetsError.value =
      caught instanceof ApiError && caught.status === 0
        ? t('networkError')
        : t('presetsError', { message: caught instanceof Error ? caught.message : String(caught) })
    return false
  } finally {
    presetsBusy.value = false
  }
}

function applyPreset(name: string | null): void {
  // The select's clear button hands over null; no preset chosen is an empty name, as before.
  presetName.value = name ?? ''
  const preset = presets.value.find((entry) => entry.name === presetName.value)
  if (preset) {
    form.value = { ...preset.form }
  }
}

async function storePreset(): Promise<void> {
  const name = newPresetName.value
  if (await withPresets(() => savePreset(name, form.value))) {
    // The server keeps the name as saved; pick it as it now stands so the select shows it.
    presetName.value = presets.value.find((entry) => entry.name.toLowerCase() === name.toLowerCase())?.name ?? ''
    newPresetName.value = ''
  }
}

async function removePreset(): Promise<void> {
  const name = presetName.value
  if (await withPresets(() => deletePreset(name))) {
    presetName.value = ''
  }
}

// ---- Conversion and export --------------------------------------------------------------------------

const logicBusy = ref(false)
const logicProgress = ref<LogicProgress | null>(null)
const logicError = ref<string | null>(null)
const logicWarnings = ref<Diagnostic[]>([])
const form = ref(loadFormState())
const outputName = ref(chosen.value ? fileNameOf(chosen.value.run.title) : 'score')
const result = ref<ConversionResult | null>(null)
const stale = ref(false)
const busy = ref(false)
const error = ref<string | null>(null)

let pending: AbortController | null = null

watch(
  form,
  (value) => {
    saveFormState(value)
    if (result.value) {
      stale.value = true
    }
  },
  { deep: true },
)

/** The form's options with every assigned track on its instrument's channel, and the drums on a drum machine's notes. */
function conversionOptions() {
  const options = withInstrumentChannels(toConversionOptions(form.value), assignments.value, instruments.value)
  return withDrumNotes(options, assignments.value, instruments.value)
}

async function convert(): Promise<void> {
  const from = source.value
  if (!from) {
    return
  }

  pending?.abort()
  const controller = new AbortController()
  pending = controller
  busy.value = true
  error.value = null

  try {
    const options = conversionOptions()
    const drumNotes = drumNotesKey.value
    result.value = await convertScore(from, options, controller.signal, stemParts())
    convertedDrumNotes = drumNotes
    stale.value = false
    // The assignments may have arrived while the score was on its way.
    if (drumNotesKey.value !== drumNotes) {
      void convert()
    }
  } catch (caught) {
    if (caught instanceof DOMException && caught.name === 'AbortError') {
      return
    }
    result.value = null
    error.value =
      caught instanceof ApiError && caught.status === 0
        ? t('networkError')
        : t('requestError', { message: caught instanceof Error ? caught.message : String(caught) })
  } finally {
    if (pending === controller) {
      busy.value = false
      pending = null
    }
  }
}

async function exportLogic(): Promise<void> {
  const from = source.value
  if (!from) {
    return
  }

  logicBusy.value = true
  logicError.value = null
  logicWarnings.value = []
  try {
    const exported = await exportLogicProject(
      from,
      conversionOptions(),
      outputName.value,
      form.value.splitSections,
      instrumentsForExport(assignments.value, instruments.value),
      mode.value === 'library' ? stemSetId.value : null,
      (progress) => (logicProgress.value = progress),
      stemParts(),
    )
    logicWarnings.value = exported.warnings
    download(exported.zip, exported.fileName)
  } catch (caught) {
    logicError.value =
      caught instanceof LogicExportError
        ? `${t('logicFailed')}: ${caught.message}`
        : caught instanceof ApiError && caught.status === 0
          ? t('networkError')
          : `${t('logicFailed')}: ${caught instanceof Error ? caught.message : String(caught)}`
  } finally {
    logicBusy.value = false
    logicProgress.value = null
  }
}

const musicXmlBusy = ref(false)
const musicXmlError = ref<string | null>(null)

async function downloadMusicXml(): Promise<void> {
  const from = source.value
  if (!from) {
    return
  }

  musicXmlBusy.value = true
  musicXmlError.value = null
  try {
    download(
      await exportMusicXml(from, conversionOptions(), outputName.value, stemParts()),
      `${outputName.value}.musicxml`,
    )
  } catch (caught) {
    musicXmlError.value =
      caught instanceof ApiError && caught.status === 0
        ? t('networkError')
        : `${t('musicXmlFailed')}: ${caught instanceof Error ? caught.message : String(caught)}`
  } finally {
    musicXmlBusy.value = false
  }
}

/** Back to a fresh start: no song or files, default parameters, no result. */
function reset(): void {
  detach()
  declinedSource = null
  rememberSession(null)
  forgetResult()
  file.value = null
  audio.value = null
  songId.value = null
  replaceLogicSong(null)
  form.value = defaultFormState()
  presetName.value = ''
  newPresetName.value = ''
  clearFormState()
  outputName.value = 'score'
  logicBusy.value = false
}

// ---- Sessions ---------------------------------------------------------------------------------------

/**
 * The page's state for one song or uploaded score, saved on the server as it changes (once the score was converted)
 * and opened again from the list, in this browser or another, or in the app. Opening a library song goes on with its
 * newest session; a reload opens the last one this browser worked on.
 */
const sessions = ref<StoredSession[]>([])
const sessionsLoaded = ref(false)
const sessionsError = ref<string | null>(null)
const session = ref<StoredSession | null>(null)
/** The source the session belongs to (`song:<id>` or `file:<n>`); a session is only saved while that is shown. */
let sessionSource: string | null = null
/** The source whose session was just deleted: none is made for it again until another source is chosen. */
let declinedSource: string | null = null
/** Mute and solo as the preview reports them; a session being opened hands its own to the preview. */
const listening = ref<Listening | null>(null)
let restoring = false
let creating = false
let lastSaved = ''
let saveTimer = 0

const sourceKey = computed(() =>
  mode.value === 'library'
    ? songId.value
      ? `song:${songId.value}`
      : null
    : file.value
      ? `file:${uploadSerial.value}`
      : null,
)
const attached = computed(() => (session.value && sessionSource === sourceKey.value ? session.value : null))

const trackIds = computed(() =>
  result.value?.score ? playableVoices(result.value.score, form.value.includeChords).map((voice) => voice.id) : null,
)
const pageState = computed<PageState>(() => ({
  form: form.value,
  outputName: outputName.value,
  stems: mode.value === 'library' && usableStems.value.length > 0 ? stemSetId.value : undefined,
  stemParts: partChoice.value,
  tracks:
    trackIds.value && listening.value
      ? {
          ids: trackIds.value,
          muted: listening.value.muted,
          solo: listening.value.solo,
          mixer: mixer.value,
          sounds: trackSounds.value,
          instruments: assignments.value,
        }
      : null,
}))
const settingsText = computed(() => JSON.stringify(settingsOf(pageState.value, attached.value?.settings)))

watch([settingsText, () => result.value?.score], () => {
  if (!restoring) {
    window.clearTimeout(saveTimer)
    saveTimer = window.setTimeout(() => void saveSession(), 1000)
  }
})

function sessionsFailed(caught: unknown): void {
  sessionsError.value =
    caught instanceof ApiError && caught.status === 0
      ? t('networkError')
      : t('sessionsError', { message: caught instanceof Error ? caught.message : String(caught) })
}

function keepInList(stored: StoredSession): void {
  sessions.value = [stored, ...sessions.value.filter((entry) => entry.id !== stored.id)]
  if (session.value?.id === stored.id) {
    session.value = stored
  }
}

/** Saves into the attached session, or makes one once the score is converted; `keepalive` as the page goes away. */
async function saveSession(keepalive = false): Promise<void> {
  saveTimer = 0
  const key = sourceKey.value
  if (restoring || !sessionsLoaded.value || !key) {
    return
  }
  const current = attached.value
  const settings = settingsOf(pageState.value, current?.settings)
  const text = JSON.stringify(settings)
  try {
    if (current) {
      if (text === lastSaved) {
        return
      }
      lastSaved = text
      keepInList(await updateSession(current.id, { settings }, keepalive))
    } else if (result.value?.score && key !== declinedSource && !creating) {
      creating = true
      try {
        const name = sessionNameOf()
        let created = await createSession(
          name,
          mode.value === 'library' ? { song: songId.value! } : { file: file.value! },
          settings,
        )
        if (mode.value === 'upload' && audio.value) {
          await setSessionAudio(created.id, audio.value)
          created = { ...created, audioFile: audio.value.name }
        }
        sessions.value = [created, ...sessions.value]
        if (sourceKey.value === key) {
          session.value = created
          sessionSource = key
          lastSaved = text
          rememberSession(created.id)
        }
      } finally {
        creating = false
      }
    }
    sessionsError.value = null
  } catch (caught) {
    lastSaved = ''
    sessionsFailed(caught)
  }
}

function sessionNameOf(): string {
  if (mode.value === 'upload') {
    return file.value ? baseName(file.value.name) : t('sessionUntitled')
  }
  return chosen.value
    ? `${chosen.value.run.title || t('untitled')} · ${t('songN', { n: chosen.value.song.index })}`
    : t('sessionUntitled')
}

function detach(): void {
  if (restoring) {
    return
  }
  session.value = null
  sessionSource = null
  listening.value = null
  wantedStems = undefined
}

/** A library song picked by hand or by address goes on with its newest session, if it has one. */
function followSong(id: string | null): void {
  if (restoring) {
    return
  }
  const found = id && sessionsLoaded.value ? sessionOfSong(sessions.value, id) : null
  if (found) {
    void openSession(found)
  } else {
    detach()
  }
}

/** Sounds, instruments and mixer belong to track names for every song; a session lays its song's back over them. */
async function layOver(settings: SessionSettings): Promise<void> {
  await Promise.all([loadSounds(), instrumentsLoaded])
  for (const [track, sound] of Object.entries(settings.sounds ?? {})) {
    const current = trackSounds.value[trackKey(track)]
    if (sound === null) {
      if (current) {
        void resetTrackSound(track)
      }
    } else if (JSON.stringify(current ?? null) !== JSON.stringify({ patch: sound.patch, preset: sound.preset })) {
      setTrackSound(track, sound.patch as SynthPatch, sound.preset)
    }
  }
  const known = new Set(instruments.value.map((instrument) => instrument.id))
  for (const [track, id] of Object.entries(settings.instruments ?? {})) {
    if ((assignments.value[track] ?? null) !== id && (id === null || known.has(id))) {
      await assign(track, id)
    }
  }
  if (settings.mixer) {
    applyMixer(settings.mixer)
  }
}

async function openSession(target: StoredSession): Promise<void> {
  window.clearTimeout(saveTimer)
  restoring = true
  convertWhenReady = false
  try {
    const settings = readSettings(target.settings)
    forgetResult()
    if (target.songId) {
      mode.value = 'library'
      songId.value = target.songId
      replaceLogicSong(target.songId)
      sessionSource = `song:${target.songId}`
    } else {
      const [score, recorded] = await Promise.all([
        sessionFile(target.id, 'file', target.scoreFile ?? 'score.abc'),
        target.audioFile ? sessionFile(target.id, 'audio', target.audioFile) : Promise.resolve(null),
      ])
      mode.value = 'upload'
      songId.value = null
      replaceLogicSong(null)
      file.value = score
      audio.value = recorded
      uploadSerial.value++
      sessionSource = `file:${uploadSerial.value}`
    }
    form.value = sessionForm(settings)
    presetName.value = ''
    partChoice.value = settings.stemParts ?? { bass: false, drums: false }
    listening.value = { muted: settings.muted ?? [], solo: settings.solo ?? [] }
    outputName.value =
      settings.outputName ||
      (target.scoreFile ? baseName(target.scoreFile) : chosen.value ? fileNameOf(chosen.value.run.title) : 'score')
    wantedStems = settings.stems
    session.value = target
    declinedSource = null
    rememberSession(target.id)
    await layOver(settings)
    // The watchers of the mode and the song run first, so they neither forget this conversion nor reset the stems.
    await nextTick()
    takeWantedStems(usableStems.value)
    lastSaved = settingsText.value
  } catch (caught) {
    restoring = false
    detach()
    sessionsFailed(caught)
    return
  } finally {
    restoring = false
  }
  await convert()
}

async function openSessionById(id: number): Promise<void> {
  const found = sessions.value.find((entry) => entry.id === id)
  if (found && found.id !== attached.value?.id) {
    await openSession(found)
  }
}

async function renameSession(name: string): Promise<void> {
  const current = attached.value
  if (!current || !name || name === current.name) {
    return
  }
  try {
    keepInList(await updateSession(current.id, { name }))
    sessionsError.value = null
  } catch (caught) {
    sessionsFailed(caught)
  }
}

const confirm = useConfirm()

function askDeleteSession(): void {
  const current = attached.value
  if (!current) {
    return
  }
  confirm.require({
    header: t('sessionDelete'),
    message: t('sessionDeleteConfirm', { name: current.name }),
    icon: 'pi pi-trash',
    rejectProps: { label: t('sessionKeep'), severity: 'secondary', outlined: true },
    acceptProps: { label: t('presetDelete'), severity: 'danger' },
    accept: async () => {
      try {
        await deleteSession(current.id)
        sessions.value = sessions.value.filter((entry) => entry.id !== current.id)
        if (session.value?.id === current.id) {
          // The page stays as it is; a session for it is only made again after another source was chosen.
          declinedSource = sessionSource
          session.value = null
          sessionSource = null
          rememberSession(null)
        }
        sessionsError.value = null
      } catch (caught) {
        sessionsFailed(caught)
      }
    },
  })
}

/** An uploaded recording goes into the session too, so the preview plays it along when the session is opened again. */
async function keepAudio(selected: File | null): Promise<void> {
  const current = attached.value
  if (restoring || !current || mode.value !== 'upload') {
    return
  }
  try {
    await setSessionAudio(current.id, selected)
    keepInList({ ...current, audioFile: selected?.name ?? null })
  } catch (caught) {
    sessionsFailed(caught)
  }
}

void (async () => {
  try {
    sessions.value = await listSessions()
    sessionsLoaded.value = true
  } catch (caught) {
    sessionsFailed(caught)
    return
  }
  if (session.value || (mode.value === 'upload' && file.value)) {
    return
  }
  const remembered = lastSession()
  const target = songId.value
    ? sessionOfSong(sessions.value, songId.value)
    : (sessions.value.find((entry) => entry.id === remembered) ?? null)
  if (target) {
    await openSession(target)
  } else if (result.value?.score) {
    // The song by address was converted before the list arrived; it gets its session now.
    void saveSession()
  }
})()

/** What was changed in the last second still reaches the server when the page goes away. */
function flushSession(): void {
  if (saveTimer) {
    window.clearTimeout(saveTimer)
    void saveSession(true)
  }
}
window.addEventListener('pagehide', flushSession)
onBeforeUnmount(() => {
  flushSession()
  window.removeEventListener('pagehide', flushSession)
})

// A track turned into MIDI on the transcription or voices page arrives as the score to upload; last in
// setup, since taking it touches state declared further up.
watch(
  handedMidi,
  (handed) => {
    if (handed?.view === 'logic') {
      mode.value = 'upload'
      selectFile(handed.file)
      handedMidi.value = null
    }
  },
  { immediate: true },
)

// The page is loaded on first use, often after the library: a song by address may be there already, which the
// watcher above would never see arrive.
if (convertWhenReady && source.value && mode.value === 'library') {
  convertWhenReady = false
  void convert()
}
</script>

<template>
  <!-- minmax(0, 1fr): a wide table inside a card scrolls on its own instead of widening the page. -->
  <div class="grid grid-cols-[minmax(0,1fr)] gap-4">
    <Message v-if="instrumentsError" severity="error" role="alert">{{ instrumentsError }}</Message>

    <Card>
      <template #title>
        <div class="flex items-center justify-between gap-2">
          <h2 class="m-0">{{ t('pageTitle') }}</h2>
          <div class="flex gap-1">
            <Button
              icon="pi pi-sliders-h"
              text
              rounded
              :aria-label="t('instrumentsManage')"
              v-tooltip.bottom="t('instrumentsManageTitle')"
              @click="openInstruments"
            />
            <Button
              icon="pi pi-undo"
              text
              rounded
              :aria-label="t('reset')"
              v-tooltip.bottom="t('resetTitle')"
              @click="reset"
            />
          </div>
        </div>
      </template>
      <template #content>
        <SessionBar
          :sessions="sessions"
          :current="attached"
          :error="sessionsError"
          :can-save="sourceKey !== null"
          class="mb-4"
          @open="openSessionById"
          @rename="renameSession"
          @remove="askDeleteSession"
        />
        <SelectButton
          v-model="mode"
          :options="modes"
          option-label="label"
          option-value="value"
          :allow-empty="false"
          :aria-label="t('source')"
          class="mb-4"
        />

        <div v-show="mode === 'library'" class="flex flex-col gap-2">
          <label for="logic-song" class="text-sm text-muted-color">{{ t('librarySong') }}</label>
          <Select
            :model-value="songId"
            input-id="logic-song"
            :options="songOptions"
            option-label="label"
            option-value="value"
            :placeholder="t('librarySongPick')"
            :empty-message="t('librarySongNone')"
            filter
            :filter-placeholder="t('librarySongFilter')"
            fluid
            @update:model-value="pickSong"
          />
          <p v-if="songMissing" class="hint warning">{{ t('librarySongMissing') }}</p>
          <p v-else-if="chosen && !chosen.song.hasAudio" class="hint muted">{{ t('librarySongNoAudio') }}</p>
          <p v-else class="hint muted">{{ t('librarySongHint') }}</p>

          <div
            v-if="chosen?.song.hasAudio && (stemsConfigured || usableStems.length > 0)"
            class="flex flex-col gap-2 mt-2"
          >
            <label for="logic-stems" class="text-sm text-muted-color">{{ t('stemsForLogic') }}</label>
            <div class="flex flex-wrap items-center gap-2">
              <Select
                v-if="usableStems.length > 0"
                v-model="stemSetId"
                input-id="logic-stems"
                :options="stemOptions"
                option-label="label"
                option-value="value"
                class="flex-[1_1_14rem]"
              />
              <Button
                v-if="stemsConfigured"
                :label="t('stemsCreate')"
                icon="pi pi-clone"
                severity="secondary"
                outlined
                v-tooltip.bottom="t('stemsCreateTitle')"
                @click="createStems"
              />
            </div>
            <p v-if="stemsRunning" class="hint muted mt-0">{{ t('stemsRunning') }}</p>
            <p v-else-if="usableStems.length === 0" class="hint muted mt-0">{{ t('stemsMissing') }}</p>
            <p v-else-if="stemSetId" class="hint muted mt-0">{{ t('stemsHint') }}</p>
          </div>

          <div v-if="midiInfo?.installed && (bassSet || drumSet)" class="flex flex-col gap-2 mt-2">
            <span class="text-sm text-muted-color">{{ t('stemPartsTitle') }}</span>
            <div v-if="bassSet" class="flex items-center gap-2">
              <Checkbox v-model="partChoice.bass" input-id="logic-stem-bass" binary />
              <label for="logic-stem-bass" class="text-sm">{{ t('stemPartsBass') }}</label>
            </div>
            <div v-if="drumSet" class="flex items-center gap-2">
              <Checkbox v-model="partChoice.drums" input-id="logic-stem-drums" binary />
              <label for="logic-stem-drums" class="text-sm">{{ t('stemPartsDrums') }}</label>
            </div>
            <p class="hint muted mt-0">{{ t('stemPartsHint') }}</p>
          </div>
          <p
            v-else-if="midiInfo?.installed && chosen && songStems.some((set) => set.stage === 'done')"
            class="hint muted mt-2"
          >
            {{ t('stemPartsMissing') }}
          </p>
        </div>

        <!-- Score and audio side by side; on a phone each one takes the whole row. -->
        <div v-show="mode === 'upload'" class="grid gap-4 grid-cols-1 md:grid-cols-2 items-stretch">
          <div class="flex flex-col gap-2">
            <h3 class="m-0 text-base">{{ t('scoreTitle') }}</h3>
            <FileDropZone
              :file="file"
              :extension="['.abc', '.mid', '.midi']"
              accept=".abc,.mid,.midi,text/plain,text/vnd.abc,audio/midi,audio/x-midi"
              :drop-hint="t('dropHint')"
              :wrong-type-hint="t('notAbc')"
              @select="selectFile"
              @clear="file = null"
            />
          </div>
          <div class="flex flex-col gap-2">
            <h3 class="m-0 text-base">{{ t('audioTitle') }}</h3>
            <FileDropZone
              :file="audio"
              extension=".flac"
              accept=".flac,audio/flac,audio/x-flac"
              :drop-hint="t('audioDropHint')"
              :wrong-type-hint="t('notFlac')"
              @select="selectAudio"
              @clear="selectAudio(null)"
            />
            <p class="hint muted mt-0">{{ t('audioInfo') }}</p>
          </div>
        </div>
      </template>
    </Card>

    <Card>
      <template #title>
        <div class="flex flex-wrap items-center justify-between gap-2">
          <h2 class="m-0">{{ t('optionsTitle') }}</h2>
          <div class="flex flex-wrap items-center gap-2 text-base font-normal">
            <Select
              :model-value="presetName || null"
              :options="presets"
              option-label="name"
              option-value="name"
              :placeholder="t('presetNone')"
              :aria-label="t('presets')"
              show-clear
              size="small"
              class="w-40"
              @update:model-value="applyPreset"
            />
            <InputText
              v-model.trim="newPresetName"
              :placeholder="t('presetName')"
              :aria-label="t('presetName')"
              spellcheck="false"
              size="small"
              class="w-32"
            />
            <Button
              :label="t('presetSave')"
              severity="secondary"
              outlined
              size="small"
              :disabled="!newPresetName || presetsBusy"
              @click="storePreset"
            />
            <Button
              :label="t('presetDelete')"
              severity="secondary"
              outlined
              size="small"
              :disabled="!presetName || presetsBusy"
              @click="removePreset"
            />
          </div>
        </div>
      </template>
      <template #content>
        <p v-if="presetsError" class="hint danger mt-0 mb-3" role="alert">{{ presetsError }}</p>
        <OptionsForm v-model="form" />

        <form class="submit" @submit.prevent="convert">
          <label class="grid gap-1 flex-[1_1_14rem] text-sm text-muted-color">
            {{ t('outputName') }}
            <InputGroup>
              <InputText v-model.trim="outputName" required spellcheck="false" />
              <InputGroupAddon>.mid</InputGroupAddon>
            </InputGroup>
          </label>
          <Button
            type="submit"
            :label="busy ? t('converting') : t('convert')"
            icon="pi pi-cog"
            :loading="busy"
            :disabled="!source || busy || !outputName"
          />
        </form>
        <p v-if="error" class="hint danger" role="alert">{{ error }}</p>
      </template>
    </Card>

    <ScorePreview
      v-if="result?.score"
      :score="result.score"
      :include-chords="form.includeChords"
      :stale="stale"
      :instruments="instruments"
      :assignments="assignments"
      :recording="recording"
      :listening="listening"
      singable
      @assign="assign"
      @sing="singTrack"
      @manage-instruments="openInstruments"
      @listening="(value) => (listening = value)"
    />

    <ResultView
      v-if="result"
      :result="result"
      :output-name="outputName"
      :stale="stale"
      :has-audio="hasAudio"
      :logic-busy="logicBusy"
      :logic-progress="logicProgress"
      :logic-error="logicError"
      :logic-warnings="logicWarnings"
      :music-xml-busy="musicXmlBusy"
      :music-xml-error="musicXmlError"
      @export-logic="exportLogic"
      @export-music-xml="downloadMusicXml"
    />
  </div>
</template>

<style scoped>
.submit {
  display: flex;
  flex-wrap: wrap;
  align-items: flex-end;
  justify-content: space-between;
  gap: 1rem;
  margin-top: 1.5rem;
  padding-top: 1.25rem;
  border-top: 1px solid var(--border);
}
</style>
