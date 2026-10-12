"""Plans a photo video for Tonwerk (Video/MontageBuilder.cs): a song's beats and a folder of photos and videos in, a
list of shots cut on the beat out, with every photo already laid out as a still that ffmpeg only has to zoom into.

Run with its own Python environment (deploy/install-montage.sh): librosa for the beats, Pillow with pillow-heif for
the iPhone's HEIC photos, and, only when a motif is given, open_clip with PyTorch to rank the pictures by it.

In: one JSON object as the only argument
    {"audio": "/abs/audio.flac", "folder": "/abs/photos", "work": "/abs/work", "width": 1920, "height": 1080,
     "motif": "dog on the beach" | null, "clipModel": "...", "clipPretrained": "...", "seed": 42,
     "ffmpeg": "/abs/ffmpeg", "ffprobe": "/abs/ffprobe"}
Out: <work>/plan.json and the stills (<work>/still-0001.jpg, …), and lines starting with "YUEUI " on stdout:
    {"event": "stage", "stage": "music" | "media" | "motif" | "plan", "fraction": 0.4}
    {"event": "note", "message": "..."}
    {"event": "done", "shots": 61, "photos": 40, "videos": 6, "used": 38, "tempo": 118.0, "motif": true}
Anything else is a library talking, which the server keeps for the error message.

plan.json: {"seconds": 182.4, "tempo": 118.0, "shots": [{"start": 0.0, "end": 2.03, "kind": "photo", "still":
"still-0001.jpg", "zoom": [1.0, 1.08], "focus": [0.4, 0.5, 0.6, 0.5]} | {"start", "end", "kind": "video", "file":
"/abs/clip.mov", "from": 3.2, "fit": "cover" | "blur", "hdr": false}]}
"""

import json
import os
import random
import subprocess
import sys
import time
from datetime import datetime

PHOTOS = {".jpg", ".jpeg", ".png", ".heic", ".heif", ".webp"}
VIDEOS = {".mp4", ".mov", ".m4v"}

# More would only make the analysis long; a song has a hundred shots at most.
MAX_FILES = 1500

# Samples taken from a video to judge it, one every few seconds, at most this many.
VIDEO_SAMPLES = 24

# A photo or video whose shape differs from the frame's by more than this is shown whole over a blurred copy of
# itself instead of being cut to fill the frame (a portrait photo in a landscape video would lose two thirds).
FIT_LIMIT = 1.35


def emit(event: dict) -> None:
    print("YUEUI " + json.dumps(event), flush=True)


def stage(name: str, fraction: float) -> None:
    emit({"event": "stage", "stage": name, "fraction": round(min(max(fraction, 0), 1), 3)})


# --- The music -------------------------------------------------------------------------------------------------


