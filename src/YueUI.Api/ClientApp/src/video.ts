import { ref } from 'vue'
import { drawCover, type ExportTarget } from './export'
import type { VideoLayerImages } from './api'
import type { VideoEffect, VideoFormat, VideoMotion } from './types'

/**
 * A music video of a song: the browser draws the still layers (blurred background, cover, title, a field of particles
 * or the frames of a plasma ball) as PNGs, the server animates them with ffmpeg (Video/FfmpegVideoRenderer.cs) and encodes the
 * MP4. `VideoDialog.vue` (one, in App.vue) asks for the settings; the library and the song menu open it. A photo video
 * takes a folder of the Mac's photos and clips instead of the blurred cover, cut to the song's beats on the server
 * (Video/MontageBuilder.cs); the browser then only draws a shade over them, the cover if wanted and the title.
 */
export const videoTarget = ref<ExportTarget | null>(null)

export function openVideo(target: ExportTarget): void {
  videoTarget.value = target
}

export const videoFormats: VideoFormat[] = ['landscape', 'portrait']
export const videoEffects: VideoEffect[] = ['bars', 'wave', 'none']
export const videoMotions: VideoMotion[] = ['particles', 'plasma', 'none']

/** What fills the picture: the song's cover, blurred, or a folder of photos and clips cut to the beat. */
export type VideoSource = 'cover' | 'photos'
export const videoSources: VideoSource[] = ['cover', 'photos']

/** The server's `VideoLayout.PlasmaFrames`: the plasma ball comes as this many pictures, which it flickers between. */
const plasmaFrames = 8

/**
 * Where things go; width, height and the band must match the server's `VideoLayout`, which puts the analyzer or the
 * waveform into the band. Cover and title are the browser's own.
 */
interface Layout {
  width: number
  height: number
  bandY: number
  bandHeight: number
  coverX: number
  coverY: number
  coverSize: number
  /** Baseline of the title's first line, and its largest size. */
  titleY: number
  titleSize: number
  /**
   * The plasma ball's square behind a cover, centred on the cover's middle (the server's `VideoLayout.PlasmaArea`);
   * without a cover it takes the whole frame.
   */
  plasmaX: number
  plasmaY: number
  plasmaSize: number
}

export const videoLayouts: Record<VideoFormat, Layout> = {
  landscape: {
    width: 1920,
    height: 1080,
    bandY: 850,
    bandHeight: 180,
    coverX: 680,
    coverY: 90,
    coverSize: 560,
    titleY: 735,
    titleSize: 64,
    plasmaX: 460,
    plasmaY: -130,
    plasmaSize: 1000,
  },
  // Everything above the bottom fifth, which the apps cover with caption and buttons.
  portrait: {
    width: 1080,
    height: 1920,
    bandY: 1270,
    bandHeight: 220,
    coverX: 160,
    coverY: 230,
    coverSize: 760,
    titleY: 1100,
    titleSize: 68,
    plasmaX: 0,
    plasmaY: 70,
    plasmaSize: 1080,
  },
}

export interface VideoSettings {
  format: VideoFormat
  effect: VideoEffect
  /** Only for a cover video; the photos move themselves. */
  motion: VideoMotion
  showCover: boolean
  showTitle: boolean
  source: VideoSource
  /** The photo video's folder, by name. */
  media: string
  /** What its pictures should show, for CLIP; empty takes the sharpest and best exposed. */
  motif: string
  /** The sung line in the band, where the song's lyrics have times. */
  showLyrics: boolean
}

const storageKey = 'yue-ui.video'

