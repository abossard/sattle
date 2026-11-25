#!/usr/bin/env bash
# Live demo (#96): the engine reads the lab's Event Hub, the forwarder streams the AKS cluster into it, the canvas shows it.
# usage: scripts/demo-live.sh [--port 5080] [--canvas-port 5173] [--state DIR]
# Needs az login, the azd env in lab/, lab/.kube/config, the Release build and the canvas packages.
# The local space binding lab/captures/acme.space.module.gkql.json (gitignored) binds the space to the demo namespace.
# The state (journal, recording) is kept in DIR (default tmp/live-demo): a second start resumes where the first stopped.
# Ctrl-C stops the canvas, the forwarder and the engine (which prints its state digest).
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
port=5080
canvas_port=5173
state="$root/tmp/live-demo"
while [ $# -gt 0 ]; do
  case "$1" in
    --port) port="$2"; shift 2 ;;
    --canvas-port) canvas_port="$2"; shift 2 ;;
    --state) state="$2"; shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

for tool in jq curl lsof kubectl azd az node dotnet; do
  command -v "$tool" >/dev/null 2>&1 || { echo "$tool is not on the PATH" >&2; exit 1; }
done
dll="$root/lab/sattle-lab/bin/Release/net11.0/sattle-lab.dll"   # the engine with the lab's Event Hubs input
kubeconfig="$root/lab/.kube/config"
overlay="$root/lab/captures/acme.space.module.gkql.json"
[ -f "$dll" ] || { echo "no lab build at $dll: run dotnet build lab/sattle-lab -c Release" >&2; exit 1; }
[ -d "$root/canvas/node_modules" ] || { echo "no canvas packages: run npm --prefix canvas install" >&2; exit 1; }
[ -d "$root/lab/capture/node_modules" ] || { echo "no forwarder packages: run npm --prefix lab/capture install" >&2; exit 1; }
az account show >/dev/null 2>&1 || { echo "not logged in to Azure: run az login" >&2; exit 1; }
[ -f "$kubeconfig" ] || { echo "no $kubeconfig: see lab/README.md, Deploy" >&2; exit 1; }
KUBECONFIG="$kubeconfig" kubectl get namespace sattle-demo >/dev/null 2>&1 || { echo "the cluster in $kubeconfig does not answer, or has no namespace sattle-demo" >&2; exit 1; }
ns="$(cd "$root/lab" && azd env get-value SATTLE_EVENTHUB_NAMESPACE 2>/dev/null)" || { echo "no Event Hubs namespace in the azd env of lab/" >&2; exit 1; }
pgrep -f "node .*eh-forward.mjs" >/dev/null && { echo "a forwarder is already running (pkill -TERM -f eh-forward.mjs): two would send everything twice" >&2; exit 1; }
for p in "$port" "$canvas_port"; do
  if lsof -nP -iTCP:"$p" -sTCP:LISTEN >/dev/null 2>&1; then echo "port $p is in use: pick another with --port or --canvas-port" >&2; exit 1; fi
done
overlay_args=()
if [ -f "$overlay" ]; then overlay_args=(--overlay "$overlay"); else echo "no $overlay: the space keeps its tracked binding" >&2; fi

mkdir -p "$state"
logs="$(mktemp -d "${TMPDIR:-/tmp}/sattle-live.XXXXXX")"
engine_pid="" forwarder_pid="" canvas_pid=""
stop() {
  [ -n "$canvas_pid" ] && kill "$canvas_pid" 2>/dev/null || true
  [ -n "$forwarder_pid" ] && kill "$forwarder_pid" 2>/dev/null || true
  [ -n "$engine_pid" ] && kill "$engine_pid" 2>/dev/null || true
  wait 2>/dev/null || true
  grep -h "state digest" "$logs/engine.log" 2>/dev/null || true
  echo "stopped; logs in $logs, state in $state"
}
trap stop EXIT
trap 'exit 130' INT TERM