def analyse_music(audio: str) -> dict:
    """Beats, the bar's first beat, how loud each beat is and where the song changes section."""
    import librosa
    import numpy as np

    y, sr = librosa.load(audio, sr=22050, mono=True)
    seconds = len(y) / sr
    hop = 512
    onset = librosa.onset.onset_strength(y=y, sr=sr, hop_length=hop)
    tempo, frames = librosa.beat.beat_track(onset_envelope=onset, sr=sr, hop_length=hop)
    tempo = float(np.atleast_1d(tempo)[0])
    beats = list(librosa.frames_to_time(frames, sr=sr, hop_length=hop))
    if len(beats) < 8:
        # Nothing to dance to (a spoken piece, a drone): a steady 120 keeps the cuts calm.
        tempo = 120.0
        beats = list(np.arange(0, seconds, 0.5))
    period = float(np.median(np.diff(beats)))
    # The grid goes on before the first and after the last beat found, so the intro and the end are cut alike.
    while beats[0] - period > 0.05:
        beats.insert(0, beats[0] - period)
    while beats[-1] + period < seconds - 0.05:
        beats.append(beats[-1] + period)
    beats = np.array(beats)
    frames = librosa.time_to_frames(beats, sr=sr, hop_length=hop)

    # The bar's first beat: of the four phases, the one whose beats are hit hardest (kick and bass mostly land there).
    strength = onset[np.clip(frames, 0, len(onset) - 1)]
    downbeat = int(np.argmax([strength[p::4].sum() for p in range(4)]))

    # How loud each beat is, between the song's quiet and loud parts (10th and 90th percentile), smoothed over a bar
    # so that one snare does not change the cutting.
    rms = librosa.feature.rms(y=y, hop_length=hop)[0]
    per_beat = np.array([rms[a:b].mean() if b > a else rms[min(a, len(rms) - 1)] for a, b in zip(frames, list(frames[1:]) + [len(rms)])])
    db = 20 * np.log10(per_beat + 1e-6)
    low, high = np.percentile(db, 10), np.percentile(db, 90)
    energy = np.clip((db - low) / max(high - low, 1e-3), 0, 1)
    energy = np.convolve(energy, np.ones(4) / 4, mode="same")

    # Sections: beat-synchronous timbre and harmony, grouped into about one part per 25 seconds.
    sections = []
    try:
        mfcc = librosa.feature.mfcc(y=y, sr=sr, hop_length=hop, n_mfcc=13)
        chroma = librosa.feature.chroma_cqt(y=y, sr=sr, hop_length=hop)
        features = np.vstack([librosa.util.normalize(mfcc, axis=1), chroma])
        synced = librosa.util.sync(features, frames, aggregate=np.median)
        parts = max(2, round(seconds / 25))
        if synced.shape[1] > parts * 2:
            # sync puts the stretch before the first beat in column 0, so column k begins on beat k - 1.
            sections = sorted(int(b) - 1 for b in librosa.segment.agglomerative(synced, parts) if b > 1)
    except Exception as error:  # a section change only adds cuts; without one the energy still cuts
        emit({"event": "note", "message": f"No sections: {error}"})

    return {
        "seconds": float(seconds),
        "tempo": tempo,
        "beats": [float(b) for b in beats],
        "downbeat": downbeat,
        "energy": [float(e) for e in energy],
        "sections": sections,
    }


def cut(music: dict) -> list[tuple[float, float, float]]:
    """
    Shots as (start, end, energy): a cut on the bar's first beat, every two bars where the song is quiet, every bar in
    between, every half bar where it is loud, and always where a section begins.
    """
    beats, energy, seconds = music["beats"], music["energy"], music["seconds"]
    period = (beats[-1] - beats[0]) / max(len(beats) - 1, 1)
    sections = set()
    for boundary in music["sections"]:
        # Moved to the nearest bar line: a section that begins on a bar's second beat was misheard.
        bar = round((boundary - music["downbeat"]) / 4) * 4 + music["downbeat"]
        if 0 < bar < len(beats):
            sections.add(bar)
    # Cut points as (time, beat index); the intro up to the first bar line is the first shot, unless it is too short
    # to see.
    index = music["downbeat"]
    while index < len(beats) and beats[index] < 1.0:
        index += 4
    cuts = [(0.0, 0)]
    while index < len(beats):
        cuts.append((beats[index], index))
        level = energy[min(index, len(energy) - 1)]
        length = 2 if level > 0.7 else 4 if level > 0.35 else 8
        # Never longer than six seconds, never shorter than six tenths: at a slow tempo two bars stand too long, at
        # a fast one half a bar flickers.
        while length > 1 and length * period > 6:
            length //= 2
        while length < 8 and length * period < 0.6:
            length *= 2
        index = next((b for b in sorted(sections) if index < b < index + length), index + length)
    cuts.append((seconds, len(beats) - 1))
    shots = [
        (start, end, energy[min(beat, len(energy) - 1)])
        for (start, beat), (end, _) in zip(cuts, cuts[1:])
    ]
    # A last shot under a second is a flash; it goes to the one before.
    if len(shots) > 1 and shots[-1][1] - shots[-1][0] < 1.0:
        last = shots.pop()
        shots[-1] = (shots[-1][0], last[1], shots[-1][2])
    return [s for s in shots if s[1] - s[0] > 0.05]


# --- The pictures ----------------------------------------------------------------------------------------------


def files_in(folder: str) -> list[str]:
    found = []
    for root, directories, names in os.walk(folder):
        directories[:] = sorted(d for d in directories if not d.startswith("."))
        for name in sorted(names):
            if name.startswith("."):
                continue
            if os.path.splitext(name)[1].lower() in PHOTOS | VIDEOS:
                found.append(os.path.join(root, name))
                if len(found) >= MAX_FILES:
                    return found
    return found


