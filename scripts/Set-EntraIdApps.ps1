#Requires -Version 7.0
<#
.SYNOPSIS
    Idempotente Entra-ID-Provisionierung fuer api-server + mcp-server, inkl. Microsoft-Graph-
    App-only-Permissions fuer Office 365 Status/Message Center/Adoption. Cross-platform
    (macOS/Windows/Linux) ueber PowerShell 7 + Azure CLI.

.PARAMETER Environment
    Kurzname der Umgebung, z.B. dev, staging, prod.

.PARAMETER Destroy
    Entfernt die App-Registrierungen der angegebenen Umgebung wieder (nur fuer Dev/Test gedacht).

.EXAMPLE
    ./scripts/Set-EntraIdApps.ps1 -Environment dev

.EXAMPLE
    ./scripts/Set-EntraIdApps.ps1 -Environment dev -Destroy
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Environment,
    [switch]$Destroy
)

$ErrorActionPreference = "Stop"
$RootDir = Split-Path -Parent $PSScriptRoot
$DesiredStateDir = Join-Path $RootDir "infra/entra-desired-state"
$DesiredStateFile = Join-Path $DesiredStateDir "$Environment.json"

$ApiAppName = "api-server-$Environment"
$McpAppName = "mcp-server-$Environment"
$KeyVaultName = "entramcp-$Environment-kv"
$AppRoleValue = "Tasks.ReadWrite.All"
$DelegatedScopeValue = "Tasks.ReadWrite"
$GraphAppId = "00000003-0000-0000-c000-000000000000"
$GraphAppOnlyPermissions = @("ServiceHealth.Read.All", "ServiceMessage.Read.All", "Reports.Read.All")

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw "Azure CLI (az) nicht gefunden. Bitte zuerst './scripts/Install-Prerequisites.ps1' ausfuehren."
}

function Invoke-AzJson {
    param([Parameter(Mandatory)][string]$Arguments)
    $result = Invoke-Expression "az $Arguments -o json" 2>$null
    if ([string]::IsNullOrWhiteSpace($result)) { return $null }
    return $result | ConvertFrom-Json
}

function Invoke-AzTsv {
    param([Parameter(Mandatory)][string]$Arguments)
    $result = (Invoke-Expression "az $Arguments -o tsv" 2>$null)
    return $result
}

function Invoke-GraphRestPost {
    param(
        [Parameter(Mandatory)][string]$Uri,
        [Parameter(Mandatory)][hashtable]$Body
    )
    $tmp = New-TemporaryFile
    try {
        ($Body | ConvertTo-Json -Depth 10) | Set-Content -Path $tmp -Encoding utf8NoBOM
        az rest --method POST --uri $Uri --headers "Content-Type=application/json" --body "@$tmp" 2>$null | Out-Null
        return $LASTEXITCODE -eq 0
    }
    finally {
        Remove-Item $tmp -ErrorAction SilentlyContinue
    }
}

function Invoke-GraphRestPatch {
    param(
        [Parameter(Mandatory)][string]$Uri,
        [Parameter(Mandatory)]$Body
    )
    $tmp = New-TemporaryFile
    try {
        ($Body | ConvertTo-Json -Depth 10) | Set-Content -Path $tmp -Encoding utf8NoBOM
        az rest --method PATCH --uri $Uri --headers "Content-Type=application/json" --body "@$tmp" | Out-Null
    }
    finally {
        Remove-Item $tmp -ErrorAction SilentlyContinue
    }
}

# --- Destroy-Pfad ---
if ($Destroy) {
    Write-Host "==> Entferne Entra-ID-App-Registrierungen fuer Umgebung '$Environment' ..." -ForegroundColor Yellow
    foreach ($name in @($ApiAppName, $McpAppName)) {
        $appId = Invoke-AzTsv "ad app list --display-name `"$name`" --query `"[0].appId`""
        if ($appId) {
            Write-Host "  - loesche $name ($appId)"
            az ad app delete --id $appId
        }
        else {
            Write-Host "  - $name nicht gefunden, ueberspringe"
        }
    }
    return
}

Write-Host "==> Entra-ID-Setup fuer Umgebung '$Environment'" -ForegroundColor Cyan

