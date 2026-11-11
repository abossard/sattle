#!/usr/bin/env bash
# Activity Log backfill via the management API (live events arrive through the Event Hub). usage: activity-log.sh <outdir> [days]
set -euo pipefail
source "$(dirname "$0")/../lib.sh"; load_context
out="$1/activity-log"; mkdir -p "$out"
az monitor activity-log list --offset "${2:-7}d" --max-events "${ACTIVITY_MAX:-5000}" -o json \
  | jq -c '.[]' | wrap --source activity-log --meta '{"via":"az monitor activity-log list"}' --out "$out/backfill.ndjson"