def measure(image) -> dict:
    """Sharpness (variance of the Laplacian), brightness and a 64-bit difference hash of a small grey copy."""
    import numpy as np

    grey = image.convert("L")
    small = grey.copy()
    small.thumbnail((256, 256))
    pixels = np.asarray(small, dtype=np.float32) / 255
    laplacian = (
        -4 * pixels[1:-1, 1:-1] + pixels[:-2, 1:-1] + pixels[2:, 1:-1] + pixels[1:-1, :-2] + pixels[1:-1, 2:]
    ) if min(pixels.shape) > 2 else np.zeros(1)
    hashed = np.asarray(grey.resize((9, 8)), dtype=np.int16)
    bits = (hashed[:, 1:] > hashed[:, :-1]).flatten()
    return {
        "sharpness": float(laplacian.var()),
        "brightness": float(pixels.mean()),
        "hash": int("".join("1" if b else "0" for b in bits), 2),
    }


def photo_date(image, path: str) -> float:
    try:
        exif = image.getexif()
        taken = exif.get_ifd(0x8769).get(36867) or exif.get(306)
        if taken:
            return datetime.strptime(str(taken).strip()[:19], "%Y:%m:%d %H:%M:%S").timestamp()
    except Exception:
        pass
    return os.path.getmtime(path)


def open_photo(path: str):
    from PIL import Image, ImageOps

    image = Image.open(path)
    date = photo_date(image, path)
    image = ImageOps.exif_transpose(image).convert("RGB")
    return image, date


def probe(ffprobe: str, path: str) -> dict | None:
    result = subprocess.run(
        [ffprobe, "-v", "error", "-select_streams", "v:0", "-show_entries",
         "stream=width,height,color_transfer:stream_side_data=rotation:stream_tags=rotate:format=duration:format_tags=creation_time",
         "-of", "json", path],
        capture_output=True, text=True, timeout=60)
    if result.returncode != 0:
        return None
    data = json.loads(result.stdout or "{}")
    streams = data.get("streams") or []
    if not streams:
        return None
    stream = streams[0]
    width, height = int(stream.get("width") or 0), int(stream.get("height") or 0)
    rotation = 0
    for side in stream.get("side_data_list") or []:
        if "rotation" in side:
            rotation = int(side["rotation"])
    rotation = rotation or int((stream.get("tags") or {}).get("rotate") or 0)
    if abs(rotation) % 180 == 90:
        # ffmpeg turns the picture upright while decoding; its shape is the turned one.
        width, height = height, width
    duration = float((data.get("format") or {}).get("duration") or 0)
    created = (data.get("format") or {}).get("tags", {}).get("creation_time")
    try:
        date = datetime.fromisoformat(created.replace("Z", "+00:00")).timestamp() if created else os.path.getmtime(path)
    except ValueError:
        date = os.path.getmtime(path)
    return {
        "width": width, "height": height, "duration": duration, "date": date,
        # iPhones film in HLG (and some cameras in PQ); without tone mapping it looks washed out next to the photos.
        "hdr": stream.get("color_transfer") in ("arib-std-b67", "smpte2084"),
    }


def video_frames(ffmpeg: str, path: str, at: float) -> list:
    """Two small frames a third of a second apart from `at`: the first to judge, both to see how much moves."""
    from PIL import Image

    result = subprocess.run(
        [ffmpeg, "-nostdin", "-loglevel", "error", "-ss", f"{at:.3f}", "-i", path, "-t", "0.34",
         "-vf", "fps=6,scale=256:-2", "-frames:v", "3", "-f", "image2pipe", "-c:v", "ppm", "-"],
        capture_output=True, timeout=60)
    data, frames, offset = result.stdout, [], 0
    while offset < len(data):
        # P6 <width> <height> 255, each separated by one whitespace, then the pixels.
        fields, position = [], offset
        while len(fields) < 4 and position < len(data):
            while position < len(data) and chr(data[position]).isspace():
                position += 1
            start = position
            while position < len(data) and not chr(data[position]).isspace():
                position += 1
            fields.append(data[start:position])
        position += 1
        if len(fields) < 4 or fields[0] != b"P6":
            break
        width, height = int(fields[1]), int(fields[2])
        end = position + width * height * 3
        if end > len(data):
            break
        frames.append(Image.frombytes("RGB", (width, height), data[position:end]))
        offset = end
    return frames


