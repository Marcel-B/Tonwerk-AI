"""Tonwerk's lyrics timing: when each word of a song is sung, heard by Whisper through mlx-whisper.

    python yueui_whisper.py '<json>'

The JSON names `audio` (a file ffmpeg can read: the song's FLAC or its vocal stem), `model` (a Hugging Face repo
mlx-whisper loads, by default the large-v3 turbo model), `language` (`de`, `en`, or null to let Whisper guess) and
`prompt` (the start of the lyrics, so Whisper expects their words and spelling). Tonwerk knows the lyrics already
and only asks when; it lays them onto the words this prints (Timing/LyricsAligner.cs).

Answers `YUEUI {json}` lines: `stage` while loading and listening, then `done` with `words` (each `word`, `start`,
`end` in seconds) and `seconds` (how long it took). mlx-whisper's own output goes around them.
"""

import json
import sys
import time


def emit(**event):
    print("YUEUI " + json.dumps(event, ensure_ascii=False), flush=True)


def main():
    request = json.loads(sys.argv[1])
    began = time.time()
    emit(event="stage", stage="loading")
    import mlx_whisper

    emit(event="stage", stage="listening")
    result = mlx_whisper.transcribe(
        request["audio"],
        path_or_hf_repo=request["model"],
        language=request.get("language"),
        initial_prompt=request.get("prompt") or None,
        word_timestamps=True,
        # Singing holds notes and repeats lines; without these Whisper loops on a chorus or drops a quiet verse.
        condition_on_previous_text=False,
        no_speech_threshold=None,
        hallucination_silence_threshold=2.0,
        verbose=None,
    )
    words = [
        {"word": word["word"].strip(), "start": round(float(word["start"]), 3), "end": round(float(word["end"]), 3)}
        for segment in result.get("segments", [])
        for word in segment.get("words", [])
        if word.get("word", "").strip()
    ]
    emit(event="done", words=words, seconds=round(time.time() - began, 1))


if __name__ == "__main__":
    try:
        main()
    except Exception as error:  # the server shows the message as the reason
        emit(event="error", message=f"{type(error).__name__}: {error}")
        sys.exit(1)
