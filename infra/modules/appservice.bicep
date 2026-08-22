param location string
param namePrefix string
param tags object = {}
param appInsightsConnectionString string
param keyVaultUri string
param sku string = 'B1'

resource appServicePlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: '${namePrefix}-plan'
  location: location
  tags: tags
  sku: {
    name: sku
  }
  kind: 'linux'
  properties: {
    reserved: true
  }
}

resource apiApp 'Microsoft.Web/sites@2023-12-01' = {
  name: '${namePrefix}-api'
  location: location
  tags: tags
  kind: 'app,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|8.0'
      alwaysOn: true
      appSettings: [
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsightsConnectionString }
        { name: 'KeyVaultUri', value: keyVaultUri }
        { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
      ]
    }
  }
}

resource mcpApp 'Microsoft.Web/sites@2023-12-01' = {
  name: '${namePrefix}-mcp'
  location: location
  tags: tags
  kind: 'app,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|8.0'
      alwaysOn: true
      appSettings: [
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsightsConnectionString }
        { name: 'KeyVaultUri', value: keyVaultUri }
        { name: 'ApiServer__BaseUrl', value: 'https://${apiApp.properties.defaultHostName}' }
        { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
      ]
    }
  }
}

output apiAppName string = apiApp.name
output apiAppHostName string = apiApp.properties.defaultHostName
output apiAppPrincipalId string = apiApp.identity.principalId
output mcpAppName string = mcpApp.name
output mcpAppHostName string = mcpApp.properties.defaultHostName
output mcpAppPrincipalId string = mcpApp.identity.principalId
