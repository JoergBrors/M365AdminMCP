#Requires -Version 7.0
<#
.SYNOPSIS
    Terraform-Aequivalent zu Invoke-BicepWhatIf.ps1: zeigt VOR jedem Deployment den vollstaendigen
    Diff - fuer Azure-Ressourcen UND Entra-ID-Objekte in einem Lauf (gemeinsamer State).

.PARAMETER Environment
    Kurzname der Umgebung, z.B. dev, staging, prod.

.EXAMPLE
    ./scripts/Invoke-TerraformPlan.ps1 -Environment dev
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Environment
)

$ErrorActionPreference = "Stop"
$RootDir = Split-Path -Parent $PSScriptRoot
$TfDir = Join-Path $RootDir "terraform"
$ReportsDir = Join-Path $RootDir "reports"
New-Item -ItemType Directory -Path $ReportsDir -Force | Out-Null
$Timestamp = Get-Date -AsUTC -Format "yyyyMMddTHHmmssZ"
$PlanBin = Join-Path $ReportsDir "tfplan-$Environment-$Timestamp.bin"
$PlanMd = Join-Path $ReportsDir "tfplan-$Environment-$Timestamp.md"
$LatestPlanPointer = Join-Path $ReportsDir ".latest-plan-$Environment"

if (-not (Get-Command terraform -ErrorAction SilentlyContinue)) {
    throw "Terraform CLI nicht gefunden. Bitte zuerst './scripts/Install-Prerequisites.ps1' ausfuehren."
}

Push-Location $TfDir
try {
    Write-Host "==> terraform init ..." -ForegroundColor Cyan
    terraform init -input=false -upgrade=false

    Write-Host "==> terraform validate ..." -ForegroundColor Cyan
    terraform validate

    Write-Host "==> terraform plan ..." -ForegroundColor Cyan
    terraform plan -var-file="terraform.$Environment.tfvars" -out=$PlanBin -input=false

    $showOutput = terraform show -no-color $PlanBin
    $lines = @(
        "# Terraform Plan Report - Umgebung: $Environment",
        "_Erstellt: $(Get-Date -AsUTC -Format 'yyyy-MM-dd HH:mm:ssZ')_",
        "",
        '```',
        $showOutput,
        '```'
    )
    $lines | Set-Content -Path $PlanMd -Encoding utf8NoBOM

    Write-Host "Plan-Report geschrieben nach: $PlanMd" -ForegroundColor Green
    Write-Host "Binaer-Plan (fuer apply) liegt unter: $PlanBin"

    $planJson = terraform show -json $PlanBin | ConvertFrom-Json
    $destroyCount = @($planJson.resource_changes | Where-Object { $_.change.actions -contains "delete" }).Count
    if ($destroyCount -gt 0) {
        Write-Warning "Plan enthaelt $destroyCount geplante Loeschung(en). Bitte Report manuell pruefen: $PlanMd"
    }

    $PlanBin | Set-Content -Path $LatestPlanPointer -Encoding utf8NoBOM
}
finally {
    Pop-Location
}
