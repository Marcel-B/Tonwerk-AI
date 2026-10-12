import {
  deleteCover,
  deleteImage,
  deleteLora,
  deleteSong,
  deleteSpeechTake,
  deleteStems,
  deleteSwap,
  deleteTranscription,
  deleteVersion,
  deleteVideo,
} from './api'
import type { StorageGroup, StorageItem, StorageItemKind, StorageSection } from './types'

/**
 * Each kind goes through the endpoint its page already uses, so the server's own rules apply: a song the worker
 * still sings is refused, a video being made is stopped first.
 */
const deleters: Record<StorageItemKind, (item: StorageItem) => Promise<void>> = {
  song: (item) => deleteSong(item.id),
  version: (item) => deleteVersion(item.songId ?? '', item.id),
  stems: (item) => deleteStems(item.id),
  video: (item) => deleteVideo(item.id),
  cover: (item) => deleteCover(item.id),
  image: (item) => deleteImage(item.id),
  swap: (item) => deleteSwap(item.id),
  take: (item) => deleteSpeechTake(item.id),
  transcription: (item) => deleteTranscription(item.id),
  lora: (item) => deleteLora(item.id),
}

export function deleteStorageItem(item: StorageItem): Promise<void> {
  return deleters[item.kind](item)
}

/** "run/song2" → 2; null for something that belongs to no song. */
export function songNumber(item: StorageItem): number | null {
  const match = /\/song(\d+)$/.exec(item.songId ?? '')
  return match ? Number(match[1]) : null
}

/** One section's parts, biggest first, with each part's share of the biggest for its bar. */
export function sectionOf(groups: StorageGroup[], section: StorageSection): { group: StorageGroup; share: number }[] {
  const parts = groups.filter((g) => g.section === section).sort((a, b) => b.bytes - a.bytes)
  const biggest = parts[0]?.bytes ?? 0
  return parts.map((group) => ({ group, share: biggest > 0 ? group.bytes / biggest : 0 }))
}

/** Deletes one after another, so a refused one (a song still being sung) does not stop the rest. */
export async function deleteAll(
  items: StorageItem[],
  remove: (item: StorageItem) => Promise<void> = deleteStorageItem,
): Promise<{ deleted: StorageItem[]; failed: { item: StorageItem; error: unknown }[] }> {
  const deleted: StorageItem[] = []
  const failed: { item: StorageItem; error: unknown }[] = []
  for (const item of items) {
    try {
      await remove(item)
      deleted.push(item)
    } catch (error) {
      failed.push({ item, error })
    }
  }
  return { deleted, failed }
}
