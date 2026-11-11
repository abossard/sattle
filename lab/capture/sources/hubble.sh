#!/usr/bin/env bash
# Hubble flows through the relay (HUBBLE_ARGS from start_relay). usage: hubble.sh <outdir> <seconds>
set -euo pipefail
source "$(dirname "$0")/../lib.sh"
out="$1/hubble"; mkdir -p "$out"; secs="$2"
# --last replays each node's ring buffer first, then --follow streams. Lost-event records stay in the output.
# shellcheck disable=SC2086
hubble observe $HUBBLE_ARGS --last "${HUBBLE_LAST:-3000}" --follow -o jsonpb 2>"$out/.stderr.log" \
  > >(wrap --source hubble --out "$out/flows.ndjson") &
pid=$!
sleep "$secs"; kill "$pid" 2>/dev/null || true
wait "$pid" 2>/dev/null || true
sleep 1   # let the wrapper drain
