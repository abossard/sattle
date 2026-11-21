#!/usr/bin/env bash
set -euo pipefail
umask 077
root="$(cd "$(dirname "$0")/.." && pwd)"
mirror="$root/lab/health-mirror"
fixture="${MIRROR_FIXTURE:-$mirror/fixture}"
work="${MIRROR_WORK:-$mirror/state}"
dll="$root/engine/src/Sattle.Cli/bin/Release/net11.0/sattle.dll"
port="${HEALTH_ENGINE_PORT:-5083}"

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
  watch)
    shift
    exec python3 -B "$mirror/watch.py" --fixture "$fixture" --work "$work" "$@"
    ;;
  verify-rules)
    exec python3 -B "$mirror/verify_workloads.py" --fixture "$fixture" --work "$work" --portable
    ;;
  capture-network)
    : "${2:?Pass a private raw checkpoint directory from capture}"
    python3 -B "$mirror/collect-network.py" "$2" --hours "${3:-72}" --output "$fixture/dependencies.ndjson"
    ;;
  import-flows)
    : "${2:?Pass the VNet ARM resource ID}" "${3:?Pass one or more downloaded flow-log blobs}"
    vnet="$2"
    shift 2
    exec node "$mirror/collect-flow-blobs.mjs" --vnet "$vnet" --output "$fixture/network.ndjson" "$@"
    ;;
  prepare)
    mkdir -p "$work"
    python3 -B "$mirror/build-space.py" --fixture "$fixture" --out "$work"
    dotnet build "$root/engine/src/Sattle.Cli/Sattle.Cli.csproj" -c Release --nologo --disable-build-servers -m:1 -nr:false -p:NuGetAudit=false
    dotnet "$dll" replay "$work/capture" --defs "$work/definitions" --space healthMirror --lateness 0s --out "$work/replay"
    ;;
  verify)
    exec python3 -B "$mirror/verify.py" --fixture "$fixture" --work "$work" --scope "${2:-all}" --negative-controls --report "$work/parity.json"
    ;;
  serve|listen)
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
      exec dotnet "$dll" serve --live "${args[@]}" --eh-namespace "$2" --hub "$3" --from "${MIRROR_FROM:-latest}" --live-out "$stream"
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
./scripts/health-mirror.sh prepare
./scripts/health-mirror.sh verify-rules
./scripts/health-mirror.sh verify [all|steady]
./scripts/health-mirror.sh serve
./scripts/health-mirror.sh listen <namespace> <hub>
./scripts/health-mirror.sh publish <namespace> <hub>
./scripts/health-mirror.sh verify-stream
./scripts/health-mirror.sh watch <resource-group-or-model-id> <namespace> <hub> [--interval 300] [--once]
HELP
    ;;
esac
