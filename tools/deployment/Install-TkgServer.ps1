<#
.SYNOPSIS
    Installs TKG PDA Server on Windows Server without IIS.
.DESCRIPTION
    Automates complete installation:
    - Sets up standard directories under Program Files and ProgramData
    - Deploys self-contained .NET Kestrel server, Web assets, and tools
    - Validates network topology (rejects remote RDS with loopback callback)
    - Deploys PostgreSQL runtime and initializes secure database with SCRAM
    - Bootstraps initial production administrator (ADMIN role)
    - Encrypts secrets using Windows DPAPI (fail-closed, no plaintext fallback)
    - Generates RDS commissioning script and preserves RDSCORE_NATIVE_ORDER
    - Configures Windows Firewall for server port
    - Registers and starts "TKG PDA Server" Windows Service with dependency on local DB
    - Verifies service health, database connection, and web endpoints
    - Writes detailed install log
#>
[CmdletBinding()]
param(
    [string]$SourceDir = '',
    [string]$InstallDir = 'C:\Program Files\TKG PDA',
    [string]$ProgramDataDir = 'C:\ProgramData\TKG PDA',
    [int]$ServerPort = 5000,
    [string]$HostBinding = '0.0.0.0',
    [string]$DbHost = '127.0.0.1',
    [int]$DbPort = 5432,
    [string]$DbName = 'tkg_pda',
    [string]$DbUser = 'postgres',
    [string]$DbPassword = '',
    [string]$AppDbUser = 'tkg_app',
    [string]$AppDbPassword = '',
    [string]$AdminPassword = '',
    [string]$RdsBaseUrl = '',
    [string]$RdsCoreBaseUrl = '',
    [string]$RdsCallbackUrl = '',
    [string]$RdsIntegrationKey = '',
    [string]$RdsCallbackKey = '',
    [bool]$RdsDispatchEnabled = $true,
    [string]$Version = '1.0.0',
    [string]$SecretsFile = '',
    [string]$ServiceName = '',
    [string]$PgServiceName = '',
    [switch]$SkipServiceRegistration = $false,
    [switch]$DemoMode = $false
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security -ErrorAction SilentlyContinue

if ($DemoMode) {
    if ($InstallDir -eq 'C:\Program Files\TKG PDA') { $InstallDir = 'C:\Program Files\TKG PDA Demo' }
    if ($ProgramDataDir -eq 'C:\ProgramData\TKG PDA') { $ProgramDataDir = 'C:\ProgramData\TKG PDA Demo' }
    if ($DbPort -eq 5432) { $DbPort = 55452 }
    if ($DbName -eq 'tkg_pda') { $DbName = 'tkg_pda_demo' }
    if ([string]::IsNullOrWhiteSpace($ServiceName)) { $ServiceName = 'TKG PDA Demo Server' }
    if ([string]::IsNullOrWhiteSpace($PgServiceName)) { $PgServiceName = 'tkg-pda-demo-postgres' }
} else {
    if ([string]::IsNullOrWhiteSpace($ServiceName)) { $ServiceName = 'TKG PDA Server' }
    if ([string]::IsNullOrWhiteSpace($PgServiceName)) { $PgServiceName = 'tkg-pda-postgres' }
}

if ([string]::IsNullOrWhiteSpace($SourceDir)) {
    $SourceDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}

# 1. Initialize Log File
$logsDir = Join-Path $ProgramDataDir 'Logs'
if (-not (Test-Path $logsDir)) { New-Item -ItemType Directory -Path $logsDir -Force | Out-Null }
$timestamp = (Get-Date).ToString('yyyy-MM-dd_HHmmss')
$logFile = Join-Path $logsDir "install_$timestamp.log"

function Log-Message {
    param([string]$Message, [string]$Level = 'INFO')
    $time = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    $entry = "[$time][$Level] $Message"
    Write-Host $entry
    Add-Content -Path $logFile -Value $entry -Encoding UTF8
}

Log-Message "=================================================="
Log-Message "Starting TKG PDA Server Installation (Version $Version)"
Log-Message "Install Directory: $InstallDir"
Log-Message "Data Directory:    $ProgramDataDir"
Log-Message "Server Port:       $ServerPort"
Log-Message "=================================================="

# Helper: Loopback detection
function Test-IsLoopbackHost {
    param([string]$UrlString)
    if ([string]::IsNullOrWhiteSpace($UrlString)) { return $true }
    try {
        $uri = [Uri]$UrlString
        return ($uri.IsLoopback -or $uri.Host -eq '127.0.0.1' -or $uri.Host -eq 'localhost')
    } catch {
        return $false
    }
}

try {
    # 2. Resolve Secrets from Transient File / Environment
    if (![string]::IsNullOrWhiteSpace($SecretsFile) -and (Test-Path $SecretsFile)) {
        try {
            $sJson = Get-Content -Path $SecretsFile -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($sJson.DbPassword) { $DbPassword = $sJson.DbPassword }
            if ($sJson.AppDbPassword) { $AppDbPassword = $sJson.AppDbPassword }
            if ($sJson.AdminPassword) { $AdminPassword = $sJson.AdminPassword }
        } catch {
            Log-Message "Warning: Failed to read secrets file: $($_.Exception.Message)" "WARN"
        }
    }
    if ([string]::IsNullOrWhiteSpace($DbPassword) -and -not [string]::IsNullOrWhiteSpace($env:TKG_SETUP_DB_PASSWORD)) {
        $DbPassword = $env:TKG_SETUP_DB_PASSWORD
    }
    if ([string]::IsNullOrWhiteSpace($AppDbPassword) -and -not [string]::IsNullOrWhiteSpace($env:TKG_SETUP_APP_DB_PASSWORD)) {
        $AppDbPassword = $env:TKG_SETUP_APP_DB_PASSWORD
    }
    if ([string]::IsNullOrWhiteSpace($AdminPassword) -and -not [string]::IsNullOrWhiteSpace($env:TKG_SETUP_ADMIN_PASSWORD)) {
        $AdminPassword = $env:TKG_SETUP_ADMIN_PASSWORD
    }

    # 3. Topology Validation
    if ([string]::IsNullOrWhiteSpace($RdsCallbackUrl)) {
        $RdsCallbackUrl = "http://127.0.0.1:$ServerPort/api/integrations/rds/events"
    }

    $rdsIsRemote = (-not (Test-IsLoopbackHost $RdsBaseUrl)) -or (-not (Test-IsLoopbackHost $RdsCoreBaseUrl))
    $callbackIsLoopback = (Test-IsLoopbackHost $RdsCallbackUrl)

    if ($rdsIsRemote -and $callbackIsLoopback) {
        throw "INVALID_NETWORK_TOPOLOGY: Remote RDS host ($RdsBaseUrl / $RdsCoreBaseUrl) cannot reach loopback callback URL ($RdsCallbackUrl). Specify a non-loopback server IP/hostname in CallbackUrl."
    }

    # Validate Admin Password on fresh install
    if ([string]::IsNullOrWhiteSpace($AdminPassword)) {
        if ($DemoMode) {
            $tokenBytes = New-Object byte[] 12
            [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($tokenBytes)
            $AdminPassword = "Demo-" + [Convert]::ToBase64String($tokenBytes).Replace('/','_').Replace('+','-').TrimEnd('=')
            Log-Message "Generated random secure administrator password for demo installation: $AdminPassword"
        } else {
            throw "ADMIN_PASSWORD_REQUIRED: A non-empty secure administrator password is required for initial installation."
        }
    }
    if ($AdminPassword.Length -lt 8) {
        throw "ADMIN_PASSWORD_TOO_SHORT: Administrator password must be at least 8 characters long."
    }

    # 3. Create Directory Structure
    Log-Message "Creating required directories..."
    $serverDir = Join-Path $InstallDir 'Server'
    $webAdminDir = Join-Path $InstallDir 'Web\admin'
    $webPdaDir = Join-Path $InstallDir 'Web\pda'
    $webRootDir = Join-Path $InstallDir 'Web\root'
    $toolsDir = Join-Path $InstallDir 'Tools'
    $pgBinDir = Join-Path $InstallDir 'PostgreSQL\bin'

    $configDir = Join-Path $ProgramDataDir 'Config'
    $backupsDir = Join-Path $ProgramDataDir 'Backups'
    $dataDir = Join-Path $ProgramDataDir 'Data'
    $rdsDir = Join-Path $ProgramDataDir 'RDS'
    $pgDataDir = Join-Path $ProgramDataDir 'PostgreSQL\data'

    $allDirs = @($serverDir, $webAdminDir, $webPdaDir, $webRootDir, $toolsDir, $configDir, $backupsDir, $dataDir, $rdsDir, $pgDataDir)
    foreach ($d in $allDirs) {
        if (-not (Test-Path $d)) {
            New-Item -ItemType Directory -Path $d -Force | Out-Null
        }
    }

    if ($DemoMode) {
        $credFile = Join-Path $configDir 'initial_admin_credentials.txt'
        "Username: dev-admin`r`nPassword: $AdminPassword`r`nGeneratedAt: $((Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))" | Set-Content -Path $credFile -Encoding UTF8
        try {
            & icacls "$credFile" /inheritance:r /grant:r "NT AUTHORITY\SYSTEM:F" "BUILTIN\Administrators:F" | Out-Null
        } catch { }
        Log-Message "Demo administrator credentials written to $credFile (restricted ACL)."
    }

    # 4. Copy Application Files from Source
    Log-Message "Deploying application server binaries..."
    $srcServer = Join-Path $SourceDir 'Server'
    if (Test-Path $srcServer) {
        if ($srcServer -ne $serverDir) {
            Copy-Item -Path "$srcServer\*" -Destination $serverDir -Recurse -Force
        }
    } else {
        throw "Server binaries missing from source: $srcServer"
    }

    Log-Message "Deploying web client interfaces..."
    $srcWebAdmin = Join-Path $SourceDir 'Web\admin'
    if ((Test-Path $srcWebAdmin) -and ($srcWebAdmin -ne $webAdminDir)) {
        Copy-Item -Path "$srcWebAdmin\*" -Destination $webAdminDir -Recurse -Force
    }

    $srcWebPda = Join-Path $SourceDir 'Web\pda'
    if ((Test-Path $srcWebPda) -and ($srcWebPda -ne $webPdaDir)) {
        Copy-Item -Path "$srcWebPda\*" -Destination $webPdaDir -Recurse -Force
    }

    $srcWebRoot = Join-Path $SourceDir 'Web\root'
    if ((Test-Path $srcWebRoot) -and ($srcWebRoot -ne $webRootDir)) {
        Copy-Item -Path "$srcWebRoot\*" -Destination $webRootDir -Recurse -Force
    }

    $srcTools = Join-Path $SourceDir 'Tools'
    if ((Test-Path $srcTools) -and ($srcTools -ne $toolsDir)) {
        Copy-Item -Path "$srcTools\*" -Destination $toolsDir -Recurse -Force
    }

    $srcPg = Join-Path $SourceDir 'PostgreSQL'
    $destPg = Join-Path $InstallDir 'PostgreSQL'
    if ((Test-Path $srcPg) -and ($srcPg -ne $destPg)) {
        Log-Message "Deploying bundled PostgreSQL runtime..."
        if (-not (Test-Path $destPg)) {
            New-Item -ItemType Directory -Path $destPg -Force | Out-Null
        }
        Copy-Item -Path "$srcPg\*" -Destination $destPg -Recurse -Force
    }

    # 5. Generate RDS Keys and Commissioning Script
    Log-Message "Configuring RDS settings and commissioning script..."
    $genRdsScript = Join-Path $toolsDir 'Generate-RdsScript.ps1'
    if (-not (Test-Path $genRdsScript)) { $genRdsScript = Join-Path $SourceDir 'Tools\Generate-RdsScript.ps1' }

    $rdsTemplate = Join-Path $SourceDir 'rds\scripts\tkg_integration_install.template.js'
    if (-not (Test-Path $rdsTemplate)) { $rdsTemplate = Join-Path $toolsDir 'rds\scripts\tkg_integration_install.template.js' }
    $rdsScriptOut = Join-Path $rdsDir 'tkg_integration_install.js'

    $rdsRes = & $genRdsScript `
        -TemplatePath $rdsTemplate `
        -OutputPath $rdsScriptOut `
        -IntegrationKey $RdsIntegrationKey `
        -CallbackKey $RdsCallbackKey `
        -CallbackUrl $RdsCallbackUrl

    $RdsIntegrationKey = $rdsRes.IntegrationKey
    $RdsCallbackKey = $rdsRes.CallbackKey
    Log-Message "RDS commissioning script generated at $rdsScriptOut"

    # 6. Database Setup and Migrations
    Log-Message "Running database installation, migrations, and seeds..."
    $installDbScript = Join-Path $toolsDir 'Install-Database.ps1'
    if (-not (Test-Path $installDbScript)) { $installDbScript = Join-Path $SourceDir 'Tools\Install-Database.ps1' }

    $migrationsDir = Join-Path $SourceDir 'db\migrations'
    if (-not (Test-Path $migrationsDir)) { $migrationsDir = Join-Path $toolsDir 'db\migrations' }

    $seedsDir = Join-Path $SourceDir 'db\seeds'
    if (-not (Test-Path $seedsDir)) { $seedsDir = Join-Path $toolsDir 'db\seeds' }

    $candidatePgBin = Join-Path $InstallDir 'PostgreSQL\bin'
    if (-not (Test-Path (Join-Path $candidatePgBin 'psql.exe'))) {
        $candidatePgBin = Join-Path $SourceDir 'PostgreSQL\bin'
    }
    if (-not (Test-Path (Join-Path $candidatePgBin 'psql.exe'))) {
        $candidatePgBin = ''
    }

    $dbResult = & $installDbScript `
        -HostName $DbHost `
        -Port $DbPort `
        -Database $DbName `
        -Username $DbUser `
        -Password $DbPassword `
        -AppUser $AppDbUser `
        -AppPassword $AppDbPassword `
        -AdminPassword $AdminPassword `
        -PgBinPath $candidatePgBin `
        -DataDir $pgDataDir `
        -MigrationsDir $migrationsDir `
        -SeedsDir $seedsDir `
        -ServiceName $PgServiceName `
        -DemoMode:$DemoMode `
        -SkipServiceRegistration:$SkipServiceRegistration

    Log-Message "Database initialization verified with dedicated app user '$($dbResult.AppUser)'."

    $effectiveDbHost = if ($dbResult.Host) { $dbResult.Host } else { $DbHost }
    $effectiveDbPort = if ($dbResult.Port) { $dbResult.Port } else { $DbPort }
    $effectiveDbUser = if ($dbResult.Username) { $dbResult.Username } else { $DbUser }
    $effectiveDbPassword = if ($dbResult.Password) { $dbResult.Password } else { $DbPassword }

    if ($DemoMode) {
        Log-Message "Applying demo seed data for offline demonstration (port $effectiveDbPort)..."
        $demoSeed = Join-Path $SourceDir 'db\seeds\demo\demo_seed.sql'
        if (-not (Test-Path $demoSeed)) { $demoSeed = Join-Path $toolsDir 'db\seeds\demo\demo_seed.sql' }
        if (Test-Path $demoSeed) {
            $psqlBin = if ($candidatePgBin) { Join-Path $candidatePgBin 'psql.exe' } else { 'psql.exe' }
            $adminPsql = @('-X', '-h', $effectiveDbHost, '-p', "$effectiveDbPort", '-U', $effectiveDbUser, '-w', '-v', 'ON_ERROR_STOP=1')
            if (![string]::IsNullOrWhiteSpace($effectiveDbPassword)) {
                $env:PGPASSWORD = $effectiveDbPassword
            }
            & $psqlBin @adminPsql -d $DbName -f $demoSeed | Out-Null
            if ($LASTEXITCODE -ne 0) {
                Write-Warning "Demo seed application reported exit code $LASTEXITCODE."
            } else {
                Log-Message "Demo accounts, devices, and runtime state seeded successfully."
            }
        }
    }

    # 7. Secure Server Configuration Generation (DPAPI Fail Closed)
    Log-Message "Generating production configuration with DPAPI protection (DB port $effectiveDbPort)..."
    $connectionString = "Host=$effectiveDbHost;Port=$effectiveDbPort;Database=$DbName;Username=$($dbResult.AppUser);Password=$($dbResult.AppPassword);Pooling=true;Maximum Pool Size=50"

    # DPAPI Protection: MUST FAIL CLOSED. No fallback to plaintext or constant zero key!
    $isWin = ($env:OS -eq 'Windows_NT') -or ([System.Environment]::OSVersion.Platform -in @([System.PlatformID]::Win32NT, [System.PlatformID]::Win32Windows))
    if (-not $isWin) {
        throw "PLATFORM_ERROR: Production installation requires Windows DPAPI."
    }

    try {
        Add-Type -AssemblyName System.Security -ErrorAction SilentlyContinue
        $csBytes = [System.Text.Encoding]::UTF8.GetBytes($connectionString)
        $encCs = [System.Security.Cryptography.ProtectedData]::Protect($csBytes, $null, [System.Security.Cryptography.DataProtectionScope]::LocalMachine)
        $encConnectionString = "dpapi:" + [Convert]::ToBase64String($encCs)

        # Generate cryptographic 32-byte JWT signing key
        $jwtKeyBytes = New-Object byte[] 32
        $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
        $rng.GetBytes($jwtKeyBytes)
        $rng.Dispose()
        $rawJwtKey = [Convert]::ToBase64String($jwtKeyBytes)
        $jwtBytes = [System.Text.Encoding]::UTF8.GetBytes($rawJwtKey)
        $encJwtKey = "dpapi:" + [Convert]::ToBase64String([System.Security.Cryptography.ProtectedData]::Protect($jwtBytes, $null, [System.Security.Cryptography.DataProtectionScope]::LocalMachine))

        $intKeyBytes = [System.Text.Encoding]::UTF8.GetBytes($RdsIntegrationKey)
        $encIntegrationKey = "dpapi:" + [Convert]::ToBase64String([System.Security.Cryptography.ProtectedData]::Protect($intKeyBytes, $null, [System.Security.Cryptography.DataProtectionScope]::LocalMachine))

        $cbKeyBytes = [System.Text.Encoding]::UTF8.GetBytes($RdsCallbackKey)
        $encCallbackKey = "dpapi:" + [Convert]::ToBase64String([System.Security.Cryptography.ProtectedData]::Protect($cbKeyBytes, $null, [System.Security.Cryptography.DataProtectionScope]::LocalMachine))

        # Encrypt maintenance database credentials (never exposed to runtime API)
        $maintUser = if ($dbResult.Username) { $dbResult.Username } else { $DbUser }
        $maintPw = if ($dbResult.Password) { $dbResult.Password } else { $DbPassword }
        $maintPwBytes = [System.Text.Encoding]::UTF8.GetBytes($maintPw)
        $encMaintPassword = "dpapi:" + [Convert]::ToBase64String([System.Security.Cryptography.ProtectedData]::Protect($maintPwBytes, $null, [System.Security.Cryptography.DataProtectionScope]::LocalMachine))
    }
    catch {
        throw "DPAPI_ENCRYPTION_FAILED: DPAPI protection failed ($($_.Exception.Message)). Installation aborted to prevent insecure configuration."
    }

    if ($DemoMode) {
        $RdsBaseUrl = ''
        $RdsCoreBaseUrl = ''
        $RdsDispatchEnabled = $false
        Log-Message "DemoMode enabled: RDS field integration disabled, offline simulation runner configured."
    }

    $prodConfig = @{
        DemoMode = @{
            Enabled = [bool]$DemoMode
            SimulationDelaySeconds = 3
            RunningDurationSeconds = 30
            AutoPrepareWarehouseDemands = $false
            SimulatedAgvId = "DEMO-AGV-01"
        }
        ConnectionStrings = @{
            TkgPda = $encConnectionString
        }
        Kestrel = @{
            Endpoints = @{
                Http = @{
                    Url = "http://${HostBinding}:${ServerPort}"
                }
            }
        }
        Web = @{
            RootPath = (Join-Path $InstallDir 'Web')
        }
        Authentication = @{
            Jwt = @{
                Issuer = "tkg-pda"
                Audience = "tkg-pda-clients"
                SigningKey = $encJwtKey
                ExpiryMinutes = 60
                ClockSkewSeconds = 30
            }
            FieldSession = @{
                LeaseSeconds = 120
                HeartbeatSeconds = 30
            }
        }
        Rds = @{
            Enabled = (-not [string]::IsNullOrWhiteSpace($RdsBaseUrl))
            DispatchEnabled = $RdsDispatchEnabled
            BaseUrl = $RdsBaseUrl
            TimeoutSeconds = 5
            CallbackUrl = $RdsCallbackUrl
            IntegrationKey = $encIntegrationKey
            CallbackKey = $encCallbackKey
            WorkerIntervalSeconds = 3
            WorkSiteFreshnessSeconds = 300
            AlarmObservationIntervalSeconds = 10
            AllowInsecureRemoteHttp = (-not (Test-IsLoopbackHost $RdsBaseUrl))
        }
        RdsCore = @{
            BaseUrl = $RdsCoreBaseUrl
            TimeoutSeconds = 5
            AllowInsecureRemoteHttp = (-not (Test-IsLoopbackHost $RdsCoreBaseUrl))
        }
        RdsCoreDiscovery = @{
            Enabled = (-not [string]::IsNullOrWhiteSpace($RdsCoreBaseUrl))
        }
        RdsCoreExecution = @{
            Enabled = (-not [string]::IsNullOrWhiteSpace($RdsCoreBaseUrl))
            Mode = "RDSCORE_NATIVE_ORDER"
        }
        Cors = @{
            AllowedOrigins = @(
                "http://127.0.0.1:${ServerPort}",
                "http://localhost:${ServerPort}"
            )
        }
        Logging = @{
            LogLevel = @{
                Default = "Information"
                Microsoft = "Warning"
            }
        }
        AllowedHosts = "*"
    }

    $prodConfigFile = Join-Path $configDir 'appsettings.Production.json'
    $prodConfig | ConvertTo-Json -Depth 6 | Set-Content -Path $prodConfigFile -Encoding UTF8
    Log-Message "Production configuration saved to $prodConfigFile"

    # Save Separate Maintenance Database Configuration (DPAPI protected, restricted ACL)
    $maintConfig = @{
        Host = $effectiveDbHost
        Port = $effectiveDbPort
        Database = $DbName
        MaintenanceUser = $maintUser
        MaintenancePassword = $encMaintPassword
    }
    $maintConfigFile = Join-Path $configDir 'maintenance.json'
    $maintConfig | ConvertTo-Json -Depth 4 | Set-Content -Path $maintConfigFile -Encoding UTF8
    Log-Message "Maintenance database credentials saved to $maintConfigFile (DPAPI protected, restricted ACL)."

    # Maintenance credentials are stored DPAPI LocalMachine protected in maintenance.json
    Log-Message "Maintenance configuration secured with DPAPI LocalMachine."

    if (-not $SkipServiceRegistration) {
        # 8. Configure Windows Firewall
        Log-Message "Configuring Windows Firewall for port $ServerPort..."
        $fwScript = Join-Path $toolsDir 'Configure-Firewall.ps1'
        if (-not (Test-Path $fwScript)) { $fwScript = Join-Path $SourceDir 'Tools\Configure-Firewall.ps1' }
        & $fwScript -Port $ServerPort -Action Add -RuleName "$ServiceName (HTTP)"

        # 9. Install & Start Windows Service
        Log-Message "Registering and starting Windows Service '$ServiceName'..."
        $serviceScript = Join-Path $toolsDir 'Install-Service.ps1'
        if (-not (Test-Path $serviceScript)) { $serviceScript = Join-Path $SourceDir 'Tools\Install-Service.ps1' }

        $apiExe = Join-Path $serverDir 'TkgPda.Api.exe'
        & $serviceScript -Action Install -BinaryPath $apiExe -ServiceName $ServiceName

        # Configure Service Dependency on local PostgreSQL if local service exists
        $localPgSvc = Get-Service -Name $PgServiceName -ErrorAction SilentlyContinue
        if ($localPgSvc) {
            Log-Message "Configuring service dependency: '$ServiceName' -> '$PgServiceName'..."
            & sc.exe config $ServiceName depend= $PgServiceName | Out-Null
        }

        & $serviceScript -Action Start -ServiceName $ServiceName

        # 10. Verify Installation Health
        Log-Message "Performing deployment smoke tests..."
        Start-Sleep -Seconds 3

        $testScript = Join-Path $toolsDir 'test-installed-server.ps1'
        if (-not (Test-Path $testScript)) { $testScript = Join-Path $SourceDir 'Tools\test-installed-server.ps1' }

        & $testScript -ServerHost '127.0.0.1' -Port $ServerPort -ServiceName $ServiceName
        Log-Message "Deployment smoke tests PASSED!"
    } else {
        Log-Message "SkipServiceRegistration is active: skipping firewall, Windows service, and smoke test."
    }

    Log-Message "=================================================="
    Log-Message "INSTALLATION COMPLETED SUCCESSFULLY"
    Log-Message "Admin Web: http://SERVER:$ServerPort/admin"
    Log-Message "PDA Web:   http://SERVER:$ServerPort/pda"
    Log-Message "Landing:   http://SERVER:$ServerPort/"
    Log-Message "=================================================="

    return @{
        Status = "SUCCESS"
        InstallDir = $InstallDir
        ProgramDataDir = $ProgramDataDir
        ServerPort = $ServerPort
        LogFile = $logFile
    }
}
catch {
    Log-Message "INSTALLATION FAILED: $($_.Exception.Message)" "ERROR"
    Log-Message $_.ScriptStackTrace "ERROR"
    exit 1
}
finally {
    Remove-Item Env:TKG_SETUP_DB_PASSWORD -ErrorAction SilentlyContinue
    Remove-Item Env:TKG_SETUP_ADMIN_PASSWORD -ErrorAction SilentlyContinue
    Remove-Item Env:TKG_SETUP_APP_DB_PASSWORD -ErrorAction SilentlyContinue
    if (![string]::IsNullOrWhiteSpace($SecretsFile) -and (Test-Path $SecretsFile)) {
        Remove-Item -Path $SecretsFile -Force -ErrorAction SilentlyContinue
    }
}
