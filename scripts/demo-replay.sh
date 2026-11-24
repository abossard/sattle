#!/usr/bin/env bash
# Replay demo (#96): the engine replays the synthetic estate at 20 times its speed and the canvas shows it. Works offline.
# usage: scripts/demo-replay.sh [--port 5080] [--canvas-port 5173] [--speed 20]
# Needs the Release build (dotnet build engine/src/Sattle.Cli -c Release) and the canvas packages (npm --prefix canvas install).
# Ctrl-C stops the engine and the canvas.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
port=5080
canvas_port=5173
speed=20
while [ $# -gt 0 ]; do
  case "$1" in
    --port) port="$2"; shift 2 ;;
    --canvas-port) canvas_port="$2"; shift 2 ;;
    --speed) speed="$2"; shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done
case "$speed" in ''|*[!0-9]*|0*) echo "--speed must be a whole number of 1 or more, not '$speed'" >&2; exit 2 ;; esac

dll="$root/engine/src/Sattle.Cli/bin/Release/net11.0/sattle.dll"
[ -f "$dll" ] || { echo "no engine build at $dll: run dotnet build engine/src/Sattle.Cli -c Release" >&2; exit 1; }
[ -d "$root/canvas/node_modules" ] || { echo "no canvas packages: run npm --prefix canvas install" >&2; exit 1; }
for p in "$port" "$canvas_port"; do
  if lsof -nP -iTCP:"$p" -sTCP:LISTEN >/dev/null 2>&1; then echo "port $p is in use: pick another with --port or --canvas-port" >&2; exit 1; fi
done

logs="$(mktemp -d "${TMPDIR:-/tmp}/sattle-demo.XXXXXX")"
engine_pid="" canvas_pid=""
stop() {
  [ -n "$canvas_pid" ] && kill "$canvas_pid" 2>/dev/null || true
  [ -n "$engine_pid" ] && kill "$engine_pid" 2>/dev/null || true
  wait 2>/dev/null || true
  echo "stopped; logs in $logs"
}
trap stop EXIT
trap 'exit 130' INT TERM

# wait_for LOG PATTERN PID SECONDS: until the log has the pattern, or fail when the process ends; after SECONDS it warns once and keeps waiting
wait_for() {
  local i=0
  until grep -q "$2" "$1" 2>/dev/null; do
    kill -0 "$3" 2>/dev/null || { echo "it stopped; last lines of $1:" >&2; tail -5 "$1" >&2; exit 1; }
    i=$((i + 1)); [ "$i" -ne $(( $4 * 2 )) ] || echo "    still no '$2' in $1 after $4 s; waiting on (Ctrl-C stops)" >&2
    sleep 0.5
  done
}

echo "1/4 engine: serving the estate on port $port at speed $speed (log $logs/engine.log)"
dotnet "$dll" serve "$root/engine/tests/Sattle.Tests/fixtures/estate" --port "$port" --speed "$speed" > "$logs/engine.log" 2>&1 &
engine_pid=$!
wait_for "$logs/engine.log" "serving http" "$engine_pid" 90
echo "    serving; the replay of 24 minutes takes about $(( 24 * 60 / speed )) s"

echo "2/4 canvas: on port $canvas_port (log $logs/canvas.log)"
(cd "$root/canvas" && ENGINE_PORT="$port" exec ./node_modules/.bin/vite --port "$canvas_port" --strictPort) > "$logs/canvas.log" 2>&1 &
canvas_pid=$!
wait_for "$logs/canvas.log" "ready in" "$canvas_pid" 60
base="http://localhost:$canvas_port"

cat <<EOF
3/4 what to look at
    $base/#source=engine&view=data
      Graph box: master (everything reported, one entity per thing), serviceTree (services and their calls),
      workloadStructure (what the workload is made of and depends on), workload (its health).
      workload, Graph tab: the cache is overloaded at 10:05, the database server down at 10:11, the storage
      account stops reporting at the end. Drag the timeline under the graph.
      Timeline at 10:11 (revision 11), Tree tab: pg-shop carries the origin mark; every row above says through
      which part it is affected. Click the row: the Inspector shows it in master and workloadStructure.
    $base/#view=space      every graph side by side
    $base/#view=ontology   the ontology modules as graphs
    $base/#view=editor     the KQL of a projection (pick acme.workload: workloadHealth), its steps and wires
EOF

wait_for "$logs/engine.log" "replay finished" "$engine_pid" $(( 24 * 60 / speed + 120 ))
echo "4/4 replay finished: $(grep -o 'state digest sha256:[0-9a-f]*' "$logs/engine.log" | tail -1)"
echo "    the engine keeps serving; Ctrl-C stops both"
wait "$engine_pid"
