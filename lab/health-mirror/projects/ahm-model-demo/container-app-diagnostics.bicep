// Deploy in the Container App's resource group when the app is provisioned.
param appName string
param eventHubName string
param eventHubAuthorizationRuleId string

resource app 'Microsoft.App/containerApps@2024-03-01' existing = {
  name: appName
}

resource metrics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'sattle-demo-platform-metrics'
  scope: app
  properties: {
    eventHubName: eventHubName
    eventHubAuthorizationRuleId: eventHubAuthorizationRuleId
    metrics: [
      {
        category: 'AllMetrics'
        enabled: true
      }
    ]
  }
}
