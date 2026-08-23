#Requires -Version 7.0
<#
.SYNOPSIS
    Prueft und installiert (falls fehlend) die fuer dieses Repo benoetigten CLIs/SDKs:
    .NET SDK (>= 10.0, aktuelle LTS), Azure CLI (az), GitHub CLI (gh), Terraform.
    Funktioniert unter macOS, Windows und Linux.

.EXAMPLE
    ./scripts/Install-Prerequisites.ps1

.EXAMPLE
    ./scripts/Install-Prerequisites.ps1 -SkipTerraform
#>
[CmdletBinding()]
param(
    [switch]$SkipDotnet,
    [switch]$SkipAzureCli,
    [switch]$SkipGitHubCli,
    [switch]$SkipTerraform,
    [int]$MinDotnetMajorVersion = 10
)

$ErrorActionPreference = "Stop"

function Test-CommandExists {
    param([Parameter(Mandatory)][string]$Name)
    return [bool](Get-Command $Name -ErrorAction SilentlyContinue)
}

function Install-Tool {
    param(
        [Parameter(Mandatory)][string]$DisplayName,
        [Parameter(Mandatory)][string]$CommandName,
        [Parameter(Mandatory)][string]$BrewFormula,
        [Parameter(Mandatory)][string]$WingetId,
        [string]$LinuxInstallScript,
        [string]$ManualInstallUrl
    )

    if (Test-CommandExists $CommandName) {
        Write-Host "OK: $DisplayName ist bereits installiert ($((Get-Command $CommandName).Source))" -ForegroundColor Green
        return
    }

    Write-Host "==> $DisplayName nicht gefunden - installiere ..." -ForegroundColor Yellow

    if ($IsMacOS) {
        if (-not (Test-CommandExists "brew")) {
            throw "Homebrew fehlt. Bitte zuerst https://brew.sh installieren und dieses Skript erneut ausfuehren."
        }
        brew install $BrewFormula
    }
    elseif ($IsWindows) {
        if (-not (Test-CommandExists "winget")) {
            throw "winget fehlt. Bitte $DisplayName manuell installieren: $ManualInstallUrl"
        }
        winget install --id $WingetId --accept-source-agreements --accept-package-agreements
    }
    elseif ($IsLinux) {
        if ([string]::IsNullOrWhiteSpace($LinuxInstallScript)) {
            throw "Keine automatische Linux-Installation hinterlegt fuer $DisplayName. Bitte manuell installieren: $ManualInstallUrl"
        }
        Invoke-Expression $LinuxInstallScript
    }
    else {
        throw "Unbekanntes Betriebssystem. Bitte $DisplayName manuell installieren: $ManualInstallUrl"
    }

    if (-not (Test-CommandExists $CommandName)) {
        Write-Warning "$DisplayName scheint installiert, ist aber noch nicht im PATH sichtbar. Bitte Terminal/PowerShell neu starten und erneut pruefen."
    }
    else {
        Write-Host "OK: $DisplayName installiert." -ForegroundColor Green
    }
}

function Test-DotnetSdkVersion {
    param([Parameter(Mandatory)][int]$MinMajorVersion)

    if (-not (Test-CommandExists "dotnet")) { return $false }

    $sdks = & dotnet --list-sdks 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $sdks) { return $false }

    foreach ($line in $sdks) {
        if ($line -match '^(\d+)\.') {
            if ([int]$Matches[1] -ge $MinMajorVersion) { return $true }
        }
    }
    return $false
}

Write-Host "=== Pruefe Voraussetzungen ===" -ForegroundColor Cyan

if (-not $SkipDotnet) {
    if (Test-DotnetSdkVersion -MinMajorVersion $MinDotnetMajorVersion) {
        $installedSdks = (& dotnet --list-sdks 2>$null) -join ", "
        Write-Host "OK: .NET SDK >= $MinDotnetMajorVersion.0 gefunden ($installedSdks)" -ForegroundColor Green
    }
    else {
        Write-Host "==> .NET SDK >= $MinDotnetMajorVersion.0 nicht gefunden - installiere aktuelle LTS ..." -ForegroundColor Yellow

        if ($IsMacOS) {
            if (-not (Test-CommandExists "brew")) {
                throw "Homebrew fehlt. Bitte zuerst https://brew.sh installieren und dieses Skript erneut ausfuehren."
            }
            brew install --cask dotnet-sdk
        }
        elseif ($IsWindows) {
            if (-not (Test-CommandExists "winget")) {
                throw "winget fehlt. Bitte .NET SDK manuell installieren: https://dotnet.microsoft.com/download"
            }
            winget install --id Microsoft.DotNet.SDK.$MinDotnetMajorVersion --accept-source-agreements --accept-package-agreements
        }
        elseif ($IsLinux) {
            Write-Host "    Linux: offizielles Install-Skript wird verwendet (aka.ms/dotnet/install.sh)."
            $installScript = Join-Path ([System.IO.Path]::GetTempPath()) "dotnet-install.sh"
            Invoke-WebRequest -Uri "https://dot.net/v1/dotnet-install.sh" -OutFile $installScript
            chmod +x $installScript
            & $installScript --channel $MinDotnetMajorVersion.0
        }
        else {
            throw "Unbekanntes Betriebssystem. Bitte .NET SDK $MinDotnetMajorVersion manuell installieren: https://dotnet.microsoft.com/download"
        }

        if (-not (Test-DotnetSdkVersion -MinMajorVersion $MinDotnetMajorVersion)) {
            Write-Warning ".NET SDK scheint installiert, ist aber noch nicht im PATH sichtbar oder hat nicht die erwartete Version. Bitte Terminal/PowerShell neu starten und erneut pruefen ('dotnet --list-sdks')."
        }
        else {
            Write-Host "OK: .NET SDK $MinDotnetMajorVersion installiert." -ForegroundColor Green
        }
    }
}

if (-not $SkipAzureCli) {
    Install-Tool -DisplayName "Azure CLI" -CommandName "az" `
        -BrewFormula "azure-cli" -WingetId "Microsoft.AzureCLI" `
        -LinuxInstallScript "curl -sL https://aka.ms/InstallAzureCLIDeb | sudo bash" `
        -ManualInstallUrl "https://learn.microsoft.com/cli/azure/install-azure-cli"
}

if (-not $SkipGitHubCli) {
    Install-Tool -DisplayName "GitHub CLI" -CommandName "gh" `
        -BrewFormula "gh" -WingetId "GitHub.cli" `
        -LinuxInstallScript "sudo apt-get update && sudo apt-get install -y gh" `
        -ManualInstallUrl "https://github.com/cli/cli#installation"
}

if (-not $SkipTerraform) {
    Install-Tool -DisplayName "Terraform" -CommandName "terraform" `
        -BrewFormula "hashicorp/tap/terraform" -WingetId "Hashicorp.Terraform" `
        -LinuxInstallScript "sudo apt-get update && sudo apt-get install -y terraform" `
        -ManualInstallUrl "https://developer.hashicorp.com/terraform/install"
}

Write-Host ""
Write-Host "=== Fertig. Naechster Schritt: ./scripts/New-GitHubRepo.ps1 oder ./scripts/Set-EntraIdApps.ps1 -Environment dev ===" -ForegroundColor Cyan
