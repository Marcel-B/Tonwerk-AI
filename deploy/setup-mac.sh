#!/bin/sh
# Sets Tonwerk up on a Mac that has nothing but macOS and the Command Line Tools (for git): Homebrew, .NET 10,
# Node.js, ffmpeg and uv, then YuE2 with its Python environment and models, without the YuE Studio app, and
# Tonwerk itself as a LaunchAgent (deploy/install.sh).
#
#   deploy/setup-mac.sh                    everything Tonwerk needs to make songs
#   deploy/setup-mac.sh --transcription    plus SheetSage2 for transcriptions (about 2 GB more)
#   deploy/setup-mac.sh --voices           plus stem separation and Seed-VC for other voices (about 3 GB more)
#   deploy/setup-mac.sh --speech           plus the speech lab's environment (deploy/install-speech.sh)
#   deploy/setup-mac.sh --images           plus FLUX.2 Klein for painted covers (deploy/install-images.sh)
#   deploy/setup-mac.sh --midi             plus Basic Pitch for audio to MIDI (deploy/install-midi.sh)
#   deploy/setup-mac.sh --whisper          plus Whisper for lyrics timing (deploy/install-whisper.sh)
#   deploy/setup-mac.sh --montage          plus photo videos cut to the beat (deploy/install-montage.sh)
#   deploy/setup-mac.sh --ace              plus ACE-Step, the second song model (deploy/install-ace.sh, about 8 GB)
#   deploy/setup-mac.sh --all              all eight
#   deploy/setup-mac.sh --engine-from-source
#                                          replace an engine the YuE Studio app installed by a git checkout
#   deploy/setup-mac.sh --update           again with the options of the last run, without asking (deploy/update.sh)
#   deploy/setup-mac.sh --no-auto-update   without turning on deploy/update.sh
#
# YuE2 comes from YuE Studio's repository (github.com/tonywestonuk/YuE-Studio), not from upstream YuE2: upstream
# runs on CUDA only, YuE Studio's fork adds the Apple Silicon engines (Neural Engine, MLX) and the worker
# (tools/yue2_worker.py) Tonwerk talks to. The steps are the ones the app's own installer takes (Installer.swift,
# SheetSageInstaller.swift), into the same folders, so Tonwerk's defaults (Yue:InstallRoot) need no change and the
# app, installed later, finds the models. The source is a git checkout at ENGINE_REF instead of the app's copy.
#
# Every step is skipped when it is already done, so running it again is safe; with a new ENGINE_REF it moves the
# engine to that version. Run it as the user Tonwerk will run as, logged in (Metal and the Neural Engine need the
# user's session), from a terminal: Homebrew and the .NET installer ask for the password. At the end it turns on
# deploy/update.sh, which deploys new commits on main and runs this again with --update, so the versions pinned here
# (ENGINE_REF, SEED_VC_REF, SEPARATOR_PACKAGE) reach the Mac with the commit that raises them.
set -eu
cd "$(dirname "$0")/.."
REPO="$(pwd)"

# The YuE Studio version whose worker yueui_worker.py's SEAMS were checked against. Raise it only after checking
# them against the new worker; the last step of this script reports whether they still fit.
ENGINE_REPO="https://github.com/tonywestonuk/YuE-Studio.git"
ENGINE_REF="${ENGINE_REF:-v0.4.0}"
ROOT="$HOME/Library/Application Support/YuE Studio"
OUTPUT="$HOME/Music/YuE Studio"
SHEETSAGE_RECIPE=1   # SheetSageInstaller.recipe: the app reinstalls when its recipe differs from the marker's
# Stems and voices: Tonwerk runs mlx-audio-separator and Seed-VC itself (Voice:EngineRoot, default below).
ENGINES="$HOME/Library/Application Support/YuE UI/engines"
SEED_VC_REPO="https://github.com/Plachtaa/seed-vc.git"
SEED_VC_REF="${SEED_VC_REF:-51383efd921027683c89e5348211d93ff12ac2a8}"
SEED_VC_RECIPE="$SEED_VC_REF 1"   # raise the number when the packages below change
# Pinned, so an update arrives through a commit (and its tests) rather than whenever the Mac happens to install.
SEPARATOR_PACKAGE="mlx-audio-separator[convert]==0.1.19"
DATA="$HOME/Library/Application Support/YuE UI"
# The options of the last run, which --update repeats.
OPTIONS_FILE="$DATA/setup-options"