/** Each browser remembers the last choice: a channel tends to post the same kind of video. */
export function loadVideoSettings(): VideoSettings {
  const defaults: VideoSettings = {
    format: 'landscape',
    effect: 'bars',
    motion: 'particles',
    showCover: true,
    showTitle: true,
    source: 'cover',
    media: '',
    motif: '',
    showLyrics: true,
  }
  try {
    const stored = JSON.parse(localStorage.getItem(storageKey) ?? '{}') as Partial<VideoSettings> & {
      particles?: boolean
    }
    // The first version had only particles, on or off.
    const motion = stored.motion ?? (stored.particles === false ? 'none' : undefined)
    return {
      format: videoFormats.includes(stored.format as VideoFormat) ? (stored.format as VideoFormat) : defaults.format,
      effect: videoEffects.includes(stored.effect as VideoEffect) ? (stored.effect as VideoEffect) : defaults.effect,
      motion: videoMotions.includes(motion as VideoMotion) ? (motion as VideoMotion) : defaults.motion,
      showCover: typeof stored.showCover === 'boolean' ? stored.showCover : defaults.showCover,
      showTitle: typeof stored.showTitle === 'boolean' ? stored.showTitle : defaults.showTitle,
      source: videoSources.includes(stored.source as VideoSource) ? (stored.source as VideoSource) : defaults.source,
      media: typeof stored.media === 'string' ? stored.media : defaults.media,
      motif: typeof stored.motif === 'string' ? stored.motif : defaults.motif,
      showLyrics: typeof stored.showLyrics === 'boolean' ? stored.showLyrics : defaults.showLyrics,
    }
  } catch {
    return defaults
  }
}

export function saveVideoSettings(settings: VideoSettings): void {
  try {
    localStorage.setItem(storageKey, JSON.stringify(settings))
  } catch {
    // private mode: only this video has them
  }
}

/** The layers as canvases; `toPngs` turns them into what the server takes. */
export interface VideoLayers {
  /** The analyzer's colour, picked from the cover (`#rrggbb`). */
  color: string
  /** A photo video: `background` is only the shade laid over its pictures. */
  photos: boolean
  background: HTMLCanvasElement
  cover: HTMLCanvasElement | null
  title: HTMLCanvasElement | null
  particles: HTMLCanvasElement | null
  /** The plasma ball's frames and where they go in the picture. */
  plasma: { frames: HTMLCanvasElement[]; x: number; y: number } | null
  /** The timed lyrics' strips (`drawLyricsStrips`). */
  lyrics: HTMLCanvasElement[] | null
}

/**
 * Draws every layer for the song. The cover is the song's own, else the drawn one the export would use; a line under
 * the title names the artist when the export dialog has one.
 */
export async function drawVideoLayers(
  target: ExportTarget,
  settings: VideoSettings,
  subtitle: string,
  /** The timed lines' texts, in order; null where the lyrics have no times or are not wanted. */
  lyrics: string[] | null = null,
): Promise<VideoLayers> {
  const layout = videoLayouts[settings.format]
  const image = await coverImage(target)
  const photos = settings.source === 'photos'
  return {
    color: coverColour(image),
    photos,
    background: photos ? drawShade(layout) : drawBackground(layout, image),
    cover: settings.showCover ? drawCoverLayer(layout, image) : null,
    title: settings.showTitle ? drawTitle(layout, target.title || 'Tonwerk', subtitle) : null,
    particles: !photos && settings.motion === 'particles' ? drawParticles(layout, target.songId) : null,
    plasma: !photos && settings.motion === 'plasma' ? drawPlasma(layout, target.songId, settings.showCover) : null,
    lyrics: settings.showLyrics && lyrics?.length ? drawLyricsStrips(layout, lyrics) : null,
  }
}

export async function toPngs(layers: VideoLayers): Promise<VideoLayerImages> {
  const images: VideoLayerImages = {
    background: layers.photos ? null : await png(layers.background),
    cover: layers.cover ? await png(layers.cover) : null,
    title: layers.title ? await png(layers.title) : null,
  }
  if (layers.photos) {
    images.shade = await png(layers.background)
  }
  if (layers.particles) {
    images.particles = await png(layers.particles)
  }
  if (layers.plasma) {
    images.plasma = await Promise.all(layers.plasma.frames.map(png))
  }
  if (layers.lyrics) {
    images.lyrics = await Promise.all(layers.lyrics.map(png))
  }
  return images
}

