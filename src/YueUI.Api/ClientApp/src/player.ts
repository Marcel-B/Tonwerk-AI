import { computed, ref } from 'vue'
import { coverUrl, streamUrl, versionStreamUrl } from './api'
import { drawCover, exportTargetFor } from './export'
import { t } from './i18n'
import { createSpectrumAnalyser, registerSource, visuals, type SpectrumSource } from './spectrum'
import type { RunInfo, SongInfo, VersionState } from './types'

/** A song as the player shows it. */
export interface Track {
  /** The song's id, or for a version `run/songN@versionId`, so that both can be in one list. */
  id: string
  /** The song the track is, or is a version of: what rating, playlist and the song's address go by. */
  songId: string
  title: string
  /** "Song 2", in the language of the moment it was queued; a version adds its voice. */
  detail: string
  /** Where a version's audio is; a song's own comes from its id. */
  src?: string
  /** The song's own cover, for the player and the lock screen; none while it has only the drawn one. */
  cover?: string
}

function coverOf(song: SongInfo): string | undefined {
  return song.coverUpdatedAt ? coverUrl(song.id, song.coverUpdatedAt) : undefined
}

export function trackOf(run: RunInfo, song: SongInfo): Track {
  return {
    id: song.id,
    songId: song.id,
    title: run.title || t('untitled'),
    detail: t('songN', { n: song.index }),
    cover: coverOf(song),
  }
}

/** The song sung with another voice. */
export function versionTrack(run: RunInfo, song: SongInfo, version: VersionState): Track {
  return {
    id: `${song.id}@${version.id}`,
    songId: song.id,
    title: run.title || t('untitled'),
    detail: `${t('songN', { n: song.index })} · ${version.voiceLabel}`,
    src: versionStreamUrl(song.id, version.id),
    cover: coverOf(song),
  }
}

/** Every song with audio, in the library's order: what plays on after a song started from the library. */
export function libraryTracks(runs: RunInfo[]): Track[] {
  return runs.flatMap((run) => run.songs.filter((song) => song.hasAudio).map((song) => trackOf(run, song)))
}

/**
 * A run renamed or a cover changed since its songs were queued: the player and the lock screen take the new title and
 * cover, without interrupting the song.
 */
export function refreshTracks(runs: RunInfo[]): void {
  const songs = new Map(
    runs.flatMap((run) =>
      run.songs.map((song) => [song.id, { title: run.title || t('untitled'), cover: coverOf(song) }] as const),
    ),
  )
  const changed = (track: Track): boolean => {
    const song = songs.get(track.songId)
    return song !== undefined && (song.title !== track.title || song.cover !== track.cover)
  }
  if (!tracks.value.some(changed)) {
    return
  }
  tracks.value = tracks.value.map((track) => (changed(track) ? { ...track, ...songs.get(track.songId)! } : track))
  if (current.value) {
    showOnLockScreen(current.value)
  }
}

/**
 * The one player of the page. It lives outside the pages (PlayerBar.vue in App.vue), so switching between them does
 * not stop the song; the list it was started from decides what plays next.
 */
const tracks = ref<Track[]>([])
const position = ref(-1)
/** Mirrors the audio element, for the play/pause icons next to the songs. */
export const playing = ref(false)

/** Where the song is and how long it runs, in seconds, for the full-screen player's progress bar. */
export const elapsed = ref(0)
export const duration = ref(0)
/** The full-screen player (NowPlaying.vue) is open. */
export const expanded = ref(false)

export const current = computed<Track | null>(() => tracks.value[position.value] ?? null)
export const hasNext = computed(() => position.value >= 0 && position.value < tracks.value.length - 1)
export const hasPrevious = computed(() => position.value > 0)

let host: HTMLElement | null = null
let audio: HTMLAudioElement | null = null
let context: AudioContext | null = null
let analyser: AnalyserNode | null = null
/**
 * The routed graph may have died: iOS suspended or interrupted the context (lock screen, a call, a long pause), and a
 * context woken from that can report `running` while its element source stays silent for good. Only a new context on a
 * new element sounds again, since an element cannot leave its source node.
 */
let stale = false
/** When the element last stopped, to rebuild after a long pause too, since iOS does not reliably report suspending. */
let pausedAt = 0
const LONG_PAUSE_MS = 20_000

/**
 * The silent graph is an iPhone and iPad matter, and so is the cure: Safari on the Mac played nothing through a context
 * made after an earlier one was closed (its analyzer moving, its tab showing sound), so elsewhere the element keeps its
 * one context, as before. An iPad reports itself as a Mac, but with touch.
 */
