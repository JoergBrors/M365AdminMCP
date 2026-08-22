targetScope = 'resourceGroup'

@description('Kurzname der Umgebung, z.B. dev, staging, prod')
param environmentName string

@description('Azure-Region')
param location string = resourceGroup().location

@description('App Service SKU. F1 (Free) ist kostenlos, unterstuetzt aber kein "Always On" (App schlaeft bei Inaktivitaet ein) - fuer Dev/Test i.d.R. unproblematisch.')
param appServiceSku string = 'F1'

@description('Optional: Entra-ID-Objekte zusätzlich per (Preview-)Bicep-Extension deployen. Standard: false, siehe docs/ENTRA-ID-SETUP.md.')
param deployEntraIdViaBicep bool = false

@description('Nur relevant wenn deployEntraIdViaBicep = true')
param mcpRedirectUri string = ''

@description('Entra-ID-Tenant-ID. Wird von scripts/Invoke-BicepWhatIf.ps1 / Invoke-BicepDeploy.ps1 automatisch aus infra/entra-desired-state/<env>.json ergänzt.')
param tenantId string

@description('App-ID der api-server-App-Registrierung (siehe infra/entra-desired-state/<env>.json)')
param apiAppId string

@description('App-ID-URI der api-server-App-Registrierung, z.B. api://<apiAppId>')
param apiAppIdentifierUri string

@description('App-ID der mcp-server-App-Registrierung (siehe infra/entra-desired-state/<env>.json)')
param mcpAppId string

@description('Hartes Tageslimit für Log-Analytics-Ingestion in GB, deckelt die Kosten auf Cent-Beträge. -1 = kein Limit (z.B. für prod mit SLA-Anforderungen).')
param logAnalyticsDailyQuotaGb int = 1

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
    dailyQuotaGb: logAnalyticsDailyQuotaGb
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
    tenantId: tenantId
    apiAppId: apiAppId
    apiAppIdentifierUri: apiAppIdentifierUri
    mcpAppId: mcpAppId
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
