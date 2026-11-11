param location string
param tags object
param suffix string
param principalId string
param flowLogRetentionDays int

var hubs = {
  activity: { name: 'sattle-activity', partitions: 2 }
  aks: { name: 'sattle-aks', partitions: 2 }
  hubble: { name: 'sattle-hubble', partitions: 2 }
  // One partition: a single totally ordered stream for the whole estate.
  ingest: { name: 'sattle-ingest', partitions: 1 }
}

// Azure Event Hubs Data Sender, Azure Event Hubs Data Receiver
var roleIds = [
  '2b629674-e913-4c01-ae53-ef4638d8f975'
  'a638d3c7-ab3a-418d-83e6-5f17a39d4fde'
]

resource ns 'Microsoft.EventHub/namespaces@2024-01-01' = {
  name: 'evhns-sattle-${suffix}'
  location: location
  tags: tags
  sku: { name: 'Standard', tier: 'Standard', capacity: 1 }
  properties: {
    disableLocalAuth: true
    minimumTlsVersion: '1.2'
    isAutoInflateEnabled: false
    zoneRedundant: true  // Azure enforces it on new Standard namespaces in this region
  }
}

resource hub 'Microsoft.EventHub/namespaces/eventhubs@2024-01-01' = [for h in items(hubs): {
  parent: ns
  name: h.value.name
  properties: {
    partitionCount: h.value.partitions
    retentionDescription: { cleanupPolicy: 'Delete', retentionTimeInHours: 24 }
  }
}]

// sattle-capture is the capture tooling's group; sattle-engine is reserved for the engine's live input.
var consumerGroups = ['sattle-capture', 'sattle-engine']
resource captureGroups 'Microsoft.EventHub/namespaces/eventhubs/consumergroups@2024-01-01' = [for (h, i) in items(hubs): {
  parent: hub[i]
  name: consumerGroups[0]
}]
resource engineGroups 'Microsoft.EventHub/namespaces/eventhubs/consumergroups@2024-01-01' = [for (h, i) in items(hubs): {
  parent: hub[i]
  name: consumerGroups[1]
}]

resource roles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for r in roleIds: {
  scope: ns
  name: guid(ns.id, principalId, r)
  properties: {
    principalId: principalId
    principalType: 'User'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', r)
  }
}]

resource flowStorage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: 'stsattle${suffix}'
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    supportsHttpsTrafficOnly: true
    allowSharedKeyAccess: false
    // Tenant policy rewrites public network access to Disabled on every storage account, so state it here.
    // Consequence: blobs are not readable from outside Azure without a private endpoint (see README).
    publicNetworkAccess: 'Disabled'
    networkAcls: { defaultAction: 'Deny', bypass: 'AzureServices' }
  }
}

// Storage Blob Data Reader: the capture reads flow log blobs with Entra, no account keys.
resource blobReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: flowStorage
  name: guid(flowStorage.id, principalId, '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1')
  properties: {
    principalId: principalId
    principalType: 'User'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1')
  }
}

resource lifecycle 'Microsoft.Storage/storageAccounts/managementPolicies@2023-05-01' = {
  parent: flowStorage
  name: 'default'
  properties: {
    policy: {
      rules: [
        {
          name: 'expire-flow-logs'
          enabled: true
          type: 'Lifecycle'
          definition: {
            filters: { blobTypes: ['blockBlob', 'appendBlob'] }
            actions: { baseBlob: { delete: { daysAfterModificationGreaterThan: flowLogRetentionDays } } }
          }
        }
      ]
    }
  }
}

// The default namespace rule is only referenced by id: diagnostic settings need it
// even when local auth is disabled. No key is ever read.
output authorizationRuleId string = '${ns.id}/authorizationRules/RootManageSharedAccessKey'
output namespaceName string = ns.name
output flowLogStorageId string = flowStorage.id
output flowLogStorageName string = flowStorage.name
output hubNames object = {
  activity: hubs.activity.name
  aks: hubs.aks.name
  hubble: hubs.hubble.name
  ingest: hubs.ingest.name
}
