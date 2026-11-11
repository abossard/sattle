#!/bin/sh
# Undoes postprovision after azd down removed the resource group. Every step tolerates "already gone".
set -u
export KUBECONFIG="$PWD/.kube/config"
aks_rg="${SATTLE_AKS_RG:?set it once: azd env set SATTLE_AKS_RG <resource group of the AKS cluster>}"
aks_name="${SATTLE_AKS_NAME:?set it once: azd env set SATTLE_AKS_NAME <AKS cluster name>}"

az monitor diagnostic-settings subscription delete --name sattle-activity-to-eventhub --yes
az monitor diagnostic-settings delete --name sattle-aks-to-eventhub --resource "${SATTLE_AKS_ID:?}"
az network watcher flow-log delete --location "${AZURE_LOCATION:?}" --name fl-sattle-aks-vnet
kubectl delete namespace sattle-demo --ignore-not-found --timeout=180s
if [ "${SATTLE_ACNS_BY_LAB:-false}" = true ]; then
  az aks update -g "$aks_rg" -n "$aks_name" --disable-acns -o none && azd env set SATTLE_ACNS_BY_LAB false
fi
exit 0
