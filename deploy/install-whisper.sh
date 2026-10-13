#!/bin/sh
# Sets up lyrics timing (when each line of a song's lyrics is sung, for the full-screen player and video subtitles):
# a Python environment of its own with mlx-whisper, where Tonwerk looks for it (Timing:Python). The Whisper model
# (large-v3 turbo, about 1.6 GB) is downloaded on the first song into the environment's models folder.
#
#   deploy/install-whisper.sh    install mlx-whisper, or move it to the version pinned here
#
# Run it on the Mac as the user Tonwerk runs as, after deploy/install.sh.
set -eu

URL="${YUEUI_URL:-http://127.0.0.1:5090}"
PACKAGE="mlx-whisper==0.4.3"

# The server says where it looks for the environment; without a running server, its default.
PYTHON="$(curl -fsS "$URL/api/timing" 2>/dev/null | /usr/bin/python3 -c 'import json, sys; print(json.load(sys.stdin)["python"])' 2>/dev/null || true)"
if [ -z "$PYTHON" ]; then
  PYTHON="$HOME/Library/Application Support/YuE UI/whisper/env/bin/python"
  echo "Tonwerk does not answer at $URL; using the default location."
fi
ENV="$(dirname "$(dirname "$PYTHON")")"
echo "Environment: $ENV"

# MLX needs Apple Silicon and Python 3.10 or later; macOS's own python3 is 3.9. uv brings one along.
if command -v uv >/dev/null 2>&1; then
  [ -x "$PYTHON" ] || uv venv --python 3.11 "$ENV"
  uv pip install --python "$PYTHON" "$PACKAGE"
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
  "$PYTHON" -m pip install "$PACKAGE"
fi
"$PYTHON" -W ignore -c 'import importlib.metadata as m; import mlx_whisper; print("mlx-whisper", m.version("mlx-whisper"), "is installed.")'
command -v ffmpeg >/dev/null 2>&1 || [ -x /opt/homebrew/bin/ffmpeg ] || echo "Warning: mlx-whisper reads audio through ffmpeg, which is missing (brew install ffmpeg)."
echo "Tonwerk times the lyrics of every new song; the full-screen player asks for older ones."
