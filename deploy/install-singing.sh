#!/bin/sh
# Sets up the singing page (notes and words in, a sung voice out): SoulX-Singer (github.com/Soul-AILab/SoulX-Singer,
# Apache 2.0) as a git checkout at the commit pinned here, a Python environment of its own with PyTorch and the
# packages that commit was tested with, its weights from huggingface.co/Soul-AILab/SoulX-Singer (model.pt, about
# 2.8 GB) and CMUdict for g2p_en, where Tonwerk looks for them (Singing:Python, Source, Model, NltkData).
#
#   deploy/install-singing.sh    install, or move to the commit and versions pinned here
#
# Run it on the Mac as the user Tonwerk runs as, after deploy/install.sh. Only score control is used: the melody and
# words come from Tonwerk, the voice from SoulX-Singer's example prompts, so none of its preprocessing models
# (transcription, separation, pitch extraction) is downloaded.
set -eu

URL="${YUEUI_URL:-http://127.0.0.1:5090}"
REPOSITORY=https://github.com/Soul-AILab/SoulX-Singer.git
REF=81aeb3ae772c70093c3de74dc23c92d983801ae4
WEIGHTS=https://huggingface.co/Soul-AILab/SoulX-Singer/resolve/main/model.pt
# transformers 4.41 is what SoulX-Singer's decoder layers were written against; later ones changed their arguments.
# numpy below 2 for librosa's and PyTorch 2.5's wheels. Not scipy 1.15: eight of its macOS arm64 extensions
# (PROPACK, ODE solvers, COBYLA; Fortran linked without an SDK version) have a __thread_bss with a file offset, which
# macOS 27's loader refuses ("zero-fill section type, but offset field is not zero"); 1.14 and 1.16 have none.
PACKAGES="torch==2.5.1 torchaudio==2.5.1 transformers==4.41.2 accelerate==1.11.0 omegaconf==2.3.0 einops==0.8.2
librosa==0.11.0 scipy==1.16.3 numpy<2 soundfile==0.13.1 g2p_en==2.1.0 nltk==3.9.2 tqdm==4.70.1"

# The server says where it looks; without a running server, its defaults.
INFO="$(curl -fsS "$URL/api/singing" 2>/dev/null || true)"
field() { printf '%s' "$INFO" | /usr/bin/python3 -c "import json, sys; print(json.load(sys.stdin)['$1'])" 2>/dev/null || true; }
PYTHON="$(field python)"
SOURCE="$(field source)"
MODEL="$(field model)"
NLTK="$(field nltkData)"
if [ -z "$PYTHON" ] || [ -z "$SOURCE" ] || [ -z "$MODEL" ] || [ -z "$NLTK" ]; then
  echo "Tonwerk does not answer at $URL; using the default locations."
  ROOT="$HOME/Library/Application Support/YuE UI/singing"
  PYTHON="$ROOT/env/bin/python"
  SOURCE="$ROOT/src"
  MODEL="$ROOT/models/SoulX-Singer/model.pt"
  NLTK="$ROOT/nltk_data"
fi
ENV="$(dirname "$(dirname "$PYTHON")")"
echo "Environment: $ENV"
echo "SoulX-Singer: $SOURCE"
echo "Weights: $MODEL"

if [ ! -d "$SOURCE/.git" ]; then
  mkdir -p "$(dirname "$SOURCE")"
  git clone --quiet "$REPOSITORY" "$SOURCE"
fi
git -C "$SOURCE" fetch --quiet origin
git -C "$SOURCE" checkout --quiet "$REF"
git -C "$SOURCE" reset --quiet --hard

# PyTorch 2.5 has wheels up to Python 3.12; macOS's own python3 is 3.9. uv brings one along.
# shellcheck disable=SC2086 # one word per package
if command -v uv >/dev/null 2>&1; then
  [ -x "$PYTHON" ] || uv venv --python 3.11 "$ENV"
  uv pip install --python "$PYTHON" $PACKAGES
else
  BASE=""
  for candidate in python3.11 python3.12 /opt/homebrew/bin/python3.11 /opt/homebrew/bin/python3.12; do
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
  "$PYTHON" -m pip install $PACKAGES
fi

# g2p_en looks for both at import and would otherwise download them into the home folder on the first take.
"$PYTHON" -W ignore -m nltk.downloader --quiet -d "$NLTK" cmudict averaged_perceptron_tagger

if [ -s "$MODEL" ]; then
  echo "The weights are there."
else
  mkdir -p "$(dirname "$MODEL")"
  echo "Downloading the weights (about 2.8 GB)"
  # Into a part file first, so a cut-off download is never taken for the model.
  curl -fL --retry 3 -C - -o "$MODEL.part" "$WEIGHTS"
  mv "$MODEL.part" "$MODEL"
fi

NLTK_DATA="$NLTK" PYTHONPATH="$SOURCE" "$PYTHON" -W ignore -c '
import importlib.metadata as m, torch, g2p_en, soulxsinger
print("SoulX-Singer is installed: torch", m.version("torch"), "with", "Metal" if torch.backends.mps.is_available() else "the CPU only")'
echo "Open the singing page in Tonwerk-AI (Werkzeuge → Singen)."
