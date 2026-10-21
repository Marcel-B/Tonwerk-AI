#!/bin/sh
# Sets up the covers painted by local image models: two Python environments where Tonwerk looks for them, one with
# mflux (FLUX.2 Klein 4B/9B, Z-Image Turbo, Krea 2 Turbo; Images:Python) and one with flux3-mlx (FLUX 3 Image Klein
# with its prompt upsampler, which mflux lacks; Images:Flux3Python). The models are downloaded by their first picture,
# into Images:ModelCache next to them (Klein 4B about 15 GB, Klein 9B 32, Z-Image Turbo 21, Krea 2 Turbo 35, FLUX 3
# Klein 15).
#
#   deploy/install-images.sh    install both, or move them to the versions pinned here
#
# Klein 9B and Krea 2 are gated behind their licences: accept them on huggingface.co/black-forest-labs/FLUX.2-klein-9B
# and huggingface.co/krea/Krea-2-Turbo, make a read token for that account and put it into
# ~/.config/tonwerk/huggingface.token (Images:TokenFile). The others need neither. Krea 2 and FLUX 3 Klein are offered
# only on a Mac with 32 GB or more; installing both environments on a smaller one does no harm.
#
# Run it on the Mac as the user Tonwerk runs as, after deploy/install.sh.
set -eu

URL="${YUEUI_URL:-http://127.0.0.1:5090}"
# Pinned, so an update arrives through a commit rather than whenever the Mac happens to install; 0.18 brought Klein,
# 0.23 Krea 2 (which loads the gated repository with the Hugging Face token, unlike krea2_alis_mlx's own downloader).
PACKAGE="mflux==0.23.0"
FLUX3="flux3-mlx[pup]==0.1.0"
# Pillow 13 imports numbers._IntegralLike at run time, which no released Python has (seen 2026-10-21 on Python 3.12),
# so mflux failed on its first import; held below 13 until Pillow fixes it.
PILLOW="pillow<13"

# The server says where it looks for the environments; without a running server, its defaults.
INFO="$(curl -fsS "$URL/api/images" 2>/dev/null || true)"
PYTHON="$(printf '%s' "$INFO" | /usr/bin/python3 -c 'import json, sys; print(json.load(sys.stdin)["python"])' 2>/dev/null || true)"
FLUX3_PYTHON="$(printf '%s' "$INFO" | /usr/bin/python3 -c 'import json, sys; print(json.load(sys.stdin).get("flux3Python") or "")' 2>/dev/null || true)"
if [ -z "$PYTHON" ]; then
  PYTHON="$HOME/Library/Application Support/YuE UI/images/env/bin/python"
  echo "Tonwerk does not answer at $URL; using the default location."
fi
if [ -z "$FLUX3_PYTHON" ]; then
  # A server from before FLUX 3 does not name it; its place beside mflux's environment.
  FLUX3_PYTHON="$(dirname "$(dirname "$(dirname "$PYTHON")")")/flux3/bin/python"
fi

# Both need Python 3.10 or newer; macOS's own python3 is 3.9. uv brings a suitable one along.
BASE=""
if ! command -v uv >/dev/null 2>&1; then
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
fi

# install <python> <packages…>: makes the environment if it is not there and installs into it.
install() {
  python="$1"
  shift
  env="$(dirname "$(dirname "$python")")"
  echo "Environment: $env"
  if [ -z "$BASE" ]; then
    [ -x "$python" ] || uv venv --python 3.12 "$env"
    uv pip install --python "$python" "$@"
  else
    [ -x "$python" ] || "$BASE" -m venv "$env"
    "$python" -m pip install --quiet --upgrade pip
    "$python" -m pip install "$@"
  fi
}

install "$PYTHON" "$PACKAGE" "$PILLOW"
"$PYTHON" -c 'import importlib.metadata as m; from mflux.models.flux2.variants import Flux2Klein; from mflux.models.krea2 import Krea2; from mflux.models.z_image import ZImage; print("mflux", m.version("mflux"), "is installed.")'
install "$FLUX3_PYTHON" "$FLUX3" "$PILLOW"
"$FLUX3_PYTHON" -c 'import importlib.metadata as m; from flux3_mlx import Flux3Klein; import mlx_vlm; print("flux3-mlx", m.version("flux3-mlx"), "is installed.")'
echo "Open a song's cover in Tonwerk and paint one; each model downloads itself with its first picture."
