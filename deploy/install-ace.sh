#!/bin/sh
# Sets up ACE-Step 1.5, the second song model: acestep.cpp (GGML, Metal on the Mac) built from a pinned commit, and
# its GGUF models from huggingface.co/Serveurperso/ACE-Step-1.5-GGUF, where Tonwerk looks for them (Ace:Binaries,
# Ace:Models). Tonwerk runs ace-lm and ace-synth once per song, so the models leave the memory with the process.
#
#   deploy/install-ace.sh            build acestep.cpp, download the default set (about 7.7 GB)
#   deploy/install-ace.sh --sft      plus the SFT DiT (finer, about 50 steps instead of 8)
#   deploy/install-ace.sh --small    the 1.7B LM instead of the 4B one (less memory, weaker lyrics)
#
# The default set: the 4B LM (writes lyrics, picks tempo and key, composes the 5 Hz codes), the turbo DiT, the text
# encoder and the VAE, all Q8_0 but the VAE. Models are MIT licensed. Running it again skips what is there; a new
# ACE_REF or patch rebuilds. Run it on the Mac as the user Tonwerk runs as, after deploy/install.sh.
set -eu

URL="${YUEUI_URL:-http://127.0.0.1:5090}"
# Pinned, so an update arrives through a commit rather than whenever the Mac happens to install.
ACE_REF=6013134c136d9fb4e970cf690e76925be9553da0
REPOSITORY=https://github.com/ServeurpersoCom/acestep.cpp.git
# Applied after the checkout: ace-lm wrote an empty request file when the LM's text held invalid UTF-8, and ace-synth
# then failed on it ("cannot read request0.json"). The stamp next to the binaries names commit and patch, so a changed
# patch rebuilds too.
PATCH="$(cd "$(dirname "$0")" && pwd)/ace/request-utf8.patch"
STAMP="$ACE_REF $(cksum < "$PATCH")"
MODELS_URL=https://huggingface.co/Serveurperso/ACE-Step-1.5-GGUF/resolve/main

LM=acestep-5Hz-lm-4B-Q8_0.gguf
EXTRA=""
for argument in "$@"; do
  case "$argument" in
    --sft) EXTRA="$EXTRA acestep-v15-sft-Q8_0.gguf" ;;
    --small) LM=acestep-5Hz-lm-1.7B-Q8_0.gguf ;;
    -h|--help) sed -n '2,13p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "Unknown argument: $argument" >&2; exit 2 ;;
  esac
done

# The server says where it looks; without a running server, its defaults.
INFO="$(curl -fsS "$URL/api/ace" 2>/dev/null || true)"
field() { printf '%s' "$INFO" | /usr/bin/python3 -c "import json, sys; print(json.load(sys.stdin)['$1'])" 2>/dev/null || true; }
BINARIES="$(field binaries)"
MODELS="$(field models)"
ROOT="$HOME/Library/Application Support/YuE UI/ace"
if [ -z "$BINARIES" ] || [ -z "$MODELS" ]; then
  echo "Tonwerk does not answer at $URL; using the default locations."
  BINARIES="$ROOT/acestep.cpp/build"
  MODELS="$ROOT/models"
fi
SOURCE="$(dirname "$BINARIES")"
echo "acestep.cpp: $SOURCE"
echo "Models: $MODELS"

command -v cmake >/dev/null 2>&1 || brew install cmake

if [ "$(cat "$BINARIES/tonwerk-stamp" 2>/dev/null || true)" != "$STAMP" ] || [ ! -x "$BINARIES/ace-synth" ]; then
  if [ ! -d "$SOURCE/.git" ]; then
    mkdir -p "$(dirname "$SOURCE")"
    git clone "$REPOSITORY" "$SOURCE"
  fi
  git -C "$SOURCE" fetch --quiet origin
  git -C "$SOURCE" checkout --quiet "$ACE_REF"
  git -C "$SOURCE" reset --quiet --hard
  git -C "$SOURCE" submodule update --init --recursive --quiet
  git -C "$SOURCE" apply "$PATCH"
  # Metal comes on by itself on Apple Silicon.
  cmake -S "$SOURCE" -B "$BINARIES" -DCMAKE_BUILD_TYPE=Release
  cmake --build "$BINARIES" --config Release -j "$(sysctl -n hw.ncpu)"
  echo "$STAMP" > "$BINARIES/tonwerk-stamp"
fi
for tool in ace-lm ace-synth; do
  [ -x "$BINARIES/$tool" ] || { echo "$tool was not built in $BINARIES." >&2; exit 1; }
done

mkdir -p "$MODELS" "$(dirname "$MODELS")/adapters"
for file in Qwen3-Embedding-0.6B-Q8_0.gguf vae-BF16.gguf acestep-v15-turbo-Q8_0.gguf "$LM" $EXTRA; do
  if [ -s "$MODELS/$file" ]; then
    echo "$file is there."
    continue
  fi
  echo "Downloading $file"
  # Into a part file first, so a cut-off download is never taken for a model.
  curl -fL --retry 3 -C - -o "$MODELS/$file.part" "$MODELS_URL/$file"
  mv "$MODELS/$file.part" "$MODELS/$file"
done

echo "ACE-Step is ready; open its page in Tonwerk (Werkzeuge → ACE-Step)."
