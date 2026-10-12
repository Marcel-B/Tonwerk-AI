"""Tonwerk's audio to MIDI: one recording (typically a singing voice) to a Standard MIDI File with Spotify's Basic Pitch.

    python yueui_basicpitch.py '<json>'

The JSON names `audio` (a file librosa can read: WAV or FLAC), `output` (the .mid to write), `name` (the track's
name), `mono` (one note at a time, as a voice sings), `quantize` (start and end on the sixteenth grid of `tempo`),
`bends` (keep Basic Pitch's pitch bends; only with `mono`, since all notes share one channel) and `tempo` (written
into the file, so Logic's bars line up with the recording; 120 when unknown). With `drums`, the track is a drum stem:
Basic Pitch hears pitches, which a drum kit has none of, so `drum_hits` finds kick, snare and hi-hat by their
frequency bands instead and writes them on General MIDI's drum notes.

Basic Pitch runs through ONNX Runtime on the CPU: the model is small (a few MB, well under a GB of memory with the
audio), so it needs no place in Tonwerk's queue, and the same path runs on the Mac and in the tests' container.
Answers one `YUEUI {json}` line when done; Basic Pitch's own output goes around it.
"""

import json
import logging
import os
import sys
import time

# Basic Pitch warns about every backend it does not find (CoreML, TensorFlow, TFLite); only ONNX is installed.
logging.disable(logging.WARNING)


def emit(**event):
    print("YUEUI " + json.dumps(event), flush=True)


def single_line(notes, minimum):
    """One note at a time: where two overlap, the louder one wins and the other gives way.

    Basic Pitch is polyphonic, so a voice comes back with ghost notes: an overtone an octave or a twelfth up, quieter
    than the sung note, or the next note starting before the last one let go. A quieter note inside a louder one is
    dropped, or keeps only the part that sticks out; a louder one cuts the one before it short.
    """
    kept = []
    for note in sorted(notes, key=lambda n: (n.start, -n.velocity)):
        if kept and note.start < kept[-1].end:
            last = kept[-1]
            if note.velocity < last.velocity:
                if note.end - last.end < minimum:
                    continue
                note.start = last.end
            else:
                last.end = note.start
                if last.end - last.start < minimum:
                    kept.pop()
        kept.append(note)
    return kept


def on_grid(notes, tempo):
    """Start and end on the nearest sixteenth; a note keeps at least one, and none overlaps the next afterwards."""
    step = 60.0 / tempo / 4
    for note in notes:
        start = round(note.start / step) * step
        end = max(start + step, round(note.end / step) * step)
        note.start, note.end = start, end
    notes.sort(key=lambda n: (n.start, n.pitch))
    return notes


def without_overlap(notes):
    """After the grid two notes of a line can share a sixteenth; the earlier one ends where the next begins."""
    kept = []
    for note in notes:
        if kept and note.start < kept[-1].end:
            if note.start <= kept[-1].start:
                continue
            kept[-1].end = note.start
        kept.append(note)
    return kept


# General MIDI's drum notes, which the Logic page maps onto a drum machine's own.
KICK, SNARE, HIHAT = 36, 38, 42


def band_flux(spectrum, freqs, low, high):
    """How much the band got louder from one frame to the next (spectral flux on a log scale), summed over its bins."""
    import numpy as np

    rows = (freqs >= low) & (freqs < high)
    level = np.log1p(100 * spectrum[rows])
    rise = np.maximum(0, np.diff(level, axis=1, prepend=level[:, :1]))
    return rise.sum(axis=0)


def onsets(flux, frames_apart):
    """Frames where the summed flux peaks above its own running average: every hit, whatever drum it is."""
    import numpy as np

    flux = flux / max(flux.max(), 1e-9)
    average = 20
    found = []
    for i in range(1, len(flux) - 1):
        if flux[i] < 0.05 or flux[i] < flux[i - 1] or flux[i] < flux[i + 1]:
            continue
        local = flux[max(0, i - average):i + average + 1]
        if flux[i] < local.mean() + 0.5 * local.std():
            continue
        if found and i - found[-1] < frames_apart:
            if flux[i] > flux[found[-1]]:
                found[-1] = i
            continue
        found.append(i)
    return found