const touchWebKit =
  typeof navigator !== 'undefined' &&
  (/iPad|iPhone|iPod/.test(navigator.userAgent) ||
    (/Macintosh/.test(navigator.userAgent) && navigator.maxTouchPoints > 1))

/** What the player's analyzer and the background listen to; empty until the analyzer first routed the element. */
export const playerSource: SpectrumSource = { analysers: () => (analyser ? [analyser] : []), playing }
registerSource(playerSource)

/**
 * The small analyzer wants the element routed, and so does the open full-screen player when its analyzer or effects
 * are on; a full-screen player never opened leaves a switched-off analyzer as it was.
 */
function wantsAnalyser(): boolean {
  return (
    visuals.value.player || (expanded.value && (visuals.value.nowPlayingAnalyzer || visuals.value.nowPlayingEffects))
  )
}

/**
 * Routes the audio element through Web Audio, for the analyzer. Once routed it stays so (an element cannot leave its
 * source node), so this only happens with the analyzer switched on, and inside a click: iOS starts a context made
 * outside one suspended, and a routed element then plays silence. Called again on every start, since iOS suspends
 * the context while nothing plays. `create: false` only wakes an existing context, for a start outside our buttons (the
 * element's own controls, the lock screen) that may not count as a click.
 */
export function listen(create = true): void {
  if (!audio || (!context && (!create || !wantsAnalyser()))) {
    return
  }
  if (!context) {
    try {
      // "playback" lets Web Audio carry on with the screen locked and the silent switch on, as the bare element does.
      const session = (navigator as Navigator & { audioSession?: { type: string } }).audioSession
      if (session) {
        session.type = 'playback'
      }
      const created = new AudioContext()
      const node = createSpectrumAnalyser(created)
      // The analyser passes its input through unchanged, so it can sit between the element and the speakers.
      created.createMediaElementSource(audio).connect(node).connect(created.destination)
      created.addEventListener('statechange', () => {
        if (created !== context || created.state === 'running') {
          return
        }
        stale = touchWebKit
        // Interrupted by a call or Siri: the element would go on silently, so the context follows it back.
        if (audio && !audio.paused) {
          void created.resume().catch(() => undefined)
        }
      })
      context = created
      analyser = node
      stale = false
    } catch {
      return
    }
  }
  if (context.state !== 'running') {
    void context.resume().catch(() => undefined)
  }
}

/**
 * Before a start from our own buttons, which is a gesture: a graph that may have died is given up, with its element,
 * and routed afresh. That is what left the player silent with its clock running after the lock screen, for the paused
 * song and every new one, since waking the old context made it say `running` without sounding.
 * Only on iPhone and iPad (`touchWebKit`); the caller routes the element afterwards (`listen`).
 * @param keep The song goes on where it stopped; a new song brings its own address.
 */
function freshStart(keep: boolean): void {
  if (touchWebKit && context && audio && (stale || context.state !== 'running' || (audio.paused && longPaused()))) {
    unroute(keep)
  }
}

function longPaused(): boolean {
  return pausedAt > 0 && performance.now() - pausedAt > LONG_PAUSE_MS
}

/** Closes the context and swaps in a new, unrouted element; the next start inside a gesture routes that one. */
function unroute(keep: boolean): void {
  const old = context
  context = null
  analyser = null
  stale = false
  void old?.close().catch(() => undefined)
  replaceElement(keep)
}

/**
 * iOS refuses resume() outside a user gesture. The element's own controls and the lock screen start playback through
 * a `play` event that fires after the tap, outside it, so a context iOS suspended while the screen was locked stays
 * suspended and the element plays on into a stopped graph: its clock runs, nothing sounds and the analyzer holds its
 * last frame. Every tap on the page is a gesture, though, and the one on the controls reaches the document before the
 * element starts, so the context wakes on whichever comes first.
 */
function wake(): void {
  if (context && context.state !== 'running') {
    void context.resume().catch(() => undefined)
  }
}
if (typeof document !== 'undefined') {
  for (const type of ['touchend', 'pointerup', 'click', 'keydown']) {
    document.addEventListener(type, wake, { capture: true, passive: true })
  }
  // Locked or sent to the background while paused: iOS may take the context away without telling it.
  document.addEventListener('visibilitychange', () => {
    if (touchWebKit && document.visibilityState === 'hidden' && context && audio?.paused) {
      stale = true
    }
  })
}

