#!/usr/bin/env bash
# Azure platform metrics for the AKS cluster, its load balancer and the lab Event Hubs namespace.
# usage: azure-metrics.sh <outdir|-> [window seconds, default 3600]   (`-`: envelopes to stdout)
set -euo pipefail
source "$(dirname "$0")/../lib.sh"; load_context
dest_for() { if [ "$1" = "-" ]; then dest=(); else mkdir -p "$1/azure-metrics"; dest=(--out "$1/azure-metrics/$2.ndjson"); fi; }
root="$1"
window="${2:-3600}"
end=$(date -u +%Y-%m-%dT%H:%M:%SZ)
start=$(date -u -v-"${window}"S +%Y-%m-%dT%H:%M:%SZ 2>/dev/null || date -u -d "-${window} seconds" +%Y-%m-%dT%H:%M:%SZ)
lb=$(az network lb list -g "$NODE_RG" --query '[0].id' -o tsv)
ns=$(az eventhubs namespace show -g "$LAB_RG" -n "$EH_NS" --query id -o tsv)

# One request per metric set; the response is native and untouched, the request goes into meta.
fetch() { # <kind> <resource id> <metric names...>
  local kind="$1" id="$2"; shift 2
  local meta; dest_for "$root" "$kind"
  meta=$(jq -nc --arg k "$kind" --arg id "$id" --arg s "$start" --arg e "$end" '{resource:$k, resourceId:$id, interval:"PT1M", start:$s, end:$e, metrics:$ARGS.positional}' --args "$@")
  az monitor metrics list --resource "$id" --metrics "$@" --interval PT1M --start-time "$start" --end-time "$end" \
    --aggregation Average Total Maximum -o json 2>/dev/null | jq -c . | wrap --source azure-metrics --meta "$meta" ${dest[@]+"${dest[@]}"}
}

for kind_id in "aks|$AKS_ID" "loadbalancer|$lb" "eventhubs|$ns"; do
  kind="${kind_id%%|*}"; id="${kind_id#*|}"
  # 20 metrics per request is the platform limit. Some definitions are not queryable together:
  # when a set is rejected, retry each metric alone and skip the ones that fail.
  az monitor metrics list-definitions --resource "$id" -o json 2>/dev/null \
    | jq -r '[.[].name.value] | [range(0; length; 20) as $i | .[$i:$i+20] | join(" ")] | .[]' \
    | while read -r names; do
        # shellcheck disable=SC2086
        fetch "$kind" "$id" $names || for m in $names; do fetch "$kind" "$id" "$m" || echo "skip metric $kind/$m" >&2; done
      done
done
