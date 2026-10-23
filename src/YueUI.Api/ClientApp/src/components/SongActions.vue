<script setup lang="ts">
import { computed, ref, useTemplateRef } from 'vue'
import type Menu from 'primevue/menu'
import { coverUrl, deleteCover, midiToAbc, putCover } from '../api'
import { openExport, photoCover, pickImage } from '../export'
import { t } from '../i18n'
import { openImages } from '../images'
import { pickMidiFile } from '../midi'
import type { RunInfo, SongInfo } from '../types'
import { openVideo } from '../video'
import { openOnLogicPage, openStems } from '../view'

/**
 * A song's actions beyond play, render, FLAC and ABC: icons on a wide screen, the lines of a "…" menu on a phone,
 * where they would push the card past the screen's edge.
 */
const props = defineProps<{
  run: RunInfo
  song: SongInfo
  /** The worker is on the song, which then cannot be deleted. */
  busy: boolean
  /** Seed-VC and the separator are set up, so the song can be sung with another voice. */
  voices: boolean
  /** The separator is set up, so the song can be split into stems on the voices page. */
  stems: boolean
}>()

const emit = defineEmits<{
  /** The song's own score into the form. */
  useScore: []
  /** A MIDI file (the song edited in Logic) read back into a score, which replaces the song's own. */
  useMidi: [abc: string, warnings: string[]]
  newSong: []
  /** Title, style and lyrics as a new ACE-Step song's, on its page. */
  aceTemplate: []
  sing: []
  delete: []
  error: [message: string]
}>()

interface SongAction {
  key: string
  label: string
  icon: string
  command: () => void
  danger?: boolean
  disabled?: boolean
  loading?: boolean
}

/** The cover is being saved. */
const covering = ref(false)
/** The MIDI file is being read back into a score. */
const importing = ref(false)

function failed(caught: unknown): void {
  emit('error', caught instanceof Error ? caught.message : String(caught))
}

/** What the export, video and painting dialogs show the song as. */
function dialogSong() {
  const { run, song } = props
  return {
    songId: song.id,
    title: run.title,
    style: run.style,
    cover: song.coverUpdatedAt ? coverUrl(song.id, song.coverUpdatedAt) : undefined,
  }
}

/** A photo as the song's cover; the server's library event brings it into the list and the player. */
function chooseCover(): void {
  pickImage(async (file) => {
    covering.value = true
    try {
      let photo: Blob
      try {
        photo = await photoCover(file)
      } catch {
        throw new Error(t('photoUnreadable'))
      }
      await putCover(props.song.id, photo)
    } catch (caught) {
      emit('error', t('coverSaveFailed', { message: caught instanceof Error ? caught.message : String(caught) }))
    } finally {
      covering.value = false
    }
  })
}

async function removeCover(): Promise<void> {
  try {
    await deleteCover(props.song.id)
  } catch (caught) {
    failed(caught)
  }
}

/** The song edited in Logic: its MIDI file as the score, with the song's style, lyrics and seed. */
function useMidi(): void {
  pickMidiFile(async (file) => {
    importing.value = true
    try {
      const midi = await midiToAbc(file)
      emit('useMidi', midi.abc, midi.warnings)
    } catch (caught) {
      failed(caught)
    } finally {
      importing.value = false
    }
  })
}

const actions = computed<SongAction[]>(() => {
  const { song } = props
  const list: SongAction[] = []
  if (song.hasAudio) {
    list.push(
      {
        key: 'export',
        label: t('shareOrExport'),
        icon: 'pi pi-share-alt',
        command: () => openExport(dialogSong()),
      },
      { key: 'video', label: t('musicVideo'), icon: 'pi pi-video', command: () => openVideo(dialogSong()) },
    )
  }
  list.push(
    {
      key: 'cover',
      label: t('songCover'),
      icon: covering.value ? 'pi pi-spin pi-spinner' : 'pi pi-image',
      command: chooseCover,
      disabled: covering.value,
      loading: covering.value,
    },
    { key: 'paintCover', label: t('paintCover'), icon: 'pi pi-palette', command: () => openImages(dialogSong()) },
  )
  if (song.coverUpdatedAt) {
    list.push({
      key: 'coverRemove',
      label: t('songCoverRemove'),
      icon: 'pi pi-eraser',
      command: () => void removeCover(),
    })
  }
  list.push({ key: 'newSong', label: t('useAsNewSong'), icon: 'pi pi-clone', command: () => emit('newSong') })
  list.push({
    key: 'aceTemplate',
    label: t('useAsAceTemplate'),
    icon: 'pi pi-wave-pulse',
    command: () => emit('aceTemplate'),
  })
  if (song.hasScore) {
    list.push({ key: 'score', label: t('useScore'), icon: 'pi pi-file-import', command: () => emit('useScore') })
  }
  list.push({
    key: 'midi',
    label: t('useMidi'),
    icon: importing.value ? 'pi pi-spin pi-spinner' : 'pi pi-file-arrow-up',
    command: useMidi,
    disabled: importing.value,
    loading: importing.value,
  })
  if (song.hasScore) {
    list.push({
      key: 'logic',
      label: t('openInLogic'),
      icon: 'pi pi-file-export',
      command: () => openOnLogicPage(song.id),
    })
  }
  if (props.voices && song.hasAudio) {
    list.push({ key: 'voice', label: t('singWithVoice'), icon: 'pi pi-user-edit', command: () => emit('sing') })
  }
  if (props.stems && song.hasAudio) {
    list.push({ key: 'stems', label: t('splitStems'), icon: 'pi pi-sliders-v', command: () => openStems(song.id) })
  }
  list.push({
    key: 'delete',
    label: props.busy ? t('deleteBusy') : t('deleteSong'),
    icon: 'pi pi-trash',
    command: () => emit('delete'),
    danger: true,
    disabled: props.busy,
  })
  return list
})

const menu = useTemplateRef<InstanceType<typeof Menu>>('menu')
const menuItems = computed(() =>
  actions.value.map((action) => ({
    label: action.label,
    icon: action.icon,
    command: action.command,
    disabled: action.disabled,
    danger: action.danger,
  })),
)
</script>

<template>
  <div class="hidden sm:flex">
    <Button
      v-for="action in actions"
      :key="action.key"
      :icon="action.icon"
      text
      size="small"
      rounded
      :severity="action.danger ? 'danger' : undefined"
      v-tooltip="action.label"
      :aria-label="action.label"
      :loading="action.loading"
      :disabled="action.disabled"
      @click="action.command()"
    />
  </div>
  <Button
    icon="pi pi-ellipsis-v"
    text
    size="small"
    rounded
    severity="secondary"
    class="sm:hidden"
    :aria-label="t('songActions')"
    aria-haspopup="true"
    @click="menu?.toggle($event)"
  />
  <Menu ref="menu" :model="menuItems" popup>
    <!-- Only so "delete" is red like its icon on a wide screen. -->
    <template #item="{ item, props: link }">
      <a v-bind="link.action" :class="{ danger: item.danger }">
        <span :class="[item.icon, 'p-menu-item-icon', { danger: item.danger }]" />
        <span class="p-menu-item-label">{{ item.label }}</span>
      </a>
    </template>
  </Menu>
</template>
