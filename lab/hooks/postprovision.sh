#!/bin/sh
# Additions to existing resources and the demo workload. All commands are upserts, so rerunning is safe.
# hooks/postdown.sh removes each of them.
set -eu
export KUBECONFIG="$PWD/.kube/config"   # dedicated kubeconfig, the user's context is never touched

# Activity Log of the subscription to its hub
cats='[{"category":"Administrative","enabled":true},{"category":"Security","enabled":true},{"category":"ServiceHealth","enabled":true},{"category":"Alert","enabled":true},{"category":"Recommendation","enabled":true},{"category":"Policy","enabled":true},{"category":"Autoscale","enabled":true},{"category":"ResourceHealth","enabled":true}]'
az monitor diagnostic-settings subscription create --name sattle-activity-to-eventhub --location "$AZURE_LOCATION" \
  --event-hub-auth-rule "$SATTLE_EVENTHUB_RULE_ID" --event-hub-name sattle-activity --logs "$cats" -o none

# AKS audit and autoscaler logs to their hub
az monitor diagnostic-settings create --name sattle-aks-to-eventhub --resource "$SATTLE_AKS_ID" \
  --event-hub-rule "$SATTLE_EVENTHUB_RULE_ID" --event-hub sattle-aks \
  --logs '[{"category":"kube-audit-admin","enabled":true},{"category":"cluster-autoscaler","enabled":true}]' -o none

# VNet flow log of the AKS VNet (the Network Watcher lives in NetworkWatcherRG)
az network watcher flow-log create --location "$AZURE_LOCATION" --name fl-sattle-aks-vnet \
  --vnet "$SATTLE_AKS_VNET_ID" --storage-account "$SATTLE_FLOWLOG_STORAGE_ID" \
  --format JSON --log-version 2 --retention 3 --enabled true --tags project=sattle -o none

# ACNS observability gives Hubble relay. Remember whether the lab switched it on, so down only undoes its own change.
if [ "$(az aks show -g "$SATTLE_AKS_RG" -n "$SATTLE_AKS_NAME" --query 'networkProfile.advancedNetworking.enabled' -o tsv)" != "true" ]; then
  az aks update -g "$SATTLE_AKS_RG" -n "$SATTLE_AKS_NAME" --enable-acns --disable-acns-security -o none
  azd env set SATTLE_ACNS_BY_LAB true
fi

az aks get-credentials -g "$SATTLE_AKS_RG" -n "$SATTLE_AKS_NAME" --overwrite-existing >/dev/null
kubectl apply -f demo/sattle-demo.yaml
