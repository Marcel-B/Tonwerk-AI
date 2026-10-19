"""
Sings notes with words through SoulX-Singer (Soul AI Lab, Apache 2.0), for Tonwerk's singing page (Singing/).

Run with the singing environment's Python (Singing:Python, made by deploy/install-singing.sh) and SoulX-Singer's
checkout on the path (the server sets PYTHONPATH). Reads one job as JSON on stdin:

    {"source": checkout, "model": model.pt, "device": null | "mps" | "cpu", "steps": 32, "cfg": 3,
     "voice": {"audio": prompt audio, "metadata": its SoulX-Singer metadata JSON},
     "notes": [{"start": s, "end": s, "pitch": midi, "lyric": null | text}, ...],
     "lyrics": text or null, "pitchShift": semitones, "autoShift": bool, "output": wav,
     "dryRun": false, "mode": null | "align"}

and answers "YUEUI {json}" lines on stdout (stage, aligned, progress, note, done) between whatever the model prints.
With dryRun it only aligns and writes the target metadata to "output", without loading the model, which is how the
alignment is tested without the 2.8 GB of weights. With mode "align" it only answers an "assigned" event: the word,
note type and phonemes of every note in the order sent, for the app's note editor; nothing is written.

The words are laid on the notes the way SoulX-Singer's own MIDI editor reads a MIDI with lyrics
(preprocess/tools/midi_parser.py, midi2notes and notes2meta): one word per note, a word sung over more notes repeats
on them as note type 3 (a slur), a gap over 0.2 s becomes a rest (<SP>) and a shorter one is closed by the note
before, a segment ends at a rest over 2 s. Without lyrics on the notes the text is split into words, each taking as
many notes as it has syllables (vowels in CMUdict, or g2p_en's guess); "-" or "+" alone holds the word before over one
more note, and "beau-ti-ful" names the syllables itself. Lines in [brackets] (section tags) are skipped.
"""

import json
import re
import resource
import sys
import time

EVENT = "YUEUI "

# SoulX-Singer's own segmentation (midi_parser.py): a rest this long ends a segment, a gap this long becomes a rest.
MAX_GAP = 2.0
SILENCE = 0.2
# The editor allows 60 s per segment; shorter ones keep the attention over prompt and target small on the Mac.
MAX_SEGMENT = 30.0
# Silence after a segment's last note, so its release is not cut (the editor adds 0.4 s where it cuts a recording).
TAIL = 0.4
HOLD = {"-", "+", "~", "_"}
TAG = re.compile(r"\[[^\]]*\]")
EDGE = re.compile(r"^[^\w']+|[^\w']+$")
# German letters as English spelling, for words g2p_en has never seen; SoulX-Singer sings no German.
SPELLING = str.maketrans({"ä": "ae", "ö": "oe", "ü": "ue", "ß": "ss", "Ä": "Ae", "Ö": "Oe", "Ü": "Ue", "’": "'"})


def emit(event, **data):
    print(EVENT + json.dumps({"event": event, **data}), flush=True)


class Phonemes:
    """g2p_en without its part-of-speech tagger: a word alone has no sentence to tag."""

    def __init__(self):
        from g2p_en import G2p

        self.g2p = G2p()
        self.known = {}

    def of(self, word):
        word = word.lower()
        if word not in self.known:
            phones = self.g2p.cmu.get(word, [None])[0] or self.g2p.predict(word)
            self.known[word] = [p for p in phones if re.fullmatch(r"[A-Z]+[0-2]?", p)] or ["AH0"]
        return self.known[word]

    def syllables(self, word):
        return max(1, sum(1 for p in self.of(word) if p[-1].isdigit()))

    def token(self, word):
        return "en_" + "-".join(self.of(word))


def words_of(lyrics):
    """The sung words in order: (word, syllables given by hyphens or None), and None for a held note."""
    words = []
    for line in (lyrics or "").splitlines():
        line = TAG.sub(" ", line)
        for raw in line.split():
            if raw in HOLD:
                words.append(None)
                continue
            raw = raw.translate(SPELLING)
            pieces = [EDGE.sub("", p) for p in raw.split("-")]
            pieces = [re.sub(r"[^A-Za-z']", "", p) for p in pieces]
            pieces = [p for p in pieces if p.strip("'")]
            if not pieces:
                continue
            words.append(("".join(pieces), len(pieces) if len(pieces) > 1 else None))
    return words


