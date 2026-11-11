# Sourced by capture scripts. Settings come from the azd environment of lab/ (`azd env get-values`);
# nothing is stored in files here.
CAP_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LAB_DIR="$(dirname "$CAP_DIR")"

export KUBECONFIG="$LAB_DIR/.kube/config"   # dedicated kubeconfig, the user's context is never touched

wrap() { node "$CAP_DIR/wrap.mjs" "$@"; }

# Exports SUB, AKS_RG, AKS_NAME, AKS_ID, NODE_RG, LAB_RG, EH_NS, FLOW_STORAGE from the azd environment.
# Values already in the environment win, so the forwarder's child scripts do not call azd again.
load_context() {
  if [ -z "${SATTLE_EVENTHUB_NAMESPACE:-}" ]; then
    set -a; eval "$(cd "$LAB_DIR" && azd env get-values)"; set +a
  fi
  SUB="$AZURE_SUBSCRIPTION_ID"; AKS_RG="$SATTLE_AKS_RG"; AKS_NAME="$SATTLE_AKS_NAME"; AKS_ID="$SATTLE_AKS_ID"
  NODE_RG="$SATTLE_NODE_RG"; LAB_RG="$AZURE_RESOURCE_GROUP"; EH_NS="$SATTLE_EVENTHUB_NAMESPACE"; FLOW_STORAGE="$SATTLE_FLOWLOG_STORAGE"
  export SUB AKS_RG AKS_NAME AKS_ID NODE_RG LAB_RG EH_NS FLOW_STORAGE
}

# Starts a relay port-forward on a free local port and exports HUBBLE_ARGS (server + mTLS flags).
# Several relays can run side by side. Cleaned by stop_relay.
start_relay() {
  RELAY_DIR=$(mktemp -d)
  chmod 700 "$RELAY_DIR"
  for s in ca.crt tls.crt tls.key; do
    kubectl -n kube-system get secret hubble-relay-client-certs -o json | jq -r --arg k "${s//./\\.}" '.data[$k|gsub("\\\\";"")] | @base64d' > "$RELAY_DIR/$s"
  done
  kubectl -n kube-system port-forward svc/hubble-relay :443 >"$RELAY_DIR/pf.log" 2>&1 &
  RELAY_PID=$!
  local port=""
  for _ in $(seq 1 30); do
    port=$(sed -n 's/^Forwarding from 127.0.0.1:\([0-9]*\) .*/\1/p' "$RELAY_DIR/pf.log" | head -1)
    [ -n "$port" ] && break
    sleep 0.5
  done
  [ -n "$port" ] || { echo "relay port-forward did not start" >&2; return 1; }
  export HUBBLE_ARGS="--port-forward=false --server localhost:$port --tls --tls-server-name instance.hubble-relay.cilium.io --tls-ca-cert-files $RELAY_DIR/ca.crt --tls-client-cert-file $RELAY_DIR/tls.crt --tls-client-key-file $RELAY_DIR/tls.key"
}
stop_relay() { [ -n "${RELAY_PID:-}" ] && kill "$RELAY_PID" 2>/dev/null; [ -n "${RELAY_DIR:-}" ] && rm -rf "$RELAY_DIR"; return 0; }