# wait_for LOG PATTERN PID SECONDS: until the log has the pattern, or fail when the process ends or time runs out
wait_for() {
  local i=0
  until grep -q "$2" "$1" 2>/dev/null; do
    kill -0 "$3" 2>/dev/null || { echo "it stopped; last lines of $1:" >&2; tail -5 "$1" >&2; exit 1; }
    i=$((i + 1)); [ "$i" -le $(( $4 * 2 )) ] || { echo "no '$2' in $1 after $4 s" >&2; exit 1; }
    sleep 0.5
  done
}
api() { curl -s "http://127.0.0.1:$port$1"; }
# until_api PATH JQ SECONDS: until the engine's answer at PATH passes the jq test (status 1 when time runs out)
until_api() {
  local i=0
  until api "$1" | jq -e "$2" >/dev/null 2>&1; do
    kill -0 "$engine_pid" 2>/dev/null || { echo "the engine stopped; last lines:" >&2; tail -5 "$logs/engine.log" >&2; exit 1; }
    i=$((i + 1)); [ "$i" -le $(( $3 / 2 )) ] || { echo "    no answer to $1 passing $2 after $3 s" >&2; return 1; }
    sleep 2
  done
}

echo "1/5 engine: reads hub sattle-ingest from its saved position, else from now (log $logs/engine.log)"
dotnet "$dll" serve --live --eh-namespace "$ns" --group sattle-engine --from latest ${overlay_args[@]+"${overlay_args[@]}"} \
  --store "sqlite=$state/space.db" --live-out "$state" --port "$port" > "$logs/engine.log" 2>&1 &
engine_pid=$!
wait_for "$logs/engine.log" "^live:" "$engine_pid" 120
echo "    serving on port $port; $(grep -m1 '^live:' "$logs/engine.log" | sed 's/^live: //')"

echo "2/5 forwarder: relists the cluster, then watches it, follows Hubble and polls metrics (log $logs/forwarder.log)"
(cd "$root/lab/capture" && exec node eh-forward.mjs --namespace "$ns" --kubeconfig "$kubeconfig" --authoring ../demo/signal-rules.ndjson) > "$logs/forwarder.log" 2>&1 &
forwarder_pid=$!
wait_for "$logs/forwarder.log" '"event":"relist-end","path":"/apis/apps/v1/deployments"' "$forwarder_pid" 120
echo "    relist sent: $(grep -c '"event":"relist-end"' "$logs/forwarder.log") kinds"

echo "3/5 first envelopes in the engine"
until_api /graphs/deploymentHealth/snapshot '.extent.entities | length > 0' 120 || exit 1
echo "    $(api /space | jq -r '.input.events') envelopes; deploymentHealth has $(api /graphs/deploymentHealth/snapshot | jq '.extent.entities | length') deployments"

echo "4/5 canvas: on port $canvas_port (log $logs/canvas.log)"
(cd "$root/canvas" && ENGINE_PORT="$port" exec ./node_modules/.bin/vite --port "$canvas_port" --strictPort) > "$logs/canvas.log" 2>&1 &
canvas_pid=$!
wait_for "$logs/canvas.log" "ready in" "$canvas_pid" 60
base="http://localhost:$canvas_port"

echo "5/5 states: the signal rules need one to two windows of 60 s"
until_api /graphs/workload/snapshot '[.extent.entities[].properties[] | select(.property.value | endswith(".state")) | .value.value] | any(. == "Healthy" or . == "Unhealthy")' 300 || true
echo "    lag $(api /space | jq -r '"\(.input.lag.events) events, \(.input.lag.seconds) s"') behind the hub"
cat <<EOF
    $base/#source=engine&view=data
      Graph box: workload, Tree tab, switch "Only where something is wrong": the cause path from the workload
      down to the part that is wrong (the intruder: its calls to db and api are dropped by network policies).
      deploymentHealth: every deployment of the cluster with its ready pods. master: what the cluster reported.
    Change the cluster in another terminal (lab/DEMO-LIVE.md, step 4), for example:
      KUBECONFIG=lab/.kube/config kubectl -n sattle-demo scale deploy/intruder --replicas=0
    Ctrl-C stops all three.
EOF
while kill -0 "$engine_pid" 2>/dev/null; do
  sleep 60
  input="$(api /space | jq -c '.input' 2>/dev/null || true)"
  if [ -z "$input" ]; then echo "    $(date -u +%H:%M:%SZ) the engine does not answer on port $port" >&2
  elif [ "$(jq -r '.retrying // empty' <<<"$input")" != "" ]; then echo "    $(date -u +%H:%M:%SZ) the input failed and is reading again: $(jq -r '.retrying' <<<"$input")" >&2
  else echo "    $(date -u +%H:%M:%SZ) lag $(jq -r '"\(.lag.events) events, \(.lag.seconds) s"' <<<"$input")"; fi
  kill -0 "$forwarder_pid" 2>/dev/null || { echo "the forwarder stopped; last lines:" >&2; tail -5 "$logs/forwarder.log" >&2; exit 1; }
done
