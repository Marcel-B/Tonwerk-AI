/**
 * Building blocks for a painted cover's description, like the style blocks for a song (`styleTags.ts`). Each block is
 * one English sentence the image models read well; the dialog shows its German or English name (`coverBlock_<key>` in
 * `i18n.ts`). A block is in the description when its sentence is, so the text stays the one truth: typing it by hand
 * counts, deleting it unchooses it. Only what should be seen, as in `suggestPrompt`: the models have no negative prompt
 * and draw what a prompt names. The app's `CoverBlocks.swift` holds the same list and functions; change both.
 */
export type CoverGroup = 'medium' | 'era' | 'light' | 'framing'

export interface CoverBlock {
  key: string
  group: CoverGroup
  text: string
}

export const coverGroups: CoverGroup[] = ['medium', 'era', 'light', 'framing']

/** In the order the description lists them. A medium is one of a kind: choosing one replaces the other. */
export const coverBlocks: CoverBlock[] = [
  { key: 'photo', group: 'medium', text: 'Shot as a photograph, natural film grain, shallow depth of field.' },
  { key: 'oil', group: 'medium', text: 'Painted in oil on canvas, visible brush strokes.' },
  { key: 'watercolour', group: 'medium', text: 'A soft watercolour painting, bleeding washes on paper.' },
  {
    key: 'pencil',
    group: 'medium',
    text: 'A graphite pencil sketch on textured paper, visible strokes and cross-hatching, monochrome.',
  },
  { key: 'charcoal', group: 'medium', text: 'A charcoal drawing, smudged deep blacks and soft grey tones.' },
  { key: 'ink', group: 'medium', text: 'An ink drawing with bold brush lines and hatching, black on white paper.' },
  { key: 'lineArt', group: 'medium', text: 'Clean line art, thin even outlines on a flat background.' },
  {
    key: 'screenPrint',
    group: 'medium',
    text: 'A screen print in two or three flat inks, slightly misregistered, on grainy paper.',
  },
  { key: 'collage', group: 'medium', text: 'A collage of cut paper and old photographs with torn edges.' },
  { key: 'render', group: 'medium', text: 'A 3D render with soft studio lighting and smooth materials.' },
  { key: 'anime', group: 'medium', text: 'A still from a hand-drawn anime film.' },
  {
    key: 'bauhaus',
    group: 'medium',
    text: 'A minimal Bauhaus composition of simple geometric shapes in primary colours.',
  },
  { key: 'instant', group: 'medium', text: 'A faded instant photo with soft colours and a slight vignette.' },

  { key: 'jazz60s', group: 'era', text: 'Like a 1960s jazz record sleeve, bold flat colour fields.' },
  { key: 'vinyl70s', group: 'era', text: 'With the look of a 1970s rock record sleeve, warm faded colours.' },
  { key: 'synthwave80s', group: 'era', text: '1980s synthwave look, a neon grid, chrome and a setting sun.' },
  { key: 'rave90s', group: 'era', text: 'Like a 1990s rave flyer, acid colours and blurred motion.' },
  { key: 'y2k', group: 'era', text: 'Y2K aesthetic, glossy chrome and iridescent plastic.' },
  { key: 'cassette', group: 'era', text: 'Lo-fi cassette aesthetic, washed-out colours, dust and tape grain.' },

  { key: 'neon', group: 'light', text: 'Lit by neon signs at night, wet reflections.' },
  { key: 'goldenHour', group: 'light', text: 'Warm golden-hour light, long shadows.' },
  { key: 'blackWhite', group: 'light', text: 'Black and white, strong contrast.' },
  { key: 'pastel', group: 'light', text: 'Soft pastel colours.' },
  { key: 'dramatic', group: 'light', text: 'High contrast, deep shadows, dramatic light.' },
  { key: 'duotone', group: 'light', text: 'A duotone of two strong colours.' },
  { key: 'mist', group: 'light', text: 'A misty, hazy atmosphere.' },

  { key: 'portrait', group: 'framing', text: 'A close portrait of a single figure in the centre.' },
  { key: 'landscape', group: 'framing', text: 'A wide landscape with a small figure in it.' },
  { key: 'closeUp', group: 'framing', text: 'An extreme close-up of one object.' },
  { key: 'symmetric', group: 'framing', text: 'A strictly symmetrical composition.' },
  { key: 'space', group: 'framing', text: 'Lots of empty space around a small central motif.' },
  { key: 'above', group: 'framing', text: 'Seen from directly above.' },
]

const byKey = new Map(coverBlocks.map((block) => [block.key, block]))

/**
 * Which blocks fit a song, from the words of its style: the dialog's first tab. Each entry is a word looked for in the
 * style (lower-cased, as a whole word or phrase) and the blocks it suggests, in order of how well they fit.
 */
