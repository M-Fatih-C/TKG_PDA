<#
.SYNOPSIS
    Manages the TKG PDA Server Windows Service.
.DESCRIPTION
    Installs, configures, starts, stops, or removes the native Windows Service
    hosting the Kestrel API and web applications.
#>
[CmdletBinding()]
param(
    [ValidateSet('Install', 'Uninstall', 'Start', 'Stop', 'Restart', 'Status')]
    [string]$Action = 'Install',
    [string]$ServiceName = 'TKG PDA Server',
    [string]$DisplayName = 'TKG PDA Server',
    [string]$BinaryPath = 'C:\Program Files\TKG PDA\Server\TkgPda.Api.exe',
    [string]$Description = 'TKG Otomotiv PDA ve Ambar Yonetim Sistemi'
)

$ErrorActionPreference = 'Stop'

function Write-ServiceLog {
    param([string]$Message, [string]$Level = 'INFO')
    $timestamp = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    Write-Host "[$timestamp][$Level] $Message"
}

switch ($Action) {
    'Install' {
        if (-not (Test-Path $BinaryPath)) {
            throw "Executable not found at: $BinaryPath"
        }

        Write-ServiceLog "Checking existing service: $ServiceName"
        $existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

        if ($existing) {
            Write-ServiceLog "Service '$ServiceName' already exists. Updating binary path..."
            & sc.exe stop "$ServiceName" | Out-Null
            Start-Sleep -Seconds 2
            & sc.exe config "$ServiceName" binPath= "`"$BinaryPath`"" start= auto DisplayName= "$DisplayName" | Out-Null
        } else {
            Write-ServiceLog "Creating Windows Service '$ServiceName'..."
            & sc.exe create "$ServiceName" binPath= "`"$BinaryPath`"" start= auto DisplayName= "$DisplayName" | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "Failed to create service $ServiceName" }
        }

        # Set service description
        & sc.exe description "$ServiceName" "$Description" | Out-Null

        # Configure automatic restart on failure: 5s, 10s, 30s; reset fail count after 1 day (86400s)
        Write-ServiceLog "Configuring failure recovery actions..."
        & sc.exe failure "$ServiceName" reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
        & sc.exe failureflag "$ServiceName" 1 | Out-Null

        Write-ServiceLog "Service '$ServiceName' configured successfully."
    }

    'Uninstall' {
        Write-ServiceLog "Removing service: $ServiceName"
        $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if ($svc) {
            if ($svc.Status -eq 'Running') {
                Write-ServiceLog "Stopping service before removal..."
                Stop-Service -Name $ServiceName -Force
                Start-Sleep -Seconds 2
            }
            & sc.exe delete "$ServiceName" | Out-Null
            Write-ServiceLog "Service '$ServiceName' deleted."
        } else {
            Write-ServiceLog "Service '$ServiceName' is not installed."
        }
    }

    'Start' {
        Write-ServiceLog "Starting service '$ServiceName'..."
        Start-Service -Name $ServiceName
        Start-Sleep -Seconds 2
        $svc = Get-Service -Name $ServiceName
        Write-ServiceLog "Service status: $($svc.Status)"
        if ($svc.Status -ne 'Running') {
            throw "Service $ServiceName failed to reach 'Running' status. Current: $($svc.Status)"
        }
    }

    'Stop' {
        Write-ServiceLog "Stopping service '$ServiceName'..."
        $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if ($svc -and $svc.Status -eq 'Running') {
            Stop-Service -Name $ServiceName -Force
            Start-Sleep -Seconds 2
        }
        Write-ServiceLog "Service stopped."
    }

    'Restart' {
        Write-ServiceLog "Restarting service '$ServiceName'..."
        Restart-Service -Name $ServiceName -Force
        Start-Sleep -Seconds 2
        $svc = Get-Service -Name $ServiceName
        Write-ServiceLog "Service status: $($svc.Status)"
    }

    'Status' {
        $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if ($svc) {
            return @{
                Installed = $true
                Name = $svc.Name
                DisplayName = $svc.DisplayName
                Status = $svc.Status.ToString()
                StartType = $svc.StartType.ToString()
            }
        } else {
            return @{
                Installed = $false
                Name = $ServiceName
                Status = 'NotInstalled'
            }
        }
    }
}
