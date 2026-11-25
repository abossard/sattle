#!/usr/bin/env bash
# One engine replay per case (cases.py write), the clock stopped at the case's last record; a7 also a second time and at
# 30 s steps:   lab/health-policy/replay.sh [SATTLE_DLL]   then   python3 lab/health-policy/cases.py check
set -euo pipefail
here=$(dirname "$0")
dll=${1:-engine/src/Sattle.Cli/bin/Release/net11.0/sattle.dll}
run() {  # case, run name, step
  local c=$here/cases/$1
  rm -rf "$c/replays/$2"
  dotnet "$dll" replay "$c/capture" --defs "$here/definitions" --space cases --advance-every "$3" --lateness 0s --no-flush \
    --out "$c/replays/$2" > "$c/replays.log" 2>&1 || { echo "$1 $2 failed"; tail -20 "$c/replays.log"; exit 1; }
}
for c in "$here"/cases/*/; do run "$(basename "$c")" 1s 1s; done
run a7 1s-again 1s
run a7 30s 30s
echo "$(ls -d "$here"/cases/*/replays/* | wc -l | tr -d ' ') replays"