const forStyle: [string, string[]][] = [
  ['synthwave', ['synthwave80s', 'neon']],
  ['synthpop', ['synthwave80s', 'pastel']],
  ['8-bit', ['synthwave80s', 'duotone']],
  ['techno', ['neon', 'duotone', 'symmetric']],
  ['house', ['neon', 'duotone']],
  ['industrial', ['blackWhite', 'dramatic']],
  ['electro', ['neon', 'duotone']],
  ['electronic', ['neon', 'render']],
  ['dubstep', ['neon', 'dramatic']],
  ['trap', ['dramatic', 'photo']],
  ['eurodance', ['rave90s', 'y2k']],
  ['dance pop', ['y2k', 'pastel']],
  ['disco', ['rave90s', 'neon']],
  ['funk', ['vinyl70s', 'screenPrint']],
  ['lo-fi', ['cassette', 'pastel', 'anime']],
  ['lofi', ['cassette', 'pastel', 'anime']],
  ['chillout', ['cassette', 'watercolour']],
  ['downtempo', ['cassette', 'mist']],
  ['lounge', ['jazz60s', 'goldenHour']],
  ['ambient', ['watercolour', 'mist', 'space']],
  ['jazz', ['jazz60s', 'screenPrint']],
  ['big band', ['jazz60s']],
  ['bossa nova', ['jazz60s', 'goldenHour']],
  ['soul', ['jazz60s', 'instant']],
  ['blues', ['vinyl70s', 'blackWhite']],
  ['r&b', ['neon', 'portrait']],
  ['hip-hop', ['photo', 'dramatic']],
  ['rap', ['photo', 'dramatic']],
  ['grime', ['photo', 'blackWhite']],
  ['punk', ['screenPrint', 'collage']],
  ['grunge', ['collage', 'blackWhite']],
  ['metal', ['charcoal', 'dramatic']],
  ['rock', ['vinyl70s', 'photo']],
  ['progressive', ['oil', 'landscape']],
  ['post-rock', ['landscape', 'mist']],
  ['folk', ['pencil', 'goldenHour']],
  ['country', ['goldenHour', 'instant']],
  ['bluegrass', ['pencil', 'goldenHour']],
  ['celtic', ['ink', 'mist']],
  ['k-pop', ['pastel', 'y2k']],
  ['j-rock', ['anime', 'dramatic']],
  ['reggae', ['screenPrint', 'goldenHour']],
  ['epic', ['oil', 'landscape', 'dramatic']],
  ['futuristic', ['render', 'neon']],
  ['dark', ['charcoal', 'dramatic']],
  ['melancholic', ['pencil', 'mist']],
  ['dreamy', ['watercolour', 'pastel']],
  ['acoustic', ['pencil', 'goldenHour']],
  ['piano', ['ink', 'space']],
  ['pop', ['pastel', 'portrait']],
]

/** The blocks whose sentence the description holds, in the list's order. */
export function blocksIn(prompt: string): string[] {
  return coverBlocks.filter((block) => prompt.includes(block.text)).map((block) => block.key)
}

/** Adds the block's sentence at the end or, if the description has it, takes it out; another medium goes. */
export function toggleBlock(prompt: string, key: string): string {
  const block = byKey.get(key)
  if (!block) {
    return prompt
  }
  if (prompt.includes(block.text)) {
    return tidy(prompt.replace(block.text, ''))
  }
  let kept = prompt
  if (block.group === 'medium') {
    for (const other of coverBlocks.filter((b) => b.group === 'medium')) {
      kept = kept.replace(other.text, '')
    }
  }
  return tidy(`${tidy(kept)} ${block.text}`)
}

function tidy(text: string): string {
  return text
    .replace(/[ \t]{2,}/g, ' ')
    .replace(/ +\n/g, '\n')
    .replace(/\n +/g, '\n')
    .trim()
}

/** Blocks for the song's style, at most `limit`, those the style hints at first. */
export function blocksForStyle(style: string, limit = 8): string[] {
  const lower = ` ${style.toLowerCase().replace(/[,;/]/g, ' ')} `
  const found: string[] = []
  for (const [word, keys] of forStyle) {
    // A whole word: "rap" must not come from "trap", "rock" not from "post-rock" when that is matched first.
    const pattern = new RegExp(`(^|[\\s])${word.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}(?=[\\s])`)
    if (pattern.test(lower)) {
      for (const key of keys) {
        if (!found.includes(key)) {
          found.push(key)
        }
      }
    }
  }
  return found.slice(0, limit)
}

/** The sentences of the chosen blocks, in the list's order. */
export function blockSentences(keys: readonly string[]): string[] {
  return coverBlocks.filter((block) => keys.includes(block.key)).map((block) => block.text)
}

/** Whether one of the blocks names a medium, so that the suggestion need not say painting. */
export function hasMedium(keys: readonly string[]): boolean {
  return keys.some((key) => byKey.get(key)?.group === 'medium')
}
