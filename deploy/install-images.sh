#!/bin/sh
# Sets up the covers painted by FLUX.2 Klein: a Python environment of its own with mflux, where Tonwerk looks for it
# (Images:Python). The models are downloaded by their first picture, into Images:ModelCache next to it (Klein 4B
# about 15 GB, Klein 9B about 32 GB).
#
#   deploy/install-images.sh    install mflux, or move it to the version pinned here
#
# Klein 9B is gated behind its non-commercial licence: accept it on huggingface.co/black-forest-labs/FLUX.2-klein-9B,
# make a read token for that account and put it into ~/.config/tonwerk/huggingface.token (Images:TokenFile).
# Klein 4B (Apache 2.0) needs neither.
#
# Run it on the Mac as the user Tonwerk runs as, after deploy/install.sh.
set -eu

URL="${YUEUI_URL:-http://127.0.0.1:5090}"
# Pinned, so an update arrives through a commit rather than whenever the Mac happens to install; 0.18 brought Klein.
PACKAGE="mflux==0.21.0"
# Pillow 13 imports numbers._IntegralLike at run time, which no released Python has (seen 2026-10-21 on Python 3.12),
# so mflux failed on its first import; held below 13 until Pillow fixes it.
PILLOW="pillow<13"

# The server says where it looks for the environment; without a running server, its default.
PYTHON="$(curl -fsS "$URL/api/images" 2>/dev/null | /usr/bin/python3 -c 'import json, sys; print(json.load(sys.stdin)["python"])' 2>/dev/null || true)"
if [ -z "$PYTHON" ]; then
  PYTHON="$HOME/Library/Application Support/YuE UI/images/env/bin/python"
  echo "Tonwerk does not answer at $URL; using the default location."
fi
ENV="$(dirname "$(dirname "$PYTHON")")"
echo "Environment: $ENV"

# mflux needs Python 3.10 or newer; macOS's own python3 is 3.9. uv brings a suitable one along.
if command -v uv >/dev/null 2>&1; then
  [ -x "$PYTHON" ] || uv venv --python 3.12 "$ENV"
  uv pip install --python "$PYTHON" "$PACKAGE" "$PILLOW"
else
  BASE=""
  for candidate in python3.13 python3.12 python3.11 python3.10 /opt/homebrew/bin/python3 python3; do
    if command -v "$candidate" >/dev/null 2>&1 && "$candidate" -c 'import sys; sys.exit(sys.version_info < (3, 10))' 2>/dev/null; then
      BASE="$(command -v "$candidate")"
      break
    fi
  done
  if [ -z "$BASE" ]; then
    echo "No Python 3.10 or newer found. Install one (brew install python@3.12, or uv) and run this again." >&2
    exit 1
  fi
  [ -x "$PYTHON" ] || "$BASE" -m venv "$ENV"
  "$PYTHON" -m pip install --quiet --upgrade pip
  "$PYTHON" -m pip install "$PACKAGE" "$PILLOW"
fi
"$PYTHON" -c 'import importlib.metadata as m; from mflux.models.flux2.variants import Flux2Klein; print("mflux", m.version("mflux"), "is installed.")'
echo "Open a song's cover in Tonwerk and paint one; the model downloads itself with the first picture."
