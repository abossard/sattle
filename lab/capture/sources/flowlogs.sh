#!/usr/bin/env bash
# VNet flow log blobs. Storage has public access disabled by tenant policy, so a short-lived pod in the AKS VNet
# reads them over the private endpoint and streams them to stdout through `kubectl exec`. One envelope per element
# of each blob's `records`. The user's own Entra token goes in on stdin, never on a command line or into the cluster's disk.
# usage: flowlogs.sh <outdir>
set -euo pipefail
source "$(dirname "$0")/../lib.sh"; load_context
out="$1/vnet-flow-logs"; mkdir -p "$out"
tmp=$(mktemp -d)
pod=flowlog-reader; ns=sattle-demo
trap 'kubectl -n $ns delete pod $pod --ignore-not-found --wait=false >/dev/null 2>&1; rm -rf "$tmp"' EXIT

kubectl apply -f "$CAP_DIR/flowlog-reader.yaml" >/dev/null
kubectl -n "$ns" wait --for=condition=Ready "pod/$pod" --timeout=180s >/dev/null

# runs inside the pod: $1 is the storage account, the token arrives on stdin
read -r -d '' remote <<'SH' || true
read -r T
A="$1"; C=insights-logs-flowlogflowevent; V="x-ms-version: 2023-11-03"; marker=""
while :; do
  xml=$(curl -fsS -H "Authorization: Bearer $T" -H "$V" "https://$A.blob.core.windows.net/$C?restype=container&comp=list&maxresults=500${marker:+&marker=$marker}") || exit 2
  echo "$xml" | grep -o '<Name>[^<]*</Name>' | sed 's/<[^>]*>//g' > /tmp/names
  while read -r n; do
    printf '#BLOB %s\n' "$n"
    curl -fsS -H "Authorization: Bearer $T" -H "$V" "https://$A.blob.core.windows.net/$C/$n"
    printf '\n#END\n'
  done < /tmp/names
  marker=$(echo "$xml" | sed -n 's/.*<NextMarker>\([^<]*\)<\/NextMarker>.*/\1/p')
  [ -n "$marker" ] || break
done
SH

az account get-access-token --resource https://storage.azure.com/ --query accessToken -o tsv \
  | kubectl -n "$ns" exec -i "$pod" -- sh -c "$remote" sh "$FLOW_STORAGE" \
  | awk -v dir="$tmp" '
      /^#BLOB /  { n++; base = sprintf("%s/%06d", dir, n); print substr($0, 7) > (base ".name"); close(base ".name"); f = base ".json"; next }
      /^#END$/   { close(f); next }
      { print > f }'

for f in "$tmp"/*.json; do
  [ -e "$f" ] || { echo "no flow log blobs found" >&2; exit 1; }
  blob=$(cat "${f%.json}.name")
  # structural split only: each element of `records` is one native record
  jq -c '.records[]' "$f" | wrap --source vnet-flow-log --meta "$(jq -nc --arg b "$blob" '{blob:$b}')" --out "$out/records.ndjson" \
    || echo "skip unreadable blob $blob" >&2
done
