#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
data="$root/lab/tutorial-fixtures"
state="$root/lab/tutorial-state"
source_defs="$root/engine/tutorial"
local_defs="$state/definitions"
project="$root/engine/src/Sattle.Cli"
dll="$project/bin/Release/net11.0/sattle.dll"

usage() {
  cat <<'EOF'
./scripts/tutorial.sh prepare [capture-directory]
./scripts/tutorial.sh serve
./scripts/tutorial.sh verify
./scripts/tutorial.sh compare-health [entry-windows]
./scripts/tutorial.sh live [event-hubs-namespace]
./scripts/tutorial.sh reset
EOF
}

require_capture() {
  if [[ ! -s "$data/azure-metrics/aks.ndjson" || ! -s "$data/arg/resources.ndjson" ]]; then
    printf 'Run ./scripts/tutorial.sh prepare <capture-directory> first.\n' >&2
    exit 1
  fi
  if [[ ! -d "$data/records" ]]; then convert_capture; fi
}

# The engine reads record envelopes only: the collector turns the native fixtures into records under
# records/. A replay of $data reads those and skips the native files, which the verifiers read for expectations.
convert_capture() {
  rm -rf "$data/records" "$data/.records"
  python3 -B "$root/lab/collector/capture.py" "$data" "$data/.records"
  mv "$data/.records" "$data/records"
}

build_engine() {
  dotnet build -c Release "$project" --nologo --disable-build-servers -m:1 -nr:false >&2
}

copy_definitions() {
  if [[ ! -d "$local_defs" ]]; then
    mkdir -p "$state"
    cp -R "$source_defs" "$local_defs"
  fi
}

latest_capture() {
  find "$root/lab/captures" -mindepth 2 -maxdepth 2 -name SUMMARY.json -print 2>/dev/null | LC_ALL=C sort | tail -n 1
}

live() {
  local namespace="$1" kubeconfig="$root/lab/.kube/config"
  if [[ ! -f "$kubeconfig" || ! -d "$root/lab/capture/node_modules" ]]; then
    printf 'Set up the Azure lab and run npm --prefix lab/capture ci.\n' >&2
    return 1
  fi
  local tool
  for tool in az azd kubectl jq node; do
    if ! command -v "$tool" >/dev/null; then
      printf 'Install %s for the Azure lab.\n' "$tool" >&2
      return 1
    fi
  done
  if ! az account show --only-show-errors >/dev/null 2>&1; then
    printf 'Run az login for the Azure lab.\n' >&2
    return 1
  fi
  build_engine
  copy_definitions
  local log
  log="$(mktemp "$state/live-engine.XXXXXX")"
  dotnet "$dll" serve --live --defs "$local_defs" --space azureTutorial --write-definitions --port 5080 \
    --eh-namespace "$namespace" --group sattle-engine --from latest --live-out "$state/live" >"$log" 2>&1 &
  local engine_pid=$! forwarder_pid='' trial=0
  # shellcheck disable=SC2329 # Called by the EXIT trap.
  stop_live() {
    if [[ -n "$forwarder_pid" ]]; then kill "$forwarder_pid" 2>/dev/null || true; fi
    kill "$engine_pid" 2>/dev/null || true
    wait 2>/dev/null || true
  }
  trap 'stop_live' EXIT
  trap 'exit 130' INT
  trap 'exit 143' TERM
  until grep -q '^live:' "$log"; do
    if ! kill -0 "$engine_pid" 2>/dev/null || [[ "$trial" -ge 120 ]]; then
      printf 'Live engine failed to start. See %s\n' "$log" >&2
      exit 1
    fi
    trial=$((trial + 1))
    sleep 1
  done
  node "$root/lab/capture/eh-forward.mjs" --namespace "$namespace" --kubeconfig "$kubeconfig" &
  forwarder_pid=$!
  printf 'Azure Monitor → Event Hubs → http://localhost:5080\n'
  while kill -0 "$engine_pid" 2>/dev/null && kill -0 "$forwarder_pid" 2>/dev/null; do sleep 1; done
  printf 'Live engine or forwarder stopped. See %s\n' "$log" >&2
  exit 1
}

case "${1:-serve}" in
  prepare)
    capture="${2:-}"
    if [[ -z "$capture" ]]; then
      summary="$(latest_capture || true)"
      capture="${summary%/SUMMARY.json}"
    fi
    if [[ ! -d "$capture" ]]; then
      printf 'Pass an Azure lab capture directory. See lab/README.md.\n' >&2
      exit 1
    fi
    bash "$root/lab/fixtures/build-tutorial.sh" "$capture" "$data"
    convert_capture
    ;;
  serve)
    require_capture
    build_engine
    copy_definitions
    exec dotnet "$dll" serve "$data" --defs "$local_defs" --space azureTutorial --write-definitions --port 5080 --speed 30 --lateness 0s --late-horizon 3
    ;;
  verify)
    require_capture
    build_engine
    work="$(mktemp -d)"
    trap 'rm -rf "$work"' EXIT
    dotnet "$dll" replay "$data" --defs "$source_defs" --space azureTutorial --lateness 0s --late-horizon 3 --out "$work"
    node "$root/scripts/verify-tutorial.mjs" "$data" "$work" "$dll" "$source_defs"
    ;;
  compare-health)
    require_capture
    build_engine
    work="$(mktemp -d)"
    trap 'rm -rf "$work"' EXIT
    node "$root/scripts/compare-tutorial-health.mjs" "$data" "$source_defs" "$dll" "$work" "${2:-4}"
    ;;
  live)
    namespace="${2:-}"
    if [[ -z "$namespace" ]] && ! namespace="$(cd "$root/lab" && azd env get-value SATTLE_EVENTHUB_NAMESPACE)"; then
      printf 'Pass the Event Hubs namespace or configure it in the Azure lab.\n' >&2
      exit 1
    fi
    live "$namespace"
    ;;
  reset)
    if [[ -d "$local_defs" ]]; then
      archive="$(mktemp -d "$state/reset.XXXXXX")"
      mv "$local_defs" "$archive/definitions"
      printf 'Saved edits in %s. Restart the tutorial engine.\n' "$archive"
    else
      printf 'The tutorial definitions are ready for a fresh start.\n'
    fi
    ;;
  -h|--help|help) usage ;;
  *) usage >&2; exit 2 ;;
esac
