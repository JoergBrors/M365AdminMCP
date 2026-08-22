#Requires -Version 7.0
<#
.SYNOPSIS
    Legt einmalig Storage Account + Container fuer den Terraform Remote State an.

.PARAMETER StorageAccountName
    Global eindeutiger Storage-Account-Name.

.PARAMETER Location
    Azure-Region. Standard: westeurope.

.EXAMPLE
    ./scripts/Initialize-TerraformBackend.ps1 -StorageAccountName sttfstateentramcp01
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$StorageAccountName,
    [string]$Location = "westeurope"
)

$ErrorActionPreference = "Stop"
$ResourceGroupName = "rg-tfstate"

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw "Azure CLI (az) nicht gefunden. Bitte zuerst './scripts/Install-Prerequisites.ps1' ausfuehren."
}

az group create --name $ResourceGroupName --location $Location | Out-Null

az storage account create `
    --name $StorageAccountName `
    --resource-group $ResourceGroupName `
    --location $Location `
    --sku Standard_LRS `
    --min-tls-version TLS1_2 `
    --allow-blob-public-access false | Out-Null

$accountKey = az storage account keys list --account-name $StorageAccountName --resource-group $ResourceGroupName --query "[0].value" -o tsv
az storage container create --name tfstate --account-name $StorageAccountName --account-key $accountKey | Out-Null

Write-Host "Fertig. Trage in terraform/providers.tf im backend-Block ein:" -ForegroundColor Green
Write-Host "  resource_group_name  = `"$ResourceGroupName`""
Write-Host "  storage_account_name = `"$StorageAccountName`""
Write-Host "  container_name       = `"tfstate`""
Write-Host "Dann den backend-Block auskommentieren (Kommentarzeichen entfernen) und 'terraform init' erneut ausfuehren."
