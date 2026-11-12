#!/usr/bin/env bash
# One command from a deployed lab (azd up done) to fixtures: a capture, then the scrubbed and verified fixtures built
# from exactly that capture. usage: capture-to-fixtures.sh [stream-seconds]   (default 420)
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
export CAPTURE_TS="${CAPTURE_TS:-$(date -u +%Y%m%dT%H%M%SZ)}"
capture="$here/captures/$CAPTURE_TS"
if ! "$here/capture/capture.sh" "${1:-420}"; then
  echo "capture-to-fixtures: the capture is incomplete (see the FAILED lines above), no fixtures were built." >&2
  echo "To build from it anyway: fixtures/build.sh $capture" >&2
  exit 1
fi
"$here/fixtures/build.sh" "$capture"
