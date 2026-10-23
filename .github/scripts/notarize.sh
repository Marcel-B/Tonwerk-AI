#!/bin/bash
# Hands a zip or DMG to Apple's notary service, waits for the verdict and prints Apple's log when it is not
# "Accepted" (notarytool's exit code does not say that). Needs APPLE_ID, APPLE_TEAM_ID and APPLE_APP_PASSWORD.
#
#   .github/scripts/notarize.sh FILE
set -euo pipefail

auth=(--apple-id "$APPLE_ID" --team-id "$APPLE_TEAM_ID" --password "$APPLE_APP_PASSWORD")
result="$(mktemp)"
trap 'rm -f "$result"' EXIT

xcrun notarytool submit "$1" "${auth[@]}" --wait --timeout 1h --output-format json > "$result" || true
cat "$result"; echo
status="$(plutil -extract status raw -o - "$result" 2>/dev/null || echo unknown)"
if [[ "$status" != Accepted ]]; then
  id="$(plutil -extract id raw -o - "$result" 2>/dev/null || true)"
  [[ -n "$id" ]] && xcrun notarytool log "$id" "${auth[@]}" || true
  echo "::error::notarization of $(basename "$1"): $status"
  exit 1
fi
echo "Notarization of $(basename "$1"): accepted."