# --- 1. api-server App Registration (idempotent) ---
$apiAppId = Invoke-AzTsv "ad app list --display-name `"$ApiAppName`" --query `"[0].appId`""
if (-not $apiAppId) {
    Write-Host "  -> lege '$ApiAppName' an"
    $apiAppId = Invoke-AzTsv "ad app create --display-name `"$ApiAppName`" --sign-in-audience AzureADMyOrg --query appId"
}
else {
    Write-Host "  -> '$ApiAppName' existiert bereits ($apiAppId), aktualisiere Konfiguration"
}

$apiApp = Invoke-AzJson "ad app show --id $apiAppId"
$appRoleId = ($apiApp.appRoles | Where-Object { $_.value -eq $AppRoleValue } | Select-Object -First 1).id
if (-not $appRoleId) { $appRoleId = [guid]::NewGuid().ToString() }

$scopeId = ($apiApp.api.oauth2PermissionScopes | Where-Object { $_.value -eq $DelegatedScopeValue } | Select-Object -First 1).id
if (-not $scopeId) { $scopeId = [guid]::NewGuid().ToString() }

az ad app update --id $apiAppId --identifier-uris "api://$apiAppId" | Out-Null

$apiPatchBody = @{
    appRoles = @(
        @{
            id                 = $appRoleId
            allowedMemberTypes = @("Application")
            description        = "App-only read/write access to tasks"
            displayName        = $AppRoleValue
            isEnabled          = $true
            value              = $AppRoleValue
        }
    )
    api      = @{
        requestedAccessTokenVersion = 2
        oauth2PermissionScopes      = @(
            @{
                id                      = $scopeId
                adminConsentDescription = "Allow the app to read/write tasks on behalf of the signed-in user"
                adminConsentDisplayName = $DelegatedScopeValue
                isEnabled               = $true
                type                    = "User"
                userConsentDescription  = "Allow the app to read/write your tasks"
                userConsentDisplayName  = "Read/write your tasks"
                value                   = $DelegatedScopeValue
            }
        )
    }
}
Invoke-GraphRestPatch -Uri "https://graph.microsoft.com/v1.0/applications(appId='$apiAppId')" -Body $apiPatchBody

if (-not (Invoke-AzTsv "ad sp show --id $apiAppId --query id")) {
    az ad sp create --id $apiAppId | Out-Null
}
$apiSpId = Invoke-AzTsv "ad sp show --id $apiAppId --query id"

# --- 2. mcp-server App Registration (idempotent) ---
$mcpAppId = Invoke-AzTsv "ad app list --display-name `"$McpAppName`" --query `"[0].appId`""
$mcpRedirectUri = "https://entramcp-$Environment-mcp.azurewebsites.net/signin-oidc"

if (-not $mcpAppId) {
    Write-Host "  -> lege '$McpAppName' an"
    $mcpAppId = Invoke-AzTsv "ad app create --display-name `"$McpAppName`" --sign-in-audience AzureADMyOrg --web-redirect-uris `"$mcpRedirectUri`" --query appId"
}
else {
    Write-Host "  -> '$McpAppName' existiert bereits ($mcpAppId), aktualisiere Konfiguration"
    az ad app update --id $mcpAppId --web-redirect-uris $mcpRedirectUri | Out-Null
}

if (-not (Invoke-AzTsv "ad sp show --id $mcpAppId --query id")) {
    az ad sp create --id $mcpAppId | Out-Null
}
$mcpSpId = Invoke-AzTsv "ad sp show --id $mcpAppId --query id"

# --- Microsoft Graph Rollen fuer Status/Message Center/Adoption dynamisch aufloesen ---
$graphSpId = Invoke-AzTsv "ad sp show --id $GraphAppId --query id"
$graphSp = Invoke-AzJson "ad sp show --id $graphSpId"
$graphRoleIds = @{}
foreach ($permName in $GraphAppOnlyPermissions) {
    $roleId = ($graphSp.appRoles | Where-Object { $_.value -eq $permName } | Select-Object -First 1).id
    if (-not $roleId) { throw "Graph-Rolle '$permName' konnte nicht aufgeloest werden - unerwartet." }
    $graphRoleIds[$permName] = $roleId
}

# Required Resource Access: Application Permission + Delegated Scope auf api-server PLUS Graph-Permissions
$requiredResourceAccess = @(
    @{
        resourceAppId  = $apiAppId
        resourceAccess = @(
            @{ id = $appRoleId; type = "Role" },
            @{ id = $scopeId; type = "Scope" }
        )
    },
    @{
        resourceAppId  = $GraphAppId
        resourceAccess = $GraphAppOnlyPermissions | ForEach-Object { @{ id = $graphRoleIds[$_]; type = "Role" } }
    }
)
Invoke-GraphRestPatch -Uri "https://graph.microsoft.com/v1.0/applications(appId='$mcpAppId')" -Body @{ requiredResourceAccess = $requiredResourceAccess }

# --- App-Role-Assignment (Application Permission) api-server, erfordert Admin-Rechte ---
Write-Host "  -> weise Application Permission '$AppRoleValue' zu (Admin Consent)"
Invoke-GraphRestPost -Uri "https://graph.microsoft.com/v1.0/servicePrincipals/$mcpSpId/appRoleAssignments" -Body @{
    principalId = $mcpSpId
    resourceId  = $apiSpId
    appRoleId   = $appRoleId
} | Out-Null

# --- Graph App-only Permissions zuweisen (ServiceHealth/ServiceMessage/Reports) ---
Write-Host "  -> weise Microsoft Graph App-only Permissions zu (ServiceHealth/ServiceMessage/Reports)"
foreach ($permName in $GraphAppOnlyPermissions) {
    $ok = Invoke-GraphRestPost -Uri "https://graph.microsoft.com/v1.0/servicePrincipals/$mcpSpId/appRoleAssignments" -Body @{
        principalId = $mcpSpId
        resourceId  = $graphSpId
        appRoleId   = $graphRoleIds[$permName]
    }
    if (-not $ok) {
        Write-Host "     ($permName bereits zugewiesen oder Consent fehlt bereits - ggf. manuell nachholen)" -ForegroundColor DarkYellow
    }
}

# --- Admin Consent fuer Delegated Scope + alle App-only Permissions ---
Write-Host "  -> erteile Admin-Consent"
az ad app permission admin-consent --id $mcpAppId 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Warning "Admin-Consent konnte nicht automatisch gesetzt werden - bitte manuell per 'az ad app permission admin-consent --id $mcpAppId' als Global-/Application-Administrator nachholen."
}

