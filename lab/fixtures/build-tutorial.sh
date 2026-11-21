#!/usr/bin/env bash
# usage: bash lab/fixtures/build-tutorial.sh <capture dir> [output dir]
set -euo pipefail
if [[ "$#" -lt 1 || "$#" -gt 2 ]]; then
  echo 'usage: bash lab/fixtures/build-tutorial.sh <capture dir> [output dir]' >&2
  exit 2
fi
here="$(cd "$(dirname "$0")" && pwd)"
capture="$(cd "$1" && pwd)"
out="${2:-$here/../tutorial-fixtures}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

node "$here/scrub.mjs" tutorial "$capture" "$work"
node "$here/scrub.mjs" verify "$capture" "$work"
mkdir -p "$out/arg" "$out/azure-metrics" "$out/kubernetes" "$out/hubble"
cp "$work/arg/resources.ndjson" "$work/arg/resourcecontainers.ndjson" "$out/arg/"
cp "$work/azure-metrics/aks.ndjson" "$work/azure-metrics/other.ndjson" "$out/azure-metrics/"
cp "$work/kubernetes/list.ndjson" "$out/kubernetes/"
cp "$work/hubble/flows.ndjson" "$out/hubble/"
