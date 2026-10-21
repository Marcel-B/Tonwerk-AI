#!/bin/sh
# Moves Tonwerk's big folders (models, songs, versions, …) to another volume, typically an external SSD, and leaves
# a symbolic link at the old place. Nothing else changes: Tonwerk, YuE Studio, LM Studio, setup-mac.sh and the
# backup keep using the paths they know, so neither a setting nor an update has to know about the move.
#
#   deploy/move-to-volume.sh                          what can be moved, how big it is and where it lies now
#   deploy/move-to-volume.sh /Volumes/SSD yue2 songs  move these parts to /Volumes/SSD/Tonwerk/<part>
#   deploy/move-to-volume.sh --back yue2              bring a part back to the internal disk
#
# A part that is not there yet (a feature installed later, whose models download on first use) is made empty on
# the volume and linked, so its downloads land there from the start.
#
# Add --yes to skip the question. Tonwerk (and its update agent) is stopped while files move and started again
# afterwards; the script refuses while Tonwerk works on something.
#
# Before Tonwerk can read the volume, macOS has to let it: System Settings → Privacy & Security → Full Disk Access,
# add the dotnet that `realpath "$(command -v dotnet)"` names. A LaunchAgent gets no prompt, only "Operation not
# permitted". This script, run in Terminal or over SSH, uses that shell's own permission.
#
# A volume that is not mounted leaves a link that points nowhere: what lies there is missing (and writing there
# fails) until the volume is back. External volumes mount when the user logs in, as Tonwerk starts.
set -eu

YUEUI_LABEL=de.bvelop.yueui
WATCH_LABEL=de.bvelop.yueui-watch
URL="${YUEUI_URL:-http://127.0.0.1:5090}"
SETTINGS="$HOME/Library/Application Support/YueUI/app/appsettings.Production.json"
LMS="$HOME/.lmstudio/bin/lms"
DOMAIN="gui/$(id -u)"
PARTS="yue2 ane songs lmstudio speech images montage acestep acesongs singing separator seedvc versions stems videos"
SUFFIX=tonwerk-move

fail() { echo "$1" >&2; exit 1; }

# A value from appsettings.Production.json (plutil reads JSON too), with ~ expanded; empty when not set.
setting() {
  value=""
  [ -f "$SETTINGS" ] && value="$(plutil -extract "$1" raw -o - "$SETTINGS" 2>/dev/null)" || true
  case "$value" in "~/"*) value="$HOME/${value#\~/}" ;; esac
  echo "$value"
}

# The same defaults the server uses (YuePaths, DataOptions, SpeechOptions, ImageOptions, MontageOptions, AceOptions,
# SingingOptions, VoiceOptions).
or_default() { if [ -n "$1" ]; then echo "$1"; else echo "$2"; fi; }
YUE_ROOT="$(or_default "$(setting Yue.InstallRoot)" "$HOME/Library/Application Support/YuE Studio")"
DATA_PATH="$(setting Data.Path)"
DATA="$(if [ -n "$DATA_PATH" ]; then dirname "$DATA_PATH"; else echo "$HOME/Library/Application Support/YuE UI"; fi)"
APP_DATA="$HOME/Library/Application Support/YuE UI"
ENGINES="$(or_default "$(setting Voice.EngineRoot)" "$APP_DATA/engines")"

path_of() {
  case "$1" in
    yue2) echo "$YUE_ROOT/models" ;;
    ane) echo "$YUE_ROOT/ane-cache" ;;
    songs) or_default "$(setting Yue.OutputDir)" "$HOME/Music/YuE Studio" ;;
    lmstudio) echo "$HOME/.lmstudio/models" ;;
    speech) or_default "$(setting Speech.ModelCache)" "$(or_default "$(setting Speech.Root)" "$APP_DATA/speech")/models" ;;
    images) or_default "$(setting Images.ModelCache)" "$(or_default "$(setting Images.Root)" "$APP_DATA/images")/models" ;;
    montage) or_default "$(setting Montage.ModelCache)" "$(or_default "$(setting Montage.Root)" "$APP_DATA/montage")/models" ;;
    acestep) or_default "$(setting Ace.Models)" "$(or_default "$(setting Ace.Root)" "$APP_DATA/ace")/models" ;;
    acesongs) or_default "$(setting Ace.Songs)" "$DATA/ace-songs" ;;
    # SoulX-Singer's weights: the folder of Singing.Model when set, else models/ under its root.
    singing)
      model="$(setting Singing.Model)"
      if [ -n "$model" ]; then dirname "$model"; else echo "$(or_default "$(setting Singing.Root)" "$APP_DATA/singing")/models"; fi ;;
    separator) or_default "$(setting Voice.SeparatorModels)" "$ENGINES/separator/models" ;;
    seedvc) or_default "$(setting Voice.SeedVcModels)" "$ENGINES/seed-vc/models" ;;
    versions | stems | videos) echo "$DATA/$1" ;;
    *) return 1 ;;
  esac
}

