// Sattle lab, azd project. This deployment owns exactly one resource group and everything in it.
// Additions to existing resources (diagnostic settings, flow log, ACNS, demo workload) are made in azd hooks:
// azd down deletes the resource groups of a deployment, so none of them may belong to this template.
targetScope = 'subscription'

@description('azd environment name, tagged on the resource group.')
param environmentName string
param location string
param labResourceGroup string = 'rg-sattle-lab'

@description('Object id of the user that gets Event Hubs data sender and receiver roles and blob read access.')
param principalId string

@description('Id of the AKS-managed VNet and of the node subnet. Used only as references by the private endpoint.')
param aksVnetId string
param aksSubnetId string

param flowLogRetentionDays int = 3

var tags = { project: 'sattle', 'azd-env-name': environmentName }
var suffix = take(uniqueString(subscription().id, labResourceGroup), 6)

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: labResourceGroup
  location: location
  tags: tags
}

module lab 'modules/lab.bicep' = {
  scope: rg
  name: 'sattle-lab-resources'
  params: {
    location: location
    tags: tags
    suffix: suffix
    principalId: principalId
    flowLogRetentionDays: flowLogRetentionDays
  }
}

module flowReader 'modules/flow-reader.bicep' = {
  scope: rg
  name: 'sattle-lab-flow-reader'
  params: {
    location: location
    tags: tags
    suffix: suffix
    vnetId: aksVnetId
    subnetId: aksSubnetId
    storageId: lab.outputs.flowLogStorageId
  }
}

output AZURE_RESOURCE_GROUP string = rg.name
output SATTLE_EVENTHUB_NAMESPACE string = lab.outputs.namespaceName
output SATTLE_EVENTHUB_RULE_ID string = lab.outputs.authorizationRuleId
output SATTLE_FLOWLOG_STORAGE string = lab.outputs.flowLogStorageName
output SATTLE_FLOWLOG_STORAGE_ID string = lab.outputs.flowLogStorageId
