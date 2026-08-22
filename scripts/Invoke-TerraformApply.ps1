#Requires -Version 7.0
<#
.SYNOPSIS
    Wendet exakt den zuvor mit Invoke-TerraformPlan.ps1 erzeugten Plan an (kein "blindes" apply).

.PARAMETER Environment
    Kurzname der Umgebung, z.B. dev, staging, prod.

.PARAMETER Force
    Erlaubt das Apply auch wenn der Plan Loeschungen enthaelt.

.EXAMPLE
    ./scripts/Invoke-TerraformApply.ps1 -Environment dev
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Environment,
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$RootDir = Split-Path -Parent $PSScriptRoot
$TfDir = Join-Path $RootDir "terraform"
$ReportsDir = Join-Path $RootDir "reports"
$LatestPlanPointer = Join-Path $ReportsDir ".latest-plan-$Environment"

Write-Host "### Schritt 1/3: Terraform Plan (Pflicht) ###" -ForegroundColor Cyan
& (Join-Path $PSScriptRoot "Invoke-TerraformPlan.ps1") -Environment $Environment

if (-not (Test-Path $LatestPlanPointer)) {
    throw "Kein Plan gefunden - Abbruch."
}
$planBin = Get-Content $LatestPlanPointer -Raw

Push-Location $TfDir
try {
    $planJson = terraform show -json $planBin | ConvertFrom-Json
    $destroyCount = @($planJson.resource_changes | Where-Object { $_.change.actions -contains "delete" }).Count

    if ($destroyCount -gt 0 -and -not $Force) {
        Write-Error "Plan enthaelt $destroyCount Loeschung(en). Abbruch. Falls beabsichtigt, erneut mit -Force aufrufen."
        exit 1
    }

    Write-Host ""
    Write-Host "### Schritt 2/3: Bestaetigung ###" -ForegroundColor Cyan
    $confirm = Read-Host "Terraform Apply fuer '$Environment' jetzt ausfuehren? [y/N]"
    if ($confirm -ne "y" -and $confirm -ne "Y") {
        Write-Host "Abgebrochen."
        exit 0
    }

    Write-Host ""
    Write-Host "### Schritt 3/3: Apply ###" -ForegroundColor Cyan
    terraform apply -input=false $planBin
    terraform output
}
finally {
    Pop-Location
}