/**
 * A start that came without a gesture of ours (the lock screen, the element's own controls). A graph that may be dead
 * cannot be rebuilt there, since a context made outside a gesture stays suspended, so the song goes on unrouted on a
 * fresh element: it sounds, only the analyzer rests until the next start from our buttons. If iOS refuses that start
 * too, the player is left paused, and the next tap on play (a gesture) starts it with sound.
 */
function onPlay(): void {
  playing.value = true
  pausedAt = 0
  refreshLockScreen()
  if (context && stale) {
    playUnrouted()
    return
  }
  listen(false)
  if (context) {
    setTimeout(() => {
      if (context && context.state !== 'running' && audio && !audio.paused) {
        // Elsewhere a paused song starts with sound on the next tap, without a second context.
        if (touchWebKit) {
          playUnrouted()
        } else {
          audio.pause()
        }
      }
    }, 1500)
  }
}

/**
 * Play from the AirPods, the lock screen or Control Center. Without this handler iOS resumed the element by itself and
 * the `play` event came afterwards, where `onPlay` found the graph stale and swapped the element outside any gesture:
 * the new element was refused in the background, the old one that iOS knew as the Now Playing session was gone, and
 * nothing short of restarting the app played again. WebKit runs Media Session handlers as a user gesture, so the
 * element can be swapped here, before it starts, and starts on the new one. No new context though: one made with the
 * page hidden may not sound, so a stale graph gives way to an unrouted element and the analyzer rests until the next
 * tap on our buttons.
 */
function resumeRemotely(): void {
  if (!audio || !current.value) {
    return
  }
  freshStart(true)
  listen(false)
  void audio.play().catch(() => undefined)
}

function playUnrouted(): void {
  unroute(true)
  void audio?.play().catch(() => undefined)
}

/** The element, made here rather than in PlayerBar's template since a silent graph is only fixed by a new one. */
function createElement(): HTMLAudioElement {
  const element = document.createElement('audio')
  element.controls = true
  element.preload = 'none'
  // Events of an element already replaced (its pause on the way out, above all) are not the player's any more.
  const own = (handler: () => void) => () => {
    if (element === audio) {
      handler()
    }
  }
  element.addEventListener('play', own(onPlay))
  element.addEventListener(
    'pause',
    own(() => {
      playing.value = false
      pausedAt = performance.now()
    }),
  )
  element.addEventListener('ended', own(ended))
  for (const type of ['timeupdate', 'durationchange', 'loadedmetadata']) {
    element.addEventListener(type, own(updateTime))
  }
  return element
}

function replaceElement(keep: boolean): void {
  const old = audio
  if (!old || !host) {
    return
  }
  const fresh = createElement()
  fresh.volume = old.volume
  fresh.muted = old.muted
  const src = old.getAttribute('src')
  const at = old.currentTime
  audio = fresh
  playing.value = false
  old.pause()
  old.removeAttribute('src')
  old.load()
  old.replaceWith(fresh)
  if (keep && src) {
    fresh.src = src
    if (at > 0) {
      // Before the metadata this becomes the start position; the check after it covers a browser that drops it.
      fresh.currentTime = at
      fresh.addEventListener(
        'loadedmetadata',
        () => {
          if (Math.abs(fresh.currentTime - at) > 1) {
            fresh.currentTime = at
          }
        },
        { once: true },
      )
    }
  }
}

function ended(): void {
  if (!next(false)) {
    playing.value = false
  }
}

/**
 * PlayerBar hands over the place for the audio element, which player.ts makes and replaces; play() then starts it
 * right in the click, which iOS insists on.
 */
export function attach(element: HTMLElement | null): void {
  if (element === host) {
    return
  }
  audio?.remove()
  host = element
  audio = null
  if (host) {
    audio = createElement()
    host.append(audio)
  }
}

/** Plays the track, and the tracks after it in `list` when it ends. Its own track again toggles pause. */
export function play(track: Track, list: Track[] = [track]): void {
  if (current.value?.id === track.id && audio) {
    toggle()
    return
  }
  const index = list.findIndex((item) => item.id === track.id)
  tracks.value = index >= 0 ? list : [track]
  position.value = Math.max(index, 0)
  start(true)
}

export function toggle(): void {
  if (!audio || !current.value) {
    return
  }
  if (audio.paused) {
    freshStart(true)
    listen()
    void audio.play().catch(() => undefined)
  } else {
    audio.pause()
  }
}

/**
 * @param gesture Started by a tap on our buttons; the end of a song and the lock screen's skip buttons are not.
 * @returns False at the end of the list.
 */
export function next(gesture = true): boolean {
  if (!hasNext.value) {
    return false
  }
  position.value++
  start(gesture)
  return true
}