/**
 * One still of the video as it will look, for the dialog: the layers on top of each other and, in the band, bars or
 * a wave of made-up heights where ffmpeg will draw the music. Scaled down, since a phone shows it a few hundred pixels
 * wide.
 */
export function drawPreview(layers: VideoLayers, settings: VideoSettings): string {
  const layout = videoLayouts[settings.format]
  const scale = 0.3
  const { canvas, context } = frame(layout.width * scale, layout.height * scale)
  context.scale(scale, scale)
  if (layers.photos) {
    drawPhotoPlaceholder(context, layout)
  }
  context.drawImage(layers.background, 0, 0)
  if (layers.particles) {
    context.drawImage(layers.particles, 0, 0)
  }
  if (layers.plasma) {
    context.drawImage(layers.plasma.frames[0]!, layers.plasma.x, layers.plasma.y)
  }
  if (settings.effect !== 'none') {
    context.fillStyle = layers.color
    const random = seeded(7)
    if (settings.effect === 'bars') {
      for (let x = 0; x + barWidth <= layout.width; x += barPitch) {
        // Louder in the lows, like a song's spectrum.
        const height = layout.bandHeight * (0.15 + 0.75 * random() * (1 - (0.7 * x) / layout.width))
        context.fillRect(x, layout.bandY + layout.bandHeight - height, barWidth, height)
      }
    } else {
      const middle = layout.bandY + layout.bandHeight / 2
      context.beginPath()
      context.moveTo(0, middle)
      for (let x = 0; x <= layout.width; x += 6) {
        const amplitude = (layout.bandHeight / 2) * (0.2 + 0.6 * Math.abs(Math.sin(x / 37) * Math.sin(x / 211)))
        context.lineTo(x, middle - amplitude * (x % 12 === 0 ? 1 : -1))
      }
      context.lineWidth = 3
      context.strokeStyle = layers.color
      context.stroke()
    }
  }
  if (layers.lyrics) {
    // The first line, as it shows while it is sung.
    const strip = layers.lyrics[0]!
    const slot = layout.bandHeight
    context.drawImage(strip, 0, slot, layout.width, slot, 0, layout.bandY, layout.width, slot)
  }
  if (layers.cover) {
    context.drawImage(layers.cover, 0, 0)
  }
  if (layers.title) {
    context.drawImage(layers.title, 0, 0)
  }
  return canvas.toDataURL('image/jpeg', 0.85)
}

/** The server's `FfmpegVideoRenderer.Accent` and the bars' pitch and width (`VideoLayout`), for the preview. */
const accent = '#34d399'
const barPitch = 40
const barWidth = 32

/**
 * The cover's most vivid hue as a light colour that stands out on the darkened background: the hues of a small copy
 * are counted weighted by saturation and brightness, so a few bright pixels outweigh a large grey area. A grey cover
 * keeps Tonwerk's emerald.
 */
export function coverColour(image: CanvasImageSource): string {
  const size = 24
  const { context } = frame(size, size)
  context.drawImage(image, 0, 0, size, size)
  const pixels = context.getImageData(0, 0, size, size).data
  const bins = new Array<number>(36).fill(0)
  const sums = new Array<number>(36).fill(0)
  let total = 0
  for (let i = 0; i < pixels.length; i += 4) {
    const [r, g, b] = [pixels[i]! / 255, pixels[i + 1]! / 255, pixels[i + 2]! / 255]
    const max = Math.max(r, g, b)
    const min = Math.min(r, g, b)
    const chroma = max - min
    if (chroma < 0.08) {
      continue
    }
    const hue = (max === r ? ((g - b) / chroma + 6) % 6 : max === g ? (b - r) / chroma + 2 : (r - g) / chroma + 4) * 60
    // Chroma is saturation times brightness; squared, vivid pixels count far more than dull ones.
    const weight = chroma * chroma
    const bin = Math.floor(hue / 10) % 36
    bins[bin]! += weight
    sums[bin]! += weight * hue
    total += weight
  }
  if (total < 0.5) {
    return accent
  }
  const best = bins.indexOf(Math.max(...bins))
  return hslToHex(sums[best]! / bins[best]!, 0.75, 0.65)
}

