<#
.SYNOPSIS
    Creates pre-update backups for TKG PDA Server.
.DESCRIPTION
    Backs up application binaries, web files, configuration, and PostgreSQL database dump
    into a timestamped directory under C:\ProgramData\TKG PDA\Backups\<timestamp>\.
#>
[CmdletBinding()]
param(
    [string]$InstallDir = 'C:\Program Files\TKG PDA',
    [string]$ProgramDataDir = 'C:\ProgramData\TKG PDA',
    [string]$DbHost = '127.0.0.1',
    [int]$DbPort = 5432,
    [string]$DbName = 'tkg_pda',
    [string]$DbUser = 'postgres',
    [string]$DbPassword = '',
    [string]$PgBinPath = '',
    [string]$BackupDestinationDir = ''
)

$ErrorActionPreference = 'Stop'

$timestamp = (Get-Date).ToString('yyyy-MM-dd_HHmmss')
if ([string]::IsNullOrWhiteSpace($BackupDestinationDir)) {
    $BackupDestinationDir = Join-Path $ProgramDataDir "Backups\$timestamp"
}

Write-Host "[INFO] Creating backup directory at: $BackupDestinationDir"
if (-not (Test-Path $BackupDestinationDir)) {
    New-Item -ItemType Directory -Path $BackupDestinationDir -Force | Out-Null
}

# 1. Backup Server binaries
$serverDir = Join-Path $InstallDir 'Server'
if (Test-Path $serverDir) {
    $backupServer = Join-Path $BackupDestinationDir 'Server'
    Write-Host "[INFO] Backing up application server binaries..."
    Copy-Item -Path $serverDir -Destination $backupServer -Recurse -Force
}

# 2. Backup Web files
$webDir = Join-Path $InstallDir 'Web'
if (Test-Path $webDir) {
    $backupWeb = Join-Path $BackupDestinationDir 'Web'
    Write-Host "[INFO] Backing up web client assets..."
    Copy-Item -Path $webDir -Destination $backupWeb -Recurse -Force
}

# 3. Backup Configuration
$configDir = Join-Path $ProgramDataDir 'Config'
if (Test-Path $configDir) {
    $backupConfig = Join-Path $BackupDestinationDir 'Config'
    Write-Host "[INFO] Backing up server configuration..."
    Copy-Item -Path $configDir -Destination $backupConfig -Recurse -Force
}

# 4. Database Dump via pg_dump
$candidatePaths = @(
    $PgBinPath,
    'C:\Program Files\PostgreSQL\18\bin',
    'C:\Program Files\PostgreSQL\17\bin',
    'C:\Program Files\PostgreSQL\16\bin'
)

$pgdump = $null
foreach ($cand in $candidatePaths) {
    if (![string]::IsNullOrWhiteSpace($cand) -and (Test-Path (Join-Path $cand 'pg_dump.exe'))) {
        $pgdump = Join-Path $cand 'pg_dump.exe'
        break
    }
}

if (-not $pgdump) {
    $inPath = Get-Command 'pg_dump.exe' -ErrorAction SilentlyContinue
    if ($inPath) { $pgdump = $inPath.Source }
}

$dumpSuccess = $false
if ($pgdump) {
    $dumpFile = Join-Path $BackupDestinationDir "database_$DbName.sql"
    Write-Host "[INFO] Creating database dump using $pgdump..."
    $prevPass = $env:PGPASSWORD
    if (![string]::IsNullOrWhiteSpace($DbPassword)) { $env:PGPASSWORD = $DbPassword }
    try {
        & $pgdump -h $DbHost -p $DbPort -U $DbUser -d $DbName -f $dumpFile --no-owner --no-privileges
        if ($LASTEXITCODE -eq 0 -and (Test-Path $dumpFile)) {
            $dumpSuccess = $true
            Write-Host "[INFO] Database dump created successfully."
        } else {
            Write-Warning "pg_dump failed with exit code $LASTEXITCODE"
        }
    }
    catch {
        Write-Warning "Database dump exception: $_"
    }
    finally {
        $env:PGPASSWORD = $prevPass
    }
} else {
    Write-Warning "pg_dump.exe not found. Database SQL dump was skipped."
}

# 5. Backup Manifest (no plaintext secrets)
$manifest = @{
    Timestamp = $timestamp
    BackupDir = $BackupDestinationDir
    ServerBackupExists = (Test-Path (Join-Path $BackupDestinationDir 'Server'))
    WebBackupExists = (Test-Path (Join-Path $BackupDestinationDir 'Web'))
    ConfigBackupExists = (Test-Path (Join-Path $BackupDestinationDir 'Config'))
    DatabaseDumpExists = $dumpSuccess
}

$manifestFile = Join-Path $BackupDestinationDir 'backup_manifest.json'
$manifest | ConvertTo-Json -Depth 3 | Set-Content -Path $manifestFile -Encoding UTF8

Write-Host "[INFO] Backup complete. Manifest written to $manifestFile"
return $manifest
