import type { CoverStyle, ImageModelInfo, ImageReferenceChoice, ReferenceRole } from './types'

/**
 * Reference pictures for a painted cover (`CoverReferences.vue`) and the cover styles that keep them
 * (Images/CoverStyles.cs). The app's `CoverReferences.swift` follows the same rules; change both together.
 */

/** The roles one chooses; `start` is given by the model (Z-Image and Krea 2 paint over the first picture). */
export const lookedRoles: readonly ReferenceRole[] = ['style', 'figure', 'place']

/** A reference in the dialog: what is sent, and what shows it. */
export interface ReferenceItem {
  choice: ImageReferenceChoice
  /** An address for the thumbnail (a blob URL for an upload). */
  preview: string
  label: string
}

/** The start picture's strength when nothing else is said: half of its layout stays. */
export const defaultStartStrength = 0.5

/**
 * What the model takes of the references: the first ones it can look at, or the first as a start picture, or none.
 * The others stay in the dialog, so switching back to Klein finds them again.
 */
export function fitReferences(
  items: readonly ReferenceItem[],
  model: Pick<ImageModelInfo, 'references' | 'startImage'> | null,
  startStrength = defaultStartStrength,
): ImageReferenceChoice[] {
  const looked = model?.references ?? 0
  if (looked > 0) {
    return items.slice(0, looked).map((item) => ({
      ...item.choice,
      role: item.choice.role === 'start' ? 'style' : item.choice.role,
      strength: null,
    }))
  }
  if (model?.startImage && items.length > 0) {
    return [{ ...items[0]!.choice, role: 'start', strength: startStrength }]
  }
  return []
}

/** A style's pictures as references; a picture kept as a start picture is looked at for its style by Klein. */
export function styleReferences(style: CoverStyle, url: (styleId: number, imageId: string) => string): ReferenceItem[] {
  return style.images.map((image) => ({
    choice: { source: 'style', id: `${style.id}/${image.id}`, role: image.role },
    preview: url(style.id, image.id),
    label: style.name,
  }))
}

/**
 * The description with one style's text swapped for another's: the old text is taken out where it still stands, the
 * new one added at the end, so choosing a style twice never doubles it and a hand-written description stays.
 */
export function withStyleText(prompt: string, previous: string | null, next: string | null): string {
  let text = prompt
  if (previous?.trim()) {
    const at = text.lastIndexOf(previous.trim())
    if (at >= 0) {
      text = (text.slice(0, at) + text.slice(at + previous.trim().length)).replace(/\s{2,}/g, ' ')
    }
  }
  text = text.trim()
  if (next?.trim() && !text.includes(next.trim())) {
    text = text ? `${text} ${next.trim()}` : next.trim()
  }
  return text
}
