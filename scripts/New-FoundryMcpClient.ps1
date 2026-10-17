#Requires -Version 7.0
<#
.SYNOPSIS
    Legt selbststaendig eine Entra-App (inkl. Service Principal) fuer Microsoft Foundry als OAuth-Client
    des M365AdminMCP-Servers an - oder ergaenzt spaeter die Redirect-URI, die Foundry erst nach "Connect" anzeigt.

.DESCRIPTION
    Foundry ("Custom MCP" + "OAuth Identity Passthrough") unterstuetzt weder DCR noch CIMD. Es verlangt eine vorab
    angelegte Entra-App (Client-ID, Client-Secret, Auth/Token/Refresh-URL, Scopes). Dieses Skript erstellt genau
    diese App, analog zur Copilot-Studio-App (Plattform "web", Secret, delegierte Berechtigung Mcp.Access):
      - Single-Tenant, Plattform "Web" (Confidential Client)
      - delegierte Berechtigung Mcp.Access auf die vorhandene MCP-App (kein Graph-/ARM-Recht)
      - Service Principal in diesem Tenant
      - Client-Secret (Wert wird EINMALIG auf der Konsole ausgegeben, nie in eine Datei geschrieben)
    Die App wird bewusst NICHT ueber Terraform verwaltet: sie ist Self-Service und beruehrt keine bestehenden
    Registrierungen. Es werden keine Ressourcen veraendert oder geloescht.

    Zwei Modi:
      Create        (Standard)   neue App anlegen
      AddRedirect   (-AppId)     Redirect-URI zu einer bestehenden Foundry-App hinzufuegen

.PARAMETER Environment
    Kurzname der Umgebung (bestimmt die MCP-App api://mcp-server-<env> und den Standard-Anzeigenamen). Standard: dev.

.PARAMETER DisplayName
    Anzeigename der neuen App. Standard: foundry-mcp-client-<Environment>.

.PARAMETER McpAppIdUri
    Identifier-URI der MCP-App. Standard: api://mcp-server-<Environment>.

.PARAMETER RedirectUri
    Optional bei Create (Foundry zeigt die Redirect-URL erst NACH "Connect" an - dann mit -AppId nachtragen).

.PARAMETER SecretValidityMonths
    Gueltigkeit des Secrets in Monaten. Standard: 6 (kurz halten, rotieren).

.PARAMETER AdminConsent
    Erteilt Admin-Consent fuer Mcp.Access (braucht entsprechende Entra-Rechte). Ohne den Schalter stimmt jeder
    Nutzer beim ersten Foundry-Login selbst zu.

.PARAMETER AppId
    Client-ID einer bestehenden Foundry-App (aktiviert den Modus AddRedirect).

.PARAMETER SkipConnect
    Ruft Connect-Azure.ps1 nicht auf (dann muss 'az login' bereits gegen den richtigen Tenant erfolgt sein).

.EXAMPLE
    ./scripts/New-FoundryMcpClient.ps1 -WhatIf
    ./scripts/New-FoundryMcpClient.ps1 -Environment dev
    ./scripts/New-FoundryMcpClient.ps1 -AppId <client-id> -RedirectUri "https://<von-foundry-angezeigte-url>"
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$Environment = "dev",
    [string]$DisplayName,
    [string]$McpAppIdUri,
    [string]$RedirectUri,
    [ValidateRange(1, 24)][int]$SecretValidityMonths = 6,
    [switch]$AdminConsent,
    [string]$AppId,
    [switch]$SkipConnect
)

$ErrorActionPreference = "Stop"

function Invoke-AzJson {
    param([Parameter(Mandatory)][string[]]$Arguments)
    $out = & az @Arguments -o json
    if ($LASTEXITCODE -ne 0) { throw "az $($Arguments -join ' ') ist fehlgeschlagen." }
    if ($out) { return ($out | ConvertFrom-Json) }
}

if (-not $SkipConnect) {
    $connect = Join-Path $PSScriptRoot "Connect-Azure.ps1"
    if (Test-Path (Join-Path (Split-Path -Parent $PSScriptRoot) ".env")) { & $connect }
}
if (-not (Get-Command az -ErrorAction SilentlyContinue)) { throw "Azure CLI (az) nicht gefunden." }

if ($RedirectUri -and $RedirectUri -notmatch '^https://') { throw "RedirectUri muss mit https:// beginnen." }

$account = Invoke-AzJson @("account", "show")
$tenantId = $account.tenantId

# ---------------------------------------------------------------- Modus AddRedirect
if ($AppId) {
    if (-not $RedirectUri) { throw "Modus AddRedirect braucht -RedirectUri." }
    $app = Invoke-AzJson @("ad", "app", "show", "--id", $AppId)
    $current = @($app.web.redirectUris)
    if ($current -contains $RedirectUri) {
        Write-Host "OK: '$RedirectUri' ist bereits eingetragen - nichts zu tun." -ForegroundColor Green
        return
    }
    if ($PSCmdlet.ShouldProcess("$($app.displayName) ($AppId)", "Redirect-URI '$RedirectUri' hinzufuegen")) {
        $all = @($current + $RedirectUri)
        & az ad app update --id $AppId --web-redirect-uris @all | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Redirect-URI konnte nicht gesetzt werden." }
        Write-Host "==> Redirect-URI '$RedirectUri' zu '$($app.displayName)' hinzugefuegt." -ForegroundColor Green
    }
    return
}

