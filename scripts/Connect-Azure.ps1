#Requires -Version 7.0
<#
.SYNOPSIS
    Liest Tenant/Subscription aus der lokalen .env (siehe .env.example) und stellt sicher, dass die
    Azure CLI (az) gegen genau diesen Tenant angemeldet und auf diese Subscription gesetzt ist.

.DESCRIPTION
    Wird von allen anderen scripts/*.ps1, die Azure-Ressourcen anlegen/ändern, als erster Schritt
    aufgerufen (idempotent - meldet nicht erneut an, wenn bereits die richtige Subscription aktiv ist).

.EXAMPLE
    ./scripts/Connect-Azure.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$RootDir = Split-Path -Parent $PSScriptRoot
$EnvFile = Join-Path $RootDir ".env"

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw "Azure CLI (az) nicht gefunden. Bitte zuerst './scripts/Install-Prerequisites.ps1' ausfuehren."
}

# In CI (z.B. GitHub Actions "azure/login@v2") ist bereits per OIDC angemeldet und die richtige
# Subscription aktiv - dort gibt es keine .env, das ist kein Fehler.
if ($env:CI -and -not (Test-Path $EnvFile)) {
    $ciAccount = az account show -o json 2>$null | ConvertFrom-Json
    if ($LASTEXITCODE -eq 0 -and $ciAccount) {
        Write-Host "OK: CI-Umgebung erkannt, bereits angemeldet (Tenant: $($ciAccount.tenantId), Subscription: $($ciAccount.name))" -ForegroundColor Green
        return
    }
    throw "CI-Umgebung erkannt, aber kein aktiver 'az login'. Bitte den azure/login-Step vor diesem Skript ausfuehren."
}

if (-not (Test-Path $EnvFile)) {
    throw "Keine .env gefunden. Bitte '.env.example' nach '.env' kopieren und AZURE_TENANT_ID/AZURE_SUBSCRIPTION_ID eintragen."
}

$envValues = @{}
foreach ($line in Get-Content $EnvFile) {
    $trimmed = $line.Trim()
    if ([string]::IsNullOrWhiteSpace($trimmed) -or $trimmed.StartsWith("#")) { continue }
    $parts = $trimmed.Split("=", 2)
    if ($parts.Count -eq 2) {
        $envValues[$parts[0].Trim()] = $parts[1].Trim()
    }
}

$TenantId = $envValues["AZURE_TENANT_ID"]
$SubscriptionId = $envValues["AZURE_SUBSCRIPTION_ID"]

if ([string]::IsNullOrWhiteSpace($TenantId) -or $TenantId -eq "<TENANT_ID_HIER_EINTRAGEN>") {
    throw "AZURE_TENANT_ID fehlt oder ist noch ein Platzhalter in .env. Bitte echten Wert eintragen (siehe .env.example)."
}
if ([string]::IsNullOrWhiteSpace($SubscriptionId)) {
    throw "AZURE_SUBSCRIPTION_ID fehlt in .env. Bitte echten Wert eintragen (siehe .env.example)."
}

$currentAccount = az account show -o json 2>$null | ConvertFrom-Json
$hasActiveTargetSubscription = ($LASTEXITCODE -eq 0) -and $currentAccount -and ($currentAccount.id -eq $SubscriptionId)
$tenantMatches = ($LASTEXITCODE -eq 0) -and $currentAccount -and
    ($currentAccount.tenantId -eq $TenantId -or $currentAccount.tenantId -like "*$TenantId*")
$needsLogin = ($LASTEXITCODE -ne 0) -or (-not $currentAccount) -or (-not $hasActiveTargetSubscription -and -not $tenantMatches)

if ($needsLogin) {
    Write-Host "==> Melde bei Azure an (Tenant: $TenantId) ..." -ForegroundColor Cyan
    $loginOutput = az login --tenant $TenantId 2>&1
    if ($LASTEXITCODE -ne 0) {
        if ($loginOutput -match "Found multiple accounts with the same username") {
            # Bekanntes Azure-CLI-Problem mit dem WAM-Broker bei mehreren gecachten Sessions
            # derselben Identität (https://github.com/Azure/azure-cli/issues/20168).
            # Fallback: Device-Code-Flow umgeht den Broker komplett.
            Write-Warning "Broker-Konflikt erkannt (mehrere gecachte Konten fuer dieselbe Identitaet). Versuche erneut mit --use-device-code ..."
            az login --tenant $TenantId --use-device-code
            if ($LASTEXITCODE -ne 0) {
                throw "az login (Device-Code-Flow) ist fehlgeschlagen. Bitte manuell 'az account clear' ausfuehren und erneut versuchen."
            }
        }
        else {
            Write-Host $loginOutput
            throw "az login ist fehlgeschlagen (Tenant: $TenantId). Siehe Ausgabe oben."
        }
    }
}
else {
    Write-Host "OK: Bereits bei Azure angemeldet (Tenant: $($currentAccount.tenantId))" -ForegroundColor Green
}

Write-Host "==> Setze aktive Subscription: $SubscriptionId ..." -ForegroundColor Cyan
az account set --subscription $SubscriptionId
if ($LASTEXITCODE -ne 0) {
    throw "az account set --subscription $SubscriptionId ist fehlgeschlagen - hat das angemeldete Konto Zugriff auf diese Subscription?"
}

$active = az account show -o json | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or -not $active) {
    throw "az account show ist nach dem Login fehlgeschlagen - kein gueltiger Azure-Kontext aktiv."
}
Write-Host "OK: Aktive Subscription '$($active.name)' ($($active.id)), Tenant $($active.tenantId)" -ForegroundColor Green
