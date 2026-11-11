#!/usr/bin/env bash
# Hubble flows to stdout (jsonpb) until the relay or the CLI dies; the forwarder restarts it.
# env: HUBBLE_FILTER (observe filter flags), HUBBLE_SINCE (replay window after a reconnect, e.g. 40s)
set -uo pipefail
source "$(dirname "$0")/../lib.sh"
trap stop_relay EXIT
start_relay || exit 1
history=(--last 0); [ -n "${HUBBLE_SINCE:-}" ] && history=(--since "$HUBBLE_SINCE")
# shellcheck disable=SC2086
hubble observe $HUBBLE_ARGS "${history[@]}" --follow -o jsonpb ${HUBBLE_FILTER:-} &
cli=$!
# either side dying ends the script; no `wait -n` on bash 3.2
while kill -0 "$cli" 2>/dev/null && kill -0 "$RELAY_PID" 2>/dev/null; do sleep 2; done
kill "$cli" 2>/dev/null
wait "$cli" 2>/dev/null
exit 1