def scan(folder: str, ffmpeg: str, ffprobe: str) -> list[dict]:
    """Every usable photo and video with how it looks; video samples as candidates of their own."""
    import numpy as np

    paths = files_in(folder)
    items = []
    for number, path in enumerate(paths):
        stage("media", number / max(len(paths), 1))
        extension = os.path.splitext(path)[1].lower()
        try:
            if extension in PHOTOS:
                image, date = open_photo(path)
                thumb = image.copy()
                thumb.thumbnail((512, 512))
                items.append({"kind": "photo", "path": path, "date": date, "width": image.width,
                              "height": image.height, "thumb": thumb, **measure(image)})
                continue
            info = probe(ffprobe, path)
            if not info or info["duration"] < 0.5 or not info["width"]:
                emit({"event": "note", "message": f"Skipped {os.path.basename(path)}: no readable video"})
                continue
            count = max(1, min(VIDEO_SAMPLES, int(info["duration"] / 2.5)))
            step = info["duration"] / count
            samples = []
            for index in range(count):
                at = min(step * (index + 0.5), max(info["duration"] - 0.4, 0))
                frames = video_frames(ffmpeg, path, at)
                if not frames:
                    continue
                first = np.asarray(frames[0].convert("L"), dtype=np.float32) / 255
                last = np.asarray(frames[-1].convert("L"), dtype=np.float32) / 255
                motion = float(np.abs(first - last).mean()) if len(frames) > 1 else 0.0
                samples.append({"at": at, "thumb": frames[0], "motion": motion, **measure(frames[0])})
            if not samples:
                emit({"event": "note", "message": f"Skipped {os.path.basename(path)}: no frames"})
                continue
            items.append({"kind": "video", "path": path, "samples": samples, "step": step, **info,
                          "hash": samples[len(samples) // 2]["hash"]})
        except Exception as error:  # one broken file must not cost the video
            emit({"event": "note", "message": f"Skipped {os.path.basename(path)}: {error}"})
    stage("media", 1)
    return items


def rank_by_motif(items: list[dict], motif: str, model_name: str, pretrained: str) -> bool:
    """
    Gives every photo and video sample `similarity` to the motif with CLIP. The model (a multilingual one by default,
    so a German motif works) is loaded only here and leaves with the process.
    """
    try:
        import open_clip
        import torch
    except ImportError as error:
        emit({"event": "note", "message": f"No motif ranking, CLIP is missing: {error}"})
        return False
    device = "mps" if torch.backends.mps.is_available() else "cpu"
    try:
        model, _, preprocess = open_clip.create_model_and_transforms(model_name, pretrained=pretrained, device=device)
        tokenizer = open_clip.get_tokenizer(model_name)
    except Exception as error:  # mostly a download that failed; the video is still worth making without the motif
        emit({"event": "note", "message": f"No motif ranking, CLIP could not be loaded: {error}"})
        return False
    model.eval()
    targets = [item for item in items if item["kind"] == "photo"] + [s for item in items if item["kind"] == "video" for s in item["samples"]]
    with torch.no_grad():
        text = model.encode_text(tokenizer([motif, f"a photo of {motif}"]).to(device))
        text = text / text.norm(dim=-1, keepdim=True)
        text = text.mean(dim=0, keepdim=True)
        text = text / text.norm(dim=-1, keepdim=True)
        for start in range(0, len(targets), 32):
            stage("motif", start / max(len(targets), 1))
            batch = torch.stack([preprocess(t["thumb"]) for t in targets[start:start + 32]]).to(device)
            images = model.encode_image(batch)
            images = images / images.norm(dim=-1, keepdim=True)
            for target, embedding, similarity in zip(targets[start:start + 32], images.cpu().numpy(), (images @ text.T).squeeze(1).cpu().tolist()):
                target["similarity"] = similarity
                target["embedding"] = embedding
    stage("motif", 1)
    return True


def percentiles(values: list[float]) -> list[float]:
    """Each value's rank among all as 0 to 1, so sharpness and similarity weigh alike whatever their scale."""
    order = sorted(range(len(values)), key=lambda i: values[i])
    ranks = [0.0] * len(values)
    for rank, index in enumerate(order):
        ranks[index] = rank / max(len(values) - 1, 1)
    return ranks


def score(items: list[dict], ranked: bool) -> None:
    """
    How much each photo and video sample is worth showing: with a motif mostly how well it fits, otherwise how sharp
    and well exposed it is. Black frames and blurred shots sink to the bottom either way.
    """
    targets = [item for item in items if item["kind"] == "photo"] + [s for item in items if item["kind"] == "video" for s in item["samples"]]
    if not targets:
        return
    sharp = percentiles([t["sharpness"] for t in targets])
    fit = percentiles([t.get("similarity", 0.0) for t in targets]) if ranked else None
    for index, target in enumerate(targets):
        exposure = 1 - min(1.0, abs(target["brightness"] - 0.45) / 0.45)
        quality = 0.6 * sharp[index] + 0.4 * exposure
        target["score"] = (0.7 * fit[index] + 0.3 * quality) if fit else quality
        if target["brightness"] < 0.05 or target["brightness"] > 0.97:
            target["score"] -= 1
    for item in items:
        if item["kind"] == "video":
            item["score"] = max(s["score"] for s in item["samples"])


def drop_duplicates(items: list[dict]) -> list[dict]:
    """Bursts and the same scene shot twice: of near-identical pictures the better one stays."""
    kept = []
    for item in sorted(items, key=lambda i: -i["score"]):
        twin = False
        for other in kept:
            if "embedding" in item and "embedding" in other:
                twin = float(item["embedding"] @ other["embedding"]) > 0.96
            else:
                twin = bin(item["hash"] ^ other["hash"]).count("1") <= 4
            if twin:
                break
        if not twin:
            kept.append(item)
    return kept


# --- The plan --------------------------------------------------------------------------------------------------


def units_of(items: list[dict], wanted: int) -> list[dict]:
    """
    What can fill a shot: each photo once, and a video once per good stretch (at most one per four seconds of it,
    never two overlapping), best first.
    """
    units = []
    for item in items:
        if item["kind"] == "photo":
            units.append({"item": item, "score": item["score"], "date": item["date"]})
            continue
        room = max(1, int(item["duration"] / 4))
        taken = []
        for sample in sorted(item["samples"], key=lambda s: -s["score"]):
            if len(taken) >= room:
                break
            if all(abs(sample["at"] - other) >= 3 for other in taken):
                taken.append(sample["at"])
                units.append({"item": item, "score": sample["score"], "date": item["date"] + sample["at"],
                              "at": sample["at"], "motion": sample["motion"]})
    units.sort(key=lambda u: -u["score"])
    # Clearly bad pictures only fill in when nothing else is left.
    good = [u for u in units if u["score"] > -0.5]
    return good[:wanted] if len(good) >= min(wanted, len(units)) else units[:wanted]


def fit_of(width: int, height: int, frame_width: int, frame_height: int) -> str:
    ratio = (width / height) / (frame_width / frame_height)
    return "blur" if max(ratio, 1 / ratio) > FIT_LIMIT else "cover"


def still(image, fit: str, width: int, height: int, path: str) -> None:
    """
    The photo at twice the frame's size, filled or whole over its blurred self, so the zoom has pixels to spare and
    ffmpeg needs to read nothing but a JPEG (HEIC and EXIF turns are dealt with here).
    """
    from PIL import Image, ImageEnhance, ImageFilter, ImageOps

    size = (width * 2, height * 2)
    if fit == "cover":
        canvas = ImageOps.fit(image, size, Image.Resampling.LANCZOS)
    else:
        background = ImageOps.fit(image, (max(width // 8, 1), max(height // 8, 1)), Image.Resampling.BILINEAR)
        background = background.filter(ImageFilter.GaussianBlur(4)).resize(size, Image.Resampling.BICUBIC)
        canvas = ImageEnhance.Brightness(background).enhance(0.55)
        inner = ImageOps.contain(image, (int(size[0] * 0.94), int(size[1] * 0.94)), Image.Resampling.LANCZOS)
        canvas.paste(inner, ((size[0] - inner.width) // 2, (size[1] - inner.height) // 2))
    canvas.save(path, "JPEG", quality=92)


def plan(request: dict, music: dict, units: list[dict]) -> list[dict]:
    shots = cut(music)
    width, height = request["width"], request["height"]
    rng = random.Random(request.get("seed", 0))
    # Chosen by how good they are, shown in the order they were taken: the video tells the day as it went. With
    # fewer pictures than shots they come round again, each time with another zoom.
    order = sorted(units, key=lambda u: u["date"])
    planned, stills = [], {}
    for number, (start, end, level) in enumerate(shots):
        stage("plan", number / max(len(shots), 1))
        if not order:
            break
        unit = order[number % len(order)]
        item, duration = unit["item"], end - start
        if item["kind"] == "video":
            # Centred on the good sample, inside the clip; a clip shorter than its shot holds its last frame.
            begin = max(0.0, min(unit["at"] - duration / 2, item["duration"] - duration))
            planned.append({"start": round(start, 4), "end": round(end, 4), "kind": "video", "file": item["path"],
                            "from": round(begin, 3), "fit": fit_of(item["width"], item["height"], width, height),
                            "hdr": item["hdr"], "energy": round(level, 3)})
            continue
        key = item["path"]
        if key not in stills:
            name = f"still-{len(stills) + 1:04d}.jpg"
            image, _ = open_photo(key)
            still(image, fit_of(item["width"], item["height"], width, height), width, height, os.path.join(request["work"], name))
            stills[key] = name
        # A slow zoom (Ken Burns) in or out around a point near the middle; a little more where the music is loud.
        depth = min(0.18, 0.04 + 0.025 * duration + 0.04 * level)
        zoom = [1.0, 1.0 + depth] if rng.random() < 0.6 else [1.0 + depth, 1.0]
        focus = [round(rng.uniform(0.35, 0.65), 3) for _ in range(4)]
        planned.append({"start": round(start, 4), "end": round(end, 4), "kind": "photo", "still": stills[key],
                        "zoom": [round(z, 4) for z in zoom], "focus": focus, "energy": round(level, 3)})
    stage("plan", 1)
    return planned


def main() -> int:
    request = json.loads(sys.argv[1])
    os.makedirs(request["work"], exist_ok=True)
    started = time.perf_counter()
    try:
        # Imported here so that a missing package shows up as the video's error rather than before the first event.
        import librosa  # noqa: F401
        import numpy  # noqa: F401
        from PIL import Image  # noqa: F401
    except ImportError as error:
        print(f"Error: {error}. Run deploy/install-montage.sh.", file=sys.stderr, flush=True)
        return 2
    try:
        import pillow_heif

        pillow_heif.register_heif_opener()
    except ImportError:
        emit({"event": "note", "message": "pillow-heif is missing, HEIC photos are skipped"})

    stage("music", 0)
    music = analyse_music(request["audio"])
    stage("music", 1)
    items = scan(request["folder"], request["ffmpeg"], request["ffprobe"])
    if not items:
        print("Error: the folder has no photos or videos Tonwerk can read.", file=sys.stderr, flush=True)
        return 3
    ranked = False
    motif = (request.get("motif") or "").strip()
    if motif:
        ranked = rank_by_motif(items, motif, request["clipModel"], request["clipPretrained"])
    score(items, ranked)
    items = drop_duplicates(items)
    shots = cut(music)
    units = units_of(items, len(shots))
    planned = plan(request, music, units)
    with open(os.path.join(request["work"], "plan.json"), "w") as file:
        json.dump({"seconds": music["seconds"], "tempo": music["tempo"], "shots": planned}, file, indent=1)
    emit({"event": "done", "shots": len(planned), "photos": sum(1 for i in items if i["kind"] == "photo"),
          "videos": sum(1 for i in items if i["kind"] == "video"),
          "used": len({u["item"]["path"] for u in units}), "tempo": round(music["tempo"], 1), "motif": ranked,
          "seconds": round(time.perf_counter() - started, 1)})
    return 0


if __name__ == "__main__":
    sys.exit(main())
