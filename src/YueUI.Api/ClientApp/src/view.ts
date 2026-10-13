import { ref } from 'vue'

/** The pages the menu bar switches between. */
export type View =
  | 'create'
  | 'queue'
  | 'transcribe'
  | 'songs'
  | 'playlist'
  | 'voices'
  | 'logic'
  | 'harmony'
  | 'instruments'
  | 'lab'
  | 'logs'
  | 'storage'
  | 'templates'
  | 'ace'
  | 'insights'

const views: readonly View[] = [
  'create',
  'queue',
  'transcribe',
  'songs',
  'playlist',
  'voices',
  'logic',
  'harmony',
  'instruments',
  'lab',
  'logs',
  'storage',
  'templates',
  'ace',
  'insights',
]

/**
 * The page lives in the URL's hash (`#/songs`), so the back button, a reload and a bookmark on the home screen keep it,
 * and the server needs no route per page. Only the page's content switches; the player sits outside of it.
 * A song has an address of its own on the songs page, `#/songs/<run>/songN`, which scrolls to it and marks it, and on
 * the Logic page, `#/logic/<run>/songN`, which picks it as the score to convert, and on the voices page,
 * `#/voices/<run>/songN`, which picks it to be split into stems.
 */
function fromHash(): { view: View; song: string | null } {
  const [name, ...song] = location.hash.replace(/^#\/?/, '').split('/')
  if (!(views as readonly string[]).includes(name ?? '')) {
    return { view: 'create', song: null }
  }
  return {
    view: name as View,
    song: (name === 'songs' || name === 'logic' || name === 'voices') && song.length === 2 ? song.join('/') : null,
  }
}

const initial = fromHash()
export const view = ref<View>(initial.view)
/** The song the address points to, marked on the songs page. */
export const focusedSong = ref<string | null>(initial.view === 'songs' ? initial.song : null)
/**
 * The song the Logic page converts, when an address named one. Only an address with a song changes it: the menu's
 * plain `#/logic` returns to the page as it was left, like every page keeps its input.
 */
export const logicSong = ref<string | null>(initial.view === 'logic' ? initial.song : null)
/** The song the voices page offers to split into stems, when an address named one; like `logicSong`. */
export const stemSong = ref<string | null>(initial.view === 'voices' ? initial.song : null)
/** Counts requests to show a song, so that asking for the one already marked scrolls to it again. */
export const focusRequest = ref(0)

window.addEventListener('hashchange', () => {
  const target = fromHash()
  view.value = target.view
  if (target.view === 'logic' || target.view === 'voices') {
    if (target.song && target.view === 'logic') {
      logicSong.value = target.song
    } else if (target.song) {
      stemSong.value = target.song
    }
    return
  }
  if (focusedSong.value !== target.song) {
    focusedSong.value = target.song
    focusRequest.value++
  }
})

function setHash(hash: string): void {
  if (location.hash !== hash) {
    location.hash = hash
  }
}

export function navigate(target: View): void {
  setHash(`#/${target}`)
  view.value = target
  focusedSong.value = null
  window.scrollTo({ top: 0 })
}

export function songHref(songId: string): string {
  return `#/songs/${songId}`
}

/** Opens the songs page at the song; the page scrolls there itself once the song is listed. */
export function showSong(songId: string): void {
  setHash(songHref(songId))
  view.value = 'songs'
  focusedSong.value = songId
  focusRequest.value++
}

export function logicHref(songId: string): string {
  return `#/logic/${songId}`
}

/** Opens the Logic page with the song picked as its score. */
export function openOnLogicPage(songId: string): void {
  setHash(logicHref(songId))
  view.value = 'logic'
  logicSong.value = songId
  window.scrollTo({ top: 0 })
}

/**
 * Keeps the address in step with the song the Logic page shows, so a reload or a bookmark returns to it. Replaced
 * rather than pushed: picking songs one after another should not fill the back button's history.
 */
export function replaceLogicSong(songId: string | null): void {
  logicSong.value = songId
  if (view.value === 'logic') {
    history.replaceState(history.state, '', songId ? logicHref(songId) : '#/logic')
  }
}

/**
 * A MIDI file made on another page (audio to MIDI) that the Logic page or the backing vocals take as their upload.
 * Both pages load on first use, so they read it when they mount as well as when it changes, and clear it once taken.
 */
export const handedMidi = ref<{ view: 'logic' | 'harmony'; file: File } | null>(null)

export function openMidiOn(target: 'logic' | 'harmony', file: File): void {
  handedMidi.value = { view: target, file }
  navigate(target)
}

/** Opens the voices page with the song picked to be split into stems. */
export function openStems(songId: string): void {
  setHash(`#/voices/${songId}`)
  view.value = 'voices'
  stemSong.value = songId
  window.scrollTo({ top: 0 })
}