export function previous(gesture = true): void {
  if (audio && audio.currentTime > 3) {
    // As every player does: back to the start of the song first, one more press for the one before.
    audio.currentTime = 0
  } else if (hasPrevious.value) {
    position.value--
    start(gesture)
  }
}

/** The element started playing, from wherever: the lock screen gets title and cover once more. */
export function refreshLockScreen(): void {
  if (current.value) {
    showOnLockScreen(current.value)
  }
}

/** Jumps within the playing song. */
export function seek(seconds: number): void {
  if (audio && Number.isFinite(seconds)) {
    audio.currentTime = seconds
    elapsed.value = seconds
  }
}

/** The audio element's clock. */
export function updateTime(): void {
  if (audio) {
    elapsed.value = audio.currentTime
    duration.value = Number.isFinite(audio.duration) ? audio.duration : 0
  }
}

export function close(): void {
  expanded.value = false
  audio?.pause()
  audio?.removeAttribute('src')
  audio?.load()
  tracks.value = []
  position.value = -1
}

function start(gesture: boolean): void {
  const track = current.value
  if (!audio || !track) {
    return
  }
  if (gesture) {
    freshStart(false)
  } else if (touchWebKit && context && (stale || context.state !== 'running')) {
    // The song ended on the lock screen, or was skipped there, with a graph that may not sound: go on unrouted.
    unroute(false)
  }
  audio.src = track.src ?? streamUrl(track.id)
  elapsed.value = 0
  duration.value = 0
  listen()
  // A refusal (autoplay rules, a deleted file) leaves the player paused with its own controls to try again.
  void audio!.play().catch(() => undefined)
  showOnLockScreen(track)
}

/**
 * The drawn covers given to the lock screen, by song id, as data URLs: the lock screen fetches artwork outside the
 * page, where a blob URL means nothing, and the drawn cover exists only in the browser.
 */
const drawnArtwork = new Map<string, string>()

/**
 * The cover for the lock screen: the song's own, else the drawn one the export would put in (made once per song),
 * so the lock screen never falls back to the app icon.
 */
async function artworkOf(track: Track): Promise<MediaImage[]> {
  if (track.cover) {
    // An absolute address: the lock screen fetches it outside the page.
    return [{ src: new URL(track.cover, location.href).href }]
  }
  let src = drawnArtwork.get(track.songId)
  if (!src) {
    const blob = await drawCover(exportTargetFor(track.songId, track.title))
    src = await new Promise<string>((resolve, reject) => {
      const reader = new FileReader()
      reader.onload = () => resolve(reader.result as string)
      reader.onerror = () => reject(reader.error ?? new Error('cover'))
      reader.readAsDataURL(blob)
    })
    if (drawnArtwork.size > 20) {
      drawnArtwork.clear()
    }
    drawnArtwork.set(track.songId, src)
  }
  return [{ src, sizes: '1000x1000', type: 'image/jpeg' }]
}

/**
 * The title on the lock screen and in Control Center, with skip buttons that follow the list. Set again when the
 * song starts playing (`refreshLockScreen`): iOS keeps what it was given only once the element plays, and a home-screen
 * app otherwise shows its own icon.
 */
function showOnLockScreen(track: Track): void {
  if (!('mediaSession' in navigator)) {
    return
  }
  const describe = (artwork: MediaImage[]) =>
    new MediaMetadata({ title: track.title, artist: track.detail, album: 'Tonwerk-AI', artwork })
  const drawn = drawnArtwork.get(track.songId)
  navigator.mediaSession.metadata = describe(
    track.cover
      ? [{ src: new URL(track.cover, location.href).href }]
      : drawn
        ? [{ src: drawn, sizes: '1000x1000', type: 'image/jpeg' }]
        : [],
  )
  if (!track.cover && !drawn) {
    void artworkOf(track)
      .then((artwork) => {
        if (current.value?.id === track.id && 'mediaSession' in navigator) {
          navigator.mediaSession.metadata = describe(artwork)
        }
      })
      .catch(() => undefined)
  }
  navigator.mediaSession.setActionHandler('play', resumeRemotely)
  navigator.mediaSession.setActionHandler('pause', () => audio?.pause())
  navigator.mediaSession.setActionHandler('nexttrack', hasNext.value ? () => void next(false) : null)
  navigator.mediaSession.setActionHandler('previoustrack', () => previous(false))
  try {
    // The lock screen's progress bar can then be dragged, as the full-screen player's can.
    navigator.mediaSession.setActionHandler('seekto', (details) => seek(details.seekTime ?? 0))
  } catch {
    // Older Safari does not know the action and throws.
  }
}