function hslToHex(hue: number, saturation: number, lightness: number): string {
  const a = saturation * Math.min(lightness, 1 - lightness)
  const channel = (n: number) => {
    const k = (n + hue / 30) % 12
    const value = lightness - a * Math.max(-1, Math.min(k - 3, 9 - k, 1))
    return Math.round(value * 255)
      .toString(16)
      .padStart(2, '0')
  }
  return `#${channel(0)}${channel(8)}${channel(4)}`
}

async function coverImage(target: ExportTarget): Promise<HTMLImageElement> {
  const url = target.cover ?? URL.createObjectURL(await drawCover(target))
  try {
    const image = new Image()
    image.src = url
    await image.decode()
    return image
  } finally {
    if (!target.cover) {
      URL.revokeObjectURL(url)
    }
  }
}

/**
 * The cover blown up to fill the frame, blurred and darkened, so every song gets a background in its own colours.
 * Blurred by drawing it tiny and scaling it up, since Safari's canvas long had no `filter`.
 */
function drawBackground(layout: Layout, image: HTMLImageElement): HTMLCanvasElement {
  const small = frame(12, Math.round((12 * layout.height) / layout.width))
  small.context.imageSmoothingQuality = 'high'
  drawCovering(small.context, image, small.canvas.width, small.canvas.height)
  const { canvas, context } = frame(layout.width, layout.height)
  context.imageSmoothingEnabled = true
  context.imageSmoothingQuality = 'high'
  context.drawImage(small.canvas, 0, 0, layout.width, layout.height)
  context.fillStyle = 'rgb(0 0 0 / 0.45)'
  context.fillRect(0, 0, layout.width, layout.height)
  // Darker towards the bottom, where the band and the title need contrast.
  const shade = context.createLinearGradient(0, 0, 0, layout.height)
  shade.addColorStop(0, 'rgb(0 0 0 / 0)')
  shade.addColorStop(0.55, 'rgb(0 0 0 / 0.1)')
  shade.addColorStop(1, 'rgb(0 0 0 / 0.55)')
  context.fillStyle = shade
  context.fillRect(0, 0, layout.width, layout.height)
  const vignette = context.createRadialGradient(
    layout.width / 2,
    layout.height / 2,
    Math.min(layout.width, layout.height) * 0.35,
    layout.width / 2,
    layout.height / 2,
    Math.max(layout.width, layout.height) * 0.75,
  )
  vignette.addColorStop(0, 'rgb(0 0 0 / 0)')
  vignette.addColorStop(1, 'rgb(0 0 0 / 0.45)')
  context.fillStyle = vignette
  context.fillRect(0, 0, layout.width, layout.height)
  return canvas
}

/**
 * A photo video's shade: the cover background's darkening without the cover, lighter in the middle so the photos keep
 * their colours, darker at the bottom where the band and the title sit.
 */
