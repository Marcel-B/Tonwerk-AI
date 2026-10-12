#!/bin/sh
# Sets up photo videos (a folder of photos and clips cut to a song's beats): a Python environment of its own with
# librosa for the beats, Pillow with pillow-heif for the iPhone's HEIC photos, and PyTorch with open_clip to pick the
# pictures that fit a motif, where Tonwerk looks for it (Montage:Python). The CLIP model (about 1.5 GB) is downloaded
# into Montage:ModelCache the first time a video asks for a motif; without a motif nothing is downloaded.
#
#   deploy/install-montage.sh    install them, or move them to the versions pinned here
#
# Run it on the Mac as the user Tonwerk runs as, after deploy/install.sh.
set -eu

URL="${YUEUI_URL:-http://127.0.0.1:5090}"
# As tested together in a Linux container (librosa's beats, the shots); the CLIP step with random weights only,
# since Hugging Face is out of the container's reach.
REQUIREMENTS="librosa==0.11.0 numpy==2.4.6 scipy==1.18.1 scikit-learn==1.9.1 numba==0.68.0 soundfile==0.14.0 pillow==12.3.0 pillow-heif==1.8.1 torch==2.14.1 torchvision==0.29.1 open_clip_torch==3.3.0 timm==1.0.30"

# The server names the folder the dialog offers; the environment goes to the default unless Montage:Python says else.
PYTHON="${YUEUI_MONTAGE_PYTHON:-$HOME/Library/Application Support/YuE UI/montage/env/bin/python}"
ENV="$(dirname "$(dirname "$PYTHON")")"
echo "Environment: $ENV"

# Tested with Python 3.12; macOS's own python3 is 3.9, which numpy 2 no longer supports. uv brings one along.
if command -v uv >/dev/null 2>&1; then
  [ -x "$PYTHON" ] || uv venv --python 3.12 "$ENV"
  # shellcheck disable=SC2086 # one word per requirement
  uv pip install --python "$PYTHON" $REQUIREMENTS
else
  BASE=""
  for candidate in python3.12 python3.11 /opt/homebrew/bin/python3.12 /opt/homebrew/bin/python3.11; do
    if command -v "$candidate" >/dev/null 2>&1; then
      BASE="$(command -v "$candidate")"
      break
    fi
  done
  if [ -z "$BASE" ]; then
    echo "No Python 3.11 or 3.12 found. Install uv (brew install uv) and run this again." >&2
    exit 1
  fi
  [ -x "$PYTHON" ] || "$BASE" -m venv "$ENV"
  "$PYTHON" -m pip install --quiet --upgrade pip
  # shellcheck disable=SC2086
  "$PYTHON" -m pip install $REQUIREMENTS
fi
"$PYTHON" -W ignore -c 'import librosa, open_clip, pillow_heif, torch; print("librosa", librosa.__version__, "and open_clip", open_clip.__version__, "are installed; PyTorch on", "MPS" if torch.backends.mps.is_available() else "the CPU")'

FOLDER="$(curl -fsS "$URL/api/videos/media" 2>/dev/null | /usr/bin/python3 -c 'import json, sys; print(json.load(sys.stdin)["root"])' 2>/dev/null || true)"
FOLDER="${FOLDER:-$HOME/Pictures/Tonwerk}"
mkdir -p "$FOLDER"
echo "Put a folder of photos and clips per video into $FOLDER; the music video dialog offers them under \"Fotos & Videos\"."
