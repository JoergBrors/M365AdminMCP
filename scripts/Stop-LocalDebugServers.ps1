#Requires -Version 7.0
<#
.SYNOPSIS
    Beendet die zu DIESEM Repo gehoerenden lokalen Debug-Prozesse (ApiServer, McpServer, MCP Inspector).
.DESCRIPTION
    Es werden nur Prozesse beendet, deren Kommandozeile auf dieses Repo zeigt UND zu einer der Komponenten gehoert:
      - src/ApiServer/bin/...   (ApiServer.dll)
      - src/McpServer/bin/...   (McpServer.dll)
      - tools/mcp-inspector/... (gepinnter MCP Inspector)
    Prozesse, die nur denselben Port belegen, aber nicht zu diesem Repo gehoeren, werden NICHT beendet;
    stattdessen erscheint eine Warnung.
.PARAMETER Ports
    Lokale Ports, die auf fremde Belegung geprueft werden (Warnung). Beenden erfolgt nur per Prozesszuordnung.
#>
[CmdletBinding()]
param(
    [int[]]$Ports = @(5043, 7043, 5143, 7143, 6274, 6275)
)

$ErrorActionPreference = "Stop"
$RootDir = Split-Path -Parent $PSScriptRoot

function ConvertTo-Normalized([string]$Value) {
    return $Value.Replace("\", "/").ToLowerInvariant()
}

$root = ConvertTo-Normalized $RootDir
$componentMarkers = @("$root/src/apiserver/bin/", "$root/src/mcpserver/bin/", "$root/tools/mcp-inspector/")

function Get-ProcessCommandLine([int]$ProcessId) {
    try {
        if ($IsWindows) {
            return (Get-CimInstance -ClassName Win32_Process -Filter "ProcessId=$ProcessId" -ErrorAction Stop).CommandLine
        }
        if ($IsLinux -and (Test-Path "/proc/$ProcessId/cmdline")) {
            return (Get-Content "/proc/$ProcessId/cmdline" -Raw).Replace([char]0, " ")
        }
        return (& ps -p $ProcessId -o command= 2>$null)
    }
    catch { return $null }
}

function Test-OwnedProcess([int]$ProcessId) {
    $commandLine = Get-ProcessCommandLine $ProcessId
    if (-not [string]::IsNullOrWhiteSpace($commandLine)) {
        $normalized = ConvertTo-Normalized $commandLine
        foreach ($marker in $componentMarkers) {
            if ($normalized.Contains($marker)) { return $true }
        }
    }

    # Zweite Zuordnung: Wurde der Server mit relativem DLL-Pfad gestartet (z.B. "dotnet bin/Debug/.../McpServer.dll"),
    # steht das Repo nicht in der Kommandozeile - dann entscheidet die tatsaechlich geladene Komponenten-DLL.
    try {
        $process = Get-Process -Id $ProcessId -ErrorAction Stop
        if ($process.ProcessName -ne "dotnet") { return $false }
        foreach ($module in $process.Modules) {
            $path = ConvertTo-Normalized $module.FileName
            if ($path -eq "$root/src/apiserver/bin/debug/net10.0/apiserver.dll" -or
                $path -eq "$root/src/mcpserver/bin/debug/net10.0/mcpserver.dll" -or
                ($path.StartsWith("$root/src/apiserver/bin/") -and $path.EndsWith("/apiserver.dll")) -or
                ($path.StartsWith("$root/src/mcpserver/bin/") -and $path.EndsWith("/mcpserver.dll"))) {
                return $true
            }
        }
    }
    catch { }
    return $false
}

# Kandidaten: Prozesse (dotnet/node) mit Repo-Kommandozeile + Listener auf den bekannten Ports
$owned = [System.Collections.Generic.HashSet[int]]::new()
$foreign = [System.Collections.Generic.Dictionary[int, string]]::new()

foreach ($name in @("dotnet", "node", "ApiServer", "McpServer")) {
    foreach ($proc in Get-Process -Name $name -ErrorAction SilentlyContinue) {
        if ($proc.Id -ne $PID -and (Test-OwnedProcess $proc.Id)) { [void]$owned.Add($proc.Id) }
    }
}

$listeners = @()
if (Get-Command Get-NetTCPConnection -ErrorAction SilentlyContinue) {
    $listeners = @(Get-NetTCPConnection -LocalPort $Ports -State Listen -ErrorAction SilentlyContinue |
        ForEach-Object { [pscustomobject]@{ Port = $_.LocalPort; ProcessId = [int]$_.OwningProcess } })
}
else {
    foreach ($port in $Ports) {
        foreach ($line in (netstat -ano | Select-String -Pattern "^\s*TCP\s+\S+:$port\s+\S+\s+LISTENING\s+(\d+)\s*$")) {
            $listeners += [pscustomobject]@{ Port = $port; ProcessId = [int]$line.Matches[0].Groups[1].Value }
        }
    }
}
foreach ($listener in $listeners) {
    if ($listener.ProcessId -le 0 -or $owned.Contains($listener.ProcessId)) { continue }
    if (Test-OwnedProcess $listener.ProcessId) { [void]$owned.Add($listener.ProcessId) }
    else { $foreign[$listener.Port] = "PID $($listener.ProcessId)" }
}

foreach ($processId in $owned) {
    try {
        $target = Get-Process -Id $processId -ErrorAction Stop
        $target.Kill($true)
        Write-Host "Beendet: $($target.ProcessName) (PID $processId)"
    }
    catch {
        Write-Host "Uebersprungen: PID ${processId}: $($_.Exception.Message)"
    }
}

foreach ($entry in $foreign.GetEnumerator()) {
    Write-Warning "Port $($entry.Key) wird von einem fremden Prozess belegt ($($entry.Value)) und wird NICHT beendet."
}

# Auf Freigabe der eigenen Ports warten (max. 10 s), damit ein sofortiger Neustart (erneutes F5) nicht kollidiert
$deadline = [DateTime]::UtcNow.AddSeconds(10)
while ($owned.Count -gt 0 -and [DateTime]::UtcNow -lt $deadline) {
    $stillListening = @()
    if (Get-Command Get-NetTCPConnection -ErrorAction SilentlyContinue) {
        $stillListening = @(Get-NetTCPConnection -LocalPort $Ports -State Listen -ErrorAction SilentlyContinue |
            Where-Object { $owned.Contains([int]$_.OwningProcess) })
    }
    if ($stillListening.Count -eq 0) { break }
    Start-Sleep -Milliseconds 250
}
