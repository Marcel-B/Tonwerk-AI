/**
 * Section tags for the lyrics. YuE2 has no tag list and passes the lyrics to the model as plain text, so a tag counts
 * by what the model learned: the sections its own scores name (`% intro`, `% verse`, `% pre-chorus`, `% chorus`,
 * `% interlude`, `% bridge`, `% outro` in the planned score.abc), plus the four YuE v1 documented. The others are
 * common in lyric sheets, but neither YuE documents them: they reach the model as words, and whether a [Drop] drops
 * is up to it.
 */
export const knownSectionTags = ['[Intro]', '[Verse]', '[Pre-Chorus]', '[Chorus]', '[Bridge]', '[Interlude]', '[Outro]']

export const otherSectionTags = ['[Hook]', '[Solo]', '[Break]', '[Build-Up]', '[Drop]']

export interface Insertion {
  text: string
  /** Where the caret belongs afterwards: the start of the line below the tag. */
  caret: number
}

/**
 * Directions in parentheses, the way chatbots write them for Suno. YuE2 documents none of this, but in Marcel's test
 * (2026-10-23) they were not sung, and screamed and controlled vocals came where the directions asked for them.
 * Kept in English, as the style is.
 */
export const voiceDirections = [
  '(screaming)',
  '(aggressive vocals)',
  '(controlled vocals)',
  '(whispered vocals)',
  '(spoken word)',
  '(robotic vocals)',
  '(soft vocals)',
  '(belting)',
  '(falsetto)',
  '(echoing vocals)',
]

export const musicDirections = [
  '(building tension)',
  '(full power)',
  '(half-time beat)',
  '(minimal beat)',
  '(breakdown)',
  '(distorted synth bass)',
  '(glitches)',
  '(slowing tempo)',
  '(fade out)',
  '(abrupt silence)',
]

const isTagLine = (line: string): boolean => /^\s*\[[^\]]+\]\s*$/.test(line)
const isDirectionLine = (line: string): boolean => /^\s*\([^)]*\)\s*$/.test(line)

/**
 * Puts the tag on a line of its own at the caret: before the caret's line when it sits at a line's start, else after
 * that line, since a tag must not split a sung line. A tag after a sung line gets an empty line before it, as sections
 * are written; tags in a row (an instrumental's structure) stay together. A direction in parentheses stays inside its
 * section, without an empty line, and joins a direction line right before it: "(a)" and "(b)" become "(a, b)".
 */
export function insertSectionTag(lyrics: string, tag: string, caret: number | null): Insertion {
  let at = Math.min(Math.max(caret ?? lyrics.length, 0), lyrics.length)
  if (at > 0 && lyrics[at - 1] !== '\n') {
    const end = lyrics.indexOf('\n', at)
    at = end === -1 ? lyrics.length : end
  }
  let before = lyrics.slice(0, at)
  const after = lyrics.slice(at)
  const direction = isDirectionLine(tag)
  if (direction) {
    const trimmed = before.replace(/\n*$/, '')
    const previous = trimmed.slice(trimmed.lastIndexOf('\n') + 1)
    if (isDirectionLine(previous) && trimmed.length === before.length - 1) {
      const joined = `${trimmed.replace(/\)\s*$/, '')}, ${tag.trim().slice(1)}\n`
      return { text: joined + after, caret: joined.length }
    }
  }
  if (before.trim() === '') {
    before = ''
  } else {
    before = before.replace(/\n*$/, '')
    const previous = before.slice(before.lastIndexOf('\n') + 1)
    before += direction || isTagLine(previous) ? '\n' : '\n\n'
  }
  const inserted = `${tag}\n`
  // Text after the caret's line already starts on a line of its own; the leading newline of `after` stays its own.
  const rest = after.startsWith('\n') ? after.slice(1) : after
  return { text: before + inserted + rest, caret: before.length + inserted.length }
}

export interface Shouted {
  text: string
  /** The changed stretch, so the field can keep it selected and a second tap turns it back. */
  start: number
  end: number
}

const isLetter = (ch: string | undefined): boolean => ch !== undefined && /\p{L}/u.test(ch)

/**
 * Writes the selection in capitals, which YuE2 tends to sing with more force (Marcel's test, 2026-10-23; not
 * documented). Without a selection it takes the caret's line. A stretch already in capitals goes back to lower case,
 * a line's first letter and the English "I" kept up. Tags in [ ] and directions in ( ) stay as written, since a tag
 * in capitals is a different word to the model.
 */
export function shout(lyrics: string, start: number, end: number): Shouted {
  let from = Math.min(Math.max(Math.min(start, end), 0), lyrics.length)
  let to = Math.min(Math.max(Math.max(start, end), 0), lyrics.length)
  if (from === to) {
    from = lyrics.lastIndexOf('\n', from - 1) + 1
    const lineEnd = lyrics.indexOf('\n', to)
    to = lineEnd === -1 ? lyrics.length : lineEnd
  }
  const part = lyrics.slice(from, to)
  let depth = 0
  let sung = ''
  for (const ch of part) {
    if (ch === '[' || ch === '(') depth++
    else if (ch === ']' || ch === ')') depth = Math.max(0, depth - 1)
    else if (depth === 0) sung += ch
  }
  const loud = /\p{Ll}/u.test(sung)
  const chars = [...part]
  let out = ''
  let lineStart = from === 0 || lyrics[from - 1] === '\n'
  depth = 0
  chars.forEach((ch, i) => {
    if (ch === '[' || ch === '(') depth++
    if (depth === 0 && isLetter(ch)) {
      const before = i > 0 ? chars[i - 1] : lyrics[from - 1]
      const after = i + 1 < chars.length ? chars[i + 1] : lyrics[to]
      const pronoun = ch === 'I' && !isLetter(before) && !isLetter(after)
      out += loud ? ch.toUpperCase() : lineStart || pronoun ? ch : ch.toLowerCase()
      lineStart = false
    } else {
      out += ch
    }
    if (ch === ']' || ch === ')') depth = Math.max(0, depth - 1)
    if (ch === '\n') lineStart = true
  })
  return { text: lyrics.slice(0, from) + out + lyrics.slice(to), start: from, end: from + out.length }
}
