#!/usr/bin/env bash
# Rebuild sanitized fixtures from a capture and verify them. usage: fixtures/build.sh <capture dir>
# Tenant, subscription and the signed-in user are scrubbed as literals; they are read from az and never written down.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
capture="$(cd "$1" && pwd)"
raw=$( {
  az account show --query '[id, tenantId, name, user.name]' -o json | jq -r '.[]'
  az ad signed-in-user show --query '[displayName, userPrincipalName, mail, id]' -o json | jq -r '.[] | select(. != null)'
} )
# the cluster and its resource group come from the azd environment (the scrubber already finds their full names in
# ARM ids); the names without the short type prefix (rg-, aks-) are added because labels and annotations use them alone
eval "$(cd "$here/.." && azd env get-values)"
raw=$( { echo "$raw"; for n in "${SATTLE_AKS_NAME:?}" "${SATTLE_AKS_RG:?}"; do echo "${n#*-}"; done; } )
# every address also contributes its local part (before @, and before _ for guest-style UPNs)
SCRUB_LITERALS=$( { echo "$raw"; echo "$raw" | grep '@' | while read -r l; do echo "${l%%@*}"; echo "${l%%_*}"; done; } | sort -u )
export SCRUB_LITERALS
rm -rf "$here"/arg "$here"/kubernetes "$here"/hubble "$here"/prometheus "$here"/azure-metrics "$here"/activity-log "$here"/vnet-flow-logs "$here"/eventhub
node "$here/scrub.mjs" build "$capture" "$here"
node "$here/scrub.mjs" verify "$capture" "$here"
