#Requires -Version 7.0
<#
.SYNOPSIS
    Initialisiert das lokale Git-Repo (falls noetig) und legt per GitHub CLI (gh) ein
    GitHub-Repository an, inkl. Remote + initialem Push. Installiert gh automatisch,
    falls nicht vorhanden.

.PARAMETER Name
    Name des GitHub-Repositories.

.PARAMETER Owner
    Optional: GitHub-Organisation/-Nutzer, unter dem das Repo angelegt wird. Standard: eigener Account.

.PARAMETER Visibility
    "private" (Standard), "public" oder "internal".

.EXAMPLE
    ./scripts/New-GitHubRepo.ps1 -Name entra-mcp-mvp -Visibility private
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Name,
    [string]$Owner,
    [ValidateSet("private", "public", "internal")][string]$Visibility = "private",
    [string]$Description = "Entra ID + MCP/API Server MVP Scaffold"
)

$ErrorActionPreference = "Stop"
$RootDir = Split-Path -Parent $PSScriptRoot

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Write-Host "==> GitHub CLI (gh) nicht gefunden - installiere ..." -ForegroundColor Yellow
    & "$PSScriptRoot/Install-Prerequisites.ps1" -SkipAzureCli -SkipTerraform
}

$ghAuthStatus = gh auth status 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "==> Nicht bei GitHub angemeldet - starte 'gh auth login' ..." -ForegroundColor Yellow
    gh auth login
}

Push-Location $RootDir
try {
    if (-not (Test-Path (Join-Path $RootDir ".git"))) {
        Write-Host "==> git init ..." -ForegroundColor Cyan
        git init | Out-Null
        git add -A
        git commit -m "init: Entra ID + MCP/API Server MVP Scaffold" | Out-Null
    }

    $repoSlug = if ($Owner) { "$Owner/$Name" } else { $Name }

    Write-Host "==> Lege GitHub-Repo '$repoSlug' an (Sichtbarkeit: $Visibility) ..." -ForegroundColor Cyan
    $existing = gh repo view $repoSlug 2>&1
    if ($LASTEXITCODE -eq 0) {
        Write-Host "    Repo existiert bereits, verknuepfe nur den Remote." -ForegroundColor Yellow
        gh repo set-default $repoSlug
    }
    else {
        gh repo create $repoSlug --$Visibility --description $Description --source $RootDir --remote origin --push
        Write-Host "==> Repo angelegt und Code gepusht." -ForegroundColor Green
        return
    }

    if (-not (git remote | Select-String -Pattern "^origin$" -Quiet)) {
        $remoteUrl = gh repo view $repoSlug --json url -q ".url"
        git remote add origin "$remoteUrl.git"
    }

    git push -u origin HEAD
    Write-Host "==> Fertig: $repoSlug" -ForegroundColor Green
}
finally {
    Pop-Location
}
