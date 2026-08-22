using '../main.bicep'

param environmentName = 'dev'
param location = 'westeurope'
param appServiceSku = 'F1' // Free-Tier - kostenlos fuer Dev/Test
param deployEntraIdViaBicep = false
param logAnalyticsDailyQuotaGb = 1 // deckelt Log-Analytics-Kosten auf Cent-Beträge

// tenantId / apiAppId / apiAppIdentifierUri / mcpAppId werden NICHT hier gesetzt, sondern von
// scripts/Invoke-BicepWhatIf.ps1 und scripts/Invoke-BicepDeploy.ps1 zur Laufzeit als zusätzliche
// --parameters-Overrides aus infra/entra-desired-state/dev.json (siehe scripts/Set-EntraIdApps.ps1)
// sowie aus .env (AZURE_TENANT_ID) übergeben - so bleiben umgebungsspezifische App-IDs nicht in
// dieser Datei einbetoniert.
