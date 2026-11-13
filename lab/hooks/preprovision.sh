#!/bin/sh
# Looks up what Bicep cannot: the AKS-managed VNet and its node subnet. Everything is written to the azd environment.
# A lookup that fails or finds nothing stops the hook, so no empty value reaches the azd environment.
set -eu
aks_rg="${SATTLE_AKS_RG:?set it once: azd env set SATTLE_AKS_RG <resource group of the AKS cluster>}"
aks_name="${SATTLE_AKS_NAME:?set it once: azd env set SATTLE_AKS_NAME <AKS cluster name>}"

# lookup <what> <command...>: prints the command's answer; fails when the command fails or answers nothing
lookup() {
  what=$1; shift
  answer=$("$@") || { echo "preprovision: $what: the lookup failed" >&2; exit 1; }
  [ -n "$answer" ] || { echo "preprovision: $what: nothing found" >&2; exit 1; }
  printf '%s' "$answer"
}

node_rg=$(lookup "node resource group of the cluster" az aks show -g "$aks_rg" -n "$aks_name" --query nodeResourceGroup -o tsv)
vnet=$(lookup "VNet in the node resource group" az network vnet list -g "$node_rg" --query '[0].name' -o tsv)
aks_id=$(lookup "id of the cluster" az aks show -g "$aks_rg" -n "$aks_name" --query id -o tsv)
vnet_id=$(lookup "id of the VNet" az network vnet show -g "$node_rg" -n "$vnet" --query id -o tsv)
subnet_id=$(lookup "id of the node subnet" az network vnet subnet show -g "$node_rg" --vnet-name "$vnet" -n aks-subnet --query id -o tsv)
principal="${AZURE_PRINCIPAL_ID:-}"
[ -n "$principal" ] || principal=$(lookup "id of the signed-in user" az ad signed-in-user show --query id -o tsv)

# azd down deletes the lab's resource group, and the hooks own one Activity Log setting per subscription. Stop before
# anything is written when either already belongs to someone else.
lab_rg="${SATTLE_LAB_RG:-rg-sattle-lab}"
env_name="${AZURE_ENV_NAME:?missing AZURE_ENV_NAME (azd sets it for hooks)}"
lower() { printf '%s' "$1" | tr '[:upper:]' '[:lower:]'; }
# assigned first: a lookup that fails inside `[ ]` would not stop the hook (set -e does not see it there)
exists=$(lookup "whether resource group $lab_rg exists" az group exists -n "$lab_rg")
if [ "$exists" = true ]; then
  owner=$(az group show -n "$lab_rg" --query 'tags."azd-env-name"' -o tsv) || { echo "preprovision: reading the tags of resource group $lab_rg failed" >&2; exit 1; }
  if [ "$owner" != "$env_name" ]; then
    echo "preprovision: resource group $lab_rg exists and was not created by this azd environment ($env_name)." >&2
    echo "azd down deletes the lab's group. Choose another one: azd env set SATTLE_LAB_RG <new group name>" >&2
    exit 1
  fi
fi
rule=$(az monitor diagnostic-settings subscription show --name sattle-activity-to-eventhub --query eventHubAuthorizationRuleId -o tsv 2>/dev/null) || rule=""
case "$(lower "$rule")" in
  ""|none|*"/resourcegroups/$(lower "$lab_rg")/"*) ;;
  *)
    other=$(printf '%s' "$rule" | sed -n 's|.*/[Rr]esource[Gg]roups/\([^/]*\)/.*|\1|p')
    echo "preprovision: the subscription's Activity Log setting sattle-activity-to-eventhub already feeds another lab (resource group $other)." >&2
    echo "One lab per subscription: this deployment would take the setting over and either teardown would remove it. Run azd down there first." >&2
    exit 1 ;;
esac

azd env set SATTLE_AKS_RG "$aks_rg"
azd env set SATTLE_AKS_NAME "$aks_name"
azd env set SATTLE_AKS_ID "$aks_id"
azd env set SATTLE_NODE_RG "$node_rg"
azd env set SATTLE_AKS_VNET_ID "$vnet_id"
azd env set SATTLE_AKS_SUBNET_ID "$subnet_id"
azd env set AZURE_PRINCIPAL_ID "$principal"