def drum_hits(path):
    """Kick, snare and hi-hat of a drum stem as (seconds, note, strength 0-1).

    Every hit is found once from the flux of the whole spectrum, then sorted by how much each band rose with it (the
    energy just after the onset over the energy just before): the kick rises below 120 Hz, the snare's crack between
    1.5 and 5 kHz, the hi-hat above 8 kHz and hardly in the crack. A band counts as hit when it rose suddenly (4 dB within
    30 ms, which a reverb's tail does not) and reached a good part of its level at the song's loudest hits (the 90th
    percentile), so a hi-hat's faint bleed into the crack band or a snare's into the bass does not count. Kick and snare
    can fall together; a hi-hat under a snare is not told apart from the snare's own noise. Toms and cymbals end up as whichever they sound most like: a sketch of the groove, not
    a transcription.
    """
    import librosa
    import numpy as np

    audio, rate = librosa.load(path, sr=44100, mono=True)
    # Silence in front, so a hit on the very first sample still rises out of something.
    lead = 0.1
    audio = np.concatenate([np.zeros(int(lead * rate), dtype=audio.dtype), audio])
    hop = 256
    spectrum = np.abs(librosa.stft(audio, n_fft=2048, hop_length=hop)) ** 2
    freqs = librosa.fft_frequencies(sr=rate, n_fft=2048)
    frame = hop / rate
    bands = {"low": (30, 120), "crack": (1500, 5000), "air": (8000, 16000)}
    energy = {name: spectrum[(freqs >= lo) & (freqs < hi)].sum(axis=0) for name, (lo, hi) in bands.items()}
    flux = sum(band_flux(np.sqrt(spectrum), freqs, lo, hi) for lo, hi in bands.values())

    window = max(1, int(0.03 / frame))
    hits = onsets(flux, max(1, int(0.05 / frame)))
    rise = {}
    level = {}
    for name, values in energy.items():
        before = np.array([values[max(0, i - window - 1):max(1, i - 1)].mean() for i in hits])
        after = np.array([values[i:i + window].mean() for i in hits])
        # In decibels, so a hit counts by how sudden it is rather than how loud: a reverb's tail rises slowly.
        rise[name] = 10 * np.log10((after + 1e-12) / (before + 1e-12))
        level[name] = after / max(np.percentile(after, 90), 1e-12) if len(after) else after

    found = []
    for k, i in enumerate(hits):
        seconds = max(0.0, i * frame - lead)
        if rise["low"][k] >= 4 and level["low"][k] >= 0.2:
            found.append((seconds, KICK, min(1.0, level["low"][k])))
        snare = rise["crack"][k] >= 4 and level["crack"][k] >= 0.35
        if snare:
            found.append((seconds, SNARE, min(1.0, level["crack"][k])))
        if rise["air"][k] >= 4 and level["air"][k] >= 0.05 and not snare:
            found.append((seconds, HIHAT, min(1.0, level["air"][k])))
    return found


def write_drums(job, tempo, started):
    import pretty_midi

    step = 60.0 / tempo / 4
    hits = drum_hits(job["audio"])
    if job.get("quantize"):
        # Two hits of one drum that land on the same sixteenth are one hit.
        snapped = {}
        for seconds, note, strength in hits:
            key = (round(seconds / step), note)
            snapped[key] = max(strength, snapped.get(key, 0))
        hits = sorted((index * step, note, strength) for (index, note), strength in snapped.items())
    midi = pretty_midi.PrettyMIDI(initial_tempo=tempo)
    drums = pretty_midi.Instrument(program=0, is_drum=True, name=job.get("name") or "Drums")
    for seconds, note, strength in hits:
        velocity = int(round(50 + 77 * min(1.0, strength)))
        drums.notes.append(pretty_midi.Note(velocity=velocity, pitch=note, start=seconds, end=seconds + min(step, 0.1)))
    midi.instruments.append(drums)
    midi.write(job["output"])
    emit(event="done", notes=len(drums.notes), seconds=round(time.monotonic() - started, 2))


def main():
    job = json.loads(sys.argv[1])
    started = time.monotonic()
    try:
        from basic_pitch import FilenameSuffix, build_icassp_2022_model_path
        from basic_pitch.inference import predict
    except ImportError as error:
        print(f"Error: Basic Pitch is not installed completely ({error}); run deploy/install-midi.sh.", flush=True)
        sys.exit(1)

    tempo = float(job.get("tempo") or 120)
    if job.get("drums"):
        write_drums(job, tempo, started)
        return
    mono = bool(job.get("mono", True))
    _, midi, _ = predict(
        job["audio"],
        build_icassp_2022_model_path(FilenameSuffix.onnx),
        onset_threshold=0.5,
        frame_threshold=0.3,
        # Shorter than a sixteenth at 120 bpm is a consonant or a breath, not a note.
        minimum_note_length=100,
        # A singing voice's range, so hiss and low rumble do not become notes; an instrument's stem keeps the full one.
        minimum_frequency=job.get("minHz"),
        maximum_frequency=job.get("maxHz"),
        multiple_pitch_bends=False,
        melodia_trick=True,
        midi_tempo=tempo,
    )

    instrument = midi.instruments[0]
    instrument.name = job.get("name") or "Vocal"
    notes = instrument.notes
    if mono:
        notes = single_line(notes, 0.06)
    if job.get("quantize"):
        notes = on_grid(notes, tempo)
        if mono:
            notes = without_overlap(notes)
    instrument.notes = notes
    if not (mono and job.get("bends")):
        instrument.pitch_bends = []
    midi.write(job["output"])
    emit(event="done", notes=len(notes), seconds=round(time.monotonic() - started, 2))


if __name__ == "__main__":
    main()
    # Leave without Python's shutdown: on the Mac, a library's static destructors (onnxruntime's, at one
    # interpreter's exit beside numba and scikit-learn) ran into a mutex already torn down and aborted with
    # "recursive_mutex lock failed" after the file was written. Nothing is left to clean up at this point.
    sys.stdout.flush()
    sys.stderr.flush()
    os._exit(0)
