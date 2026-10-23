import { ref } from 'vue'
import type { PaintingDetails } from './types'

/**
 * A picture seen large (`ImageViewer.vue`, one, in App.vue): a painted candidate from the cover dialog or a song's cover
 * from the library, to download, with the text it was painted with to read and copy.
 */
export interface ViewedImage {
  src: string
  /** The name a download is saved under. */
  fileName: string
  title: string
  /** How it was painted; null for a photo. */
  painting?: PaintingDetails | null
  /** A song's cover: its painting is read from the server (`GET .../cover/painting`) when it is opened. */
  coverOf?: string
}

export const viewedImage = ref<ViewedImage | null>(null)

export function openViewer(image: ViewedImage): void {
  viewedImage.value = image
}

/** A file name from a title: what file systems refuse taken out, spaces kept. */
export function imageFileName(title: string, suffix: string): string {
  const base =
    title
      .replace(/[\\/:*?"<>|\u0000-\u001f]+/g, ' ')
      .replace(/\s+/g, ' ')
      .trim() || 'Tonwerk'
  return `${base}${suffix ? ` ${suffix}` : ''}.jpg`
}
