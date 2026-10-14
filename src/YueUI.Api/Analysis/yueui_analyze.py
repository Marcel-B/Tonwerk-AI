"""Hears tempo and key of songs for Tonwerk (Analysis/SongAnalyzer.cs).

Run with a Python that has librosa: the photo videos' environment (deploy/install-montage.sh) or audio to MIDI's
(deploy/install-midi.sh); both have it. One process takes several songs, since importing librosa costs more than
hearing one song.

In: one JSON object as the only argument
    {"songs": [{"id": "run/song1", "audio": "/abs/audio.flac", "tempoHint": 88.0 | null}]}
Out: lines starting with "YUEUI " on stdout, one per song:
    {"event": "song", "id": "run/song1", "tempo": 88.1, "key": "Am", "keyStrength": 0.71}
    {"event": "failed", "id": "run/song1", "message": "..."}
Anything else is a library talking.
"""

import json
import sys

# Krumhansl and Kessler's key profiles: how strongly each scale degree belongs to a major or minor key, from
# listeners' ratings. Correlating a song's pitch classes with every rotation names the key that fits best.
MAJOR = [6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88]
MINOR = [6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17]

# Spelled as the key picker spells them (abcTranspose.ts): flats where a key signature has flats.
MAJOR_NAMES = ["C", "Db", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B"]
MINOR_NAMES = ["Cm", "C#m", "Dm", "Ebm", "Em", "Fm", "F#m", "Gm", "G#m", "Am", "Bbm", "Bm"]


def emit(event: dict) -> None:
    print("YUEUI " + json.dumps(event), flush=True)


def tempo_of(y, sr, hint):
    import librosa
    import numpy as np

    onset = librosa.onset.onset_strength(y=y, sr=sr)
    # beat_track settles on one octave of the tempo; songs are often heard at double or half speed by it. A tempo the
    # song was asked for (the score's Q:, ACE's bpm) picks the octave, else the beat tracker's own prior (120) does.
    tempo, _ = librosa.beat.beat_track(onset_envelope=onset, sr=sr, start_bpm=hint or 120.0)
    tempo = float(np.atleast_1d(tempo)[0])
    if tempo <= 0:
        return None
    if hint:
        candidates = [tempo / 2, tempo, tempo * 2, tempo * 2 / 3, tempo * 3 / 2]
        best = min(candidates, key=lambda t: abs(np.log2(t / hint)))
        # Only an octave or a third off is the tracker's choice of level; anything further the song really plays.
        if abs(np.log2(best / hint)) < 0.08:
            tempo = best
    return round(tempo, 1)


def key_of(y, sr):
    import librosa
    import numpy as np

    # The harmonic part only: drums smear every pitch class alike.
    harmonic = librosa.effects.harmonic(y, margin=4)
    chroma = librosa.feature.chroma_cqt(y=harmonic, sr=sr)
    profile = chroma.sum(axis=1)
    if profile.sum() <= 0:
        return None, 0.0
    best, best_score, scores = None, -2.0, []
    for root in range(12):
        for names, weights in ((MAJOR_NAMES, MAJOR), (MINOR_NAMES, MINOR)):
            score = float(np.corrcoef(profile, np.roll(weights, root))[0, 1])
            scores.append(score)
            if score > best_score:
                best, best_score = names[root], score
    return best, round(best_score, 3)


def main() -> None:
    import librosa

    request = json.loads(sys.argv[1])
    for song in request["songs"]:
        try:
            # The middle of a song tells as much as all of it; four minutes at most keeps a long one quick.
            y, sr = librosa.load(song["audio"], sr=22050, mono=True, duration=240)
            if len(y) < sr * 5:
                raise ValueError("The song is shorter than five seconds.")
            key, strength = key_of(y, sr)
            emit({"event": "song", "id": song["id"], "tempo": tempo_of(y, sr, song.get("tempoHint")), "key": key, "keyStrength": strength})
        except Exception as error:  # noqa: BLE001 - one bad file must not stop the others
            emit({"event": "failed", "id": song["id"], "message": f"{type(error).__name__}: {error}"})


if __name__ == "__main__":
    main()