function drawShade(layout: Layout): HTMLCanvasElement {
  const { canvas, context } = frame(layout.width, layout.height)
  const shade = context.createLinearGradient(0, 0, 0, layout.height)
  shade.addColorStop(0, 'rgb(0 0 0 / 0.15)')
  shade.addColorStop(0.5, 'rgb(0 0 0 / 0)')
  shade.addColorStop(0.75, 'rgb(0 0 0 / 0.25)')
  shade.addColorStop(1, 'rgb(0 0 0 / 0.6)')
  context.fillStyle = shade
  context.fillRect(0, 0, layout.width, layout.height)
  const vignette = context.createRadialGradient(
    layout.width / 2,
    layout.height / 2,
    Math.min(layout.width, layout.height) * 0.4,
    layout.width / 2,
    layout.height / 2,
    Math.max(layout.width, layout.height) * 0.75,
  )
  vignette.addColorStop(0, 'rgb(0 0 0 / 0)')
  vignette.addColorStop(1, 'rgb(0 0 0 / 0.35)')
  context.fillStyle = vignette
  context.fillRect(0, 0, layout.width, layout.height)
  return canvas
}

/**
 * Where the preview cannot show the photos (they are on the Mac and picked only while the video is made): a few
 * tiles in muted colours that stand for them.
 */
function drawPhotoPlaceholder(context: CanvasRenderingContext2D, layout: Layout): void {
  const columns = layout.width > layout.height ? 4 : 2
  const rows = layout.width > layout.height ? 2 : 4
  const tileWidth = layout.width / columns
  const tileHeight = layout.height / rows
  const random = seeded(11)
  for (let row = 0; row < rows; row++) {
    for (let column = 0; column < columns; column++) {
      context.fillStyle = `hsl(${Math.round(random() * 360)} 25% ${Math.round(28 + random() * 22)}%)`
      context.fillRect(column * tileWidth, row * tileHeight, tileWidth + 1, tileHeight + 1)
    }
  }
}

function drawCoverLayer(layout: Layout, image: HTMLImageElement): HTMLCanvasElement {
  const { canvas, context } = frame(layout.width, layout.height)
  const { coverX: x, coverY: y, coverSize: size } = layout
  const radius = size * 0.04
  context.save()
  context.shadowColor = 'rgb(0 0 0 / 0.55)'
  context.shadowBlur = size * 0.1
  context.shadowOffsetY = size * 0.03
  context.fillStyle = '#000'
  roundedRect(context, x, y, size, size, radius)
  context.fill()
  context.restore()
  context.save()
  roundedRect(context, x, y, size, size, radius)
  context.clip()
  drawCovering(context, image, size, size, x, y)
  context.restore()
  return canvas
}

function drawTitle(layout: Layout, title: string, subtitle: string): HTMLCanvasElement {
  const { canvas, context } = frame(layout.width, layout.height)
  const width = layout.width - 2 * 100
  const font = (weight: number, size: number) => `${weight} ${size}px system-ui, -apple-system, "Segoe UI", sans-serif`
  let size = layout.titleSize
  let lines: string[] = []
  // As large as fits in two lines.
  for (; size >= 36; size -= 4) {
    context.font = font(700, size)
    lines = wrap(context, title, width)
    if (lines.length <= 2 && lines.every((line) => context.measureText(line).width <= width)) {
      break
    }
  }
  context.textAlign = 'center'
  context.textBaseline = 'alphabetic'
  context.shadowColor = 'rgb(0 0 0 / 0.6)'
  context.shadowBlur = 18
  context.shadowOffsetY = 3
  context.fillStyle = 'rgb(255 255 255 / 0.97)'
  let y = layout.titleY
  for (const line of lines.slice(0, 2)) {
    context.fillText(line, layout.width / 2, y, width)
    y += size * 1.15
  }
  if (subtitle) {
    context.font = font(500, Math.round(size * 0.55))
    context.fillStyle = 'rgb(255 255 255 / 0.75)'
    context.fillText(subtitle, layout.width / 2, y - size * 0.15 + size * 0.45, width)
  }
  return canvas
}

/** The server's `VideoLayout.LyricsPerStrip`: a whole song in one canvas would be taller than iOS allows. */
export const lyricsPerStrip = 24

/**
 * The sung lines for the band, as the server's renderer picks them by time: strips of up to `lyricsPerStrip` slots of
 * the band's size under an empty one, each line centred in its slot, as large as fits in two lines, with a shadow so
 * it stays readable over the analyzer.
 */