# ---------------------------------------------------------------- Modus Create
if (-not $McpAppIdUri) { $McpAppIdUri = "api://mcp-server-$Environment" }
if (-not $DisplayName) { $DisplayName = "foundry-mcp-client-$Environment" }

# MCP-App und Scope-ID "Mcp.Access" ermitteln (nur lesend)
$mcpApps = Invoke-AzJson @("ad", "app", "list", "--filter", "identifierUris/any(u:u eq '$McpAppIdUri')")
if (-not $mcpApps -or @($mcpApps).Count -ne 1) { throw "MCP-App mit Identifier-URI '$McpAppIdUri' nicht (eindeutig) gefunden." }
$mcpApp = @($mcpApps)[0]
$scope = @($mcpApp.api.oauth2PermissionScopes) | Where-Object { $_.value -eq "Mcp.Access" -and $_.isEnabled }
if (-not $scope) { throw "Scope 'Mcp.Access' an der MCP-App '$McpAppIdUri' nicht gefunden." }

$existing = Invoke-AzJson @("ad", "app", "list", "--display-name", $DisplayName)
if ($existing -and @($existing).Count -gt 0) {
    throw "Eine App namens '$DisplayName' existiert bereits (appId: $(@($existing)[0].appId)). Anderen -DisplayName waehlen oder Modus -AppId nutzen."
}

Write-Host "Tenant:        $tenantId"
Write-Host "MCP-App:       $($mcpApp.displayName) ($($mcpApp.appId)), Scope Mcp.Access ($($scope.id))"
Write-Host "Neue App:      $DisplayName (Single-Tenant, Web, Secret $SecretValidityMonths Monate)"

if (-not $PSCmdlet.ShouldProcess($DisplayName, "Entra-App + Service Principal + Secret anlegen")) { return }

$requiredAccess = @(@{
        resourceAppId  = $mcpApp.appId
        resourceAccess = @(@{ id = $scope.id; type = "Scope" })
    }) | ConvertTo-Json -Depth 5 -Compress
$tmp = New-TemporaryFile
try {
    Set-Content -Path $tmp -Value $requiredAccess -Encoding utf8NoBOM
    $createArgs = @("ad", "app", "create", "--display-name", $DisplayName, "--sign-in-audience", "AzureADMyOrg",
        "--required-resource-accesses", "@$tmp")
    if ($RedirectUri) { $createArgs += @("--web-redirect-uris", $RedirectUri) }
    $app = Invoke-AzJson $createArgs
}
finally { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }

$null = Invoke-AzJson @("ad", "sp", "create", "--id", $app.appId)

$endDate = (Get-Date).ToUniversalTime().AddMonths($SecretValidityMonths).ToString("yyyy-MM-dd")
$cred = Invoke-AzJson @("ad", "app", "credential", "reset", "--id", $app.appId, "--append",
    "--display-name", "foundry-secret-$(Get-Date -Format yyyyMMdd)", "--end-date", $endDate)

if ($AdminConsent) {
    Write-Host "==> Admin-Consent fuer Mcp.Access ..." -ForegroundColor Cyan
    & az ad app permission admin-consent --id $app.appId
    if ($LASTEXITCODE -ne 0) { Write-Warning "Admin-Consent fehlgeschlagen (Rechte?). Nutzer stimmen beim ersten Login selbst zu." }
}

$mcpHost = $McpAppIdUri -replace '^api://mcp-server-', ''
Write-Host ""
Write-Host "==================== Werte fuer die Foundry-Oberflaeche ====================" -ForegroundColor Green
Write-Host "Remote MCP Server endpoint : https://entramcp-$mcpHost-mcp.azurewebsites.net/mcp   (ggf. an eigene Umgebung anpassen)"
Write-Host "Authentication             : OAuth Identity Passthrough"
Write-Host "Client ID                  : $($app.appId)"
Write-Host "Client secret              : $($cred.password)   <-- nur JETZT sichtbar, sicher ablegen, nicht committen"
Write-Host "Auth URL                   : https://login.microsoftonline.com/$tenantId/oauth2/v2.0/authorize"
Write-Host "Token URL                  : https://login.microsoftonline.com/$tenantId/oauth2/v2.0/token"
Write-Host "Refresh URL                : https://login.microsoftonline.com/$tenantId/oauth2/v2.0/token"
Write-Host "Scopes                     : $McpAppIdUri/Mcp.Access offline_access"
Write-Host "============================================================================" -ForegroundColor Green
Write-Host ""
Write-Host "Naechster Schritt: In Foundry 'Connect' waehlen, die angezeigte Redirect URL kopieren und eintragen mit:" -ForegroundColor Cyan
Write-Host "  ./scripts/New-FoundryMcpClient.ps1 -AppId $($app.appId) -RedirectUri `"<Redirect URL aus Foundry>`""
