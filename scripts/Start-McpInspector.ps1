#Requires -Version 7.0
<#
.SYNOPSIS
    Startet den MCP Inspector (v2, gepinnt) fuer das lokale VS-Code-Debugging und verbindet ihn automatisch
    mit dem lokalen MCP-Server.
.DESCRIPTION
    Ablauf:
      1. Voraussetzungen pruefen (Node >= 22.19.0, lokale appsettings.Development.json, gepinnte Inspector-Version).
      2. Zufaelligen, sitzungsgebundenen API-Token erzeugen (nur im Speicher; nie in Dateien/Git).
      3. Inspector an 127.0.0.1:6274 starten (MCP_INSPECTOR_API_TOKEN, kein Auto-Open des Inspectors selbst).
         Die Inspector-Ausgabe wird durchgereicht, der Token wird dabei maskiert.
      4. Marker "[inspector] ready" ausgeben (VS-Code-Task gilt damit als bereit -> Debug-Sessions starten).
      5. Warten, bis ApiServer und McpServer auf /healthz antworten.
      6. Browser mit v2-Deep-Link oeffnen: ?serverUrl=...&transport=http&autoConnect=<Token>
      7. Bleibt aktiv, bis der Inspector beendet wird (Stop-LocalDebugServers.ps1 / Task beenden).
    Der Inspector wird NICHT mit DANGEROUSLY_OMIT_AUTH gestartet; der Token schuetzt die Inspector-Oberflaeche.
.PARAMETER McpUrl
    MCP-Endpunkt, mit dem der Inspector verbunden wird.
.PARAMETER HealthUrls
    Health-Endpunkte, die vor dem Oeffnen des Browsers antworten muessen.
.PARAMETER HealthTimeoutSec
    Maximale Wartezeit auf die Health-Endpunkte.
.PARAMETER NoBrowser
    Browser nicht oeffnen (Test/CI). Die Deep-Link-URL wird NICHT ausgegeben (enthaelt den Token).
#>
[CmdletBinding()]
param(
    [string]$McpUrl = "http://localhost:5143/mcp",
    [string[]]$HealthUrls = @("http://localhost:5043/healthz", "http://localhost:5143/healthz"),
    [int]$HealthTimeoutSec = 180,
    [switch]$NoBrowser
)

$ErrorActionPreference = "Stop"
$RootDir = Split-Path -Parent $PSScriptRoot
$InspectorDir = Join-Path $RootDir "tools/mcp-inspector"
$InspectorPort = 6274
$InspectorHost = "127.0.0.1"
$MinNode = [version]"22.19.0"

function Write-Inspector([string]$Message) { Write-Host "[inspector] $Message" }
function Fail([string]$Message) { Write-Host "[inspector] ERROR $Message"; exit 1 }

Write-Inspector "starting"

# --- 1. Voraussetzungen ---------------------------------------------------------------------------
if (-not (Get-Command node -ErrorAction SilentlyContinue)) { Fail "Node.js nicht gefunden. Node >= $MinNode installieren." }
$nodeVersion = [version]((node --version).TrimStart("v"))
if ($nodeVersion -lt $MinNode) { Fail "Node $nodeVersion gefunden, benoetigt wird >= $MinNode (Inspector v2)." }

foreach ($settings in @("src/ApiServer/appsettings.Development.json", "src/McpServer/appsettings.Development.json")) {
    $path = Join-Path $RootDir $settings
    if (-not (Test-Path $path)) {
        Fail "$settings fehlt. Erzeugen mit: pwsh ./scripts/Export-TerraformLocalSettings.ps1 -Environment dev"
    }
    $json = Get-Content $path -Raw | ConvertFrom-Json
    $secret = [string]$json.AzureAd.ClientSecret
    if ([string]::IsNullOrWhiteSpace($secret) -or $secret.StartsWith("__")) {
        Fail "$settings enthaelt kein gueltiges AzureAd:ClientSecret. Export-TerraformLocalSettings.ps1 ausfuehren."
    }
}

$pin = (Get-Content (Join-Path $InspectorDir "package.json") -Raw | ConvertFrom-Json).dependencies."@modelcontextprotocol/inspector"
$installedPackage = Join-Path $InspectorDir "node_modules/@modelcontextprotocol/inspector/package.json"
$installed = if (Test-Path $installedPackage) { (Get-Content $installedPackage -Raw | ConvertFrom-Json).version } else { $null }
if ($installed -ne $pin) {
    Write-Inspector "installiere gepinnten Inspector $pin (npm ci) ..."
    Push-Location $InspectorDir
    try {
        npm ci --ignore-scripts --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { Fail "npm ci fehlgeschlagen (ExitCode $LASTEXITCODE)." }
    }
    finally { Pop-Location }
}
$launcher = Join-Path $InspectorDir "node_modules/@modelcontextprotocol/inspector/clients/launcher/build/index.js"
if (-not (Test-Path $launcher)) { Fail "Inspector-Launcher nicht gefunden: $launcher" }