describe() {
  case "$1" in
    yue2) echo "YuE2 models (also YuE Studio's)" ;;
    ane) echo "YuE2's compiled Neural Engine programs" ;;
    songs) echo "songs and transcriptions (also YuE Studio's)" ;;
    lmstudio) echo "LM Studio's models" ;;
    speech) echo "speech lab models" ;;
    images) echo "image models for painted covers (Klein, Z-Image, Krea 2, FLUX 3)" ;;
    montage) echo "CLIP models for photo videos with a motif" ;;
    acestep) echo "ACE-Step models" ;;
    acesongs) echo "songs made with ACE-Step" ;;
    singing) echo "SoulX-Singer's weights for the singing page" ;;
    separator) echo "stem separation models" ;;
    seedvc) echo "Seed-VC checkpoints" ;;
    versions) echo "songs sung with another voice" ;;
    stems) echo "separated stems" ;;
    videos) echo "music videos" ;;
  esac
}

size_of() { du -sh "$1/" 2>/dev/null | cut -f1; }

# Files, symbolic links and bytes below a folder: equal on both sides means the copy is complete.
fingerprint() {
  files="$(find "$1/" -type f | wc -l | tr -d ' ')"
  links="$(find "$1/" -type l | wc -l | tr -d ' ')"
  bytes="$(find "$1/" -type f -exec stat -f %z {} + 2>/dev/null | awk '{ s += $1 } END { printf "%.0f", s }')"
  echo "$files files, $links links, $bytes bytes"
}

list() {
  printf '%-10s %8s  %s\n' part size where
  for part in $PARTS; do
    path="$(path_of "$part")"
    if [ -L "$path" ]; then
      target="$(readlink "$path")"
      if [ -d "$target" ]; then
        printf '%-10s %8s  moved to %s\n' "$part" "$(size_of "$target")" "$target"
      else
        printf '%-10s %8s  moved to %s (not mounted)\n' "$part" "-" "$target"
      fi
    elif [ -d "$path" ]; then
      printf '%-10s %8s  %s\n' "$part" "$(size_of "$path")" "$path"
    else
      printf '%-10s %8s  %s (not there yet; moving it puts later downloads on the volume)\n' "$part" "-" "$path"
    fi
    printf '%-10s %8s  %s\n' "" "" "$(describe "$part")"
  done
  echo
  df -h "$HOME" | awk 'NR == 2 { print "Internal disk: " $4 " free of " $2 }'
  echo
  echo "Move with: $0 /Volumes/<name> <part> [<part> …]"
}

# Nothing may write into a folder while it is copied: Tonwerk and its updater stop, LM Studio unloads its models.
STOPPED=""
stop_agent() {
  plist="$HOME/Library/LaunchAgents/$1.plist"
  launchctl print "$DOMAIN/$1" >/dev/null 2>&1 || return 0
  launchctl bootout "$DOMAIN/$1" 2>/dev/null || true
  # bootout returns before the process is gone (install.sh waits the same way).
  for _ in $(seq 1 50); do
    launchctl print "$DOMAIN/$1" >/dev/null 2>&1 || break
    sleep 0.2
  done
  STOPPED="$STOPPED $1"
}

start_agents() {
  for label in $STOPPED; do
    launchctl bootstrap "$DOMAIN" "$HOME/Library/LaunchAgents/$label.plist" 2>/dev/null \
      || echo "Could not start $label again: launchctl bootstrap $DOMAIN ~/Library/LaunchAgents/$label.plist" >&2
  done
  STOPPED=""
}

quiesce() {
  # Waiting jobs survive the restart; running ones would be cut off (as deploy/update.sh checks).
  if busy="$(curl -fsS --max-time 5 "$URL/api/busy" 2>/dev/null)" && echo "$busy" | grep -q '"busy":true'; then
    fail "Tonwerk is working ($busy); try again when it is done."
  fi
  for part in "$@"; do
    case "$part" in
      yue2 | ane | songs)
        ! pgrep -f "YuE Studio.app/" >/dev/null 2>&1 || fail "YuE Studio is open and uses $part; quit it first."
        ;;
      lmstudio)
        [ -x "$LMS" ] && "$LMS" unload --all >/dev/null 2>&1 || true
        ;;
    esac
  done
  trap start_agents EXIT
  stop_agent "$WATCH_LABEL"
  stop_agent "$YUEUI_LABEL"
}

confirm() {
  [ "$YES" = true ] && return 0
  printf '%s [y/N] ' "$1"
  read -r answer
  case "$answer" in y | Y | j | J | yes | ja) return 0 ;; *) fail "Nothing changed." ;; esac
}

# Copies a folder with ditto (keeps links, permissions and extended attributes), checks the copy and only then
# swaps it in, so a cut-off run leaves the original untouched and a .partial copy to delete.
copy_checked() {
  src="$1"
  dest="$2"
  rm -rf "$dest.partial"
  mkdir -p "$(dirname "$dest")"
  echo "Copying $src → $dest ($(size_of "$src"))…"
  ditto "$src" "$dest.partial"
  before="$(fingerprint "$src")"
  after="$(fingerprint "$dest.partial")"
  if [ "$before" != "$after" ]; then
    fail "The copy differs from the original ($before, copy $after); nothing was switched. $dest.partial can go."
  fi
  mv "$dest.partial" "$dest"
}

