<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, useTemplateRef, watch } from 'vue'
import { useConfirm } from 'primevue/useconfirm'
import { formatBytes, formatDuration, t } from '../i18n'
import { current, play, playing, trackOf, type Track } from '../player'
import {
  activePlaylist,
  addPlaylist,
  playlistIds,
  playlists,
  removeActivePlaylist,
  renameActivePlaylist,
  savePlaylist,
  selectPlaylist,
} from '../playlist'
import { isOffline, offlineBytes, offlineProgress, offlineSongs, setOffline, syncOffline } from '../offline'
import type { RunInfo, SongInfo } from '../types'
import { showSong, songHref } from '../view'
import SongMenu from './SongMenu.vue'

const props = defineProps<{ runs: RunInfo[] }>()

const emit = defineEmits<{ error: [message: string]; useScore: [songId: string]; newSong: [songId: string] }>()

interface Entry {
  song: SongInfo
  track: Track
}

/** The playlist's songs as the library knows them; one deleted meanwhile is left out until the next reload. */
const entries = computed<Entry[]>(() => {
  const songs = new Map(props.runs.flatMap((run) => run.songs.map((song) => [song.id, { run, song }] as const)))
  return playlistIds.value.flatMap((id) => {
    const found = songs.get(id)
    return found ? [{ song: found.song, track: trackOf(found.run, found.song) }] : []
  })
})

const playable = computed(() => entries.value.filter((e) => e.song.hasAudio).map((e) => e.track))
const duration = computed(() => formatDuration(entries.value.reduce((sum, e) => sum + (e.song.seconds ?? 0), 0)))

function playEntry(entry: Entry): void {
  play(entry.track, playable.value)
}

async function save(songIds: string[]): Promise<void> {
  try {
    await savePlaylist(songIds)
  } catch (caught) {
    emit('error', caught instanceof Error ? caught.message : String(caught))
  }
}

function move(index: number, by: number): void {
  const ids = entries.value.map((e) => e.song.id)
  const [moved] = ids.splice(index, 1)
  ids.splice(index + by, 0, moved!)
  void save(ids)
}

function remove(entry: Entry): void {
  void save(playlistIds.value.filter((id) => id !== entry.song.id))
}

// ---- Offline ------------------------------------------------------------------------------------------

const keptOffline = computed(() => isOffline(activePlaylist.value?.id))
const keptCount = computed(() => entries.value.filter((e) => offlineSongs.value.has(e.song.id)).length)

function toggleOffline(): void {
  const playlist = activePlaylist.value
  if (playlist) {
    setOffline(playlist.id, !keptOffline.value)
    void syncOffline(playlists.value, props.runs)
  }
}

// The copies follow the playlists and the library (a render changes a song's audio), a moment after the last change,
// and once on the way in, when the app comes back online.
let timer: ReturnType<typeof setTimeout> | undefined
function scheduleSync(): void {
  clearTimeout(timer)
  timer = setTimeout(() => void syncOffline(playlists.value, props.runs), 1500)
}
watch([playlists, () => props.runs], scheduleSync, { immediate: true })
window.addEventListener('online', scheduleSync)
onBeforeUnmount(() => {
  clearTimeout(timer)
  window.removeEventListener('online', scheduleSync)
})

// ---- Choosing, creating, renaming and deleting playlists ----------------------------------------------

/** What the name field is for while it replaces the picker; null shows the picker. */
const editing = ref<'create' | 'rename' | null>(null)
const name = ref('')
const savingName = ref(false)
const nameInput = useTemplateRef<{ $el: HTMLInputElement }>('nameInput')

function startEditing(mode: 'create' | 'rename'): void {
  editing.value = mode
  name.value = mode === 'rename' ? (activePlaylist.value?.name ?? '') : ''
  void nextTick(() => nameInput.value?.$el.focus())
}

async function saveName(): Promise<void> {
  const trimmed = name.value.trim()
  if (!trimmed) {
    return
  }
  savingName.value = true
  try {
    await (editing.value === 'create' ? addPlaylist(trimmed) : renameActivePlaylist(trimmed))
    editing.value = null
  } catch (caught) {
    emit('error', caught instanceof Error ? caught.message : String(caught))
  } finally {
    savingName.value = false
  }
}

const confirm = useConfirm()

/** Only the list goes; the songs stay in the library and in other playlists. */
function askDelete(): void {
  const playlist = activePlaylist.value
  if (!playlist) {
    return
  }
  confirm.require({
    header: t('confirmDelete'),
    message: t('confirmDeletePlaylist', { name: playlist.name, count: playlist.songIds.length }),
    icon: 'pi pi-trash',
    rejectProps: { label: t('keep'), severity: 'secondary', outlined: true },
    acceptProps: { label: t('delete'), severity: 'danger' },
    accept: async () => {
      try {
        await removeActivePlaylist()
      } catch (caught) {
        emit('error', caught instanceof Error ? caught.message : String(caught))
      }
    },
  })
}
</script>

