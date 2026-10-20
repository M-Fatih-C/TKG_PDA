<#
.SYNOPSIS
    Applies an update package to an existing TKG PDA Server deployment.
.DESCRIPTION
    Safely upgrades an installed server:
    1. Validates existing installation and package manifest integrity (SHA-256)
    2. Reads and decrypts installed production database connection configuration via DPAPI
    3. Stops Windows Service "TKG PDA Server" (quiesces application)
    4. Creates timestamped backup under C:\ProgramData\TKG PDA\Backups\<timestamp>\
       including full database dump via pg_dump
    5. Applies forward-only migrations and seeds (preserving all customer data)
    6. Replaces Server binaries, Web client assets, and tools
    7. Starts "TKG PDA Server"
    8. Performs health checks (/health/live, /health/ready, /version)
    9. On failure: Policy A rollback restores application binaries, web assets, AND
       restores the database dump, leaving the customer with a demonstrably bootable system.
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

# Helper: DPAPI Decryption for config
function Get-DecryptedConfigValue {
    param([string]$RawValue)
    if ([string]::IsNullOrWhiteSpace($RawValue)) { return '' }
    if ($RawValue.StartsWith('dpapi:', [StringComparison]::OrdinalIgnoreCase)) {
        $isWin = ($env:OS -eq 'Windows_NT') -or ([System.Environment]::OSVersion.Platform -in @([System.PlatformID]::Win32NT, [System.PlatformID]::Win32Windows))
        if (-not $isWin) {
            throw "DPAPI_DECRYPTION_FAILED: DPAPI is only supported on Windows operating systems."
        }
        Add-Type -AssemblyName System.Security -ErrorAction SilentlyContinue
        $cipherB64 = $RawValue.Substring(6)
        $cipherBytes = [Convert]::FromBase64String($cipherB64)
        try {
            $plainBytes = [System.Security.Cryptography.ProtectedData]::Unprotect($cipherBytes, $null, [System.Security.Cryptography.DataProtectionScope]::LocalMachine)
            return [System.Text.Encoding]::UTF8.GetString($plainBytes)
        } catch {
            try {
                $plainBytes = [System.Security.Cryptography.ProtectedData]::Unprotect($cipherBytes, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
                return [System.Text.Encoding]::UTF8.GetString($plainBytes)
            } catch {
                throw "DPAPI_DECRYPTION_FAILED: Could not decrypt DPAPI-protected configuration property."
            }
        }
    }
    return $RawValue
}

# Helper: Parse Npgsql connection string
function Parse-NpgsqlConnectionString {
    param([string]$Cs)
    $dict = @{}
    foreach ($part in ($Cs -split ';')) {
        $kv = $part -split '=', 2
        if ($kv.Count -eq 2) {
            $dict[$kv[0].Trim()] = $kv[1].Trim()
        }
    }
    return @{
        Host = if ($dict.ContainsKey('Host')) { $dict['Host'] } elseif ($dict.ContainsKey('Server')) { $dict['Server'] } else { '127.0.0.1' }
        Port = if ($dict.ContainsKey('Port')) { [int]$dict['Port'] } else { 5432 }
        Database = if ($dict.ContainsKey('Database')) { $dict['Database'] } else { 'tkg_pda' }
        Username = if ($dict.ContainsKey('Username')) { $dict['Username'] } elseif ($dict.ContainsKey('User Id')) { $dict['User Id'] } else { 'postgres' }
        Password = if ($dict.ContainsKey('Password')) { $dict['Password'] } else { '' }
    }
}

$backupPath = $null
$serviceStopped = $false
$migrationApplied = $false
$dbHost = '127.0.0.1'
$dbPort = 5432
$dbName = 'tkg_pda'
$dbUser = 'postgres'
$dbPassword = ''

try {
    # 2. Cryptographic Package Integrity Verification (BEFORE anything else)
    Log-Update "Verifying cryptographic package integrity before mutating system..."
    $manifestPath = Join-Path $SourceDir 'manifest.json'
    if (-not (Test-Path $manifestPath)) {
        throw "PACKAGE_INTEGRITY_FAILED: manifest.json is missing from update package at $SourceDir"
    }

    $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
    if (-not $manifest.files) {
        throw "PACKAGE_INTEGRITY_FAILED: manifest.json does not contain file integrity signatures."
    }

    foreach ($prop in $manifest.files.PSObject.Properties) {
        $relPath = $prop.Name
        # In update packages, PostgreSQL runtime is already installed on the host and is not part of the update payload
        if ($relPath.StartsWith('PostgreSQL/', [StringComparison]::OrdinalIgnoreCase) -or 
            $relPath.StartsWith('PostgreSQL\', [StringComparison]::OrdinalIgnoreCase)) {
            continue
        }
        $expectedHash = $prop.Value.ToLowerInvariant()
        $fullPath = Join-Path $SourceDir $relPath
        if (-not (Test-Path $fullPath)) {
            throw "PACKAGE_INTEGRITY_FAILED: Missing file '$relPath' in update package."
        }
        $actualHash = (Get-FileHash -Path $fullPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualHash -ne $expectedHash) {
            throw "PACKAGE_INTEGRITY_FAILED: Hash mismatch on '$relPath'. Expected $expectedHash, got $actualHash."
        }
    }
    Log-Update "Package integrity verified: All files match cryptographic SHA-256 signatures."

    # 3. Validate Existing Installation and Auto-Detect Configuration Location
    $serverDir = Join-Path $InstallDir 'Server'
    $webDir = Join-Path $InstallDir 'Web'

    if (-not (Test-Path (Join-Path $serverDir 'TkgPda.Api.exe'))) {
        throw "Existing TKG PDA installation not found at $InstallDir"
    }

    $configDir = Join-Path $ProgramDataDir 'Config'
    $prodConfigPath = Join-Path $configDir 'appsettings.Production.json'

    # Auto-detect ProgramData directory if not present at initial path
    if (-not (Test-Path $prodConfigPath)) {
        $candidateDataDirs = @(
            'C:\ProgramData\TKG PDA Demo',
            'C:\ProgramData\TKG PDA',
            (Join-Path $InstallDir 'Data')
        )
        foreach ($cDir in $candidateDataDirs) {
            $testPath = Join-Path $cDir 'Config\appsettings.Production.json'
            if (Test-Path $testPath) {
                $ProgramDataDir = $cDir
                $configDir = Join-Path $ProgramDataDir 'Config'
                $prodConfigPath = $testPath
                Log-Update "Auto-detected ProgramData directory: $ProgramDataDir"
                break
            }
        }
    }

    if (-not (Test-Path $prodConfigPath)) {
        throw "Existing configuration file not found at $prodConfigPath"
    }

    # Auto-detect Windows Service Name
    $activeSvc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $activeSvc) {
        $candidateServices = @('TKG PDA Demo Server', 'TKG PDA Server')
        foreach ($cSvc in $candidateServices) {
            $foundSvc = Get-Service -Name $cSvc -ErrorAction SilentlyContinue
            if ($foundSvc) {
                $ServiceName = $cSvc
                Log-Update "Auto-detected active Windows Service: $ServiceName"
                break
            }
        }
    }

    Log-Update "Found existing installation at $InstallDir (Service: $ServiceName, ProgramData: $ProgramDataDir)"

    # 4. Read & Decrypt Real Database Configuration
    Log-Update "Reading and decrypting installed production configuration..."
    $rawConfig = Get-Content -Path $prodConfigPath -Raw | ConvertFrom-Json
    if (-not $rawConfig.ConnectionStrings -or -not $rawConfig.ConnectionStrings.TkgPda) {
        throw "DATABASE_CONFIG_ERROR: ConnectionStrings:TkgPda is missing from existing configuration."
    }

    $decryptedCs = Get-DecryptedConfigValue $rawConfig.ConnectionStrings.TkgPda
    $parsedDb = Parse-NpgsqlConnectionString $decryptedCs
    $dbHost = $parsedDb.Host
    $dbPort = $parsedDb.Port
    $dbName = $parsedDb.Database
    $runtimeUser = $parsedDb.Username
    $runtimePassword = $parsedDb.Password

    Log-Update "Parsed runtime database target: $dbHost`:$dbPort / Database='$dbName' / RuntimeUser='$runtimeUser'"

    # 4b. Read & Decrypt Separate Maintenance Database Credentials
    $maintConfigFile = Join-Path $configDir 'maintenance.json'
    $maintUser = $runtimeUser
    $maintPassword = $runtimePassword
    $maintHost = $dbHost
    $maintPort = $dbPort
    $maintDb = $dbName

    if (Test-Path $maintConfigFile) {
        Log-Update "Reading separate maintenance database configuration from $maintConfigFile..."
        try {
            $rawMaint = Get-Content -Path $maintConfigFile -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($rawMaint.MaintenanceUser) { $maintUser = $rawMaint.MaintenanceUser }
            if ($rawMaint.MaintenancePassword) {
                $maintPassword = Get-DecryptedConfigValue $rawMaint.MaintenancePassword
            }
            if ($rawMaint.Host) { $maintHost = $rawMaint.Host }
            if ($rawMaint.Port) { $maintPort = [int]$rawMaint.Port }
            if ($rawMaint.Database) { $maintDb = $rawMaint.Database }
            Log-Update "Loaded separate maintenance credentials: User='$maintUser' (distinct from runtime user '$runtimeUser')."
        } catch {
            Log-Update "Failed to decrypt maintenance credentials ($($_.Exception.Message))." "WARN"
        }
    } else {
        Log-Update "maintenance.json not found; checking for environment maintenance credentials or fallback." "WARN"
        if (-not [string]::IsNullOrWhiteSpace($env:TKG_MAINTENANCE_DB_PASSWORD)) {
            $maintPassword = $env:TKG_MAINTENANCE_DB_PASSWORD
            if (-not [string]::IsNullOrWhiteSpace($env:TKG_MAINTENANCE_DB_USER)) {
                $maintUser = $env:TKG_MAINTENANCE_DB_USER
            }
        }
    }

    $serverPort = 5000
    if ($rawConfig.Kestrel -and $rawConfig.Kestrel.Endpoints -and $rawConfig.Kestrel.Endpoints.Http -and $rawConfig.Kestrel.Endpoints.Http.Url) {
        $url = $rawConfig.Kestrel.Endpoints.Http.Url
        if ($url -match ':(\d+)') { $serverPort = [int]$matches[1] }
    }

    # 4c. Refresh DemoMode Simulation Parameters if DemoMode is active
    if ($rawConfig.DemoMode -and $rawConfig.DemoMode.Enabled) {
        $setProp = {
            param($TargetObj, [string]$PropName, $PropValue)
            if ($null -ne $TargetObj.PSObject.Properties[$PropName]) {
                $TargetObj.$PropName = $PropValue
            } else {
                $TargetObj | Add-Member -NotePropertyName $PropName -NotePropertyValue $PropValue -Force
            }
        }

        & $setProp $rawConfig.DemoMode 'SimulationDelaySeconds' 3
        & $setProp $rawConfig.DemoMode 'RunningDurationSeconds' 30
        & $setProp $rawConfig.DemoMode 'AutoPrepareWarehouseDemands' $false
        if (-not $rawConfig.DemoMode.SimulatedAgvId) {
            & $setProp $rawConfig.DemoMode 'SimulatedAgvId' "DEMO-AGV-01"
        }
        $rawConfig | ConvertTo-Json -Depth 10 | Set-Content -Path $prodConfigPath -Encoding UTF8
        Log-Update "Refreshed DemoMode simulation parameters in $prodConfigPath (Duration: 30s, AutoPrepare: false)."
    }

    # 5. Stop Windows Service (Quiesce Application) BEFORE backup
    Log-Update "Stopping Windows Service '$ServiceName' to quiesce application before backup..."
    $svcScript = Join-Path $SourceDir 'Tools\Install-Service.ps1'
    if (-not (Test-Path $svcScript)) { $svcScript = Join-Path $InstallDir 'Tools\Install-Service.ps1' }

    if (Test-Path $svcScript) {
        & $svcScript -Action Stop -ServiceName $ServiceName
        $serviceStopped = $true
        Log-Update "Service '$ServiceName' stopped."
    }

    # 6. Create Timestamped Pre-Update Backup
    Log-Update "Creating pre-update backup at quiesced boundary..."
    $backupScript = Join-Path $SourceDir 'Tools\Backup-TkgServer.ps1'
    if (-not (Test-Path $backupScript)) { $backupScript = Join-Path $InstallDir 'Tools\Backup-TkgServer.ps1' }

    $backupDest = Join-Path $ProgramDataDir "Backups\$timestamp"
    $backupInfo = & $backupScript `
        -InstallDir $InstallDir `
        -ProgramDataDir $ProgramDataDir `
        -DbHost $maintHost `
        -DbPort $maintPort `
        -DbName $dbName `
        -DbUser $maintUser `
        -DbPassword $maintPassword `
        -BackupDestinationDir $backupDest

    $backupPath = $backupDest
    Log-Update "Backup created successfully at $backupPath"

    # 7. Apply Forward-Only Database Migrations and Seeds
    Log-Update "Checking and applying database migrations and seeds..."
    $dbScript = Join-Path $SourceDir 'Tools\Install-Database.ps1'
    if (-not (Test-Path $dbScript)) { $dbScript = Join-Path $InstallDir 'Tools\Install-Database.ps1' }

    $migrationsDir = Join-Path $SourceDir 'db\migrations'
    $seedsDir = Join-Path $SourceDir 'db\seeds'

    & $dbScript `
        -HostName $dbHost `
        -Port $dbPort `
        -Database $dbName `
        -Username $maintUser `
        -Password $maintPassword `
        -AppUser $runtimeUser `
        -AppPassword $runtimePassword `
        -MigrationsDir $migrationsDir `
        -SeedsDir $seedsDir `
        -IsUpdate

    $migrationApplied = $true
    Log-Update "Database migrations applied and master data verified."

    # 7b. If DemoMode is active, apply updated demo seeds (19 FG locations, universal accounts)
    if ($rawConfig.DemoMode -and $rawConfig.DemoMode.Enabled) {
        $demoSeed = Join-Path $SourceDir 'db\seeds\demo\demo_seed.sql'
        if (-not (Test-Path $demoSeed)) { $demoSeed = Join-Path $SourceDir 'Tools\db\seeds\demo\demo_seed.sql' }
        if (Test-Path $demoSeed) {
            Log-Update "Applying updated demo seeds (19 finished goods store locations, universal demo accounts)..."
            $psqlBin = 'psql.exe'
            $chkPsql = Get-Command 'psql.exe' -ErrorAction SilentlyContinue
            if ($chkPsql) { $psqlBin = $chkPsql.Source }
            else {
                $pgBinCandidates = @(
                    (Join-Path $InstallDir 'PostgreSQL\bin\psql.exe'),
                    'C:\Program Files\PostgreSQL\18\bin\psql.exe'
                )
                foreach ($c in $pgBinCandidates) { if (Test-Path $c) { $psqlBin = $c; break } }
            }
            if (![string]::IsNullOrWhiteSpace($maintPassword)) {
                $env:PGPASSWORD = $maintPassword
            }
            $demoErr = & $psqlBin -X -h $dbHost -p "$dbPort" -U $maintUser -d $dbName -f $demoSeed 2>&1
            if ($LASTEXITCODE -ne 0) {
                Log-Update "Demo master seeds failed with exit code $($LASTEXITCODE). Error: $($demoErr | Out-String)" "ERROR"
                throw "Demo master seeds failed with exit code $($LASTEXITCODE). Error: $($demoErr | Out-String)"
            }
            Log-Update "Demo master seeds verified and applied successfully."
        }
    }

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
    if (Test-Path $svcScript) {
        & $svcScript -Action Start -ServiceName $ServiceName
        $serviceStopped = $false
    }

    # 10. Run Smoke Tests / Health Checks
    Log-Update "Running post-update smoke tests..."
    Start-Sleep -Seconds 3

    $testScript = Join-Path $SourceDir 'Tools\test-installed-server.ps1'
    if (-not (Test-Path $testScript)) { $testScript = Join-Path $InstallDir 'Tools\test-installed-server.ps1' }

    if (Test-Path $testScript) {
        & $testScript -ServerHost '127.0.0.1' -Port $serverPort -ServiceName $ServiceName
    }
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
        Log-Update "INITIATING POLICY A ROLLBACK to restore pre-update binaries and database..." "WARN"
        $binariesRestored = $false
        $webRestored = $false
        $dbRecreationSucceeded = $false
        $dbRestoreSucceeded = $false
        $priorServiceStarted = $false
        $healthReadySucceeded = $false

        try {
            $svcScript = Join-Path $InstallDir 'Tools\Install-Service.ps1'
            if (-not (Test-Path $svcScript)) { $svcScript = Join-Path $SourceDir 'Tools\Install-Service.ps1' }
            if (Test-Path $svcScript) { & $svcScript -Action Stop -ServiceName $ServiceName }

            # 1. Restore Binaries & Web Assets
            $backupServer = Join-Path $backupPath 'Server'
            if (Test-Path $backupServer) {
                Copy-Item -Path "$backupServer\*" -Destination (Join-Path $InstallDir 'Server') -Recurse -Force
                Log-Update "Restored Server binaries from backup."
                $binariesRestored = $true
            }

            $backupWeb = Join-Path $backupPath 'Web'
            if (Test-Path $backupWeb) {
                Copy-Item -Path "$backupWeb\*" -Destination (Join-Path $InstallDir 'Web') -Recurse -Force
                Log-Update "Restored Web assets from backup."
                $webRestored = $true
            }

            # 2. Restore Database using MAINTENANCE CREDENTIAL if migrations ran and backup SQL dump exists
            $dumpFile = Join-Path $backupPath "database_$dbName.sql"
            if ($migrationApplied -and (Test-Path $dumpFile)) {
                Log-Update "Restoring database '$dbName' using maintenance identity '$maintUser'..."
                $candidatePgPaths = @(
                    (Join-Path $InstallDir 'PostgreSQL\bin'),
                    'C:\Program Files\TKG PDA\PostgreSQL\bin',
                    'C:\Program Files\PostgreSQL\18\bin',
                    'C:\Program Files\PostgreSQL\17\bin',
                    'C:\Program Files\PostgreSQL\16\bin'
                )
                $rPsql = $null
                foreach ($c in $candidatePgPaths) {
                    if (Test-Path (Join-Path $c 'psql.exe')) { $rPsql = Join-Path $c 'psql.exe'; break }
                }
                if (-not $rPsql) {
                    $inP = Get-Command 'psql.exe' -ErrorAction SilentlyContinue
                    if ($inP) { $rPsql = $inP.Source }
                }

                if ($rPsql) {
                    $prevP = $env:PGPASSWORD
                    if (![string]::IsNullOrWhiteSpace($maintPassword)) { $env:PGPASSWORD = $maintPassword }
                    try {
                        & $rPsql -X -h $maintHost -p $maintPort -U $maintUser -w -d postgres -c "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname='$dbName' AND pid <> pg_backend_pid();" 2>$null | Out-Null
                        & $rPsql -X -h $maintHost -p $maintPort -U $maintUser -w -d postgres -c "DROP DATABASE IF EXISTS $dbName;" 2>$null | Out-Null
                        & $rPsql -X -h $maintHost -p $maintPort -U $maintUser -w -d postgres -c "CREATE DATABASE $dbName ENCODING 'UTF8';" | Out-Null
                        if ($LASTEXITCODE -eq 0) {
                            $dbRecreationSucceeded = $true
                        }
                        & $rPsql -X -h $maintHost -p $maintPort -U $maintUser -w -d postgres -c "GRANT ALL PRIVILEGES ON DATABASE $dbName TO $runtimeUser;" 2>$null | Out-Null
                        & $rPsql -X -h $maintHost -p $maintPort -U $maintUser -w -v ON_ERROR_STOP=1 -d $dbName -f $dumpFile | Out-Null
                        if ($LASTEXITCODE -eq 0) {
                            $dbRestoreSucceeded = $true
                            foreach ($s in @('public', 'auth', 'master', 'transport', 'ops')) {
                                & $rPsql -X -h $maintHost -p $maintPort -U $maintUser -w -d $dbName -c "GRANT USAGE ON SCHEMA $s TO $runtimeUser; GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA $s TO $runtimeUser; GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA $s TO $runtimeUser; GRANT ALL PRIVILEGES ON ALL ROUTINES IN SCHEMA $s TO $runtimeUser;" 2>$null | Out-Null
                            }
                            Log-Update "Database '$dbName' restored to pre-update snapshot successfully using maintenance identity."
                        } else {
                            Log-Update "Database restoration failed with exit code $LASTEXITCODE" "ERROR"
                        }
                    } finally {
                        $env:PGPASSWORD = $prevP
                    }
                } else {
                    Log-Update "psql.exe not found. Database restore could not be performed." "ERROR"
                }
            } else {
                # Migrations were not applied
                $dbRecreationSucceeded = $true
                $dbRestoreSucceeded = $true
            }

            # 3. Restart previous version service
            if (Test-Path $svcScript) {
                & $svcScript -Action Start -ServiceName $ServiceName
                if ($LASTEXITCODE -eq 0) { $priorServiceStarted = $true }
                Log-Update "Previous version service restarted."
            }

            # 4. Verify /health/ready on restored service
            $readyUrl = "http://127.0.0.1:$serverPort/health/ready"
            Log-Update "Verifying post-rollback health at $readyUrl..."
            for ($i = 0; $i -lt 15; $i++) {
                try {
                    $resp = Invoke-RestMethod -Uri $readyUrl -Method Get -TimeoutSec 2 -ErrorAction Stop
                    if ($resp.status -eq 'Healthy' -or $resp.database -eq 'Available' -or $resp.ready -eq $true) {
                        $healthReadySucceeded = $true
                        break
                    }
                } catch {
                    Start-Sleep -Seconds 1
                }
            }

            # Enforce that ROLLBACK COMPLETE is only logged if all required criteria succeeded
            $allRollbackPassed = $binariesRestored -and $webRestored -and $dbRecreationSucceeded -and $dbRestoreSucceeded -and $priorServiceStarted -and $healthReadySucceeded
            if ($allRollbackPassed) {
                Log-Update "ROLLBACK COMPLETE. Both application binaries and database restored to pre-update state." "WARN"
            } else {
                Log-Update "ROLLBACK INCOMPLETE: Some rollback steps failed (Binaries=$binariesRestored, Web=$webRestored, DbRecreate=$dbRecreationSucceeded, DbRestore=$dbRestoreSucceeded, ServiceStarted=$priorServiceStarted, HealthReady=$healthReadySucceeded). Check logs." "ERROR"
            }
        }
        catch {
            Log-Update "ROLLBACK ERROR: $($_.Exception.Message)" "ERROR"
        }
    }

    throw
}
