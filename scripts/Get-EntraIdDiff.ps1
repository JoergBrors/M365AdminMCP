#Requires -Version 7.0
<#
.SYNOPSIS
    Vergleicht den Soll-Zustand (infra/entra-desired-state/<env>.json) mit dem
    tatsaechlichen Ist-Zustand der App-Registrierungen in Entra ID.

.PARAMETER Environment
    Kurzname der Umgebung, z.B. dev, staging, prod.

.EXAMPLE
    ./scripts/Get-EntraIdDiff.ps1 -Environment dev
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Environment
)

$ErrorActionPreference = "Stop"
$RootDir = Split-Path -Parent $PSScriptRoot
$DesiredStateFile = Join-Path $RootDir "infra/entra-desired-state/$Environment.json"
$ReportsDir = Join-Path $RootDir "reports"
New-Item -ItemType Directory -Path $ReportsDir -Force | Out-Null
$Timestamp = Get-Date -AsUTC -Format "yyyyMMddTHHmmssZ"
$ReportFile = Join-Path $ReportsDir "entra-diff-$Environment-$Timestamp.md"

if (-not (Test-Path $DesiredStateFile)) {
    throw "Kein Soll-Zustand gefunden ($DesiredStateFile). Fuehre zuerst './scripts/Set-EntraIdApps.ps1 -Environment $Environment' aus."
}

$desired = Get-Content $DesiredStateFile -Raw | ConvertFrom-Json

function Get-NormalizedAppState {
    param([string]$AppId)
    $json = az ad app show --id $AppId -o json 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($json)) {
        return [pscustomobject]@{ error = "App nicht gefunden - wurde sie zwischenzeitlich geloescht?" }
    }
    $app = $json | ConvertFrom-Json
    return [pscustomobject]@{
        displayName      = $app.displayName
        signInAudience   = $app.signInAudience
        appRoles         = @($app.appRoles | ForEach-Object { $_.value })
        delegatedScopes  = @($app.api.oauth2PermissionScopes | ForEach-Object { $_.value })
        redirectUris     = $app.web.redirectUris
    }
}

$lines = @()
$lines += "# Entra ID Config-Diff - Umgebung: $Environment"
$lines += "_Erstellt: $(Get-Date -AsUTC -Format 'yyyy-MM-dd HH:mm:ssZ')_"
$lines += ""

foreach ($entry in @(
        @{ Key = "apiApp"; AppId = $desired.apiApp.appId },
        @{ Key = "mcpApp"; AppId = $desired.mcpApp.appId }
    )) {
    $lines += "## $($entry.Key) ($($entry.AppId))"
    $lines += '```diff'

    $desiredJson = ($desired.($entry.Key) | ConvertTo-Json -Depth 10)
    $actual = Get-NormalizedAppState -AppId $entry.AppId
    $actualJson = ($actual | ConvertTo-Json -Depth 10)

    if ($desiredJson -eq $actualJson) {
        $lines += "(keine wesentliche Abweichung in den erfassten Feldern)"
    }
    else {
        $lines += "- SOLL:"
        $lines += $desiredJson
        $lines += "+ IST:"
        $lines += $actualJson
    }

    $lines += '```'
    $lines += ""
}

$lines | Set-Content -Path $ReportFile -Encoding utf8NoBOM

Write-Host "Entra-Config-Diff geschrieben nach: $ReportFile" -ForegroundColor Green
Get-Content $ReportFile | Write-Host
