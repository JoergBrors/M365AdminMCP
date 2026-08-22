#Requires -Version 7.0
<#
.SYNOPSIS
    Stoppt lokale Debug-Server auf den bekannten API/MCP-Ports.
#>
[CmdletBinding()]
param(
    [int[]]$Ports = @(5043, 7043, 5143, 7143)
)

$ErrorActionPreference = "Stop"
$processIds = [System.Collections.Generic.HashSet[int]]::new()

if (Get-Command Get-NetTCPConnection -ErrorAction SilentlyContinue) {
    foreach ($connection in Get-NetTCPConnection -LocalPort $Ports -State Listen -ErrorAction SilentlyContinue) {
        if ($connection.OwningProcess -gt 0) {
            [void]$processIds.Add([int]$connection.OwningProcess)
        }
    }
}
else {
    foreach ($port in $Ports) {
        $lines = netstat -ano | Select-String -Pattern "^\s*TCP\s+\S+:$port\s+\S+\s+\S+\s+\d+\s*$"
        foreach ($line in $lines) {
            $parts = ($line.ToString() -split "\s+") | Where-Object { $_ }
            if ($parts.Count -lt 5) { continue }

            $pidValue = 0
            if ([int]::TryParse($parts[-1], [ref]$pidValue) -and $pidValue -gt 0) {
                [void]$processIds.Add($pidValue)
            }
        }
    }
}

foreach ($processId in $processIds) {
    try {
        Stop-Process -Id $processId -Force -ErrorAction Stop
        Write-Host "Stopped local debug process $processId"
    }
    catch {
        Write-Host "Skip process ${processId}: $($_.Exception.Message)"
    }
}