# --- Client Secret fuer mcp-server erzeugen und in Key Vault ablegen ---
Write-Host "  -> erzeuge Client Secret fuer '$McpAppName'"
$mcpSecret = Invoke-AzTsv "ad app credential reset --id $mcpAppId --display-name `"mvp-secret-$Environment`" --years 1 --query password"

$null = az keyvault show --name $KeyVaultName -o none 2>$null
$kvExists = ($LASTEXITCODE -eq 0)

if ($kvExists) {
    az keyvault secret set --vault-name $KeyVaultName --name "mcp-server-client-secret" --value $mcpSecret | Out-Null
    az keyvault secret set --vault-name $KeyVaultName --name "api-server-app-id" --value $apiAppId | Out-Null
    az keyvault secret set --vault-name $KeyVaultName --name "mcp-server-app-id" --value $mcpAppId | Out-Null
    Write-Host "  -> Secrets im Key Vault '$KeyVaultName' gespeichert" -ForegroundColor Green
}
else {
    Write-Warning "Key Vault '$KeyVaultName' existiert noch nicht (erst nach Azure-Deployment vorhanden)."
    Write-Host "     Bitte nach dem Deployment nachtragen: az keyvault secret set --vault-name $KeyVaultName --name mcp-server-client-secret --value <secret>"
    Write-Host "     Secret (nur jetzt sichtbar): $mcpSecret" -ForegroundColor Yellow
}

# --- Soll-Zustand fuer den Config-Diff schreiben (ohne Secrets!) ---
New-Item -ItemType Directory -Path $DesiredStateDir -Force | Out-Null
$desiredState = @{
    apiApp = @{
        appId           = $apiAppId
        displayName     = $ApiAppName
        signInAudience  = "AzureADMyOrg"
        appRoles        = @($AppRoleValue)
        delegatedScopes = @($DelegatedScopeValue)
    }
    mcpApp = @{
        appId                   = $mcpAppId
        displayName             = $McpAppName
        signInAudience          = "AzureADMyOrg"
        redirectUris            = @($mcpRedirectUri)
        graphAppOnlyPermissions = $GraphAppOnlyPermissions
    }
    grants = @{
        appRoleAssignment = @{ from = $McpAppName; to = $ApiAppName; role = $AppRoleValue }
        delegatedGrant    = @{ from = $McpAppName; to = $ApiAppName; scope = $DelegatedScopeValue }
        graphGrants       = @{ from = $McpAppName; to = "Microsoft Graph"; roles = $GraphAppOnlyPermissions }
    }
}
($desiredState | ConvertTo-Json -Depth 10) | Set-Content -Path $DesiredStateFile -Encoding utf8NoBOM

Write-Host ""
Write-Host "==> Fertig. Soll-Zustand geschrieben nach: $DesiredStateFile" -ForegroundColor Green
Write-Host "==> api-server appId: $apiAppId"
Write-Host "==> mcp-server appId: $mcpAppId"
