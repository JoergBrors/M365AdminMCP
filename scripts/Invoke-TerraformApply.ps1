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
    [switch]$Force,
    [switch]$AutoApprove
)

$ErrorActionPreference = "Stop"
$RootDir = Split-Path -Parent $PSScriptRoot
$TfDir = Join-Path $RootDir "terraform"
$ReportsDir = Join-Path $RootDir "reports"
$LatestPlanPointer = Join-Path $ReportsDir ".latest-plan-$Environment"

function Invoke-Terraform {
    param(
        [Parameter(Mandatory)][string[]]$Arguments
    )

    & terraform @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "terraform $($Arguments -join ' ') ist fehlgeschlagen (ExitCode: $LASTEXITCODE)."
    }
}

Write-Host "### Schritt 1/3: Terraform Plan (Pflicht) ###" -ForegroundColor Cyan
& (Join-Path $PSScriptRoot "Invoke-TerraformPlan.ps1") -Environment $Environment

if (-not (Test-Path $LatestPlanPointer)) {
    throw "Kein Plan gefunden - Abbruch."
}
$planBin = (Get-Content $LatestPlanPointer -Raw).Trim()

Push-Location $TfDir
try {
    $planJsonText = & terraform show -json $planBin
    if ($LASTEXITCODE -ne 0) {
        throw "terraform show -json $planBin ist fehlgeschlagen (ExitCode: $LASTEXITCODE)."
    }
    $planJson = $planJsonText | ConvertFrom-Json
    $destroyCount = @($planJson.resource_changes | Where-Object { $_.change.actions -contains "delete" }).Count

    if ($destroyCount -gt 0 -and -not $Force) {
        Write-Error "Plan enthaelt $destroyCount Loeschung(en). Abbruch. Falls beabsichtigt, erneut mit -Force aufrufen."
        exit 1
    }

    if (-not $AutoApprove) {
        Write-Host ""
        Write-Host "### Schritt 2/3: Bestaetigung ###" -ForegroundColor Cyan
        $confirm = Read-Host "Terraform Apply fuer '$Environment' jetzt ausfuehren? [y/N]"
        if ($confirm -ne "y" -and $confirm -ne "Y") {
            Write-Host "Abgebrochen."
            exit 0
        }
    }
    else {
        Write-Host ""
        Write-Host "### Schritt 2/3: Bestaetigung uebersprungen (-AutoApprove) ###" -ForegroundColor Cyan
    }

    Write-Host ""
    Write-Host "### Schritt 3/3: Apply ###" -ForegroundColor Cyan
    Invoke-Terraform @("apply", "-input=false", $planBin)
    Invoke-Terraform @("output")
}
finally {
    Pop-Location
}

Write-Host ""
Write-Host "### Lokale Entwicklungs-Konfiguration aktualisieren ###" -ForegroundColor Cyan
& (Join-Path $PSScriptRoot "Export-TerraformLocalSettings.ps1") -Environment $Environment
