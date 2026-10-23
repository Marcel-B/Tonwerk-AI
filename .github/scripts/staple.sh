#!/bin/bash
# Staples the notarization ticket to an app or DMG. Right after notarization the ticket comes from Apple's
# CloudKit, which the runner sometimes loses the connection to ("The network connection was lost", error 68),
# so try again with growing pauses before giving up.
#
#   .github/scripts/staple.sh FILE
set -uo pipefail

for pause in 10 30 60 120; do
  xcrun stapler staple "$1" && exit 0
  echo "Stapling $(basename "$1") failed, trying again in $pause s."
  sleep "$pause"
done
xcrun stapler staple "$1"
