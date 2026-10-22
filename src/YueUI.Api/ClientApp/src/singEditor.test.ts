import { describe, expect, test } from 'vitest'
import { SingEditor, isHold, noteName, variable } from './singEditor'

function editorWith(...notes: [start: number, length: number, pitch: number, lyric: string][]): SingEditor {
  const editor = new SingEditor()
  editor.load({
    tempoBpm: 120,
    lyrics: '',
    title: '',
    // At 120 BPM a beat is half a second.
    notes: notes.map(([start, length, pitch, lyric]) => ({
      start: start / 2,
      end: (start + length) / 2,
      pitch,
      lyric,
    })),
  })
  return editor
}

describe('SingEditor', () => {
  test('a drawn note snaps to the grid, sings "la" and never reaches into the next one', () => {
    const editor = editorWith([2, 1, 64, 'you'])
    editor.drawLength = 4
    const note = editor.add(1.3, 60)
    expect(note).toMatchObject({ start: 1.25, length: 0.75, pitch: 60, lyric: 'la' })
    expect(editor.selection).toBe(note.id)
    expect(editor.notes.map((n) => n.lyric)).toEqual(['la', 'you'])
  })

  test('typing several words writes one per note from there and answers the note after', () => {
    const editor = editorWith([0, 1, 60, ''], [1, 1, 62, ''], [2, 1, 64, ''], [3, 1, 65, ''])
    const next = editor.type('hap py birth', editor.notes[0]!.id)
    expect(editor.notes.map((n) => n.lyric)).toEqual(['hap', 'py', 'birth', ''])
    expect(next).toBe(editor.notes[3]!.id)
  })

  test('moves and stretches land on the grid, undo brings the notes back', () => {
    const editor = editorWith([0, 1, 60, 'la'])
    const id = editor.notes[0]!.id
    editor.move(id, 1.1, 2)
    expect(editor.notes[0]).toMatchObject({ start: 1, pitch: 62 })
    editor.resize(id, 0.6)
    expect(editor.notes[0]!.length).toBe(1.5)
    expect(editor.drawLength).toBe(1.5)
    editor.undo()
    editor.undo()
    expect(editor.notes[0]).toMatchObject({ start: 0, pitch: 60, length: 1 })
    expect(editor.canUndo).toBe(false)
  })

  test('only notes with words are sung, unless none has any', () => {
    const editor = editorWith([0, 1, 60, 'oh'], [1, 1, 62, ''], [2, 2, 64, '-'])
    expect(editor.sung()).toEqual([
      { start: 0, end: 0.5, pitch: 60, lyric: 'oh' },
      { start: 1, end: 2, pitch: 64, lyric: '-' },
    ])
    expect(editor.sung(true)).toHaveLength(3)
    const silent = editorWith([0, 1, 60, ''], [1, 1, 62, ''])
    expect(silent.hasLyrics).toBe(false)
    expect(silent.sung().map((n) => n.lyric)).toEqual([undefined, undefined])
  })

  test('spreading lays words on first notes, holds on the rest, and keeps the phonemes by note', () => {
    const editor = editorWith([0, 1, 60, ''], [1, 1, 62, ''], [2, 1, 64, ''])
    editor.spread({
      leftover: 1,
      notes: [
        { word: 'star', type: 2, phonemes: 's t aa r' },
        { word: 'star', type: 3, phonemes: '' },
        { word: null, type: 1, phonemes: '' },
      ],
    })
    expect(editor.notes.map((n) => n.lyric)).toEqual(['star', '-', ''])
    expect(isHold(editor.notes[1]!)).toBe(true)
    expect(editor.phonemes).toEqual({ [editor.notes[0]!.id]: 's t aa r' })
    expect(editor.leftover).toBe(1)
  })

  test('the MIDI file carries tempo, a lyric event per sung note and ends its notes before the next starts', () => {
    const editor = editorWith([0, 1, 60, 'hi'], [1, 1, 62, ''])
    const bytes = [...editor.midi('Song')]
    const text = (s: string) => [...new TextEncoder().encode(s)]
    expect(bytes.slice(0, 4)).toEqual(text('MThd'))
    // 500000 µs per beat at 120 BPM.
    const tempo = bytes.findIndex((b, i) => b === 0xff && bytes[i + 1] === 0x51)
    expect(bytes.slice(tempo + 3, tempo + 6)).toEqual([0x07, 0xa1, 0x20])
    const lyric = bytes.findIndex((b, i) => b === 0xff && bytes[i + 1] === 0x05)
    expect(bytes.slice(lyric + 3, lyric + 5)).toEqual(text('hi'))
    // Note off of 60 at tick 480 comes before note on of 62 at the same tick.
    const off = bytes.findIndex((b, i) => b === 0x80 && bytes[i + 1] === 60)
    const on = bytes.findIndex((b, i) => b === 0x90 && bytes[i + 1] === 62)
    expect(off).toBeLessThan(on)
  })

  test('variable lengths and note names', () => {
    expect(variable(0)).toEqual([0])
    expect(variable(480)).toEqual([0x83, 0x60])
    expect(noteName(60)).toBe('C3')
    expect(noteName(37)).toBe('C#1')
  })
})
