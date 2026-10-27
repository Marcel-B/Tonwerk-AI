<script setup lang="ts">
import { computed, useTemplateRef } from 'vue'
import type Menu from 'primevue/menu'
import { t } from '../i18n'
import { exportTargetFor, openExportFor, openTonregalFor, tonregalReady } from '../export'
import { openVideo } from '../video'
import { showSong } from '../view'

/** What else can be done with a song where only a line of it is shown (player, playlist): a menu behind one button. */
const props = defineProps<{ songId: string; hasScore: boolean }>()

const emit = defineEmits<{
  /** The song's score into the form, with style, lyrics and seed of its run. */
  useScore: [songId: string]
  /** Every parameter the song was made with into the form. */
  newSong: [songId: string]
}>()

const menu = useTemplateRef<InstanceType<typeof Menu>>('menu')

const items = computed(() => [
  { label: t('showSong'), icon: 'pi pi-list', command: () => showSong(props.songId) },
  {
    label: t('useScore'),
    icon: 'pi pi-file-import',
    disabled: !props.hasScore,
    command: () => emit('useScore', props.songId),
  },
  { label: t('useAsNewSong'), icon: 'pi pi-clone', command: () => emit('newSong', props.songId) },
  { label: t('shareOrExport'), icon: 'pi pi-share-alt', command: () => openExportFor(props.songId) },
  { label: t('musicVideo'), icon: 'pi pi-video', command: () => openVideo(exportTargetFor(props.songId)) },
  ...(tonregalReady.value
    ? [{ label: t('tonregalSend'), icon: 'pi pi-book', command: () => openTonregalFor(props.songId) }]
    : []),
])
</script>

<template>
  <Button
    icon="pi pi-ellipsis-v"
    text
    rounded
    severity="secondary"
    :aria-label="t('songActions')"
    aria-haspopup="true"
    @click="menu?.toggle($event)"
  />
  <Menu ref="menu" :model="items" popup />
</template>
