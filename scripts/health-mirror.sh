#!/usr/bin/env bash
set -euo pipefail
umask 077
root="$(cd "$(dirname "$0")/.." && pwd)"
mirror="$root/lab/health-mirror"
fixture="${MIRROR_FIXTURE:-$mirror/fixture}"
work="${MIRROR_WORK:-$mirror/state}"
dll="$root/engine/src/Sattle.Cli/bin/Release/net11.0/sattle.dll"
port="${HEALTH_ENGINE_PORT:-5083}"
key="$work/flow-alias.key"

# The same local alias key each time, so addresses and names stay joinable between captures.
alias_key() {
  python3 -c 'import pathlib,secrets,sys
try:
 with pathlib.Path(sys.argv[1]).open("x") as file: file.write(secrets.token_hex(32))
except FileExistsError: pass' "$key"
}

case "${1:-help}" in
  capture)
    : "${2:?Pass the Azure health model ARM resource ID}"
    mkdir -p "$fixture"
    mkdir -p "$fixture/raw"
    private="$(mktemp -d "$fixture/raw/checkpoint.XXXXXX")"
    python3 -B "$mirror/export-model.py" --model-id "$2" --raw-out "$private" --out "$fixture"
    python3 -B "$mirror/collect-observations.py" "$private" --output "$fixture/observations.ndjson"
    ;;
  inventory)
    : "${2:?Pass a resource group or model ARM resource ID}"
    mkdir -p "$fixture"
    exec python3 -B "$mirror/collect-inventory.py" "$2" --output "$fixture/container-apps.ndjson"
    ;;
  curate-flows)
    shift
    exec python3 -B "$mirror/curate-flows.py" --fixture "$fixture" --template "$mirror/projects/ahm-model-demo/flows.json" "$@"
    ;;
  watch)
    shift
    exec python3 -B "$mirror/watch.py" --fixture "$fixture" --work "$work" "$@"
    ;;
  compare-discovery)
    shift
    exec python3 -B "$mirror/compare-discovery.py" --fixture "$fixture" --work "$work" --cli "$dll" "$@"
    ;;
  verify-rules)
    shift
    exec python3 -B "$mirror/verify_workloads.py" --fixture "$fixture" --work "$work" "$@"
    ;;
  verify-telemetry)
    exec python3 -B "$mirror/verify_telemetry.py" --fixture "$fixture" --replay "$work/project/replay"
    ;;
  verify-diagnostics)
    exec python3 -B "$mirror/verify_native.py" --fixture "$fixture" --port "${PROJECT_ENGINE_PORT:-5084}" --out "$work/project/native-proof.json"
    ;;
  capture-network)
    : "${2:?Pass a private raw checkpoint directory from capture}"
    mkdir -p "$work"
    alias_key
    python3 -B "$mirror/collect-network.py" "$2" --hours "${3:-72}" --output "$fixture/dependencies.ndjson" --scrub "$key"
    ;;
  import-flows)
    : "${2:?Pass the VNet ARM resource ID}" "${3:?Pass one or more downloaded flow-log blobs}"
    vnet="$2"
    shift 2
    mkdir -p "$work" "$fixture"
    alias_key
    exec node "$mirror/collect-flow-blobs.mjs" --vnet "$vnet" --output "$fixture/network.ndjson" --scrub-key-file "$key" "$@"
    ;;
  prepare|prepare-project)
    flags=()
    if [[ "$1" = prepare-project ]]; then work="$work/project"; flags+=(--deployment-only); fi
    mkdir -p "$work"
    python3 -B "$mirror/build-space.py" --fixture "$fixture" --out "$work" ${flags[@]+"${flags[@]}"}
    dotnet build "$root/lab/sattle-lab/Sattle.Lab.csproj" -c Release --nologo --disable-build-servers -m:1 -nr:false -p:NuGetAudit=false
    dotnet "$dll" replay "$work/capture" --defs "$work/definitions" --space healthMirror --lateness 0s --out "$work/replay"
    ;;
  verify)
    exec python3 -B "$mirror/verify.py" --fixture "$fixture" --work "$work" --scope "${2:-all}" --negative-controls --report "$work/parity.json"
    ;;
  listen-diagnostics)
    shift
    dest=()
    if [[ "$#" != 0 ]]; then
      [[ "$#" = 2 ]] || { echo 'Pass both the namespace and the metric hub.' >&2; exit 2; }
      dest=(--namespace "$1" --hub "$2")
    fi
    exec python3 -B "$mirror/serve_diagnostics.py" --fixture "$fixture" --work "$work/project" --dll "$dll" --port "${PROJECT_ENGINE_PORT:-5084}" ${dest[@]+"${dest[@]}"}
    ;;
  serve|serve-project|listen)
    if [[ "$1" = serve-project ]]; then work="$work/project"; port="${PROJECT_ENGINE_PORT:-5084}"; fi
    [[ -s "$work/definitions/mirror.module.gkql.json" ]] || { echo 'Run ./scripts/health-mirror.sh prepare first.' >&2; exit 1; }
    # Reuse a user's edited draft when the generated starter is unchanged.
    version="$(cd "$work/definitions" && shasum -a 256 ./*.gkql.json | shasum -a 256 | cut -c1-24)"
    editor="$work/editors/$version"
    mkdir -p "$work/editors"
    if [[ ! -d "$editor" ]]; then cp -R "$work/definitions" "$editor"; fi
    args=(--defs "$editor" --space healthMirror --port "$port" --lateness 0s --write-definitions)
    if [[ "$1" = listen ]]; then
      : "${2:?Pass the dedicated Event Hubs namespace}" "${3:?Pass a dedicated single-partition hub}"
      mkdir -p "$work/live-runs"
      stream="$(mktemp -d "$work/live-runs/run.XXXXXX")"
      lab_dll="$root/lab/sattle-lab/bin/Release/net11.0/sattle-lab.dll"   # the engine with the lab's Event Hubs input
      exec dotnet "$lab_dll" serve --live "${args[@]}" --eh-namespace "$2" --hub "$3" --from "${MIRROR_FROM:-latest}" --live-out "$stream"
    fi
    exec dotnet "$dll" serve "$work/capture" "${args[@]}"
    ;;
  verify-stream)
    exec python3 -B "$mirror/verify-stream.py" --work "$work" --port "$port"
    ;;
  publish)
    : "${2:?Pass the dedicated Event Hubs namespace}" "${3:?Pass a dedicated single-partition hub}"
    exec node "$mirror/publish.mjs" "$2" "$3" "$work/capture"
    ;;
  *)
    cat <<'HELP'
./scripts/health-mirror.sh capture <Azure-model-resource-id>
./scripts/health-mirror.sh capture-network <raw-checkpoint-dir> [hours]
./scripts/health-mirror.sh import-flows <vnet-resource-id> <PT1H.json>...
./scripts/health-mirror.sh inventory <resource-group-id>
./scripts/health-mirror.sh curate-flows [--hours 72] [--offline | --print-kql routes|dependencies]
./scripts/health-mirror.sh prepare-project
./scripts/health-mirror.sh listen-diagnostics [<namespace> <metric-hub>]
./scripts/health-mirror.sh serve-project
./scripts/health-mirror.sh prepare
./scripts/health-mirror.sh verify-rules
./scripts/health-mirror.sh verify-telemetry
./scripts/health-mirror.sh verify-diagnostics
./scripts/health-mirror.sh compare-discovery
./scripts/health-mirror.sh verify [all|steady]
./scripts/health-mirror.sh serve
./scripts/health-mirror.sh listen <namespace> <hub>
./scripts/health-mirror.sh publish <namespace> <hub>
./scripts/health-mirror.sh verify-stream
./scripts/health-mirror.sh watch <resource-group-or-model-id> <namespace> <hub> [--interval 300] [--once]
HELP
    ;;
esac
