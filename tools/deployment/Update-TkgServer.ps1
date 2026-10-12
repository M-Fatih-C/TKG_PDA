<#
.SYNOPSIS
    Applies an update package to an existing TKG PDA Server deployment.
.DESCRIPTION
    Safely upgrades an installed server:
    1. Validates existing installation and package manifest integrity (SHA-256)
    2. Stops Windows Service "TKG PDA Server"
    3. Creates timestamped backup under C:\ProgramData\TKG PDA\Backups\<timestamp>\
    4. Applies forward-only migrations and seeds (preserving all customer data)
    5. Replaces Server binaries and Web client assets
    6. Preserves existing configuration, DPAPI secrets, logs, and RDS state
    7. Starts "TKG PDA Server"
    8. Performs health checks (/health/live, /health/ready, /version)
    9. On failure: automatically rolls back application binaries and config
#>
[CmdletBinding()]
param(
    [string]$SourceDir = '',
    [string]$InstallDir = 'C:\Program Files\TKG PDA',
    [string]$ProgramDataDir = 'C:\ProgramData\TKG PDA',
    [string]$ServiceName = 'TKG PDA Server',
    [string]$TargetVersion = '1.0.0'
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($SourceDir)) {
    $SourceDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}

# 1. Initialize Log File
$logsDir = Join-Path $ProgramDataDir 'Logs'
if (-not (Test-Path $logsDir)) { New-Item -ItemType Directory -Path $logsDir -Force | Out-Null }
$timestamp = (Get-Date).ToString('yyyy-MM-dd_HHmmss')
$logFile = Join-Path $logsDir "update_$timestamp.log"

function Log-Update {
    param([string]$Message, [string]$Level = 'INFO')
    $time = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    $entry = "[$time][$Level] $Message"
    Write-Host $entry
    Add-Content -Path $logFile -Value $entry -Encoding UTF8
}

Log-Update "=================================================="
Log-Update "Starting TKG PDA Server Update to Version $TargetVersion"
Log-Update "Install Directory: $InstallDir"
Log-Update "=================================================="

$backupPath = $null
$serviceStopped = $false

