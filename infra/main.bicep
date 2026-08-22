targetScope = 'resourceGroup'

@description('Kurzname der Umgebung, z.B. dev, staging, prod')
param environmentName string

@description('Azure-Region')
param location string = resourceGroup().location

@description('App Service SKU')
param appServiceSku string = 'B1'

@description('Optional: Entra-ID-Objekte zusätzlich per (Preview-)Bicep-Extension deployen. Standard: false, siehe docs/ENTRA-ID-SETUP.md.')
param deployEntraIdViaBicep bool = false

@description('Nur relevant wenn deployEntraIdViaBicep = true')
param mcpRedirectUri string = ''

var namePrefix = 'entramcp-${environmentName}'
var tags = {
  environment: environmentName
  project: 'entra-mcp-mvp'
  managedBy: 'bicep'
}

module monitoring 'modules/monitoring.bicep' = {
  name: 'monitoring'
  params: {
    location: location
    namePrefix: namePrefix
    tags: tags
  }
}

module appService 'modules/appservice.bicep' = {
  name: 'appservice'
  params: {
    location: location
    namePrefix: namePrefix
    tags: tags
    sku: appServiceSku
    appInsightsConnectionString: monitoring.outputs.appInsightsConnectionString
    keyVaultUri: keyVault.outputs.keyVaultUri
  }
}

module keyVault 'modules/keyvault.bicep' = {
  name: 'keyvault'
  params: {
    location: location
    namePrefix: namePrefix
    tags: tags
    secretReaderPrincipalIds: [] // wird nach appService-Deployment idealerweise per zweitem Lauf ergänzt,
                                 // siehe scripts/deploy.sh (Two-Pass wegen zirkulärer Abhängigkeit MI <-> KV)
  }
}

// Optionaler Preview-Pfad, siehe modules/entra-id.bicep Kopfkommentar.
module entraId 'modules/entra-id.bicep' = if (deployEntraIdViaBicep) {
  name: 'entra-id'
  params: {
    mcpRedirectUri: mcpRedirectUri
  }
}

output apiAppHostName string = appService.outputs.apiAppHostName
output mcpAppHostName string = appService.outputs.mcpAppHostName
output keyVaultName string = keyVault.outputs.keyVaultName
