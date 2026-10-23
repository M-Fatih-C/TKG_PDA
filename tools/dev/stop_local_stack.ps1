<#
.SYNOPSIS
    Stops the TKG PDA local development stack.
.DESCRIPTION
    Safely terminates the API, Admin Web dev server, Flutter Web dev server,
    and PostgreSQL cluster owned by this repository/local stack.
    Only processes owned by this repository are stopped. Foreign processes
    and unrelated API instances are left untouched.
    Leaves persistent PostgreSQL data intact.
    Compatible with Windows PowerShell 5.1.
#>

[CmdletBinding()]
param(
    [string]$PgBin = 'C:\Program Files\PostgreSQL\18\bin',
    [int]$ApiPort = 5000,
    [int]$AdminPort = 4173,
    [int]$OperatorPort = 7357,
    [switch]$KeepPostgres
)

$ErrorActionPreference = 'Continue'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

$localAppData = [Environment]::GetEnvironmentVariable('LOCALAPPDATA')
if ([string]::IsNullOrWhiteSpace($localAppData)) {
    $localAppData = Join-Path $env:USERPROFILE 'AppData\Local'
}
$devRoot   = Join-Path $localAppData 'TKG_PDA_DEV'
$pgDataDir = Join-Path $devRoot 'postgres'
$stateFile = Join-Path $devRoot 'state\stack_state.json'
$runtimeDir = Join-Path $projectRoot '.runtime'

$stoppedProcesses = @()

# Process identity and ownership helpers
function Get-ProcessCim {
    param([int]$ProcessId)
    Get-CimInstance Win32_Process -Filter "ProcessId = $ProcessId" -ErrorAction SilentlyContinue
}

