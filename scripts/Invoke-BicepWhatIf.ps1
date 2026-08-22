#Requires -Version 7.0
<#
.SYNOPSIS
    Fuehrt Azure "what-if" fuer die Bicep-Infrastruktur UND den Entra-ID-Config-Diff aus.

.PARAMETER Environment
    Kurzname der Umgebung, z.B. dev, staging, prod.

.PARAMETER ResourceGroupName
    Optional: Name der Resource Group. Standard: rg-entramcp-<Environment>.

.EXAMPLE
    ./scripts/Invoke-BicepWhatIf.ps1 -Environment dev
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Environment,
    [string]$ResourceGroupName = "rg-entramcp-$Environment"
)

$ErrorActionPreference = "Stop"
$RootDir = Split-Path -Parent $PSScriptRoot
$ReportsDir = Join-Path $RootDir "reports"
New-Item -ItemType Directory -Path $ReportsDir -Force | Out-Null
$Timestamp = Get-Date -AsUTC -Format "yyyyMMddTHHmmssZ"
$AzureReport = Join-Path $ReportsDir "whatif-azure-$Environment-$Timestamp.md"

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw "Azure CLI (az) nicht gefunden. Bitte zuerst './scripts/Install-Prerequisites.ps1' ausfuehren."
}

Write-Host "==> Pruefe Bicep-Syntax (az bicep build) ..." -ForegroundColor Cyan
$tmpJson = Join-Path ([System.IO.Path]::GetTempPath()) "main.json"
az bicep build --file (Join-Path $RootDir "infra/main.bicep") --outfile $tmpJson

Write-Host "==> Stelle sicher, dass Resource Group '$ResourceGroupName' existiert ..." -ForegroundColor Cyan
az group show --name $ResourceGroupName 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) {
    $paramFile = Join-Path $RootDir "infra/parameters/main.$Environment.bicepparam"
    $locationLine = Select-String -Path $paramFile -Pattern "param location = '([^']+)'" | Select-Object -First 1
    $location = if ($locationLine) { $locationLine.Matches[0].Groups[1].Value } else { "westeurope" }
    Write-Host "    Resource Group existiert nicht - lege sie an (Location: $location)."
    az group create --name $ResourceGroupName --location $location | Out-Null
}

Write-Host "==> Fuehre Azure what-if aus ..." -ForegroundColor Cyan
$whatIfOutput = az deployment group what-if `
    --resource-group $ResourceGroupName `
    --template-file (Join-Path $RootDir "infra/main.bicep") `
    --parameters (Join-Path $RootDir "infra/parameters/main.$Environment.bicepparam") `
    --result-format FullResourcePayloads `
    --no-pretty-print 2>&1

$lines = @(
    "# Azure What-If Report - Umgebung: $Environment",
    "_Erstellt: $(Get-Date -AsUTC -Format 'yyyy-MM-dd HH:mm:ssZ')_",
    "",
    '```',
    $whatIfOutput,
    '```'
)
$lines | Set-Content -Path $AzureReport -Encoding utf8NoBOM

Write-Host "Azure-what-if-Report geschrieben nach: $AzureReport" -ForegroundColor Green

if ($whatIfOutput -match '"changeType":\s*"Delete"') {
    Write-Warning "what-if zeigt geplante LOESCHUNGEN an. Bitte Report manuell pruefen, bevor Invoke-BicepDeploy.ps1 ausgefuehrt wird."
}

Write-Host ""
Write-Host "==> Fuehre Entra-ID-Config-Diff aus ..." -ForegroundColor Cyan
& (Join-Path $PSScriptRoot "Get-EntraIdDiff.ps1") -Environment $Environment

Write-Host ""
Write-Host "==> What-If komplett. Reports liegen in: $ReportsDir" -ForegroundColor Green
