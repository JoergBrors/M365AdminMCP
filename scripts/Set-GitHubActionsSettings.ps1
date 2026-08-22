#Requires -Version 7.0
<#
.SYNOPSIS
    Setzt GitHub Actions Variables/Secrets fuer Terraform- und App-Service-Deployment.

.DESCRIPTION
    Nutzt Terraform-Outputs fuer App-Namen/Hostnames und die lokale .env fuer Tenant/Subscription.
    AZURE_CLIENT_ID ist die Client-ID der GitHub-OIDC Deployment-App/Service-Principal und muss
    per Parameter, Umgebungsvariable oder .env bereitgestellt werden.

.PARAMETER Environment
    Kurzname der Umgebung, z.B. dev, staging, prod.

.PARAMETER Repo
    GitHub Repo im Format owner/name. Standard: aktuelles gh repo.

.PARAMETER AzureClientId
    Client-ID der GitHub-OIDC Deployment-App/Service-Principal fuer azure/login.

.EXAMPLE
    ./scripts/Set-GitHubActionsSettings.ps1 -Environment dev -AzureClientId 00000000-0000-0000-0000-000000000000
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Environment,
    [string]$Repo,
    [string]$AzureClientId
)

$ErrorActionPreference = "Stop"
$RootDir = Split-Path -Parent $PSScriptRoot
$TfDir = Join-Path $RootDir "terraform"
$EnvFile = Join-Path $RootDir ".env"

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "GitHub CLI (gh) nicht gefunden. Bitte zuerst './scripts/Install-Prerequisites.ps1' ausfuehren."
}
if (-not (Get-Command terraform -ErrorAction SilentlyContinue)) {
    throw "Terraform CLI nicht gefunden. Bitte zuerst './scripts/Install-Prerequisites.ps1' ausfuehren."
}

function Read-DotEnv {
    param([Parameter(Mandatory)][string]$Path)

    $values = @{}
    if (-not (Test-Path $Path)) {
        return $values
    }

    foreach ($line in Get-Content -LiteralPath $Path) {
        $trimmed = $line.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed) -or $trimmed.StartsWith("#")) { continue }
        $parts = $trimmed.Split("=", 2)
        if ($parts.Count -eq 2) {
            $values[$parts[0].Trim()] = $parts[1].Trim()
        }
    }

    return $values
}

$envValues = Read-DotEnv -Path $EnvFile
if ([string]::IsNullOrWhiteSpace($AzureClientId)) {
    $AzureClientId = if ($env:AZURE_CLIENT_ID) { $env:AZURE_CLIENT_ID } else { $envValues["AZURE_CLIENT_ID"] }
}

$tenantId = if ($envValues["ENTRA_TENANT_ID"]) { $envValues["ENTRA_TENANT_ID"] } else { $envValues["AZURE_TENANT_ID"] }
$subscriptionId = $envValues["AZURE_SUBSCRIPTION_ID"]

if ([string]::IsNullOrWhiteSpace($tenantId)) {
    throw "Tenant-ID fehlt. Bitte .env mit AZURE_TENANT_ID/ENTRA_TENANT_ID pflegen."
}
if ([string]::IsNullOrWhiteSpace($subscriptionId)) {
    throw "Subscription-ID fehlt. Bitte .env mit AZURE_SUBSCRIPTION_ID pflegen."
}

if ([string]::IsNullOrWhiteSpace($Repo)) {
    $Repo = gh repo view --json nameWithOwner -q ".nameWithOwner"
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($Repo)) {
        throw "GitHub Repo konnte nicht ermittelt werden. Bitte -Repo owner/name angeben."
    }
}

Push-Location $TfDir
try {
    $outputs = terraform output -json | ConvertFrom-Json
}
finally {
    Pop-Location
}

function Get-TerraformOutputValue {
    param(
        [Parameter(Mandatory)][object]$Outputs,
        [Parameter(Mandatory)][string]$Name
    )

    $property = $Outputs.PSObject.Properties[$Name]
    if (-not $property -or [string]::IsNullOrWhiteSpace($property.Value.value)) {
        throw "Terraform-Output '$Name' fehlt. Wurde Terraform Apply fuer '$Environment' bereits ausgefuehrt?"
    }

    return [string]$property.Value.value
}

$apiAppHostname = Get-TerraformOutputValue $outputs "api_app_hostname"
$mcpAppHostname = Get-TerraformOutputValue $outputs "mcp_app_hostname"
$apiAppName = $apiAppHostname -replace "\.azurewebsites\.net$", ""
$mcpAppName = $mcpAppHostname -replace "\.azurewebsites\.net$", ""

$variables = [ordered]@{
    "ENVIRONMENT_NAME" = $Environment
    "API_APP_NAME" = $apiAppName
    "MCP_APP_NAME" = $mcpAppName
    "API_APP_HOSTNAME" = $apiAppHostname
    "MCP_APP_HOSTNAME" = $mcpAppHostname
    "API_APP_ID" = Get-TerraformOutputValue $outputs "api_app_id"
    "API_APP_IDENTIFIER_URI" = Get-TerraformOutputValue $outputs "api_app_identifier_uri"
    "MCP_APP_ID" = Get-TerraformOutputValue $outputs "mcp_app_id"
    "KEY_VAULT_NAME" = Get-TerraformOutputValue $outputs "key_vault_name"
}

Write-Host "==> Setze GitHub Actions Variables fuer $Repo ..." -ForegroundColor Cyan
foreach ($key in $variables.Keys) {
    gh variable set $key --body $variables[$key] --repo $Repo
    if ($LASTEXITCODE -ne 0) {
        throw "gh variable set $key ist fehlgeschlagen."
    }
}

Write-Host "==> Setze GitHub Actions Secrets fuer Azure OIDC ..." -ForegroundColor Cyan
gh secret set AZURE_TENANT_ID --body $tenantId --repo $Repo
if ($LASTEXITCODE -ne 0) { throw "gh secret set AZURE_TENANT_ID ist fehlgeschlagen." }

gh secret set AZURE_SUBSCRIPTION_ID --body $subscriptionId --repo $Repo
if ($LASTEXITCODE -ne 0) { throw "gh secret set AZURE_SUBSCRIPTION_ID ist fehlgeschlagen." }

if ([string]::IsNullOrWhiteSpace($AzureClientId)) {
    Write-Warning "AZURE_CLIENT_ID wurde nicht gesetzt. Bitte erneut mit -AzureClientId <deployment-app-client-id> ausfuehren oder AZURE_CLIENT_ID in .env hinterlegen."
}
else {
    gh secret set AZURE_CLIENT_ID --body $AzureClientId --repo $Repo
    if ($LASTEXITCODE -ne 0) { throw "gh secret set AZURE_CLIENT_ID ist fehlgeschlagen." }
}

Write-Host "GitHub Actions Settings aktualisiert fuer $Repo" -ForegroundColor Green