def slots_of(words, phonemes):
    """One entry per sung note: (word, type), type 2 for its first note and 3 for the notes it is held over."""
    slots = []
    last = None
    for entry in words:
        if entry is None:
            if last is not None:
                slots.append((last, 3))
            continue
        word, given = entry
        count = given or phonemes.syllables(word)
        slots.append((word, 2))
        slots.extend([(word, 3)] * (count - 1))
        last = word
    return slots


def assign(notes, lyrics, phonemes):
    """
    What each of the notes (in time) sings, as (word or None, type), plus the syllables left over. Notes with words of
    their own keep them (a held mark or an empty note after a word holds it), else the lyrics are spread over them.
    """
    words = []
    if any((n.get("lyric") or "").strip() for n in notes):
        last = None
        for note in notes:
            lyric = (note.get("lyric") or "").strip()
            if lyric in HOLD or (not lyric and last is not None):
                words.append((last, 3) if last else (None, 1))
                continue
            found = words_of(lyric)
            word = found[0][0] if found and found[0] else None
            if word is None:
                words.append((None, 1))
                continue
            last = word
            words.append((word, 2))
        return words, 0
    slots = slots_of(words_of(lyrics), phonemes)
    for index in range(len(notes)):
        words.append(slots[index] if index < len(slots) else (None, 1))
    return words, max(0, len(slots) - len(notes))


def align(notes, lyrics, phonemes):
    """
    The notes as (start, end, word or None, pitch, type), in time and without overlaps, plus what was left over.
    A note without a word is a rest; words without a note are counted, not sung.
    """
    notes = sorted((n for n in notes if n["end"] > n["start"]), key=lambda n: (n["start"], n["pitch"]))
    # Monophonic, as the editor makes it: a note that starts ends the one before.
    flat = []
    for note in notes:
        note = dict(note)
        while flat and note["start"] < flat[-1]["end"]:
            flat[-1]["end"] = note["start"]
            if flat[-1]["end"] - flat[-1]["start"] <= 0.01:
                flat.pop()
                continue
            break
        flat.append(note)

    words, leftover = assign(flat, lyrics, phonemes)
    sung = [(note, word, kind) for note, (word, kind) in zip(flat, words)]

    result = []
    for note, word, kind in sung:
        if word is None:
            continue
        result.append([note["start"], note["end"], word, int(min(127, max(0, note["pitch"]))), kind])
    unsung = sum(1 for _, word, _ in sung if word is None)
    return result, unsung, leftover


def segments_of(sung, phonemes):
    """SoulX-Singer metadata segments: rests between the notes, cut at long rests and before MAX_SEGMENT."""
    segments = []
    current = []

    def flush():
        if not current:
            return
        while current and current[-1][2] == "<SP>":
            current.pop()
        if not current:
            return
        start = current[0][0]
        end = current[-1][1]
        items = current + [[end, end + TAIL, "<SP>", 0, 1]]
        segments.append(
            {
                "index": f"tonwerk_{len(segments)}",
                "language": "English",
                "time": [int(round(start * 1000)), int(round((end + TAIL) * 1000))],
                "duration": " ".join(f"{max(0.01, e - s):.3f}" for s, e, _, _, _ in items),
                "text": " ".join(w for _, _, w, _, _ in items),
                "phoneme": " ".join("<SP>" if w == "<SP>" else phonemes.token(w) for _, _, w, _, _ in items),
                "note_pitch": " ".join(str(p) for _, _, _, p, _ in items),
                "note_type": " ".join(str(t) for _, _, _, _, t in items),
                "f0": "",
            }
        )
        current.clear()

    previous_end = None
    for start, end, word, pitch, kind in sung:
        if previous_end is not None:
            gap = start - previous_end
            if gap > MAX_GAP:
                flush()
            elif gap > SILENCE:
                current.append([previous_end, start, "<SP>", 0, 1])
            elif current and gap > 0:
                # Legato: the note before lasts until this one starts.
                current[-1][1] = start
        if current and end - current[0][0] > MAX_SEGMENT:
            flush()
        current.append([start, end, word, pitch, kind])
        previous_end = end
    flush()
    return segments


def device_of(asked):
    import torch

    if asked:
        return asked
    if torch.backends.mps.is_available():
        return "mps"
    return "cuda" if torch.cuda.is_available() else "cpu"


def peak_memory_gb():
    peak = resource.getrusage(resource.RUSAGE_SELF).ru_maxrss
    # Bytes on macOS, kilobytes on Linux.
    return round(peak / (1 << 30) if sys.platform == "darwin" else peak / (1 << 20), 2)


