#!/usr/bin/env bash
# Generates Kubernetes change events during a capture: restarts and scales the demo workloads. usage: churn.sh [rounds]
set -euo pipefail
export KUBECONFIG="$(cd "$(dirname "$0")/.." && pwd)/.kube/config"
for i in $(seq 1 "${1:-3}"); do
  kubectl -n sattle-demo rollout restart deploy/api >/dev/null
  sleep 45
  kubectl -n sattle-demo scale deploy/frontend --replicas=$(( (i % 2) + 2 )) >/dev/null
  sleep 45
  kubectl -n sattle-demo rollout restart deploy/frontend >/dev/null
  sleep 45
done