function Test-ProcessOwnedByRepo {
    param(
        [Parameter(Mandatory = $true)][int]$ProcessId,
        [Parameter(Mandatory = $true)][string]$RepoRoot
    )
    $cim = Get-ProcessCim $ProcessId
    if (-not $cim) { return $false }
    $normalizedRoot = $RepoRoot.TrimEnd('\', '/')

    # 1. Direct command line or executable path check
    if (($cim.CommandLine -like "*$normalizedRoot*") -or ($cim.ExecutablePath -like "*$normalizedRoot*")) {
        return $true
    }

    # 2. Check parent and grandparent processes
    $currParentId = $cim.ParentProcessId
    for ($depth = 0; $depth -lt 3 -and $currParentId -gt 4; $depth++) {
        $parentCim = Get-ProcessCim $currParentId
        if (-not $parentCim) { break }
        if (($parentCim.CommandLine -like "*$normalizedRoot*") -or ($parentCim.ExecutablePath -like "*$normalizedRoot*")) {
            return $true
        }
        $currParentId = $parentCim.ParentProcessId
    }

    # 3. Check child and grandchild processes
    $children = Get-CimInstance Win32_Process -Filter "ParentProcessId = $ProcessId" -ErrorAction SilentlyContinue
    foreach ($ch in $children) {
        if (($ch.CommandLine -like "*$normalizedRoot*") -or ($ch.ExecutablePath -like "*$normalizedRoot*")) {
            return $true
        }
        $grandChildren = Get-CimInstance Win32_Process -Filter "ParentProcessId = $($ch.ProcessId)" -ErrorAction SilentlyContinue
        foreach ($gch in $grandChildren) {
            if (($gch.CommandLine -like "*$normalizedRoot*") -or ($gch.ExecutablePath -like "*$normalizedRoot*")) {
                return $true
            }
        }
    }

    return $false
}

function Stop-OwnedProcessSafely {
    param(
        [Parameter(Mandatory = $true)][int]$ProcessId,
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [string]$Description = "Process"
    )
    $cim = Get-ProcessCim $ProcessId
    if (-not $cim) { return $false }

    if (-not (Test-ProcessOwnedByRepo -ProcessId $ProcessId -RepoRoot $RepoRoot)) {
        Write-Warning "$Description with PID $ProcessId does not belong to this repository ($RepoRoot). Skipping."
        return $false
    }

    Write-Host "Stopping $Description (PID $ProcessId)..." -ForegroundColor Cyan
    & taskkill.exe /PID $ProcessId /T /F 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Stop-Process -Id $ProcessId -Force -ErrorAction SilentlyContinue
    }

    for ($i = 0; $i -lt 10; $i++) {
        Start-Sleep -Milliseconds 500
        if (-not (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) {
            return $true
        }
    }

    Stop-Process -Id $ProcessId -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
    return $true
}

# 1. Check .runtime/*.pid files
$pidFiles = @(
    @{ Name = "API"; File = (Join-Path $runtimeDir 'api.pid') },
    @{ Name = "Demo API"; File = (Join-Path $runtimeDir 'demo_api.pid') },
    @{ Name = "Admin Web"; File = (Join-Path $runtimeDir 'admin.pid') },
    @{ Name = "Operator Web"; File = (Join-Path $runtimeDir 'operator.pid') }
)

foreach ($item in $pidFiles) {
    if (Test-Path -LiteralPath $item.File) {
        try {
            $raw = Get-Content -LiteralPath $item.File -Raw -ErrorAction SilentlyContinue
            if (-not [string]::IsNullOrWhiteSpace($raw)) {
                $pidVal = [int]($raw.Trim())
                if ($pidVal -gt 0) {
                    $cim = Get-ProcessCim $pidVal
                    if ($cim) {
                        if (Test-ProcessOwnedByRepo -ProcessId $pidVal -RepoRoot $projectRoot) {
                            $ok = Stop-OwnedProcessSafely -ProcessId $pidVal -RepoRoot $projectRoot -Description "$($item.Name) ($($item.File))"
                            if ($ok) {
                                $stoppedProcesses += "$($item.Name) (PID $pidVal from PID file)"
                            }
                        } else {
                            Write-Warning "Stored PID $pidVal in $($item.File) belongs to a foreign process ($($cim.Name)). Leaving untouched."
                        }
                    }
                }
            }
        } catch {
            Write-Warning "Could not read or process $($item.File): $($_.Exception.Message)"
        } finally {
            Remove-Item -LiteralPath $item.File -Force -ErrorAction SilentlyContinue
        }
    }
}

# 2. Check stack_state.json for previous processes
if (Test-Path -LiteralPath $stateFile) {
    try {
        $state = Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json

        if ($state.apiPid) {
            $cim = Get-ProcessCim $state.apiPid
            if ($cim -and (Test-ProcessOwnedByRepo -ProcessId $state.apiPid -RepoRoot $projectRoot)) {
                $ok = Stop-OwnedProcessSafely -ProcessId $state.apiPid -RepoRoot $projectRoot -Description "API (state)"
                if ($ok) { $stoppedProcesses += "API (PID $($state.apiPid) from state)" }
            }
        }

        if ($state.adminPid) {
            $cim = Get-ProcessCim $state.adminPid
            if ($cim -and (Test-ProcessOwnedByRepo -ProcessId $state.adminPid -RepoRoot $projectRoot)) {
                $ok = Stop-OwnedProcessSafely -ProcessId $state.adminPid -RepoRoot $projectRoot -Description "Admin Web (state)"
                if ($ok) { $stoppedProcesses += "Admin Web (PID $($state.adminPid) from state)" }
            }
        }

        if ($state.operatorPid) {
            $cim = Get-ProcessCim $state.operatorPid
            if ($cim -and (Test-ProcessOwnedByRepo -ProcessId $state.operatorPid -RepoRoot $projectRoot)) {
                $ok = Stop-OwnedProcessSafely -ProcessId $state.operatorPid -RepoRoot $projectRoot -Description "Operator Web (state)"
                if ($ok) { $stoppedProcesses += "Operator Web (PID $($state.operatorPid) from state)" }
            }
        }

        if ($state.pgDataDir) {
            $pgDataDir = $state.pgDataDir
        }
    } catch {
        Write-Warning "Could not parse state file: $($_.Exception.Message)"
    }
}

# 3. Check ports for any remaining repo-owned processes (reclaim safely)
$portChecks = @(
    @{ Port = $ApiPort; Name = "API" },
    @{ Port = $AdminPort; Name = "Admin Web" },
    @{ Port = $OperatorPort; Name = "Operator Web" }
)

foreach ($pc in $portChecks) {
    $conn = Get-NetTCPConnection -LocalPort $pc.Port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($conn -and $conn.OwningProcess -gt 4) {
        $owningPid = $conn.OwningProcess
        $cim = Get-ProcessCim $owningPid
        if ($cim) {
            if (Test-ProcessOwnedByRepo -ProcessId $owningPid -RepoRoot $projectRoot) {
                Write-Host "Port $($pc.Port) is occupied by owned $($pc.Name) process (PID $owningPid). Stopping..." -ForegroundColor Cyan
                $ok = Stop-OwnedProcessSafely -ProcessId $owningPid -RepoRoot $projectRoot -Description "$($pc.Name) (port $($pc.Port))"
                if ($ok) {
                    $stoppedProcesses += "$($pc.Name) (PID $owningPid on port $($pc.Port))"
                }
            } else {
                Write-Warning "Port $($pc.Port) is occupied by a foreign process (PID $owningPid, $($cim.Name)). Leaving untouched."
            }
        }
    }
}

# 4. Stop PostgreSQL unless -KeepPostgres is requested
if (-not $KeepPostgres) {
    $pgCtl = Join-Path $PgBin 'pg_ctl.exe'
    if ((Test-Path -LiteralPath $pgCtl) -and (Test-Path -LiteralPath $pgDataDir)) {
        Write-Host "Stopping PostgreSQL development cluster at $pgDataDir..." -ForegroundColor Cyan
        & $pgCtl -D $pgDataDir -m fast -w stop 2>$null | Out-Null
        $stoppedProcesses += "PostgreSQL cluster ($pgDataDir)"
    }
}

# 5. Remove state file
if (Test-Path -LiteralPath $stateFile) {
    Remove-Item -LiteralPath $stateFile -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "TKG LOCAL DEV STACK STOPPED" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
if ($stoppedProcesses.Count -gt 0) {
    Write-Host "Stopped components:"
    foreach ($item in $stoppedProcesses) {
        Write-Host "  - $item"
    }
} else {
    Write-Host "No active development stack processes were found."
}
Write-Host "Persistent data remains intact in: $pgDataDir" -ForegroundColor Gray
Write-Host "========================================" -ForegroundColor Green
