<#
.SYNOPSIS
    Installs TKG PDA Server on Windows Server without IIS.
.DESCRIPTION
    Automates complete installation:
    - Sets up standard directories under Program Files and ProgramData
    - Deploys self-contained .NET Kestrel server and web assets
    - Configures and migrates PostgreSQL database
    - Generates RDS commissioning script
    - Configures Windows Firewall for server port
    - Registers and starts "TKG PDA Server" Windows Service
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
    [string]$RdsBaseUrl = 'http://127.0.0.1:8080',
    [string]$RdsCallbackUrl = '',
    [string]$RdsIntegrationKey = '',
    [string]$RdsCallbackKey = '',
    [bool]$RdsDispatchEnabled = $false,
    [string]$AdminPassword = '',
    [string]$Version = '1.0.0'
)

$ErrorActionPreference = 'Stop'

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

try {
    # 2. Create Directory Structure
    Log-Message "Creating required directories..."
    $serverDir = Join-Path $InstallDir 'Server'
    $webAdminDir = Join-Path $InstallDir 'Web\admin'
    $webPdaDir = Join-Path $InstallDir 'Web\pda'
    $webRootDir = Join-Path $InstallDir 'Web\root'
    $toolsDir = Join-Path $InstallDir 'Tools'

    $configDir = Join-Path $ProgramDataDir 'Config'
    $backupsDir = Join-Path $ProgramDataDir 'Backups'
    $dataDir = Join-Path $ProgramDataDir 'Data'
    $rdsDir = Join-Path $ProgramDataDir 'RDS'

    $allDirs = @($serverDir, $webAdminDir, $webPdaDir, $webRootDir, $toolsDir, $configDir, $backupsDir, $dataDir, $rdsDir)
    foreach ($d in $allDirs) {
        if (-not (Test-Path $d)) {
            New-Item -ItemType Directory -Path $d -Force | Out-Null
        }
    }

    # 3. Copy Application Files from Source
    Log-Message "Deploying application server binaries..."
    $srcServer = Join-Path $SourceDir 'Server'
    if (Test-Path $srcServer) {
        Copy-Item -Path "$srcServer\*" -Destination $serverDir -Recurse -Force
    } else {
        throw "Server binaries missing from source: $srcServer"
    }

    Log-Message "Deploying web client interfaces..."
    $srcWebAdmin = Join-Path $SourceDir 'Web\admin'
    if (Test-Path $srcWebAdmin) {
        Copy-Item -Path "$srcWebAdmin\*" -Destination $webAdminDir -Recurse -Force
    }

    $srcWebPda = Join-Path $SourceDir 'Web\pda'
    if (Test-Path $srcWebPda) {
        Copy-Item -Path "$srcWebPda\*" -Destination $webPdaDir -Recurse -Force
    }

    $srcWebRoot = Join-Path $SourceDir 'Web\root'
    if (Test-Path $srcWebRoot) {
        Copy-Item -Path "$srcWebRoot\*" -Destination $webRootDir -Recurse -Force
    }

    $srcTools = Join-Path $SourceDir 'Tools'
    if (Test-Path $srcTools) {
        Copy-Item -Path "$srcTools\*" -Destination $toolsDir -Recurse -Force
    }

    # 4. Generate RDS Keys and Commissioning Script
    Log-Message "Configuring RDS settings and script..."
    if ([string]::IsNullOrWhiteSpace($RdsCallbackUrl)) {
        $RdsCallbackUrl = "http://127.0.0.1:$ServerPort/api/integrations/rds/events"
    }

    $genRdsScript = Join-Path $toolsDir 'Generate-RdsScript.ps1'
    if (-not (Test-Path $genRdsScript)) { $genRdsScript = Join-Path $SourceDir 'Tools\Generate-RdsScript.ps1' }

    $rdsTemplate = Join-Path $SourceDir 'rds\scripts\tkg_integration_install.template.js'
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

    # 5. Database Setup and Migrations
    Log-Message "Running database installation and migrations..."
    $installDbScript = Join-Path $toolsDir 'Install-Database.ps1'
    if (-not (Test-Path $installDbScript)) { $installDbScript = Join-Path $SourceDir 'Tools\Install-Database.ps1' }

    $migrationsDir = Join-Path $SourceDir 'db\migrations'
    $seedsDir = Join-Path $SourceDir 'db\seeds'

    & $installDbScript `
        -HostName $DbHost `
        -Port $DbPort `
        -Database $DbName `
        -Username $DbUser `
        -Password $DbPassword `
        -MigrationsDir $migrationsDir `
        -SeedsDir $seedsDir

    Log-Message "Database initialization verified."

    # 6. Secure Server Configuration Generation
    Log-Message "Generating production configuration..."
    $connectionString = "Host=$DbHost;Port=$DbPort;Database=$DbName;Username=$DbUser;Password=$DbPassword;Pooling=true;Maximum Pool Size=50"
    
    # DPAPI Protection for sensitive fields
    $encConnectionString = ""
    $encJwtKey = ""
    $encIntegrationKey = ""
    $encCallbackKey = ""

    try {
        $csBytes = [System.Text.Encoding]::UTF8.GetBytes($connectionString)
        $encCs = [System.Security.Cryptography.ProtectedData]::Protect($csBytes, $null, [System.Security.Cryptography.DataProtectionScope]::LocalMachine)
        $encConnectionString = "dpapi:" + [Convert]::ToBase64String($encCs)

        # Generate or protect JWT key
        $jwtKeyBytes = New-Object byte[] 32
        $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
        $rng.GetBytes($jwtKeyBytes)
        $rng.Dispose()
        $rawJwtKey = [Convert]::ToBase64String($jwtKeyBytes)
        $encJwtKey = "dpapi:" + [Convert]::ToBase64String([System.Security.Cryptography.ProtectedData]::Protect([System.Text.Encoding]::UTF8.GetBytes($rawJwtKey), $null, [System.Security.Cryptography.DataProtectionScope]::LocalMachine))

        $encIntegrationKey = "dpapi:" + [Convert]::ToBase64String([System.Security.Cryptography.ProtectedData]::Protect([System.Text.Encoding]::UTF8.GetBytes($RdsIntegrationKey), $null, [System.Security.Cryptography.DataProtectionScope]::LocalMachine))
        $encCallbackKey = "dpapi:" + [Convert]::ToBase64String([System.Security.Cryptography.ProtectedData]::Protect([System.Text.Encoding]::UTF8.GetBytes($RdsCallbackKey), $null, [System.Security.Cryptography.DataProtectionScope]::LocalMachine))
    } catch {
        Log-Message "DPAPI protection skipped (non-Windows or limited permission): $_" "WARN"
        $encConnectionString = $connectionString
        $encJwtKey = [Convert]::ToBase64String((New-Object byte[] 32))
        $encIntegrationKey = $RdsIntegrationKey
        $encCallbackKey = $RdsCallbackKey
    }

    $prodConfig = @{
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
            Enabled = $true
            DispatchEnabled = $RdsDispatchEnabled
            BaseUrl = $RdsBaseUrl
            TimeoutSeconds = 5
            CallbackUrl = $RdsCallbackUrl
            IntegrationKey = $encIntegrationKey
            CallbackKey = $encCallbackKey
            WorkerIntervalSeconds = 3
            WorkSiteFreshnessSeconds = 300
            AlarmObservationIntervalSeconds = 10
        }
        RdsCore = @{
            BaseUrl = "http://127.0.0.1:8088"
            TimeoutSeconds = 5
            AllowInsecureRemoteHttp = $false
        }
        RdsCoreDiscovery = @{
            Enabled = $true
        }
        RdsCoreExecution = @{
            Enabled = $true
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

    # Restrict ACL on Config directory to SYSTEM and Administrators
    try {
        & icacls "$configDir" /inheritance:r /grant:r "SYSTEM:(OI)(CI)F" "Administrators:(OI)(CI)F" | Out-Null
        Log-Message "Config directory ACL secured."
    } catch {
        Log-Message "ACL restriction warning: $_" "WARN"
    }

    # 7. Configure Windows Firewall
    Log-Message "Configuring Windows Firewall for port $ServerPort..."
    $fwScript = Join-Path $toolsDir 'Configure-Firewall.ps1'
    if (-not (Test-Path $fwScript)) { $fwScript = Join-Path $SourceDir 'Tools\Configure-Firewall.ps1' }
    & $fwScript -Port $ServerPort -Action Add

    # 8. Install & Start Windows Service
    Log-Message "Registering and starting Windows Service 'TKG PDA Server'..."
    $serviceScript = Join-Path $toolsDir 'Install-Service.ps1'
    if (-not (Test-Path $serviceScript)) { $serviceScript = Join-Path $SourceDir 'Tools\Install-Service.ps1' }

    $apiExe = Join-Path $serverDir 'TkgPda.Api.exe'
    & $serviceScript -Action Install -BinaryPath $apiExe -ServiceName 'TKG PDA Server'
    & $serviceScript -Action Start -ServiceName 'TKG PDA Server'

    # 9. Verify Installation Health
    Log-Message "Performing deployment smoke tests..."
    Start-Sleep -Seconds 3

    $testScript = Join-Path $toolsDir 'test-installed-server.ps1'
    if (-not (Test-Path $testScript)) { $testScript = Join-Path $SourceDir 'Tools\test-installed-server.ps1' }

    & $testScript -ServerHost '127.0.0.1' -Port $ServerPort -ServiceName 'TKG PDA Server'
    Log-Message "Deployment smoke tests PASSED!"

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
    throw
}
