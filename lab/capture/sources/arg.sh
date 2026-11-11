#!/usr/bin/env bash
# Azure Resource Graph: subscription inventory, paged to completion. usage: arg.sh <outdir>
set -euo pipefail
source "$(dirname "$0")/../lib.sh"; load_context
out="$1/arg"; mkdir -p "$out"
page_size="${ARG_PAGE_SIZE:-100}"
url="https://management.azure.com/providers/Microsoft.ResourceGraph/resources?api-version=2022-10-01"

# name | KQL
declare -a queries=(
  "resources|Resources"
  "resourcecontainers|ResourceContainers"
)

for q in "${queries[@]}"; do
  name="${q%%|*}"; kql="${q#*|}"; skip=""; page=0
  while :; do
    body=$(jq -n --arg s "$SUB" --arg q "$kql" --argjson top "$page_size" --arg skip "$skip" \
      '{subscriptions:[$s], query:$q, options:({"$top":$top, resultFormat:"objectArray"} + (if $skip=="" then {} else {"$skipToken":$skip} end))}')
    resp=$(az rest --method post --url "$url" --body "$body")
    meta=$(jq -nc --arg n "$name" --arg q "$kql" --argjson p "$page" --argjson top "$page_size" '{query:$n, kql:$q, page:$p, pageSize:$top}')
    echo "$resp" | jq -c '.data[]' | wrap --source arg --meta "$meta" --out "$out/$name.ndjson"
    # page boundary record: the request and the response without its rows
    jq -nc --argjson req "$body" --argjson resp "$(echo "$resp" | jq -c 'del(.data)')" '{request:$req, response:$resp}' \
      | wrap --source arg-page --meta "$meta" --out "$out/pages.ndjson"
    skip=$(echo "$resp" | jq -r '.["$skipToken"] // empty')
    [ -z "$skip" ] && break
    page=$((page+1))
  done
done
