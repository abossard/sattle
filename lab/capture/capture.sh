#!/usr/bin/env bash
# One capture: snapshots, a live window of streams, then end-of-window pulls.
# usage: capture.sh [stream-seconds]   (default 420)  ->  lab/captures/<UTC timestamp>/
set -euo pipefail
source "$(dirname "$0")/lib.sh"; load_context
secs="${1:-420}"
ts="${CAPTURE_TS:-$(date -u +%Y%m%dT%H%M%SZ)}"   # capture-to-fixtures.sh names the directory in advance
out="$LAB_DIR/captures/$ts"; mkdir -p "$out"
src="$CAP_DIR/sources"
echo "capture -> $out (stream window ${secs}s)"

trap 'stop_relay; kill $(jobs -p) 2>/dev/null || true' EXIT
start_relay

# 1. Snapshots that must precede the window: inventory and the Kubernetes list the watch resumes from.
"$src/arg.sh" "$out"
"$src/kubernetes.sh" list "$out"

# 2. Live window. Every stream runs for the same wall-clock time.
pids=() # name:pid, waited for one by one
"$src/kubernetes.sh" watch "$out" "$secs" & pids+=("watch:$!")
"$src/hubble.sh" "$out" "$secs" & pids+=("hubble:$!")
mkdir -p "$out/eventhub"
# Azure-written hubs replay their retained history; the forwarder hubs start at the window.
for hub in sattle-activity sattle-aks sattle-hubble sattle-ingest; do
  from=latest; case "$hub" in sattle-activity|sattle-aks) from=earliest;; esac
  node "$CAP_DIR/eh-consume.mjs" --namespace "$EH_NS" --hub "$hub" --out "$out/eventhub/$hub.ndjson" --from "$from" --seconds "$((secs + 20))" & pids+=("consume-$hub:$!")
done
sleep 5   # consumers attach before the forwarder publishes
node "$CAP_DIR/eh-forward.mjs" --namespace "$EH_NS" --hub sattle-ingest --kubeconfig "$KUBECONFIG" --seconds "$secs" --mirror-hubble & pids+=("forward:$!")
if [ -x "$LAB_DIR/demo/churn.sh" ]; then "$LAB_DIR/demo/churn.sh" "$(( secs / 135 + 1 ))" & pids+=("churn:$!"); fi
streams_status=0
wait_streams "${pids[@]}" || streams_status=$?

# 3. Pulls that should include the window: metrics, Activity Log, flow log blobs.
# A failing pull is reported and skipped; the capture keeps the rest.
for pull in "prometheus.sh $out 3600" "azure-metrics.sh $out 3600" "activity-log.sh $out 7" "flowlogs.sh $out"; do
  # shellcheck disable=SC2086
  "$src/${pull%% *}" ${pull#* } || echo "FAILED: ${pull%% *}" >&2
done

# 4. Counts per source directory.
for d in "$out"/*/; do
  for f in "$d"*.ndjson; do [ -e "$f" ] && printf '%s\t%s\n' "${f#"$out"/}" "$(wc -l < "$f" | tr -d ' ')"; done
done | jq -R 'split("\t") | {file:.[0], records:(.[1]|tonumber)}' | jq -s . > "$out/SUMMARY.json"
jq -r '.[] | "\(.records)\t\(.file)"' "$out/SUMMARY.json"
# A dead watch or consumer is a hole in the capture: the files and the summary are written, the exit status says so.
exit "$streams_status"