export function drawLyricsStrips(layout: Layout, lines: string[]): HTMLCanvasElement[] {
  const strips: HTMLCanvasElement[] = []
  const slot = layout.bandHeight
  const width = layout.width - 2 * 80
  const font = (size: number) => `700 ${size}px system-ui, -apple-system, "Segoe UI", sans-serif`
  for (let first = 0; first < lines.length; first += lyricsPerStrip) {
    const chunk = lines.slice(first, first + lyricsPerStrip)
    const { canvas, context } = frame(layout.width, slot * (chunk.length + 1))
    context.textAlign = 'center'
    context.textBaseline = 'middle'
    context.shadowColor = 'rgb(0 0 0 / 0.85)'
    context.shadowBlur = 16
    context.shadowOffsetY = 2
    context.fillStyle = 'rgb(255 255 255 / 0.97)'
    chunk.forEach((text, index) => {
      let size = Math.round(slot * 0.32)
      let wrapped: string[] = []
      for (; size >= 28; size -= 2) {
        context.font = font(size)
        wrapped = wrap(context, text, width)
        if (wrapped.length <= 2 && wrapped.every((line) => context.measureText(line).width <= width)) {
          break
        }
      }
      const middle = slot * (index + 1) + slot / 2
      const step = size * 1.15
      wrapped.slice(0, 2).forEach((line, i, shown) => {
        context.fillText(line, layout.width / 2, middle + (i - (shown.length - 1) / 2) * step, width)
      })
    })
    strips.push(canvas)
  }
  return strips
}

/**
 * Specks of light and a few out-of-focus ones, in the app's mint and white. The server scrolls two copies of the
 * picture stacked, so whatever crosses the top or bottom edge is drawn on the other side as well. Seeded by the song,
 * so the same song gets the same sky.
 */
function drawParticles(layout: Layout, songId: string): HTMLCanvasElement {
  const { canvas, context } = frame(layout.width, layout.height)
  const random = seeded(hashOf(songId))
  const count = Math.round((layout.width * layout.height) / 9000)
  const colours = ['255 255 255', '209 250 229', '167 243 208']
  for (let i = 0; i < count; i++) {
    const bokeh = random() < 0.12
    const radius = bokeh ? 6 + random() * 12 : 1 + random() * 2.5
    const alpha = bokeh ? 0.12 + random() * 0.2 : 0.45 + random() * 0.5
    const colour = colours[Math.floor(random() * colours.length)]!
    const x = random() * layout.width
    const y = random() * layout.height
    const reach = radius * 3
    for (const shift of [-layout.height, 0, layout.height]) {
      const cy = y + shift
      if (cy + reach < 0 || cy - reach > layout.height) {
        continue
      }
      const glow = context.createRadialGradient(x, cy, 0, x, cy, reach)
      glow.addColorStop(0, `rgb(${colour} / ${alpha})`)
      glow.addColorStop(bokeh ? 0.3 : 0.25, `rgb(${colour} / ${alpha * (bokeh ? 0.8 : 0.6)})`)
      glow.addColorStop(1, `rgb(${colour} / 0)`)
      context.fillStyle = glow
      context.fillRect(x - reach, cy - reach, reach * 2, reach * 2)
    }
  }
  return canvas
}

/**
 * A plasma ball without its glass: a glowing core in the cover's middle and lightning from it, drawn `plasmaFrames`
 * times with different bolts; the server flickers between the frames and lets the bass flare them. Behind a cover the
 * bolts reach out around it in a square and end in a spark; without one they run to the frame's edges and leave it.
 * Seeded by the song.
 */
