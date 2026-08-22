#Requires -Version 7.0
<#
.SYNOPSIS
    Schreibt Terraform-Outputs in lokale Entwicklungs-Konfigurationen.

.DESCRIPTION
    Erstellt/aktualisiert die ignorierten Dateien:
    - .env
    - src/ApiServer/appsettings.Development.json
    - src/McpServer/appsettings.Development.json

    Der MCP Client-Secret-Wert wird aus dem von Terraform erzeugten Key Vault gelesen.

.PARAMETER Environment
    Kurzname der Umgebung, z.B. dev, staging, prod.

.EXAMPLE
    ./scripts/Export-TerraformLocalSettings.ps1 -Environment dev
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Environment
)

$ErrorActionPreference = "Stop"
$RootDir = Split-Path -Parent $PSScriptRoot
$TfDir = Join-Path $RootDir "terraform"
$EnvFile = Join-Path $RootDir ".env"
$ApiSettingsFile = Join-Path $RootDir "src/ApiServer/appsettings.Development.json"
$McpSettingsFile = Join-Path $RootDir "src/McpServer/appsettings.Development.json"

if (-not (Get-Command terraform -ErrorAction SilentlyContinue)) {
    throw "Terraform CLI nicht gefunden. Bitte zuerst './scripts/Install-Prerequisites.ps1' ausfuehren."
}
if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw "Azure CLI (az) nicht gefunden. Bitte zuerst './scripts/Install-Prerequisites.ps1' ausfuehren."
}

& (Join-Path $PSScriptRoot "Connect-Azure.ps1")

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
        throw "Terraform-Output '$Name' fehlt. Wurde 'Invoke-TerraformApply.ps1 -Environment $Environment' bereits erfolgreich ausgefuehrt?"
    }

    return [string]$property.Value.value
}

$apiAppHostname = Get-TerraformOutputValue $outputs "api_app_hostname"
$apiAppId = Get-TerraformOutputValue $outputs "api_app_id"
$apiAppIdUri = Get-TerraformOutputValue $outputs "api_app_identifier_uri"
$keyVaultName = Get-TerraformOutputValue $outputs "key_vault_name"
$mcpAppHostname = Get-TerraformOutputValue $outputs "mcp_app_hostname"
$mcpAppId = Get-TerraformOutputValue $outputs "mcp_app_id"

$account = az account show -o json | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or -not $account) {
    throw "az account show ist fehlgeschlagen - kein gueltiger Azure-Kontext aktiv."
}
$tenantId = [string]$account.tenantId
$apiBaseUrl = "https://$apiAppHostname"

Write-Host "==> Lese MCP Client Secret aus Key Vault '$keyVaultName' ..." -ForegroundColor Cyan
$mcpClientSecret = az keyvault secret show `
    --vault-name $keyVaultName `
    --name "mcp-server-client-secret" `
    --query value `
    -o tsv
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($mcpClientSecret)) {
    throw "MCP Client Secret konnte nicht aus Key Vault '$keyVaultName' gelesen werden."
}

function Update-DotEnvFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][System.Collections.IDictionary]$Values
    )

    $lines = [System.Collections.Generic.List[string]]::new()
    if (Test-Path $Path) {
        foreach ($existingLine in Get-Content -LiteralPath $Path) {
            $lines.Add($existingLine)
        }
    }

    foreach ($key in $Values.Keys) {
        $line = "$key=$($Values[$key])"
        $updated = $false

        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match "^\s*$([regex]::Escape($key))\s*=") {
                $lines[$i] = $line
                $updated = $true
                break
            }
        }

        if (-not $updated) {
            $lines.Add($line)
        }
    }

    $lines | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
}

Update-DotEnvFile -Path $EnvFile -Values ([ordered]@{
    "LOCAL_ENVIRONMENT" = $Environment
    "ENTRA_TENANT_ID" = $tenantId
    "API_APP_HOSTNAME" = $apiAppHostname
    "API_APP_ID" = $apiAppId
    "API_APP_ID_URI" = $apiAppIdUri
    "MCP_APP_HOSTNAME" = $mcpAppHostname
    "MCP_APP_ID" = $mcpAppId
    "KEY_VAULT_NAME" = $keyVaultName
    "API_SERVER_BASE_URL" = $apiBaseUrl
})

$apiSettings = [ordered]@{
    "Logging" = [ordered]@{
        "LogLevel" = [ordered]@{
            "Default" = "Information"
            "Microsoft.AspNetCore" = "Warning"
        }
    }
    "AzureAd" = [ordered]@{
        "Instance" = "https://login.microsoftonline.com/"
        "TenantId" = $tenantId
        "ClientId" = $apiAppId
        "Audience" = $apiAppIdUri
    }
    "SwaggerOAuth" = [ordered]@{
        "ClientId" = $apiAppId
        "Scope" = "$apiAppIdUri/Tasks.ReadWrite"
    }
}

$mcpSettings = [ordered]@{
    "Logging" = [ordered]@{
        "LogLevel" = [ordered]@{
            "Default" = "Information"
            "Microsoft.AspNetCore" = "Warning"
        }
    }
    "AzureAd" = [ordered]@{
        "TenantId" = $tenantId
        "ClientId" = $mcpAppId
        "ClientSecret" = $mcpClientSecret
        "ApiAppIdUri" = $apiAppIdUri
    }
    "ApiServer" = [ordered]@{
        "BaseUrl" = $apiBaseUrl
    }
}

$apiSettings | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $ApiSettingsFile -Encoding utf8NoBOM
$mcpSettings | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $McpSettingsFile -Encoding utf8NoBOM

Write-Host "Lokale .env aktualisiert: $EnvFile" -ForegroundColor Green
Write-Host "ApiServer Development-Settings geschrieben: $ApiSettingsFile" -ForegroundColor Green
Write-Host "McpServer Development-Settings geschrieben: $McpSettingsFile" -ForegroundColor Green
