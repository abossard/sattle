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
  secs="$3"
  while read -r v; do
    p=$(echo "$v" | jq -r .path); rv=$(echo "$v" | jq -r .resourceVersion)
    meta=$(jq -nc --arg p "$p" --arg rv "$rv" '{path:$p, startResourceVersion:$rv}')
    kubectl get --raw "$p?watch=1&allowWatchBookmarks=true&resourceVersion=$rv&timeoutSeconds=$secs" 2>/dev/null \
      | wrap --source kubernetes-watch --meta "$meta" --out "$out/watch.$(name "$p").ndjson" &
  done < "$out/.versions.ndjson"
  wait ;;
esac
