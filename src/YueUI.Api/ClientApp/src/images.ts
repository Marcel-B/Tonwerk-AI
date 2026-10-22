import { ref } from 'vue'
import { blockSentences, hasMedium } from './coverBlocks'
import type { ExportTarget } from './export'

/**
 * Covers painted by FLUX.2 Klein on the Mac (Images/ImageMaker.cs). `ImageDialog.vue` (one, in App.vue) asks for the
 * description and lists the song's candidates; the library opens it.
 */
export const imageTarget = ref<ExportTarget | null>(null)

export function openImages(target: ExportTarget): void {
  imageTarget.value = target
}

/**
 * A description for a cover from the song's title and style. In English, since Klein reads it through Qwen3, which
 * follows English best.
 *
 * Only what should be seen: distilled Klein has no negative prompt (mflux encodes one only for the base models with
 * guidance above 1), and "no text, no logos" in the prompt itself put text and logos in, since a diffusion model draws
 * what a prompt names. For the same reason it says painting, not album cover (covers carry type), leaves the title out
 * unless it is wanted in the picture, and drops the style's tempo and vocal parts, which have nothing to show.
 *
 * Chosen cover blocks (`coverBlocks.ts`) follow the mood; with a medium among them (pencil, photo, …) the first sentence
 * stops saying painting, which would fight it.
 */
export function suggestPrompt(
  title: string,
  style: string,
  withTitle: boolean,
  blocks: readonly string[] = [],
): string {
  const mood = style
    .split(',')
    .map((part) => part.trim().replace(/\s+/g, ' '))
    .filter((part) => part !== '' && !/\d|bpm|tempo|vocal|voice|singer|sung|lyrics|language/i.test(part))
    .join(', ')
  return [
    hasMedium(blocks)
      ? 'A square picture, one striking scene with a strong central motif, high detail.'
      : 'A square painting, one striking scene with a strong central motif, rich colour and dramatic light, painterly, high detail.',
    mood ? `Its mood and colours evoke ${mood} music.` : null,
    ...blockSentences(blocks),
    withTitle && title ? `The words "${title}" are written once in elegant, legible lettering.` : null,
  ]
    .filter(Boolean)
    .join(' ')
}

export interface ImageSettings {
  model: string | null
  withTitle: boolean
}

const storageKey = 'yue-ui.images'

/** Each browser remembers the model and the title switch. */
export function loadImageSettings(): ImageSettings {
  try {
    const stored = JSON.parse(localStorage.getItem(storageKey) ?? '{}') as Partial<ImageSettings>
    return {
      model: typeof stored.model === 'string' ? stored.model : null,
      withTitle: stored.withTitle === true,
    }
  } catch {
    return { model: null, withTitle: false }
  }
}

export function saveImageSettings(settings: ImageSettings): void {
  try {
    localStorage.setItem(storageKey, JSON.stringify(settings))
  } catch {
    // private mode: only this dialog has them
  }
}
