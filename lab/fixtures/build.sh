#!/usr/bin/env bash
# Rebuild sanitized fixtures from a capture and verify them. usage: fixtures/build.sh <capture dir> [output dir]
# The capture names its own tenant, subscription, resources and people; nothing is read from az or azd.
# SCRUB_LITERALS (one value per line) can add values that the capture does not show, for example a display name.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
capture="$(cd "$1" && pwd)"
out="${2:-$here}"
mkdir -p "$out"
out="$(cd "$out" && pwd)"
# build into a fresh directory first, so a failed build leaves the current fixtures alone
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
node "$here/scrub.mjs" build "$capture" "$work"
node "$here/scrub.mjs" verify "$capture" "$work"
for d in arg kubernetes hubble prometheus azure-metrics activity-log vnet-flow-logs eventhub; do rm -rf "${out:?}/$d"; done
cp -R "$work"/. "$out"/