# --- 2. Token (nur im Speicher) -------------------------------------------------------------------
$tokenBytes = [byte[]]::new(32)
[System.Security.Cryptography.RandomNumberGenerator]::Fill($tokenBytes)
$token = [Convert]::ToHexString($tokenBytes).ToLowerInvariant()

# --- 3. Inspector starten -------------------------------------------------------------------------
$psi = [System.Diagnostics.ProcessStartInfo]::new("node")
$psi.ArgumentList.Add($launcher)
$psi.WorkingDirectory = $InspectorDir
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.Environment["MCP_INSPECTOR_API_TOKEN"] = $token
$psi.Environment["CLIENT_PORT"] = "$InspectorPort"
$psi.Environment["HOST"] = $InspectorHost
$psi.Environment["MCP_AUTO_OPEN_ENABLED"] = "false"   # Browser oeffnet dieses Skript selbst (mit Deep-Link)
[void]$psi.Environment.Remove("DANGEROUSLY_OMIT_AUTH")
[void]$psi.Environment.Remove("MCP_PROXY_AUTH_TOKEN")

$process = [System.Diagnostics.Process]::new()
$process.StartInfo = $psi
[void]$process.Start()

function Open-Browser([string]$Url) {
    if ($IsWindows) { Start-Process $Url }
    elseif ($IsMacOS) { & open $Url }
    else { & xdg-open $Url }
}

$deepLink = "http://${InspectorHost}:$InspectorPort/?MCP_INSPECTOR_API_TOKEN=$token" +
    "&serverUrl=$([uri]::EscapeDataString($McpUrl))&transport=http&autoConnect=$token"

$stdout = $process.StandardOutput.ReadLineAsync()
$stderr = $process.StandardError.ReadLineAsync()
$state = "waitBanner"
$bannerDeadline = [DateTime]::UtcNow.AddSeconds(60)
$healthDeadline = $null
$nextHealthCheck = [DateTime]::MinValue

try {
    while ($true) {
        foreach ($stream in @("out", "err")) {
            $task = if ($stream -eq "out") { $stdout } else { $stderr }
            if ($task.IsCompleted) {
                $line = $task.Result
                if ($null -ne $line) {
                    $masked = $line.Replace($token, "***")
                    # Banner-Zeile mit Token-URL und "Auth token:"-Zeile nicht ausgeben
                    if ($masked -notmatch "Auth token:" -and $masked -notmatch "MCP_INSPECTOR_API_TOKEN=") {
                        Write-Host "[inspector:node] $masked"
                    }
                    if ($line -match "MCP Inspector Web is up and running") { $script:bannerSeen = $true }
                    if ($stream -eq "out") { $stdout = $process.StandardOutput.ReadLineAsync() }
                    else { $stderr = $process.StandardError.ReadLineAsync() }
                }
            }
        }

        if ($state -eq "waitBanner") {
            if ($script:bannerSeen) {
                Write-Inspector "ready (http://${InspectorHost}:$InspectorPort, Token verborgen) - warte auf /healthz von API und MCP"
                $state = "waitHealth"
                $healthDeadline = [DateTime]::UtcNow.AddSeconds($HealthTimeoutSec)
            }
            elseif ($process.HasExited) { Fail "Inspector-Prozess unerwartet beendet (ExitCode $($process.ExitCode))." }
            elseif ([DateTime]::UtcNow -gt $bannerDeadline) { Fail "Inspector meldet sich nicht innerhalb von 60 s." }
        }
        elseif ($state -eq "waitHealth" -and [DateTime]::UtcNow -ge $nextHealthCheck) {
            $nextHealthCheck = [DateTime]::UtcNow.AddSeconds(1)
            $pending = @()
            foreach ($url in $HealthUrls) {
                try {
                    $response = Invoke-WebRequest -Uri $url -TimeoutSec 2 -SkipHttpErrorCheck
                    if ($response.StatusCode -ne 200) { $pending += $url }
                }
                catch { $pending += $url }
            }
            if ($pending.Count -eq 0) {
                Write-Inspector "API und MCP sind erreichbar - oeffne Browser mit Auto-Connect auf $McpUrl"
                if ($NoBrowser) { Write-Inspector "NoBrowser gesetzt - Browser nicht geoeffnet." }
                else { Open-Browser $deepLink }
                $state = "running"
            }
            elseif ([DateTime]::UtcNow -gt $healthDeadline) {
                Fail "Health-Check nach $HealthTimeoutSec s nicht erfolgreich: $($pending -join ', ')"
            }
        }

        if ($process.HasExited -and $stdout.IsCompleted -and $stderr.IsCompleted -and $null -eq $stdout.Result -and $null -eq $stderr.Result) { break }
        Start-Sleep -Milliseconds 100
    }
    Write-Inspector "Inspector beendet (ExitCode $($process.ExitCode))."
}
finally {
    if (-not $process.HasExited) { $process.Kill($true) }
}
