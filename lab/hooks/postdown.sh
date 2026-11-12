#!/bin/sh
# Undoes postprovision after azd down removed the resource group. A step whose target is already gone counts as done;
# any other failure (an expired login, no permission) is reported and makes the hook fail, so azd down never claims
# success while a billed feature or a diagnostic setting stays. Every step is tried either way.
set -u
export KUBECONFIG="$PWD/.kube/config"
aks_rg="${SATTLE_AKS_RG:?set it once: azd env set SATTLE_AKS_RG <resource group of the AKS cluster>}"
aks_name="${SATTLE_AKS_NAME:?set it once: azd env set SATTLE_AKS_NAME <AKS cluster name>}"
aks_id="${SATTLE_AKS_ID:?missing SATTLE_AKS_ID in the azd environment}"
location="${AZURE_LOCATION:?missing AZURE_LOCATION in the azd environment}"

failed=0
# step <what> <command...>
step() {
  what=$1; shift
  if err=$("$@" 2>&1 >/dev/null); then return 0; fi
  case "$err" in
    *NotFound*|*"not found"*|*"was not found"*|*"could not be found"*|*"does not exist"*|*"doesn't exist"*|*"no such host"*) return 0 ;; # the cluster itself is gone
  esac
  echo "postdown: $what failed: $err" >&2
  failed=1
  return 1
}

step "delete the Activity Log diagnostic setting" az monitor diagnostic-settings subscription delete --name sattle-activity-to-eventhub --yes
step "delete the AKS diagnostic setting" az monitor diagnostic-settings delete --name sattle-aks-to-eventhub --resource "$aks_id"
step "delete the VNet flow log" az network watcher flow-log delete --location "$location" --name fl-sattle-aks-vnet
step "delete namespace sattle-demo" kubectl delete namespace sattle-demo --ignore-not-found --timeout=180s
if [ "${SATTLE_ACNS_BY_LAB:-false}" = true ]; then
  step "disable ACNS" az aks update -g "$aks_rg" -n "$aks_name" --disable-acns -o none && azd env set SATTLE_ACNS_BY_LAB false
fi
exit "$failed"
