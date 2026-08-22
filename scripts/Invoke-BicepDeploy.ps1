#Requires -Version 7.0
<#
.SYNOPSIS
    Kompletter Deploy-Flow: Entra ID -> What-If (Pflicht) -> Bestaetigung -> Deployment.

.PARAMETER Environment
    Kurzname der Umgebung, z.B. dev, staging, prod.

.PARAMETER ResourceGroupName
    Optional: Name der Resource Group. Standard: rg-entramcp-<Environment>.

.PARAMETER Force
    Erlaubt das Deployment auch wenn What-If Loeschungen anzeigt.

.EXAMPLE
    ./scripts/Invoke-BicepDeploy.ps1 -Environment dev
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Environment,
    [string]$ResourceGroupName = "rg-entramcp-$Environment",
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$RootDir = Split-Path -Parent $PSScriptRoot

Write-Host "===================================================================" -ForegroundColor Cyan
Write-Host " Deployment: Umgebung=$Environment  Resource Group=$ResourceGroupName" -ForegroundColor Cyan
Write-Host "===================================================================" -ForegroundColor Cyan

Write-Host ""
Write-Host "### Schritt 1/4: Entra ID Setup (idempotent) ###" -ForegroundColor Cyan
& (Join-Path $PSScriptRoot "Set-EntraIdApps.ps1") -Environment $Environment

Write-Host ""
Write-Host "### Schritt 2/4: What-If (Pflicht vor jedem Deployment) ###" -ForegroundColor Cyan
& (Join-Path $PSScriptRoot "Invoke-BicepWhatIf.ps1") -Environment $Environment -ResourceGroupName $ResourceGroupName

$reportsDir = Join-Path $RootDir "reports"
$latestReport = Get-ChildItem -Path $reportsDir -Filter "whatif-azure-$Environment-*.md" |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1

if (-not $latestReport) {
    throw "Kein What-If-Report gefunden - Abbruch. Deployment ohne What-If ist nicht erlaubt."
}

$reportContent = Get-Content $latestReport.FullName -Raw
if ($reportContent -match '"changeType":\s*"Delete"' -and -not $Force) {
    Write-Error "What-If zeigt Loeschungen an. Deployment abgebrochen. Report pruefen: $($latestReport.FullName). Falls beabsichtigt, mit -Force erneut aufrufen."
    exit 1
}

Write-Host ""
Write-Host "### Schritt 3/4: Bestaetigung ###" -ForegroundColor Cyan
$confirm = Read-Host "Deployment fuer '$Environment' in Resource Group '$ResourceGroupName' jetzt ausfuehren? [y/N]"
if ($confirm -ne "y" -and $confirm -ne "Y") {
    Write-Host "Abgebrochen."
    exit 0
}

Write-Host ""
Write-Host "### Schritt 4/4: Deployment ###" -ForegroundColor Cyan
az deployment group create `
    --resource-group $ResourceGroupName `
    --template-file (Join-Path $RootDir "infra/main.bicep") `
    --parameters (Join-Path $RootDir "infra/parameters/main.$Environment.bicepparam") `
    --query "properties.outputs"

Write-Host ""
Write-Host "==> Deployment abgeschlossen." -ForegroundColor Green
Write-Host "==> Denk daran, das Key-Vault-Secret 'mcp-server-client-secret' zu pruefen, falls der Key Vault zum Zeitpunkt des Entra-Setups noch nicht existierte."
