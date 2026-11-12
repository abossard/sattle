#!/usr/bin/env bash
# Kubernetes: `list <outdir>` writes every object of the core kinds, `watch <outdir> <seconds>` follows them
# from the list's resourceVersion so no change is missed between the two.
set -euo pipefail
source "$(dirname "$0")/../lib.sh"
mode="$1"; out="$2/kubernetes"; mkdir -p "$out"

# <path>:<kind>
kinds=(
  /api/v1/namespaces /api/v1/nodes /api/v1/pods /api/v1/services /api/v1/endpoints /api/v1/events
  /api/v1/serviceaccounts /api/v1/persistentvolumes /api/v1/persistentvolumeclaims
  /apis/apps/v1/deployments /apis/apps/v1/replicasets /apis/apps/v1/daemonsets /apis/apps/v1/statefulsets
  /apis/batch/v1/jobs /apis/batch/v1/cronjobs /apis/discovery.k8s.io/v1/endpointslices
  /apis/networking.k8s.io/v1/networkpolicies /apis/networking.k8s.io/v1/ingresses
  /apis/cilium.io/v2/ciliumendpoints /apis/cilium.io/v2/ciliumidentities /apis/cilium.io/v2/ciliumnodes
  /apis/cilium.io/v2/ciliumnetworkpolicies /apis/cilium.io/v2/ciliumclusterwidenetworkpolicies
)
name() { basename "$1"; }

case "$mode" in
list)
  for p in "${kinds[@]}"; do
    raw=$(kubectl get --raw "$p" 2>/dev/null) || { echo "skip $p" >&2; continue; }
    meta=$(echo "$raw" | jq -c '{list:{kind, apiVersion, resourceVersion:.metadata.resourceVersion}, path:"'"$p"'"}')
    echo "$raw" | jq -c '.items[]' | wrap --source kubernetes-list --meta "$meta" --out "$out/list.ndjson"
    echo "$raw" | jq -c '{path:"'"$p"'", resourceVersion:.metadata.resourceVersion}' >> "$out/.versions.ndjson"
  done ;;
watch)
  secs="$3"; end=$(( $(date +%s) + secs ))
  # One watch per kind until the window ends. A watch the server ends early resumes from the last version it delivered.
  # A watch that fails, or whose version the server no longer has, is reported with the reason and fails the script
  # after the other kinds have finished: the file capture has no relist, so that kind has a hole.
  watch_kind() {
    local p="$1" rv="$2" file err left meta last
    file="$out/watch.$(name "$p").ndjson"; err="$out/.watch.$(name "$p").err"
    while left=$(( end - $(date +%s) )); [ "$left" -gt 0 ]; do
      meta=$(jq -nc --arg p "$p" --arg rv "$rv" '{path:$p, startResourceVersion:$rv}')
      if ! kubectl get --raw "$p?watch=1&allowWatchBookmarks=true&resourceVersion=$rv&timeoutSeconds=$left" 2>"$err" \
          | wrap --source kubernetes-watch --meta "$meta" --out "$file"; then
        echo "watch $p failed: $(tail -1 "$err")" >&2; return 1
      fi
      last=$(tail -1 "$file" 2>/dev/null | jq -r 'if .payload.type == "ERROR" then "ERROR" else (.payload.object.metadata.resourceVersion // "") end' 2>/dev/null) || last=""
      if [ "$last" = ERROR ]; then echo "watch $p failed: the server ended it with an error event (resourceVersion $rv expired?)" >&2; return 1; fi
      [ -n "$last" ] && rv="$last"
      sleep 1
    done
  }
  watches=() # path:pid
  while read -r v; do
    watch_kind "$(echo "$v" | jq -r .path)" "$(echo "$v" | jq -r .resourceVersion)" & watches+=("$(echo "$v" | jq -r .path):$!")
  done < "$out/.versions.ndjson"
  # SIGTERM or SIGINT (capture.sh stopping) ends the watches early and is not a failure
  stopped=0
  # the watcher first, so it cannot start another round, then what it was running (kubectl, wrap)
  stop_watches() {
    stopped=1
    local w pid kids
    for w in ${watches[@]+"${watches[@]}"}; do
      pid="${w##*:}"; kids=$(pgrep -P "$pid" 2>/dev/null || true)
      kill "$pid" 2>/dev/null || true        # (set -e is on: a watcher that already ended must not stop this loop)
      if [ -n "$kids" ]; then kill $kids 2>/dev/null || true; fi
    done
    return 0
  }
  trap stop_watches TERM INT
  status=0
  for w in ${watches[@]+"${watches[@]}"}; do
    code=0; wait "${w##*:}" || code=$?
    if [ "$stopped" = 1 ] && [ "$code" -ge 128 ]; then code=0; wait "${w##*:}" || code=$?; fi
    # 128 and up here is a watch that was stopped on purpose
    if [ "$code" -ne 0 ] && [ "$code" -lt 128 ]; then status=1; fi
  done
  exit "$status" ;;
esac
