import { computed, ref, watch } from 'vue'
import { createPlaylist, deletePlaylist, getPlaylists, putPlaylistSongs, renamePlaylist } from './api'
import type { PlaylistInfo } from './types'

/**
 * The playlists with their song ids in order. The server keeps them, so the phone and the Mac see the same ones; this
 * is the copy the pages show, updated right away and put back if the server refuses.
 */
export const playlists = ref<PlaylistInfo[]>([])

const storageKey = 'yueui.playlist'

function storedActiveId(): number | null {
  try {
    const stored = Number(localStorage.getItem(storageKey))
    return Number.isInteger(stored) && stored > 0 ? stored : null
  } catch {
    // Storage may be blocked; the first playlist is shown then.
    return null
  }
}

const activeId = ref<number | null>(storedActiveId())

watch(activeId, (id) => {
  try {
    if (id !== null) {
      localStorage.setItem(storageKey, String(id))
    }
  } catch {
    // Remembering the choice is a convenience only.
  }
})

/**
 * The playlist the page shows and a single tap adds to. Chosen per browser (the phone may listen to another one than
 * the Mac); one deleted elsewhere falls back to the first.
 */
export const activePlaylist = computed<PlaylistInfo | undefined>(
  () => playlists.value.find((p) => p.id === activeId.value) ?? playlists.value[0],
)

/** The active playlist's song ids. */
export const playlistIds = computed(() => activePlaylist.value?.songIds ?? [])

export function selectPlaylist(id: number): void {
  activeId.value = id
}

export async function loadPlaylists(): Promise<void> {
  playlists.value = await getPlaylists()
}

function replace(playlist: PlaylistInfo): void {
  playlists.value = playlists.value.map((p) => (p.id === playlist.id ? playlist : p))
}

export async function savePlaylist(songIds: string[], id = activePlaylist.value?.id): Promise<void> {
  const before = playlists.value.find((p) => p.id === id)
  if (!before) {
    return
  }
  replace({ ...before, songIds })
  try {
    replace(await putPlaylistSongs(before.id, songIds))
  } catch (caught) {
    replace(before)
    throw caught
  }
}

export function inPlaylist(songId: string, id = activePlaylist.value?.id): boolean {
  return !!playlists.value.find((p) => p.id === id)?.songIds.includes(songId)
}

export function inAnyPlaylist(songId: string): boolean {
  return playlists.value.some((p) => p.songIds.includes(songId))
}

export function toggleInPlaylist(songId: string, id = activePlaylist.value?.id): Promise<void> {
  const songIds = playlists.value.find((p) => p.id === id)?.songIds ?? []
  return savePlaylist(songIds.includes(songId) ? songIds.filter((s) => s !== songId) : [...songIds, songId], id)
}

/** A new, empty playlist, which becomes the active one. */
export async function addPlaylist(name: string, songIds: string[] = []): Promise<void> {
  const created = await createPlaylist(name)
  playlists.value = [...playlists.value, created]
  activeId.value = created.id
  if (songIds.length > 0) {
    await savePlaylist(songIds, created.id)
  }
}

export async function renameActivePlaylist(name: string): Promise<void> {
  if (activePlaylist.value) {
    replace(await renamePlaylist(activePlaylist.value.id, name))
  }
}

export async function removeActivePlaylist(): Promise<void> {
  const id = activePlaylist.value?.id
  if (id === undefined) {
    return
  }
  await deletePlaylist(id)
  playlists.value = playlists.value.filter((p) => p.id !== id)
  activeId.value = playlists.value[0]?.id ?? null
}
