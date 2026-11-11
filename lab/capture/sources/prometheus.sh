#!/usr/bin/env bash
# Azure Monitor workspace (managed Prometheus) range queries. usage: prometheus.sh <outdir|-> [window seconds, default 3600]
# `-` writes envelopes to stdout (the forwarder uses that).
set -euo pipefail
source "$(dirname "$0")/../lib.sh"; load_context
dest=(); if [ "$1" != "-" ]; then out="$1/prometheus"; mkdir -p "$out"; dest=(--out "$out/range.ndjson"); fi
window="${2:-3600}"; step="${PROM_STEP:-30s}"
amw=$(az resource list -g "$AKS_RG" --resource-type Microsoft.Monitor/accounts --query '[0].id' -o tsv)
endpoint=$(az resource show --ids "$amw" --query 'properties.metrics.prometheusQueryEndpoint' -o tsv)
end=$(date -u +%s); start=$((end - window))
token=$(az account get-access-token --resource https://prometheus.monitor.azure.com --query accessToken -o tsv)

jq -c '.[]' "$CAP_DIR/prometheus-queries.json" | while read -r q; do
  name=$(echo "$q" | jq -r .name); query=$(echo "$q" | jq -r .query)
  meta=$(jq -nc --arg n "$name" --arg q "$query" --argjson s "$start" --argjson e "$end" --arg st "$step" '{name:$n, query:$q, start:$s, end:$e, step:$st, hashSalt:$n}')
  # the token goes through a curl config on stdin, never on the command line
  printf 'header = "Authorization: Bearer %s"\n' "$token" \
    | curl -sS -K - "$endpoint/api/v1/query_range" --data-urlencode "query=$query" -d "start=$start" -d "end=$end" -d "step=$step" \
    | jq -c . | wrap --source prometheus --meta "$meta" ${dest[@]+"${dest[@]}"}
done