TRANSCRIPTION=false
VOICES=false
SPEECH=false
IMAGES=false
MIDI=false
WHISPER=false
MONTAGE=false
ACE=false
FROM_SOURCE=false
UPDATE=false
AUTO_UPDATE=true
if [ "${1:-}" = --update ]; then
  UPDATE=true
  shift
  # shellcheck disable=SC2046 # one word per option
  [ -f "$OPTIONS_FILE" ] && set -- $(cat "$OPTIONS_FILE") "$@"
fi
OPTIONS="$*"
for argument in "$@"; do
  case "$argument" in
    --transcription) TRANSCRIPTION=true ;;
    --voices) VOICES=true ;;
    --speech) SPEECH=true ;;
    --images) IMAGES=true ;;
    --midi) MIDI=true ;;
    --whisper) WHISPER=true ;;
    --montage) MONTAGE=true ;;
    --ace) ACE=true ;;
    --all) TRANSCRIPTION=true; VOICES=true; SPEECH=true; IMAGES=true; MIDI=true; WHISPER=true; MONTAGE=true; ACE=true ;;
    --engine-from-source) FROM_SOURCE=true ;;
    --no-auto-update) AUTO_UPDATE=false ;;
    --update) echo "--update comes first." >&2; exit 2 ;;
    -h|--help) sed -n '2,31p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "Unknown argument: $argument" >&2; exit 2 ;;
  esac
done

step() { printf '\n== %s\n' "$1"; }
fail() { echo "$1" >&2; exit 1; }

step "Checking the Mac"
[ "$(uname -s)" = Darwin ] || fail "This sets up a Mac; this is $(uname -s)."
[ "$(uname -m)" = arm64 ] || fail "YuE2's engines need Apple Silicon; this Mac is $(uname -m)."
MACOS="$(sw_vers -productVersion)"
[ "${MACOS%%.*}" -ge 14 ] || fail "YuE Studio's engines need macOS 14 or later; this is $MACOS."
MEMORY_GB=$(( $(sysctl -n hw.memsize) / 1073741824 ))
echo "macOS $MACOS, $MEMORY_GB GB memory"
[ "$MEMORY_GB" -ge 16 ] || echo "Warning: YuE2 needs at least 16 GB; with $MEMORY_GB GB it will hardly run."
# clang builds the Neural Engine bridge; the Command Line Tools are enough, Xcode is not needed.
xcode-select -p >/dev/null 2>&1 || fail "The Command Line Tools are missing: run xcode-select --install, then this again."
FREE_GB=$(( $(df -k "$HOME" | awk 'NR == 2 { print $4 }') / 1048576 ))
[ "$FREE_GB" -ge 20 ] || fail "Only $FREE_GB GB free; the models, environments and tools need about 20 GB."

step "Homebrew"
if ! command -v brew >/dev/null 2>&1; then
  [ -x /opt/homebrew/bin/brew ] || /bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"
  # A non-login shell (an ssh host command) has no Homebrew in its PATH.
  eval "$(/opt/homebrew/bin/brew shellenv)"
fi
brew --version | head -1

step "Tools: uv, ffmpeg, Node.js, .NET 10"
for formula in uv ffmpeg; do
  command -v "$formula" >/dev/null 2>&1 || brew install "$formula"
done
# The frontend build needs Node.js 22.12 or later.
if ! command -v node >/dev/null 2>&1 \
  || ! node -e 'const [a, b] = process.versions.node.split(".").map(Number); process.exit(a > 22 || (a === 22 && b >= 12) ? 0 : 1)'; then
  brew install node
fi
# The cask installs Microsoft's package to /usr/local/share/dotnet, which deploy/install.sh puts into the LaunchAgent.
PATH="$PATH:/usr/local/share/dotnet"
if ! command -v dotnet >/dev/null 2>&1 || ! dotnet --list-sdks | grep -q '^10\.'; then
  brew install --cask dotnet-sdk
