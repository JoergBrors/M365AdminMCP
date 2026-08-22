param location string
param namePrefix string
param tags object = {}
param appInsightsConnectionString string
param keyVaultUri string
@description('App Service Plan SKU. F1 (Free) ist kostenlos, unterstuetzt aber kein "Always On" - die App schlaeft nach ca. 20 Min. Inaktivitaet ein (fuer Dev/Test i.d.R. unproblematisch).')
param sku string = 'F1'

var isFreeTier = sku == 'F1'

@description('Entra-ID-Tenant-ID, wird als AzureAd__TenantId auf beide Web Apps gesetzt')
param tenantId string

@description('App-ID der api-server-App-Registrierung, wird als AzureAd__ClientId (api) gesetzt')
param apiAppId string

@description('App-ID-URI der api-server-App-Registrierung, wird als AzureAd__Audience (api) gesetzt')
param apiAppIdentifierUri string

@description('App-ID der mcp-server-App-Registrierung, wird als AzureAd__ClientId (mcp) gesetzt')
param mcpAppId string

@description('Key-Vault-Secret-Name des mcp-server Client Secrets (Key-Vault-Reference statt Klartext)')
param mcpClientSecretName string = 'mcp-server-client-secret'


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
      alwaysOn: !isFreeTier
      appSettings: [
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsightsConnectionString }
        { name: 'KeyVaultUri', value: keyVaultUri }
        { name: 'AzureAd__TenantId', value: tenantId }
        { name: 'AzureAd__ClientId', value: apiAppId }
        { name: 'AzureAd__Audience', value: apiAppIdentifierUri }
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
      alwaysOn: !isFreeTier
      appSettings: [
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsightsConnectionString }
        { name: 'KeyVaultUri', value: keyVaultUri }
        { name: 'AzureAd__TenantId', value: tenantId }
        { name: 'AzureAd__ClientId', value: mcpAppId }
        { name: 'AzureAd__ClientSecret', value: '@Microsoft.KeyVault(SecretUri=${keyVaultUri}secrets/${mcpClientSecretName})' }
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