move_out() {
  volume="$1"
  shift
  case "$volume" in /Volumes/?*) ;; *) fail "$volume is not a volume under /Volumes." ;; esac
  volume="${volume%/}"
  [ -d "$volume" ] || fail "$volume is not mounted."
  # NTFS is read-only on macOS, exFAT and FAT have no symbolic links and permissions, which Hugging Face's cache
  # and the Python tools rely on.
  personality="$(diskutil info "$volume" 2>/dev/null | sed -n 's/^ *File System Personality: *//p')"
  case "$personality" in
    *APFS* | *HFS* | *"Mac OS Extended"*) ;;
    "") fail "diskutil does not know $volume." ;;
    *) fail "$volume is $personality; it needs APFS (Disk Utility → Erase, which deletes what is on it)." ;;
  esac

  [ $# -gt 0 ] || fail "Name at least one part: $PARTS"
  need=0
  for part in "$@"; do
    path="$(path_of "$part")" || fail "Unknown part $part; there are: $PARTS"
    [ ! -L "$path" ] || fail "$part already lies at $(readlink "$path")."
    [ -d "$path" ] || [ ! -e "$path" ] || fail "$part is no folder ($path)."
    [ ! -e "$volume/Tonwerk/$part" ] || fail "$volume/Tonwerk/$part exists already; move it aside first."
    [ ! -d "$path" ] || need=$((need + $(du -sk "$path/" | cut -f1)))
  done
  free="$(df -k "$volume" | awk 'NR == 2 { print $4 }')"
  [ "$free" -gt $((need + need / 20)) ] || fail "$volume has $((free / 1048576)) GB free, the parts need $((need / 1048576)) GB."
  mkdir -p "$volume/Tonwerk" 2>/dev/null && touch "$volume/Tonwerk/.write-test" 2>/dev/null \
    || fail "Cannot write to $volume; this shell may need Full Disk Access (or Remote Login's \"full disk access for remote users\")."
  rm -f "$volume/Tonwerk/.write-test"

  confirm "Move $* ($((need / 1048576)) GB) to $volume/Tonwerk? Tonwerk stops meanwhile."
  quiesce "$@"
  for part in "$@"; do
    path="$(path_of "$part")"
    dest="$volume/Tonwerk/$part"
    if [ ! -e "$path" ]; then
      # Nothing to copy yet: an empty folder on the volume, so the first download goes there.
      mkdir -p "$dest" "$(dirname "$path")"
      ln -s "$dest" "$path"
      echo "$part does not exist yet; it will be made at $dest."
      continue
    fi
    copy_checked "$path" "$dest"
    # The original goes only once the link is in place; until then a failure leaves everything as it was.
    mv "$path" "$path.$SUFFIX"
    ln -s "$dest" "$path"
    rm -rf "$path.$SUFFIX"
    echo "$part now lies at $dest."
  done
  start_agents
  trap - EXIT
  echo
  echo "Done. Unless dotnet has Full Disk Access already, Tonwerk now fails on these parts with \"Operation not"
  echo "permitted\": System Settings → Privacy & Security → Full Disk Access, add $(realpath "$(command -v dotnet)" 2>/dev/null || echo dotnet),"
  echo "then launchctl kickstart -k $DOMAIN/$YUEUI_LABEL."
}

move_back() {
  [ $# -gt 0 ] || fail "Name at least one part to bring back."
  need=0
  for part in "$@"; do
    path="$(path_of "$part")" || fail "Unknown part $part; there are: $PARTS"
    [ -L "$path" ] || fail "$part was not moved ($path is no link)."
    target="$(readlink "$path")"
    [ -d "$target" ] || fail "$target, where $part lies, is not mounted."
    need=$((need + $(du -sk "$target/" | cut -f1)))
  done
  free="$(df -k "$HOME" | awk 'NR == 2 { print $4 }')"
  [ "$free" -gt $((need + need / 20)) ] || fail "The internal disk has $((free / 1048576)) GB free, the parts need $((need / 1048576)) GB."

  confirm "Bring $* ($((need / 1048576)) GB) back to the internal disk? Tonwerk stops meanwhile."
  quiesce "$@"
  for part in "$@"; do
    path="$(path_of "$part")"
    target="$(readlink "$path")"
    copy_checked "$target" "$path.$SUFFIX"
    rm "$path"
    mv "$path.$SUFFIX" "$path"
    echo "$part lies at $path again; the copy at $target stays until you delete it."
  done
  start_agents
  trap - EXIT
}

YES=false
BACK=false
set -- "$@" --end
while [ "$1" != --end ]; do
  case "$1" in
    --yes | -y) YES=true ;;
    --back) BACK=true ;;
    -h | --help) sed -n '2,19p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) set -- "$@" "$1" ;;
  esac
  shift
done
shift

[ "$(uname -s)" = Darwin ] || [ -n "${MOVE_TO_VOLUME_TEST:-}" ] || fail "This moves folders on the Mac; this is $(uname -s)."
if [ "$BACK" = true ]; then
  move_back "$@"
elif [ $# -eq 0 ]; then
  list
else
  move_out "$@"
fi