def load_audio(path, sample_rate):
    """
    Read through soundfile and librosa instead of torchaudio.load, whose MP3 backend depends on which ffmpeg the Mac
    has; the bundled example voices are MP3.
    """
    import librosa
    import numpy as np
    import torch

    audio, _ = librosa.load(path, sr=sample_rate, mono=True)
    return torch.from_numpy(np.ascontiguousarray(audio, dtype=np.float32)).unsqueeze(0)


def sing(job, segments):
    import numpy as np
    import soundfile as sf
    import torch

    sys.path.insert(0, job["source"])
    from soulxsinger.models.soulxsinger import SoulXSinger
    from soulxsinger.utils import data_processor as processing
    from soulxsinger.utils.file_utils import load_config

    processing.load_wav = load_audio
    config = load_config(f"{job['source']}/soulxsinger/config/soulxsinger.yaml")
    device = device_of(job.get("device"))
    emit("stage", stage="loading")
    started = time.monotonic()
    model = SoulXSinger(config)
    checkpoint = torch.load(job["model"], weights_only=False, map_location="cpu")
    model.load_state_dict(checkpoint["state_dict"], strict=True)
    del checkpoint
    model.eval().to(device)
    data = processing.DataProcessor(
        hop_size=config.audio.hop_size,
        sample_rate=config.audio.sample_rate,
        phoneset_path=f"{job['source']}/soulxsinger/utils/phoneme/phone_set.json",
        device=device,
    )
    with open(job["voice"]["metadata"], encoding="utf-8") as file:
        prompt_meta = json.load(file)[0]
    prompt = data.process(prompt_meta, job["voice"]["audio"])
    loaded = time.monotonic()
    emit("note", text=f"SoulX-Singer on {device}, loaded in {loaded - started:.1f} s")

    emit("stage", stage="singing")
    rate = config.audio.sample_rate
    total = int(segments[-1]["time"][1] / 1000 * rate)
    merged = np.zeros(total, dtype=np.float32)
    steps = int(job.get("steps") or config.infer.n_steps)
    cfg = float(job.get("cfg") or config.infer.cfg)
    for index, segment in enumerate(segments):
        target = data.process(dict(segment), None)
        with torch.no_grad():
            audio = model.infer(
                {"prompt": prompt, "target": target},
                auto_shift=bool(job.get("autoShift")),
                pitch_shift=int(job.get("pitchShift") or 0),
                n_steps=steps,
                cfg=cfg,
                control="score",
            )
        audio = audio.squeeze().float().cpu().numpy()
        start = int(segment["time"][0] / 1000 * rate)
        length = min(len(audio), total - start)
        merged[start : start + length] = audio[:length]
        emit("progress", share=round((index + 1) / len(segments), 3))
    peak = float(np.max(np.abs(merged))) if len(merged) else 0
    if peak > 0.99:
        merged *= 0.99 / peak
    sf.write(job["output"], merged, rate, subtype="PCM_16")
    return loaded - started, time.monotonic() - loaded


def assign_only(job, phonemes):
    """
    For the app's note editor: what each note sings and how its word is spoken, in the order the notes came, without
    making them monophonic (the editor shows what the user drew).
    """
    notes = job["notes"]
    order = sorted(range(len(notes)), key=lambda i: (notes[i]["start"], notes[i]["pitch"]))
    words, leftover = assign([notes[i] for i in order], job.get("lyrics"), phonemes)
    result = [None] * len(notes)
    for index, (word, kind) in zip(order, words):
        result[index] = {"word": word, "type": kind, "phonemes": " ".join(phonemes.of(word)) if word else ""}
    emit("assigned", notes=result, leftover=leftover)


def main():
    job = json.loads(sys.stdin.read())
    phonemes = Phonemes()
    if job.get("mode") == "align":
        assign_only(job, phonemes)
        return
    sung, unsung, leftover = align(job["notes"], job.get("lyrics"), phonemes)
    if not sung:
        raise SystemExit("Error: no note has a word to sing; write lyrics for the melody.")
    segments = segments_of(sung, phonemes)
    emit(
        "aligned",
        notes=len(sung) + unsung,
        sung=len(sung),
        unsung=unsung,
        leftover=leftover,
        segments=len(segments),
        seconds=round(segments[-1]["time"][1] / 1000, 2),
    )
    if job.get("dryRun"):
        with open(job["output"], "w", encoding="utf-8") as file:
            json.dump(segments, file, ensure_ascii=False, indent=1)
        emit("done", loadSeconds=None, singSeconds=None, peakMemoryGb=None)
        return
    load_seconds, sing_seconds = sing(job, segments)
    emit("done", loadSeconds=round(load_seconds, 1), singSeconds=round(sing_seconds, 1), peakMemoryGb=peak_memory_gb())


if __name__ == "__main__":
    main()
