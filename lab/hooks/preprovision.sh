#!/bin/sh
# Looks up what Bicep cannot: the AKS-managed VNet and its node subnet. Everything is written to the azd environment.
set -eu
aks_rg="${SATTLE_AKS_RG:?set it once: azd env set SATTLE_AKS_RG <resource group of the AKS cluster>}"
aks_name="${SATTLE_AKS_NAME:?set it once: azd env set SATTLE_AKS_NAME <AKS cluster name>}"

node_rg=$(az aks show -g "$aks_rg" -n "$aks_name" --query nodeResourceGroup -o tsv)
vnet=$(az network vnet list -g "$node_rg" --query '[0].name' -o tsv)

azd env set SATTLE_AKS_RG "$aks_rg"
azd env set SATTLE_AKS_NAME "$aks_name"
azd env set SATTLE_AKS_ID "$(az aks show -g "$aks_rg" -n "$aks_name" --query id -o tsv)"
azd env set SATTLE_NODE_RG "$node_rg"
azd env set SATTLE_AKS_VNET_ID "$(az network vnet show -g "$node_rg" -n "$vnet" --query id -o tsv)"
azd env set SATTLE_AKS_SUBNET_ID "$(az network vnet subnet show -g "$node_rg" --vnet-name "$vnet" -n aks-subnet --query id -o tsv)"
[ -n "${AZURE_PRINCIPAL_ID:-}" ] || azd env set AZURE_PRINCIPAL_ID "$(az ad signed-in-user show --query id -o tsv)"