fi
dotnet --list-sdks | grep -q '^10\.' \
  || fail "No .NET 10 SDK after installing Homebrew's dotnet-sdk; install it from https://dotnet.microsoft.com/download/dotnet/10.0 and run this again."
echo "$(uv --version), node $(node --version), .NET $(dotnet --list-sdks | grep '^10\.' | tail -1 | cut -d' ' -f1)"

# Where uv puts Python and where the models go, as the app's installer does.
export UV_PYTHON_INSTALL_DIR="$ROOT/python"
export HF_HOME="$ROOT/models"
export HF_HUB_DISABLE_TELEMETRY=1
PYTHON="$ROOT/env/bin/python"
WORKER_SOURCE="$ROOT/src"
mkdir -p "$ROOT" "$OUTPUT"

step "YuE2 engine ($ENGINE_REF)"
CHECKOUT="$WORKER_SOURCE"
if [ -d "$CHECKOUT" ] && [ ! -d "$CHECKOUT/.git" ]; then
  if [ "$FROM_SOURCE" = true ]; then
    # The environment and the models stay; only the app's copy of the source is replaced.
    mv "$CHECKOUT" "$ROOT/src.app-$(date +%Y%m%d-%H%M%S)"
    echo "The app's copy of the source was moved aside."
  else
    echo "The YuE Studio app installed this engine; it is left as it is (--engine-from-source replaces it by a checkout)."
    CHECKOUT=""
  fi
fi
if [ -n "$CHECKOUT" ]; then
  CHANGED=false
  if [ ! -d "$CHECKOUT/.git" ]; then
    git clone --filter=blob:none --quiet "$ENGINE_REPO" "$CHECKOUT"
    CHANGED=true
  fi
  git -C "$CHECKOUT" fetch --quiet --tags origin
  WANTED="$(git -C "$CHECKOUT" rev-parse "$ENGINE_REF^{commit}")"
  if [ "$(git -C "$CHECKOUT" rev-parse HEAD)" != "$WANTED" ]; then
    git -C "$CHECKOUT" checkout --quiet --detach "$WANTED"
    CHANGED=true
  fi
  git -C "$CHECKOUT" describe --tags --always

  # The Neural Engine bridge, built as YuE Studio's app/package.sh builds it.
  if [ "$CHANGED" = true ] || [ ! -f "$CHECKOUT/src/yue2/ane/libyue2ane.dylib" ]; then
    (cd "$CHECKOUT/src/yue2/ane" && clang -O2 -fobjc-arc -dynamiclib libyue2ane.m -o libyue2ane.dylib -framework Foundation -framework IOSurface)
    echo "Neural Engine bridge built."
  fi

  step "Python environment"
  [ -x "$PYTHON" ] || { uv python install 3.12; uv venv "$ROOT/env" --python 3.12; }
  # Editable: the package's version (0.1.6) stays the same across YuE Studio releases, so a plain install of a
  # newer checkout would be skipped as already satisfied.
  uv pip install --python "$PYTHON" --quiet -e "${CHECKOUT}[apple]"
  "$PYTHON" -c 'import importlib.metadata as m; print("torch", m.version("torch") + ", mlx", m.version("mlx"))'
fi
[ -x "$PYTHON" ] || fail "No Python environment at $ROOT/env."

step "YuE2 models (about 7 GB)"
models_present() {
  [ -d "$HF_HOME/hub/models--m-a-p--YuE2-3B/snapshots" ] && [ -d "$HF_HOME/hub/models--m-a-p--YuE2-Vae/snapshots" ]
}
if models_present; then
  echo "Already downloaded."
else
  # The script prints {"bytes", "total", "rate_mbps"} lines; this shows every tenth. Without pipefail a failed
  # download would pass unnoticed, hence the check after it.
  "$PYTHON" "$WORKER_SOURCE/tools/download_models.py" | "$PYTHON" -c '
import json, sys
shown = -1
for line in sys.stdin:
    try:
        p = json.loads(line)
    except ValueError:
        print(line, end="")
        continue
    done, total = p.get("bytes", 0), p.get("total") or 0
    if total and int(10 * done / total) > shown:
        shown = int(10 * done / total)
        print("%.1f of %.1f GB" % (done / 1e9, total / 1e9), flush=True)
'
  models_present || fail "The models did not download completely; run this again to resume."