function drawPlasma(
  layout: Layout,
  songId: string,
  behindCover: boolean,
): { frames: HTMLCanvasElement[]; x: number; y: number } {
  const area = behindCover
    ? { x: layout.plasmaX, y: layout.plasmaY, width: layout.plasmaSize, height: layout.plasmaSize }
    : { x: 0, y: 0, width: layout.width, height: layout.height }
  const random = seeded(hashOf(songId) ^ 0x5bd1e995)
  const centre = {
    x: layout.coverX + layout.coverSize / 2 - area.x,
    y: layout.coverY + layout.coverSize / 2 - area.y,
  }
  const square = layout.plasmaSize
  // Where each bolt starts its life, so neighbouring frames keep a family likeness while the shapes change.
  const roots = Array.from({ length: behindCover ? 12 : 10 }, () => random() * Math.PI * 2)
  const frames: HTMLCanvasElement[] = []
  for (let index = 0; index < plasmaFrames; index++) {
    const { canvas, context } = frame(area.width, area.height)
    for (const root of roots) {
      if (random() < 0.15) {
        continue // a bolt that is out this frame
      }
      const angle = root + (random() - 0.5) * 0.7
      const full = behindCover ? square * 0.48 : distanceToEdge(centre, angle, area.width, area.height) + 20
      // Most reach the end, some die out on the way.
      const whole = random() < 0.75
      const reach = whole ? full : full * (0.55 + random() * 0.35)
      const end = { x: centre.x + Math.cos(angle) * reach, y: centre.y + Math.sin(angle) * reach }
      const path = bolt(centre, end, random)
      drawBolt(context, path, 1)
      // A branch or two off the bolt's outer half.
      for (let b = 0; b < 2; b++) {
        if (random() < 0.5) {
          continue
        }
        const from = path[Math.floor(path.length * (0.4 + random() * 0.4))]!
        const turn = angle + (random() < 0.5 ? -1 : 1) * (0.35 + random() * 0.5)
        const length = Math.min(full, square * 0.48) * (0.15 + random() * 0.25)
        drawBolt(
          context,
          bolt(from, { x: from.x + Math.cos(turn) * length, y: from.y + Math.sin(turn) * length }, random),
          0.6,
        )
      }
      if (whole && behindCover) {
        glow(context, end.x, end.y, square * 0.03, '255 230 255', 0.9)
      }
    }
    if (!behindCover) {
      const pulse = 0.85 + random() * 0.3
      glow(context, centre.x, centre.y, square * 0.1 * pulse, '250 232 255', 1)
      glow(context, centre.x, centre.y, square * 0.2 * pulse, '192 132 252', 0.35)
    }
    frames.push(canvas)
  }
  return { frames, x: area.x, y: area.y }
}

/** How far a ray from the point at this angle runs inside the box before it leaves it. */
function distanceToEdge(from: Point, angle: number, width: number, height: number): number {
  const dx = Math.cos(angle)
  const dy = Math.sin(angle)
  const along = [
    dx > 0 ? (width - from.x) / dx : dx < 0 ? -from.x / dx : Infinity,
    dy > 0 ? (height - from.y) / dy : dy < 0 ? -from.y / dy : Infinity,
  ]
  return Math.min(...along)
}

interface Point {
  x: number
  y: number
}

/** A jagged line from one point to another by midpoint displacement: each half bends sideways by a share of its length. */
function bolt(from: Point, to: Point, random: () => number): Point[] {
  let points = [from, to]
  let roughness = 0.22
  for (let depth = 0; depth < 6; depth++) {
    const next: Point[] = [points[0]!]
    for (let i = 1; i < points.length; i++) {
      const a = points[i - 1]!
      const b = points[i]!
      const dx = b.x - a.x
      const dy = b.y - a.y
      const shift = (random() - 0.5) * roughness
      next.push({ x: (a.x + b.x) / 2 - dy * shift, y: (a.y + b.y) / 2 + dx * shift }, b)
    }
    points = next
    roughness *= 0.85
  }
  return points
}

