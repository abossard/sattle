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

azd env set SATTLE_AKS_RG "$aks_rg"
azd env set SATTLE_AKS_NAME "$aks_name"
azd env set SATTLE_AKS_ID "$aks_id"
azd env set SATTLE_NODE_RG "$node_rg"
azd env set SATTLE_AKS_VNET_ID "$vnet_id"
azd env set SATTLE_AKS_SUBNET_ID "$subnet_id"
azd env set AZURE_PRINCIPAL_ID "$principal"