fi
# YuE Studio's marker; `yue doctor` reads the version from it. The app, opened later, compares it with its own
# build and installs its copy over the checkout, which leaves a working engine without the git history.
if [ -n "$CHECKOUT" ]; then
  printf '{"version": "%s", "source": "%s"}\n' "$(git -C "$CHECKOUT" describe --tags --always)" "$ENGINE_REPO" > "$ROOT/installed.json"
fi

if [ "$TRANSCRIPTION" = true ]; then
  step "SheetSage2 for transcriptions (about 2 GB)"
  SHEETSAGE="$ROOT/sheetsage-env"
  if [ -x "$SHEETSAGE/bin/python" ] && grep -q "\"recipe\": *\"$SHEETSAGE_RECIPE\"" "$ROOT/sheetsage-installed.json" 2>/dev/null; then
    echo "Already installed."
  else
    # An environment of its own: SheetSage2's pins (torch 2.8, transformers 4.45, numpy 1.24) clash with YuE2's.
    "$PYTHON" "$WORKER_SOURCE/tools/download_models.py" m-a-p/SheetSage2 m-a-p/MERT-v2-FullSong >/dev/null
    uv python install 3.11
    uv venv "$SHEETSAGE" --python 3.11 --clear
    uv pip install --python "$SHEETSAGE/bin/python" torch==2.8.0 torchaudio==2.8.0
    for SNAPSHOT in "$HF_HOME"/hub/models--m-a-p--SheetSage2/snapshots/*/; do break; done
    uv pip install --python "$SHEETSAGE/bin/python" -r "${SNAPSHOT}requirements.txt" soundfile
    printf '{"recipe": "%s"}\n' "$SHEETSAGE_RECIPE" > "$ROOT/sheetsage-installed.json"
  fi
fi

if [ "$VOICES" = true ]; then
  step "Stem separation (mlx-audio-separator)"
  # Separator and Seed-VC each get an environment of their own: Seed-VC needs transformers 4.46 and
  # huggingface-hub below 1.0, which neither YuE2 nor mlx-audio-separator should be held to.
  SEPARATOR="$ENGINES/separator"
  OVERRIDES=""
  if [ -x "$SEPARATOR/env/bin/mlx-audio-separator" ] && [ "$(cat "$SEPARATOR/recipe" 2>/dev/null)" = "$SEPARATOR_PACKAGE" ]; then
    echo "Already installed."
  elif [ ! -d "$SEPARATOR/env" ] && [ -x "$HOME/repos/StemMyWav/.venv/bin/mlx-audio-separator" ]; then
    # StemMyWav's Mac API ran it before; its environment and downloaded models serve as they are.
    echo "Using StemMyWav's installation."
    OVERRIDES="${OVERRIDES}Separator=$HOME/repos/StemMyWav/.venv/bin/mlx-audio-separator
SeparatorModels=$HOME/repos/StemMyWav/.models
"
  else
    # A pin raised since the last run installs over the environment; the downloaded models stay.
    uv python install 3.12
    [ -x "$SEPARATOR/env/bin/python" ] || uv venv "$SEPARATOR/env" --python 3.12
    uv pip install --python "$SEPARATOR/env/bin/python" --quiet "$SEPARATOR_PACKAGE"
    mkdir -p "$SEPARATOR/models"
    echo "$SEPARATOR_PACKAGE" > "$SEPARATOR/recipe"
    echo "Installed $SEPARATOR_PACKAGE; each model downloads on its first separation."
  fi

  step "Seed-VC"
  SEED_VC="$ENGINES/seed-vc"
  if [ -x "$SEED_VC/env/bin/python" ] && [ "$(cat "$SEED_VC/recipe" 2>/dev/null)" = "$SEED_VC_RECIPE" ]; then
    echo "Already installed."
  elif [ ! -d "$SEED_VC/env" ] && [ -x "$HOME/mlx-vc/.venv/bin/python" ] && [ -f "$HOME/seed-vc-ref/inference.py" ]; then
    # ChangeMyVoice's installation (its init.md), with the checkpoints it already downloaded.
    echo "Using ChangeMyVoice's installation."
    OVERRIDES="${OVERRIDES}SeedVcPython=$HOME/mlx-vc/.venv/bin/python
SeedVcPath=$HOME/seed-vc-ref
SeedVcModels=$HOME/seed-vc-ref/checkpoints/hf_cache
"
  else
    [ -d "$SEED_VC/src/.git" ] || git clone --filter=blob:none --quiet "$SEED_VC_REPO" "$SEED_VC/src"
    git -C "$SEED_VC/src" fetch --quiet origin
    git -C "$SEED_VC/src" checkout --quiet --detach "$SEED_VC_REF"
    # The steps of ChangeMyVoice's init.md, which found this combination working on the Mac: Python 3.10, PyTorch
    # from PyPI (requirements-mac.txt's first four lines want nightlies and CUDA indexes), matplotlib for BigVGAN,
    # huggingface-hub 0.28.1 for transformers 4.46.3.
    uv python install 3.10
    # From scratch also when only the recipe changed: the pins fight each other, an install over them would not.
    uv venv "$SEED_VC/env" --python 3.10 --clear
    SEED_PYTHON="$SEED_VC/env/bin/python"
    uv pip install --python "$SEED_PYTHON" --quiet torch torchaudio
    tail -n +5 "$SEED_VC/src/requirements-mac.txt" > "$SEED_VC/requirements.txt"
    uv pip install --python "$SEED_PYTHON" --quiet -r "$SEED_VC/requirements.txt" matplotlib
    uv pip install --python "$SEED_PYTHON" --quiet "huggingface-hub==0.28.1"
    mkdir -p "$SEED_VC/models"
    "$SEED_PYTHON" -c 'import torch, munch, librosa, transformers; print("torch", torch.__version__, "MPS", torch.backends.mps.is_available())'
    echo "$SEED_VC_RECIPE" > "$SEED_VC/recipe"
    echo "Installed; the checkpoints download on the first conversion."
  fi

  if [ -n "$OVERRIDES" ]; then
    # install.sh rewrites appsettings.json, not this file, which the server reads over it.
    SETTINGS="$HOME/Library/Application Support/YueUI/app/appsettings.Production.json"
    mkdir -p "$(dirname "$SETTINGS")"
    OVERRIDES="$OVERRIDES" /usr/bin/python3 - "$SETTINGS" <<'PY'
import json, os, sys
path, pairs = sys.argv[1], os.environ["OVERRIDES"].split("\n")[:-1]
settings = json.load(open(path)) if os.path.exists(path) else {}
voice = settings.setdefault("Voice", {})
for pair in pairs:
    key, value = pair.split("=", 1)
    voice[key] = value
json.dump(settings, open(path, "w"), indent=2)
PY
    echo "Settings for the existing installations written to $SETTINGS."
  fi
fi

step "Tonwerk"
"$REPO/deploy/install.sh"

if [ "$SPEECH" = true ]; then
  step "Speech lab"
  # install-speech.sh upgrades mlx-audio to the newest; an update runs it only when the script itself changed.
  SPEECH_RECIPE="$(cksum < "$REPO/deploy/install-speech.sh")"
  if [ "$UPDATE" = true ] && [ "$(cat "$DATA/speech/recipe" 2>/dev/null)" = "$SPEECH_RECIPE" ]; then
    echo "Unchanged."
  else
    "$REPO/deploy/install-speech.sh"
    mkdir -p "$DATA/speech"
    echo "$SPEECH_RECIPE" > "$DATA/speech/recipe"
  fi
fi

if [ "$IMAGES" = true ]; then
  step "Painted covers"
  # install-images.sh pins mflux; an update runs it only when the script (and with it the pin) changed.
  IMAGES_RECIPE="$(cksum < "$REPO/deploy/install-images.sh")"
  if [ "$UPDATE" = true ] && [ "$(cat "$DATA/images/recipe" 2>/dev/null)" = "$IMAGES_RECIPE" ]; then
    echo "Unchanged."
  else
    "$REPO/deploy/install-images.sh"
    mkdir -p "$DATA/images"
    echo "$IMAGES_RECIPE" > "$DATA/images/recipe"
  fi
fi

if [ "$MIDI" = true ]; then
  step "Audio to MIDI"
  # install-midi.sh pins Basic Pitch and its packages; an update runs it only when the script changed.
  MIDI_RECIPE="$(cksum < "$REPO/deploy/install-midi.sh")"
  if [ "$UPDATE" = true ] && [ "$(cat "$DATA/midi/recipe" 2>/dev/null)" = "$MIDI_RECIPE" ]; then
    echo "Unchanged."
  else
    "$REPO/deploy/install-midi.sh"
    mkdir -p "$DATA/midi"
    echo "$MIDI_RECIPE" > "$DATA/midi/recipe"
  fi
fi

if [ "$WHISPER" = true ]; then
  step "Lyrics timing"
  # install-whisper.sh pins mlx-whisper; an update runs it only when the script changed.
  WHISPER_RECIPE="$(cksum < "$REPO/deploy/install-whisper.sh")"
  if [ "$UPDATE" = true ] && [ "$(cat "$DATA/whisper/recipe" 2>/dev/null)" = "$WHISPER_RECIPE" ]; then
    echo "Unchanged."
  else
    "$REPO/deploy/install-whisper.sh"
    mkdir -p "$DATA/whisper"
    echo "$WHISPER_RECIPE" > "$DATA/whisper/recipe"
  fi
fi

if [ "$ACE" = true ]; then
  step "ACE-Step"
  # install-ace.sh pins acestep.cpp; an update runs it only when the script (and with it the pin) or its patch changed.
  ACE_RECIPE="$(cat "$REPO/deploy/install-ace.sh" "$REPO/deploy/ace/request-utf8.patch" | cksum)"
  if [ "$UPDATE" = true ] && [ "$(cat "$DATA/ace/recipe" 2>/dev/null)" = "$ACE_RECIPE" ]; then
    echo "Unchanged."
  else
    "$REPO/deploy/install-ace.sh"
    mkdir -p "$DATA/ace"
    echo "$ACE_RECIPE" > "$DATA/ace/recipe"
  fi
fi

if [ "$MONTAGE" = true ]; then
  step "Photo videos"
  # install-montage.sh pins librosa, Pillow, PyTorch and open_clip; an update runs it only when the script changed.
  MONTAGE_RECIPE="$(cksum < "$REPO/deploy/install-montage.sh")"
  if [ "$UPDATE" = true ] && [ "$(cat "$DATA/montage/recipe" 2>/dev/null)" = "$MONTAGE_RECIPE" ]; then
    echo "Unchanged."
  else
    "$REPO/deploy/install-montage.sh"
    mkdir -p "$DATA/montage"
    echo "$MONTAGE_RECIPE" > "$DATA/montage/recipe"
  fi
fi

step "Checking the worker"
# The worker announces itself before it loads a model; the ready event says whether Tonwerk's extension could
# patch it (SEAMS in yueui_worker.py).
READY="$(echo '{"cmd": "quit"}' | YUE2_OUTPUT_DIR="$OUTPUT" YUE2_ANE_CACHE="$ROOT/ane-cache" \
  "$PYTHON" -u "$REPO/src/YueUI.Api/Worker/yueui_worker.py" "$WORKER_SOURCE/tools/yue2_worker.py" 2>/dev/null \
  | grep '"ready"' | head -1 || true)"
case "$READY" in
  *'"yueui_extensions": true'*) echo "The worker starts, with Tonwerk's extensions." ;;
  *'"yueui_extensions": false'*) echo "Warning: the worker starts without Tonwerk's extensions; check SEAMS in yueui_worker.py against this engine." ;;
  *) echo "Warning: the worker did not report ready; run it by hand to see why (see CLAUDE.md, the worker)." ;;
esac

mkdir -p "$DATA"
echo "$OPTIONS" > "$OPTIONS_FILE"
if [ "$UPDATE" = true ]; then
  exit 0
fi
if [ "$AUTO_UPDATE" = true ]; then
  step "Updates"
  "$REPO/deploy/update.sh" on
fi

cat <<EOF

Tonwerk runs on http://127.0.0.1:5090/ui/. Not set up by this script:
  - access from the phone: install Tailscale, then  tailscale serve --bg --https=8443 5090
  - lyrics drafts: LM Studio (brew install --cask lm-studio) with a model, see the README
EOF
[ "$VOICES" = true ] || echo "  - voices and stems: run this again with --voices"