<template>
  <section>
    <form v-if="editing" class="flex items-center gap-1 mb-3" @submit.prevent="saveName">
      <InputText
        ref="nameInput"
        v-model="name"
        :placeholder="t('playlistName')"
        :aria-label="t('playlistName')"
        :maxlength="100"
        class="min-w-0 flex-1"
        @keydown.esc="editing = null"
      />
      <Button
        type="submit"
        icon="pi pi-check"
        text
        rounded
        :loading="savingName"
        :disabled="!name.trim()"
        :aria-label="t('save')"
      />
      <Button
        type="button"
        icon="pi pi-times"
        text
        rounded
        severity="secondary"
        :aria-label="t('cancel')"
        @click="editing = null"
      />
    </form>
    <div v-else class="flex items-center gap-1 mb-3">
      <Select
        :model-value="activePlaylist?.id"
        :options="playlists"
        option-label="name"
        option-value="id"
        :aria-label="t('choosePlaylist')"
        class="min-w-0 flex-1"
        @update:model-value="selectPlaylist"
      >
        <template #option="{ option }">
          <span class="flex-1 truncate">{{ option.name }}</span>
          <span class="muted text-sm ml-2">{{ option.songIds.length }}</span>
        </template>
      </Select>
      <Button
        icon="pi pi-plus"
        text
        rounded
        v-tooltip.top="t('newPlaylist')"
        :aria-label="t('newPlaylist')"
        @click="startEditing('create')"
      />
      <Button
        icon="pi pi-pencil"
        text
        rounded
        severity="secondary"
        v-tooltip.top="t('renamePlaylist')"
        :aria-label="t('renamePlaylist')"
        @click="startEditing('rename')"
      />
      <Button
        :icon="keptOffline ? 'pi pi-cloud-download' : 'pi pi-cloud'"
        text
        rounded
        :severity="keptOffline ? undefined : 'secondary'"
        :aria-pressed="keptOffline"
        v-tooltip.top="keptOffline ? t('offlineOff') : t('offlineOn')"
        :aria-label="t('offline')"
        @click="toggleOffline"
      />
      <Button
        icon="pi pi-trash"
        text
        rounded
        severity="danger"
        :disabled="playlists.length < 2"
        v-tooltip.top="playlists.length < 2 ? t('lastPlaylist') : t('deletePlaylist')"
        :aria-label="t('deletePlaylist')"
        @click="askDelete"
      />
    </div>
    <p v-if="keptOffline" class="muted text-sm mt-0 mb-3 flex items-center gap-2">
      <i :class="['pi', offlineProgress ? 'pi-spin pi-spinner' : 'pi-cloud-download', 'text-xs']" aria-hidden="true" />
      <span>{{
        offlineProgress
          ? t('offlineSyncing', { done: offlineProgress.done, total: offlineProgress.total })
          : t('offlineKept', { count: keptCount, total: entries.length, size: formatBytes(offlineBytes) })
      }}</span>
    </p>
    <p v-if="entries.length === 0" class="muted">{{ t('playlistEmpty') }}</p>
    <template v-else>
      <div class="flex items-center justify-between gap-2 mb-3">
        <span class="muted text-sm">{{ t('playlistSummary', { count: entries.length, duration }) }}</span>
        <Button
          icon="pi pi-play"
          :label="t('playAll')"
          size="small"
          :disabled="playable.length === 0"
          @click="play(playable[0]!, playable)"
        />
      </div>
      <ol class="entries">
        <li
          v-for="(entry, index) in entries"
          :key="entry.song.id"
          :class="['entry', { 'bg-emphasis': current?.id === entry.song.id }]"
        >
          <Button
            :icon="current?.id === entry.song.id && playing ? 'pi pi-pause' : 'pi pi-play'"
            rounded
            text
            :disabled="!entry.song.hasAudio"
            :aria-label="current?.id === entry.song.id && playing ? t('pause') : t('play')"
            @click="playEntry(entry)"
          />
          <div class="min-w-0 flex-1">
            <a
              :href="songHref(entry.song.id)"
              class="block truncate font-semibold text-color no-underline hover:underline"
              :title="t('showSong')"
              @click.prevent="showSong(entry.song.id)"
              >{{ entry.track.title }}</a
            >
            <div class="muted text-sm">
              <i
                v-if="keptOffline && offlineSongs.has(entry.song.id)"
                class="pi pi-check-circle text-xs mr-1"
                :title="t('offlineSong')"
                :aria-label="t('offlineSong')"
              />{{ entry.track.detail
              }}<template v-if="entry.song.seconds"> · {{ formatDuration(entry.song.seconds) }}</template>
            </div>
          </div>
          <Button
            icon="pi pi-arrow-up"
            text
            rounded
            size="small"
            severity="secondary"
            :disabled="index === 0"
            :aria-label="t('moveUp')"
            @click="move(index, -1)"
          />
          <Button
            icon="pi pi-arrow-down"
            text
            rounded
            size="small"
            severity="secondary"
            :disabled="index === entries.length - 1"
            :aria-label="t('moveDown')"
            @click="move(index, 1)"
          />
          <SongMenu
            :song-id="entry.song.id"
            :has-score="entry.song.hasScore"
            @use-score="emit('useScore', $event)"
            @new-song="emit('newSong', $event)"
          />
          <Button
            icon="pi pi-minus-circle"
            text
            rounded
            size="small"
            severity="danger"
            v-tooltip="t('removeFromPlaylist')"
            :aria-label="t('removeFromPlaylist')"
            @click="remove(entry)"
          />
        </li>
      </ol>
    </template>
  </section>
</template>

<style scoped>
.entries {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
  margin: 0;
  padding: 0;
  list-style: none;
}

.entry {
  display: flex;
  align-items: center;
  gap: 0.25rem;
  padding: 0.35rem 0.25rem;
  border-radius: var(--radius-small);
}
</style>