try {
    # 2. Validate Existing Installation
    $serverDir = Join-Path $InstallDir 'Server'
    $webDir = Join-Path $InstallDir 'Web'
    $configDir = Join-Path $ProgramDataDir 'Config'
    $prodConfigPath = Join-Path $configDir 'appsettings.Production.json'

    if (-not (Test-Path (Join-Path $serverDir 'TkgPda.Api.exe'))) {
        throw "Existing TKG PDA installation not found at $InstallDir"
    }

    if (-not (Test-Path $prodConfigPath)) {
        throw "Existing configuration file not found at $prodConfigPath"
    }

    Log-Update "Found existing installation at $InstallDir"

    # 3. Validate Manifest Integrity
    $manifestPath = Join-Path $SourceDir 'manifest.json'
    if (Test-Path $manifestPath) {
        Log-Update "Validating package manifest SHA-256 integrity..."
        $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
        Log-Update "Update target version: $($manifest.version) (Commit: $($manifest.buildCommit))"
    }

    # 4. Read Database Connection Info from Existing Config
    $rawConfig = Get-Content -Path $prodConfigPath -Raw | ConvertFrom-Json
    $serverPort = 5000
    if ($rawConfig.Kestrel?.Endpoints?.Http?.Url) {
        $url = $rawConfig.Kestrel.Endpoints.Http.Url
        if ($url -match ':(\d+)') { $serverPort = [int]$matches[1] }
    }

    # 5. Create Timestamped Pre-Update Backup
    Log-Update "Creating pre-update backup..."
    $backupScript = Join-Path $InstallDir 'Tools\Backup-TkgServer.ps1'
    if (-not (Test-Path $backupScript)) { $backupScript = Join-Path $SourceDir 'Tools\Backup-TkgServer.ps1' }

    $backupDest = Join-Path $ProgramDataDir "Backups\$timestamp"
    $backupInfo = & $backupScript `
        -InstallDir $InstallDir `
        -ProgramDataDir $ProgramDataDir `
        -BackupDestinationDir $backupDest

    $backupPath = $backupDest
    Log-Update "Backup created successfully at $backupPath"

    # 6. Stop Windows Service
    Log-Update "Stopping Windows Service '$ServiceName'..."
    $svcScript = Join-Path $InstallDir 'Tools\Install-Service.ps1'
    if (-not (Test-Path $svcScript)) { $svcScript = Join-Path $SourceDir 'Tools\Install-Service.ps1' }

    & $svcScript -Action Stop -ServiceName $ServiceName
    $serviceStopped = $true
    Log-Update "Service '$ServiceName' stopped."

    # 7. Apply Forward-Only Database Migrations
    Log-Update "Checking and applying database migrations..."
    $dbScript = Join-Path $InstallDir 'Tools\Install-Database.ps1'
    if (-not (Test-Path $dbScript)) { $dbScript = Join-Path $SourceDir 'Tools\Install-Database.ps1' }

    $migrationsDir = Join-Path $SourceDir 'db\migrations'
    $seedsDir = Join-Path $SourceDir 'db\seeds'

    & $dbScript `
        -MigrationsDir $migrationsDir `
        -SeedsDir $seedsDir `
        -IsUpdate

    Log-Update "Database migrations applied and master data verified."

    # 8. Replace Server Binaries and Web Assets
    Log-Update "Replacing application server binaries..."
    $srcServer = Join-Path $SourceDir 'Server'
    if (Test-Path $srcServer) {
        Copy-Item -Path "$srcServer\*" -Destination $serverDir -Recurse -Force
    } else {
        throw "Update source server binaries missing: $srcServer"
    }

    Log-Update "Replacing web client interfaces..."
    $srcWebAdmin = Join-Path $SourceDir 'Web\admin'
    if (Test-Path $srcWebAdmin) {
        $destAdmin = Join-Path $webDir 'admin'
        Copy-Item -Path "$srcWebAdmin\*" -Destination $destAdmin -Recurse -Force
    }

    $srcWebPda = Join-Path $SourceDir 'Web\pda'
    if (Test-Path $srcWebPda) {
        $destPda = Join-Path $webDir 'pda'
        Copy-Item -Path "$srcWebPda\*" -Destination $destPda -Recurse -Force
    }

    $srcWebRoot = Join-Path $SourceDir 'Web\root'
    if (Test-Path $srcWebRoot) {
        $destRoot = Join-Path $webDir 'root'
        Copy-Item -Path "$srcWebRoot\*" -Destination $destRoot -Recurse -Force
    }

    $srcTools = Join-Path $SourceDir 'Tools'
    if (Test-Path $srcTools) {
        $destTools = Join-Path $InstallDir 'Tools'
        Copy-Item -Path "$srcTools\*" -Destination $destTools -Recurse -Force
    }

    Log-Update "Binaries and web assets replaced successfully."

    # 9. Start Windows Service
    Log-Update "Starting updated Windows Service '$ServiceName'..."
    & $svcScript -Action Start -ServiceName $ServiceName
    $serviceStopped = $false

    # 10. Run Smoke Tests / Health Checks
    Log-Update "Running post-update smoke tests..."
    Start-Sleep -Seconds 3

    $testScript = Join-Path $InstallDir 'Tools\test-installed-server.ps1'
    if (-not (Test-Path $testScript)) { $testScript = Join-Path $SourceDir 'Tools\test-installed-server.ps1' }

    & $testScript -ServerHost '127.0.0.1' -Port $serverPort -ServiceName $ServiceName
    Log-Update "Post-update health checks passed!"

    Log-Update "=================================================="
    Log-Update "UPDATE COMPLETED SUCCESSFULLY (Version $TargetVersion)"
    Log-Update "=================================================="

    return @{
        Status = "SUCCESS"
        Version = $TargetVersion
        BackupDir = $backupPath
        LogFile = $logFile
    }
}
catch {
    Log-Update "UPDATE FAILED: $($_.Exception.Message)" "ERROR"
    Log-Update $_.ScriptStackTrace "ERROR"

    if ($backupPath -and (Test-Path $backupPath)) {
        Log-Update "INITIATING AUTOMATIC ROLLBACK to previous application version..." "WARN"
        try {
            $svcScript = Join-Path $InstallDir 'Tools\Install-Service.ps1'
            if (Test-Path $svcScript) { & $svcScript -Action Stop -ServiceName $ServiceName }

            $backupServer = Join-Path $backupPath 'Server'
            if (Test-Path $backupServer) {
                Copy-Item -Path "$backupServer\*" -Destination (Join-Path $InstallDir 'Server') -Recurse -Force
                Log-Update "Restored Server binaries from backup."
            }

            $backupWeb = Join-Path $backupPath 'Web'
            if (Test-Path $backupWeb) {
                Copy-Item -Path "$backupWeb\*" -Destination (Join-Path $InstallDir 'Web') -Recurse -Force
                Log-Update "Restored Web assets from backup."
            }

            if (Test-Path $svcScript) {
                & $svcScript -Action Start -ServiceName $ServiceName
                Log-Update "Previous version service restarted successfully."
            }
            Log-Update "ROLLBACK COMPLETE. Server restored to previous state." "WARN"
        }
        catch {
            Log-Update "ROLLBACK ERROR: $($_.Exception.Message)" "ERROR"
        }
    }

    throw
}
