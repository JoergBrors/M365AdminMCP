#Requires -Version 7.0
<#
.SYNOPSIS
    Ergaenzt eine neue OAuth-Redirect-URI (z.B. von einer ChatGPT-Connector-Neuanlage) in
    terraform/terraform.<env>.tfvars und wendet die Aenderung sofort per terraform apply an.

.DESCRIPTION
    Entra ID unterstuetzt weder Dynamic Client Registration (RFC 7591) noch CIMD - deshalb
    generiert z.B. ChatGPT bei jeder Neuanlage eines MCP-Connectors eine neue, zufaellige
    Callback-URL (https://chatgpt.com/connector/oauth/<random>), die manuell als Redirect-URI
    in der jeweiligen App-Registrierung ergaenzt werden muss (siehe docs/DEPLOYMENT.md,
    Abschnitt "Warum DCR/CIMD grau sind"). Dieses Skript automatisiert genau diesen Schritt.

.PARAMETER Client
    Welcher MCP-OAuth-Client betroffen ist: chatgpt, claude oder copilot.

.PARAMETER RedirectUri
    Die neue Redirect-URI, z.B. https://chatgpt.com/connector/oauth/Mw_WfO7C7DHQ.

.PARAMETER Environment
    Kurzname der Umgebung, z.B. dev, staging, prod. Standard: dev.

.EXAMPLE
    ./scripts/Add-McpOauthRedirectUri.ps1 -Client chatgpt -RedirectUri "https://chatgpt.com/connector/oauth/Mw_WfO7C7DHQ"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet("chatgpt", "claude", "copilot")][string]$Client,
    [Parameter(Mandatory)][string]$RedirectUri,
    [string]$Environment = "dev"
)

$ErrorActionPreference = "Stop"
$RootDir = Split-Path -Parent $PSScriptRoot
$TfDir = Join-Path $RootDir "terraform"
$TfVarsFile = Join-Path $TfDir "terraform.$Environment.tfvars"

if (-not (Test-Path $TfVarsFile)) {
    throw "Keine $TfVarsFile gefunden."
}
if ($RedirectUri -notmatch '^https://') {
    throw "RedirectUri muss mit https:// beginnen."
}

$varName = "${Client}_mcp_redirect_uris"
$content = Get-Content $TfVarsFile -Raw

if ($content -match "$varName\s*=\s*\[") {
    if ($content -match [regex]::Escape($RedirectUri)) {
        Write-Host "OK: '$RedirectUri' ist bereits in $varName ($TfVarsFile) enthalten - nichts zu tun." -ForegroundColor Green
    }
    else {
        # Fuegt die neue URI als letztes Element vor der schliessenden Klammer des Arrays ein.
        $pattern = "($varName\s*=\s*\[[^\]]*)"
        $replacement = "`$1,`n  `"$RedirectUri`""
        $newContent = [regex]::Replace($content, $pattern, $replacement, 1)
        Set-Content -Path $TfVarsFile -Value $newContent -Encoding utf8NoBOM -NoNewline
        Write-Host "==> '$RedirectUri' zu $varName in $TfVarsFile hinzugefuegt" -ForegroundColor Cyan
    }
}
else {
    $newBlock = "`n$varName = [`n  `"$RedirectUri`"`n]`n"
    Add-Content -Path $TfVarsFile -Value $newBlock -Encoding utf8NoBOM
    Write-Host "==> $varName mit '$RedirectUri' neu in $TfVarsFile angelegt" -ForegroundColor Cyan
}

Write-Host "==> Wende Aenderung per terraform apply an ..." -ForegroundColor Cyan
& (Join-Path $PSScriptRoot "Invoke-TerraformApply.ps1") -Environment $Environment -AutoApprove

Write-Host ""
Write-Host "==> Fertig. '$Client' akzeptiert jetzt zusaetzlich '$RedirectUri'." -ForegroundColor Green
