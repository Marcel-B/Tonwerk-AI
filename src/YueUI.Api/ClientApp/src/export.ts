import { ref } from 'vue'
import { styleCategories } from './styleTags'

/**
 * Sharing or exporting a song, behind one button: a small AAC for a messenger, or MP3, M4A or FLAC for a music
 * library, each with title, lyrics, artist, genre and cover in its tags, then saved or handed to the share sheet.
 * The server writes the tags and puts in the song's own cover; only a song without one gets the drawn cover from
 * the browser. `ExportDialog.vue` (one, in App.vue) asks for the rest.
 */
export type ExportFormat = 'small' | 'mp3' | 'm4a' | 'flac'

export const exportFormats: ExportFormat[] = ['small', 'mp3', 'm4a', 'flac']

/** The song the dialog is open for. */
export interface ExportTarget {
  songId: string
  title: string
  style: string
  /** The song's own cover, which the server puts in unless another one is sent. */
  cover?: string
  /** Into Tonregal, the music library, instead of a file to save or share. */
  tonregal?: boolean
}

export const exportTarget = ref<ExportTarget | null>(null)

export function openExport(target: ExportTarget): void {
  exportTarget.value = target
}

/** App.vue knows the library; a place that has only a song id (player, playlist) asks it through this. */
let lookup: (songId: string) => ExportTarget | undefined = () => undefined

export function registerExportLookup(find: (songId: string) => ExportTarget | undefined): void {
  lookup = find
}

/** What the library knows about a song known only by its id; without the library at hand, what the id says. */
export function exportTargetFor(songId: string, title = ''): ExportTarget {
  return lookup(songId) ?? { songId, title, style: '' }
}

/** Opens the dialog for a song known only by its id. */
export function openExportFor(songId: string, title = ''): void {
  openExport(exportTargetFor(songId, title))
}

/** Tonregal is set up on the server (`GET /api/tonregal`), so the menus offer handing a song to it. */
export const tonregalReady = ref(false)

/** Opens the dialog to hand a song to Tonregal: the same artist, genre and cover as an export. */
export function openTonregalFor(songId: string, title = ''): void {
  openExport({ ...exportTargetFor(songId, title), tonregal: true })
}

export interface ExportSettings {
  format: ExportFormat
  artist: string
}

const storageKey = 'yue-ui.export'

/** Format and artist are the same for most exports, so each browser remembers them. */
export function loadExportSettings(): ExportSettings {
  try {
    const stored = JSON.parse(localStorage.getItem(storageKey) ?? '{}') as Partial<ExportSettings>
    return {
      format: exportFormats.includes(stored.format as ExportFormat) ? (stored.format as ExportFormat) : 'small',
      artist: typeof stored.artist === 'string' ? stored.artist : '',
    }
  } catch {
    return { format: 'small', artist: '' }
  }
}

export function saveExportSettings(settings: ExportSettings): void {
  try {
    localStorage.setItem(storageKey, JSON.stringify(settings))
  } catch {
    // private mode: only this export has them
  }
}

/** Longest first, so "synthpop" wins over "pop" and "indie rock" over "rock". */
const genres = styleCategories
  .find((category) => category.key === 'genre')!
  .tags.slice()
  .sort((a, b) => b.length - a.length)

/**
 * The style's first genre, as a player's genre field wants one short word rather than the whole prompt: a building
 * block from the genre list where the style has one, else nothing (the field stays open for typing).
 */
export function genreOf(style: string): string {
  const parts = style
    .split(',')
    .map((part) => part.trim().toLowerCase())
    .filter((part) => part !== '')
  for (const part of parts) {
    const genre = genres.find((candidate) => candidate.toLowerCase() === part)
    if (genre) {
      return capitalize(genre)
    }
  }
  // Free-form styles ("dark synthwave with female vocals"): the first genre named anywhere in them.
  const text = ` ${parts.join(' ')} `
  const found = genres.find((candidate) => text.includes(` ${candidate.toLowerCase()} `))
  return found ? capitalize(found) : ''
}

function capitalize(text: string): string {
  return text.charAt(0).toUpperCase() + text.slice(1)
}

/** Square and large enough for a car's screen or Apple Music's full-screen player, some 150 KB as JPEG. */
const coverSize = 1000

/**
 * The default cover: the title on a gradient whose colours come from the song id, so every song looks different
 * but the same song always looks the same, and variants of one run are told apart by their hue.
 */
