#!/usr/bin/env bash
# One engine replay per cut of a prepared checkpoint (checkpoint.py prepare), the clock stopped at the cut:
#   lab/health-model/replay-cuts.sh OUT [SATTLE_DLL]   then   python3 lab/health-model/checkpoint.py compare OUT
set -euo pipefail
out=$1
dll=${2:-engine/src/Sattle.Cli/bin/Release/net11.0/sattle.dll}
defs=$(dirname "$0")/definitions
while read -r cut; do
  dotnet "$dll" replay "$out/capture" --defs "$defs" --space healthModel --advance-every 1s --lateness 0s --no-flush \
    --until "$cut" --out "$out/replays/$cut" > "$out/replays.log" 2>&1 || { tail -20 "$out/replays.log"; exit 1; }
done < "$out/cuts.txt"
echo "$(wc -l < "$out/cuts.txt" | tr -d ' ') replays"