/** A bolt as a plasma ball shows it: a wide violet haze, a pink body and a white-hot core. */
function drawBolt(context: CanvasRenderingContext2D, path: Point[], strength: number): void {
  const strokes: [width: number, colour: string, blur: number][] = [
    [14 * strength, 'rgb(168 85 247 / 0.22)', 30],
    [5 * strength, 'rgb(232 121 249 / 0.7)', 12],
    [1.8 * strength, 'rgb(255 255 255 / 0.95)', 0],
  ]
  context.save()
  context.lineCap = 'round'
  context.lineJoin = 'round'
  for (const [width, colour, blur] of strokes) {
    context.beginPath()
    context.moveTo(path[0]!.x, path[0]!.y)
    for (const point of path.slice(1)) {
      context.lineTo(point.x, point.y)
    }
    context.lineWidth = width
    context.strokeStyle = colour
    context.shadowColor = blur > 0 ? 'rgb(192 132 252 / 0.9)' : 'transparent'
    context.shadowBlur = blur
    context.stroke()
  }
  context.restore()
}

function glow(
  context: CanvasRenderingContext2D,
  x: number,
  y: number,
  radius: number,
  colour: string,
  alpha: number,
): void {
  const gradient = context.createRadialGradient(x, y, 0, x, y, radius)
  gradient.addColorStop(0, `rgb(${colour} / ${alpha})`)
  gradient.addColorStop(0.35, `rgb(${colour} / ${alpha * 0.5})`)
  gradient.addColorStop(1, `rgb(${colour} / 0)`)
  context.fillStyle = gradient
  context.fillRect(x - radius, y - radius, radius * 2, radius * 2)
}

/** Draws the image cut to fill the box, like `object-fit: cover`. */
function drawCovering(
  context: CanvasRenderingContext2D,
  image: HTMLImageElement,
  width: number,
  height: number,
  x = 0,
  y = 0,
): void {
  const scale = Math.max(width / image.naturalWidth, height / image.naturalHeight)
  const sourceWidth = width / scale
  const sourceHeight = height / scale
  context.drawImage(
    image,
    (image.naturalWidth - sourceWidth) / 2,
    (image.naturalHeight - sourceHeight) / 2,
    sourceWidth,
    sourceHeight,
    x,
    y,
    width,
    height,
  )
}

function roundedRect(
  context: CanvasRenderingContext2D,
  x: number,
  y: number,
  width: number,
  height: number,
  radius: number,
): void {
  context.beginPath()
  if (typeof context.roundRect === 'function') {
    context.roundRect(x, y, width, height, radius)
  } else {
    context.rect(x, y, width, height)
  }
}

function frame(width: number, height: number): { canvas: HTMLCanvasElement; context: CanvasRenderingContext2D } {
  const canvas = document.createElement('canvas')
  canvas.width = width
  canvas.height = height
  const context = canvas.getContext('2d')
  if (!context) {
    throw new Error('No canvas to draw the video.')
  }
  return { canvas, context }
}

function png(canvas: HTMLCanvasElement): Promise<Blob> {
  return new Promise((resolve, reject) =>
    canvas.toBlob((blob) => (blob ? resolve(blob) : reject(new Error('The picture could not be drawn.'))), 'image/png'),
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

/** Mulberry32: a small generator that gives the same numbers for the same seed. */
function seeded(seed: number): () => number {
  let state = seed >>> 0
  return () => {
    state = (state + 0x6d2b79f5) >>> 0
    let value = state
    value = Math.imul(value ^ (value >>> 15), value | 1)
    value ^= value + Math.imul(value ^ (value >>> 7), value | 61)
    return ((value ^ (value >>> 14)) >>> 0) / 4294967296
  }
}

/** FNV-1a, as for the drawn cover's colours. */
function hashOf(text: string): number {
  let hash = 0x811c9dc5
  for (let i = 0; i < text.length; i++) {
    hash ^= text.charCodeAt(i)
    hash = Math.imul(hash, 0x01000193)
  }
  return hash >>> 0
}
