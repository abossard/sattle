#!/usr/bin/env bash
# The source cluster the lab reads: AKS with Azure CNI overlay powered by Cilium and managed Prometheus into its own
# Azure Monitor workspace. It is created outside the azd deployment, in its own resource group, so `azd down` never
# deletes it. Names and region come from the azd environment of lab/ (set once, see README); existing resources are
# left as they are, so rerunning is safe. Encryption at rest of Kubernetes objects with platform-managed keys is on
# (required by policy in some tenants, no extra cost). ACNS (Hubble) is switched on later by hooks/postprovision.sh.
# usage: ./source-cluster.sh          cost while it runs: see README, section Cost
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
eval "$(cd "$here" && azd env get-values)"
rg="${SATTLE_AKS_RG:?set it once: azd env set SATTLE_AKS_RG <resource group for the cluster>}"
name="${SATTLE_AKS_NAME:?set it once: azd env set SATTLE_AKS_NAME <cluster name>}"
location="${AZURE_LOCATION:?missing AZURE_LOCATION in the azd environment}"
size="${SATTLE_AKS_NODE_SIZE:-Standard_D4ds_v5}"
workspace="amw-$name"

az group create -n "$rg" -l "$location" --tags project=sattle -o none
if ! az monitor account show -g "$rg" -n "$workspace" -o none 2>/dev/null; then
  az monitor account create -g "$rg" -n "$workspace" -l "$location" --tags project=sattle -o none
fi
workspace_id=$(az monitor account show -g "$rg" -n "$workspace" --query id -o tsv)
if az aks show -g "$rg" -n "$name" -o none 2>/dev/null; then
  echo "cluster $name exists, left as it is"
else
  az aks create -g "$rg" -n "$name" -l "$location" --tier free \
    --node-count 1 --node-vm-size "$size" --nodepool-name systempool \
    --network-plugin azure --network-plugin-mode overlay --network-dataplane cilium \
    --enable-azure-monitor-metrics --azure-monitor-workspace-resource-id "$workspace_id" \
    --kms-infrastructure-encryption Enabled \
    --generate-ssh-keys --tags project=sattle -o none
fi
az aks show -g "$rg" -n "$name" --query '{state:provisioningState, power:powerState.code, nodes:agentPoolProfiles[0].count, dataplane:networkProfile.networkDataplane, prometheus:azureMonitorProfile.metrics.enabled}' -o json