export async function drawCover(target: ExportTarget): Promise<Blob> {
  const { canvas, context } = square()
  const hash = hashOf(target.songId)
  const hue = hash % 360
  const second = (hue + 40 + ((hash >> 9) % 80)) % 360

  const gradient = context.createLinearGradient(0, 0, coverSize, coverSize)
  gradient.addColorStop(0, `hsl(${hue} 70% 22%)`)
  gradient.addColorStop(1, `hsl(${second} 75% 45%)`)
  context.fillStyle = gradient
  context.fillRect(0, 0, coverSize, coverSize)

  // Two soft glows, like the app's own background.
  for (const [x, y, radius, h] of [
    [0.78, 0.22, 0.55, second],
    [0.15, 0.85, 0.5, hue],
  ] as const) {
    const glow = context.createRadialGradient(
      x * coverSize,
      y * coverSize,
      0,
      x * coverSize,
      y * coverSize,
      radius * coverSize,
    )
    glow.addColorStop(0, `hsl(${h} 90% 70% / 0.45)`)
    glow.addColorStop(1, `hsl(${h} 90% 70% / 0)`)
    context.fillStyle = glow
    context.fillRect(0, 0, coverSize, coverSize)
  }

  const margin = 80
  const width = coverSize - 2 * margin
  context.fillStyle = 'rgb(255 255 255 / 0.95)'
  context.textBaseline = 'alphabetic'
  let size = 120
  let lines: string[] = []
  // As large as fits in four lines.
  for (; size >= 48; size -= 8) {
    context.font = `700 ${size}px system-ui, -apple-system, "Segoe UI", sans-serif`
    lines = wrap(context, target.title || 'Tonwerk', width)
    if (lines.length <= 4 && lines.every((line) => context.measureText(line).width <= width)) {
      break
    }
  }
  const lineHeight = size * 1.1
  let y = coverSize - margin - 70 - (lines.length - 1) * lineHeight
  for (const line of lines.slice(0, 4)) {
    context.fillText(line, margin, y, width)
    y += lineHeight
  }

  context.font = `500 36px system-ui, -apple-system, "Segoe UI", sans-serif`
  context.fillStyle = 'rgb(255 255 255 / 0.7)'
  const genre = genreOf(target.style)
  context.fillText(genre ? `Tonwerk · ${genre}` : 'Tonwerk', margin, coverSize - margin, width)
  return jpeg(canvas)
}

/**
 * Lets the user pick a photo. The input is made on the spot, so a button anywhere can open the picker; iOS only allows
 * that inside the click.
 */
export function pickImage(picked: (file: File) => void): void {
  const input = document.createElement('input')
  input.type = 'file'
  input.accept = 'image/*'
  input.addEventListener('change', () => {
    const file = input.files?.[0]
    if (file) {
      picked(file)
    }
  })
  input.click()
}

/** A photo cut to the middle square, since players show covers square and would otherwise squeeze it. */
export async function photoCover(file: File): Promise<Blob> {
  const url = URL.createObjectURL(file)
  try {
    const image = new Image()
    image.src = url
    await image.decode()
    const side = Math.min(image.naturalWidth, image.naturalHeight)
    const { canvas, context } = square()
    context.drawImage(
      image,
      (image.naturalWidth - side) / 2,
      (image.naturalHeight - side) / 2,
      side,
      side,
      0,
      0,
      coverSize,
      coverSize,
    )
    return jpeg(canvas)
  } finally {
    URL.revokeObjectURL(url)
  }
}

function square(): { canvas: HTMLCanvasElement; context: CanvasRenderingContext2D } {
  const canvas = document.createElement('canvas')
  canvas.width = coverSize
  canvas.height = coverSize
  const context = canvas.getContext('2d')
  if (!context) {
    throw new Error('No canvas to draw the cover.')
  }
  return { canvas, context }
}

function jpeg(canvas: HTMLCanvasElement): Promise<Blob> {
  return new Promise((resolve, reject) =>
    canvas.toBlob(
      (blob) => (blob ? resolve(blob) : reject(new Error('The cover could not be drawn.'))),
      'image/jpeg',
      0.88,
    ),
  )
}

function wrap(context: CanvasRenderingContext2D, text: string, width: number): string[] {
  const lines: string[] = []
  let line = ''
  for (const word of text.split(/\s+/).filter((w) => w !== '')) {
    const candidate = line ? `${line} ${word}` : word
    if (line && context.measureText(candidate).width > width) {
      lines.push(line)
      line = word
    } else {
      line = candidate
    }
  }
  if (line) {
    lines.push(line)
  }
  return lines
}

/** FNV-1a: small and stable, enough to pick colours. */
function hashOf(text: string): number {
  let hash = 0x811c9dc5
  for (let i = 0; i < text.length; i++) {
    hash ^= text.charCodeAt(i)
    hash = Math.imul(hash, 0x01000193)
  }
  return hash >>> 0
}
