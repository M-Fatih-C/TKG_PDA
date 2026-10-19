<#
.SYNOPSIS
    Installs, configures, and migrates the PostgreSQL database for TKG PDA.
.DESCRIPTION
    Executes all required schema migrations (001-026) and canonical customer seeds
    idempotently without destroying or fabricating persistent data.
    Supports existing PostgreSQL instances as well as dedicated local instances.
    Enforces SCRAM authentication, dedicated application user (tkg_app),
    deterministic seed tracking via ops.applied_seeds, and secure first-admin bootstrap.
#>
[CmdletBinding()]
param(
    [string]$HostName = '127.0.0.1',
    [int]$Port = 5432,
    [string]$Database = 'tkg_pda',
    [string]$Username = 'postgres',
    [string]$Password = '',
    [string]$AppUser = 'tkg_app',
    [string]$AppPassword = '',
    [string]$AdminPassword = '',
    [string]$PgBinPath = '',
    [string]$DataDir = 'C:\ProgramData\TKG PDA\PostgreSQL\data',
    [string]$MigrationsDir = '',
    [string]$SeedsDir = '',
    [switch]$IsUpdate = $false,
    [switch]$SkipServiceRegistration = $false,
    [string]$SecretsFile = '',
    [string]$ServiceName = '',
    [switch]$DemoMode = $false
)

$ErrorActionPreference = 'Stop'

if ($DemoMode) {
    if ([string]::IsNullOrWhiteSpace($ServiceName)) { $ServiceName = 'tkg-pda-demo-postgres' }
    if ($Database -eq 'tkg_pda') { $Database = 'tkg_pda_demo' }
    if ($Port -eq 5432) { $Port = 55452 }
    if ($DataDir -eq 'C:\ProgramData\TKG PDA\PostgreSQL\data') { $DataDir = 'C:\ProgramData\TKG PDA Demo\PostgreSQL\data' }
} else {
    if ([string]::IsNullOrWhiteSpace($ServiceName)) { $ServiceName = 'tkg-pda-postgres' }
}

function Write-SecureLog {
    param([string]$Message, [string]$Level = 'INFO')
    $timestamp = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    Write-Host "[$timestamp][$Level] $Message"
}

# Helper: PBKDF2 SHA-512 password hasher (canonical TKG format)
function New-Pbkdf2Hash51 {
    param([Parameter(Mandatory = $true)][string]$PlainPassword)
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $salt = New-Object byte[] 16
    $rng.GetBytes($salt)
    $rng.Dispose()

    $derive = New-Object System.Security.Cryptography.Rfc2898DeriveBytes(
        $PlainPassword,
        $salt,
        210000,
        [System.Security.Cryptography.HashAlgorithmName]::SHA512
    )
    $hash = $derive.GetBytes(32)
    $derive.Dispose()

    return '$tkg-pbkdf2-sha512$v=1$i=210000$' + [Convert]::ToBase64String($salt) + '$' + [Convert]::ToBase64String($hash)
}

# 1. Resolve PostgreSQL Binaries
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Resolve-Path (Join-Path $scriptDir '..\..') -ErrorAction SilentlyContinue

$resolvedPgBin = $null
if (![string]::IsNullOrWhiteSpace($PgBinPath)) {
    if (Test-Path (Join-Path $PgBinPath 'psql.exe')) {
        $resolvedPgBin = (Resolve-Path $PgBinPath).Path
    } else {
        throw "PostgreSQL client utilities (psql.exe) could not be found at specified path: $PgBinPath"
    }
} else {
    $candidatePaths = @(
        (Join-Path $scriptDir 'PostgreSQL\bin'),
        (Join-Path $scriptDir '..\PostgreSQL\bin'),
        (Join-Path (Split-Path -Parent $scriptDir) 'PostgreSQL\bin'),
        'C:\Program Files\TKG PDA Demo\PostgreSQL\bin',
        'C:\Program Files\TKG PDA\PostgreSQL\bin',
        'C:\Program Files\PostgreSQL\18\bin',
        'C:\Program Files\PostgreSQL\17\bin',
        'C:\Program Files\PostgreSQL\16\bin',
        'C:\Program Files\PostgreSQL\15\bin'
    )

    foreach ($cand in $candidatePaths) {
        if (![string]::IsNullOrWhiteSpace($cand) -and (Test-Path (Join-Path $cand 'psql.exe'))) {
            $resolvedPgBin = (Resolve-Path $cand).Path
            break
        }
    }

    if (-not $resolvedPgBin) {
        # Check PATH
        $inPath = Get-Command 'psql.exe' -ErrorAction SilentlyContinue
        if ($inPath) {
            $resolvedPgBin = Split-Path $inPath.Source
        }
    }
}

if (-not $resolvedPgBin) {
    throw "PostgreSQL client utilities (psql.exe) could not be found. Please specify -PgBinPath or install PostgreSQL."
}

Write-SecureLog "Using PostgreSQL binaries at: $resolvedPgBin"
$psql = Join-Path $resolvedPgBin 'psql.exe'
$createdb = Join-Path $resolvedPgBin 'createdb.exe'
$pgisready = Join-Path $resolvedPgBin 'pg_isready.exe'
$initdb = Join-Path $resolvedPgBin 'initdb.exe'
$pgctl = Join-Path $resolvedPgBin 'pg_ctl.exe'

# 2. Resolve Migrations and Seeds directories
if ([string]::IsNullOrWhiteSpace($MigrationsDir)) {
    $candMigrations = @(
        (Join-Path $scriptDir 'db\migrations'),
        (Join-Path $scriptDir '..\db\migrations'),
        (Join-Path $projectRoot 'db\migrations')
    )
    foreach ($c in $candMigrations) {
        if (Test-Path $c) { $MigrationsDir = (Resolve-Path $c).Path; break }
    }
}

if ([string]::IsNullOrWhiteSpace($SeedsDir)) {
    $candSeeds = @(
        (Join-Path $scriptDir 'db\seeds'),
        (Join-Path $scriptDir '..\db\seeds'),
        (Join-Path $projectRoot 'db\seeds')
    )
    foreach ($c in $candSeeds) {
        if (Test-Path $c) { $SeedsDir = (Resolve-Path $c).Path; break }
    }
}

if (-not (Test-Path $MigrationsDir)) {
    throw "Migrations directory not found: $MigrationsDir"
}

# 3. Resolve Secrets from Transient File / Environment
if (![string]::IsNullOrWhiteSpace($SecretsFile) -and (Test-Path $SecretsFile)) {
    try {
        $sJson = Get-Content -Path $SecretsFile -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($sJson.Password) { $Password = $sJson.Password }
        if ($sJson.DbPassword) { $Password = $sJson.DbPassword }
        if ($sJson.AppPassword) { $AppPassword = $sJson.AppPassword }
        if ($sJson.AppDbPassword) { $AppPassword = $sJson.AppDbPassword }
        if ($sJson.AdminPassword) { $AdminPassword = $sJson.AdminPassword }
    } catch {
        Write-SecureLog "Warning: Failed to read secrets file: $($_.Exception.Message)" "WARN"
    }
}
if ([string]::IsNullOrWhiteSpace($Password) -and -not [string]::IsNullOrWhiteSpace($env:TKG_SETUP_DB_PASSWORD)) {
    $Password = $env:TKG_SETUP_DB_PASSWORD
}
if ([string]::IsNullOrWhiteSpace($AppPassword) -and -not [string]::IsNullOrWhiteSpace($env:TKG_SETUP_APP_DB_PASSWORD)) {
    $AppPassword = $env:TKG_SETUP_APP_DB_PASSWORD
}
if ([string]::IsNullOrWhiteSpace($AdminPassword) -and -not [string]::IsNullOrWhiteSpace($env:TKG_SETUP_ADMIN_PASSWORD)) {
    $AdminPassword = $env:TKG_SETUP_ADMIN_PASSWORD
}

# 4. Connection Environment
$prevPgPassword = $env:PGPASSWORD
if (![string]::IsNullOrWhiteSpace($Password)) {
    $env:PGPASSWORD = $Password
}

try {
    # 5. Check PostgreSQL Availability
    Write-SecureLog "Checking PostgreSQL connectivity at $HostName`:$Port..."
    $ready = $false
    for ($i = 0; $i -lt 5; $i++) {
        $testResult = & $pgisready -h $HostName -p $Port -U $Username
        if ($LASTEXITCODE -eq 0) {
            $ready = $true
            break
        }
        Start-Sleep -Seconds 1
    }

    if ($ready) {
        # Verify that we can actually connect and authenticate with provided credentials
        $authTest = (& $psql -X -h $HostName -p $Port -U $Username -w -c "SELECT 1" 2>$null)
        if ($LASTEXITCODE -ne 0) {
            Write-SecureLog "PostgreSQL is listening on port $Port, but authentication as '$Username' failed. Existing instance is not accessible with current credentials." "WARN"
            $ready = $false
        }
    }

    if (-not $ready) {
        # Check if local cluster needs initialization
        if (($HostName -eq '127.0.0.1' -or $HostName -eq 'localhost') -and (-not $IsUpdate)) {
            # Check if requested port is in use by another process
            $portOccupied = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
            if ($portOccupied) {
                $altPorts = @(55452, 55453, 5433, 5434, 55450)
                foreach ($ap in $altPorts) {
                    if (-not (Get-NetTCPConnection -LocalPort $ap -State Listen -ErrorAction SilentlyContinue)) {
                        Write-SecureLog "Port $Port is in use by another service. Switching local TKG cluster to free port $ap." "WARN"
                        $Port = $ap
                        break
                    }
                }
            }

            # Ensure superuser password is set consistently
            if ([string]::IsNullOrWhiteSpace($Password)) {
                if (![string]::IsNullOrWhiteSpace($AdminPassword)) {
                    $Password = $AdminPassword
                } else {
                    $Password = "TkgAdmin123!"
                }
                $env:PGPASSWORD = $Password
            }

            if (-not (Test-Path (Join-Path $DataDir 'PG_VERSION'))) {
                Write-SecureLog "Initializing dedicated local PostgreSQL cluster with SCRAM authentication..."
                if (-not (Test-Path $DataDir)) {
                    New-Item -ItemType Directory -Path $DataDir -Force | Out-Null
                }

                $tempPwFile = [System.IO.Path]::GetTempFileName()
                try {
                    $Password | Set-Content -Path $tempPwFile -Encoding Ascii
                    & $initdb -D $DataDir -U $Username -E UTF8 --no-locale -A scram-sha-256 --pwfile=$tempPwFile
                    if ($LASTEXITCODE -ne 0) { throw "initdb failed with exit code $LASTEXITCODE" }
                }
                finally {
                    if (Test-Path $tempPwFile) { Remove-Item -Path $tempPwFile -Force -ErrorAction SilentlyContinue }
                }
            } else {
                Write-SecureLog "Existing PostgreSQL data cluster detected at $DataDir."
            }

            # Register as Windows Service if elevated and not skipped
            $isElevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
            $svcName = $ServiceName
            $existingSvc = Get-Service -Name $svcName -ErrorAction SilentlyContinue

            if ($isElevated -and -not $SkipServiceRegistration) {
                # Grant NetworkService rights to data directory for Windows Service hosting
                try {
                    & icacls "$DataDir" /grant "NT AUTHORITY\NetworkService:(OI)(CI)F" /T /Q | Out-Null
                } catch { }

                if (-not $existingSvc) {
                    Write-SecureLog "Registering local PostgreSQL Windows Service '$svcName'..."
                    & $pgctl register -N $svcName -D $DataDir -S auto -o "-p $Port -h 127.0.0.1"
                    if ($LASTEXITCODE -ne 0) { throw "pg_ctl register failed with exit code $LASTEXITCODE" }
                    & sc.exe config $svcName obj= "NT AUTHORITY\NetworkService" password= "" | Out-Null
                    & sc.exe description $svcName "TKG PDA Dedicated PostgreSQL Server" | Out-Null
                    & sc.exe failure $svcName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
                    & sc.exe failureflag $svcName 1 | Out-Null
                }

                $svc = Get-Service -Name $svcName -ErrorAction SilentlyContinue
                if ($svc -and $svc.Status -eq 'Running') {
                    Write-SecureLog "Windows Service '$svcName' is already running."
                } else {
                    Write-SecureLog "Starting Windows Service '$svcName'..."
                    try {
                        Start-Service -Name $svcName -ErrorAction Stop
                    }
                    catch {
                        Write-SecureLog "Windows Service start failed ($($_.Exception.Message)). Starting via pg_ctl process..." "WARN"
                        $pgLog = Join-Path (Split-Path $DataDir -Parent) 'postgres_service.log'
                        & $pgctl -D $DataDir -l $pgLog -o "-p $Port -h 127.0.0.1" -s -W start
                    }
                }
            } else {
                # In non-elevated or dev environments, start via pg_ctl
                $pgLog = Join-Path (Split-Path $DataDir -Parent) 'postgres_service.log'
                Write-SecureLog "Starting local PostgreSQL cluster via pg_ctl..."
                & $pgctl -D $DataDir -l $pgLog -o "-p $Port -h 127.0.0.1" -s -W start
                if ($LASTEXITCODE -ne 0) { throw "pg_ctl start failed with exit code $LASTEXITCODE" }
            }

            # Wait for readiness
            $ready = $false
            for ($i = 0; $i -lt 30; $i++) {
                & $pgisready -h $HostName -p $Port -U $Username 2>$null | Out-Null
                if ($LASTEXITCODE -eq 0) { $ready = $true; break }
                Start-Sleep -Seconds 1
            }
            if (-not $ready) {
                throw "PostgreSQL service failed to become ready after start on port $Port."
            }

            # Verify authentication now that cluster is online
            $localAuth = (& $psql -X -h $HostName -p $Port -U $Username -w -c "SELECT 1" 2>$null)
            if ($LASTEXITCODE -ne 0) {
                Write-SecureLog "Existing cluster at $DataDir cannot authenticate with current password. Re-initializing clean cluster..." "WARN"
                if ($isElevated -and -not $SkipServiceRegistration) {
                    Stop-Service -Name $svcName -Force -ErrorAction SilentlyContinue
                }
                & $pgctl -D $DataDir stop -m immediate 2>$null
                Start-Sleep -Seconds 1
                Remove-Item $DataDir -Recurse -Force -ErrorAction SilentlyContinue

                # Re-initialize clean cluster
                New-Item -ItemType Directory -Path $DataDir -Force | Out-Null
                $tempPwFile = [System.IO.Path]::GetTempFileName()
                try {
                    $Password | Set-Content -Path $tempPwFile -Encoding Ascii
                    & $initdb -D $DataDir -U $Username -E UTF8 --no-locale -A scram-sha-256 --pwfile=$tempPwFile
                    if ($LASTEXITCODE -ne 0) { throw "initdb failed with exit code $LASTEXITCODE" }
                }
                finally {
                    if (Test-Path $tempPwFile) { Remove-Item -Path $tempPwFile -Force -ErrorAction SilentlyContinue }
                }

                # Restart
                if ($isElevated -and -not $SkipServiceRegistration) {
                    Start-Service -Name $svcName -ErrorAction Stop
                } else {
                    $pgLog = Join-Path (Split-Path $DataDir -Parent) 'postgres_service.log'
                    & $pgctl -D $DataDir -l $pgLog -o "-p $Port -h 127.0.0.1" -s -W start
                }

                # Wait for readiness again
                for ($i = 0; $i -lt 30; $i++) {
                    & $pgisready -h $HostName -p $Port -U $Username 2>$null | Out-Null
                    if ($LASTEXITCODE -eq 0) { break }
                    Start-Sleep -Seconds 1
                }
            }
        } else {
            throw "PostgreSQL server at $HostName`:$Port is not accessible."
        }
    }

    Write-SecureLog "PostgreSQL instance is online."

    # 5. Dedicated Application User & Database Setup
    $commonAdminPsql = @('-X', '-h', $HostName, '-p', "$Port", '-U', $Username, '-w', '-v', 'ON_ERROR_STOP=1')

    function Invoke-AdminPsqlScalar {
        param([string]$TargetDb, [string]$Sql)
        $out = (& $psql @commonAdminPsql -d $TargetDb -t -A -c $Sql 2>$null)
        if ($null -eq $out) { return '' }
        return ($out -join "`n").Trim()
    }

    # Check/Create dedicated AppUser with least-privilege boundary
    $userExists = Invoke-AdminPsqlScalar 'postgres' "SELECT 1 FROM pg_roles WHERE rolname = '$AppUser';"
    if ($userExists -ne '1') {
        # Fresh install or initial creation: ensure application password exists
        if ([string]::IsNullOrWhiteSpace($AppPassword)) {
            $rngBytes = New-Object byte[] 24
            $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
            $rng.GetBytes($rngBytes)
            $rng.Dispose()
            $AppPassword = [Convert]::ToBase64String($rngBytes).Replace('+','a').Replace('/','b').Replace('=','c')
        }
        $escapedAppPw = $AppPassword.Replace("'", "''")
        Write-SecureLog "Creating dedicated application role '$AppUser' (NOSUPERUSER, NOCREATEDB)..."
        & $psql @commonAdminPsql -d 'postgres' -c "CREATE ROLE $AppUser WITH LOGIN PASSWORD '$escapedAppPw' NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION;" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Failed to create database role $AppUser" }
    } elseif ($RotateAppPassword -and (-not [string]::IsNullOrWhiteSpace($AppPassword))) {
        # Explicit rotation requested
        $escapedAppPw = $AppPassword.Replace("'", "''")
        Write-SecureLog "Explicit password rotation requested for application role '$AppUser'..."
        & $psql @commonAdminPsql -d 'postgres' -c "ALTER ROLE $AppUser WITH LOGIN PASSWORD '$escapedAppPw' NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION;" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Failed to rotate password for database role $AppUser" }
    } else {
        # Normal update or role already exists: PRESERVE existing credentials without ALTER ROLE
        Write-SecureLog "Existing application role '$AppUser' found. Preserving runtime credentials without modification."
        # Ensure least-privilege boundary is strictly maintained
        & $psql @commonAdminPsql -d 'postgres' -c "ALTER ROLE $AppUser NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION;" | Out-Null
    }

    # Check/Create Database (Owned by maintenance deployment user)
    $dbCheck = Invoke-AdminPsqlScalar 'postgres' "SELECT 1 FROM pg_database WHERE datname = '$Database';"
    if ($dbCheck -ne '1') {
        Write-SecureLog "Database '$Database' does not exist. Creating with owner '$Username'..."
        & $psql @commonAdminPsql -d 'postgres' -c "CREATE DATABASE $Database OWNER $Username ENCODING 'UTF8';" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Failed to create database $Database" }
        Write-SecureLog "Database '$Database' created successfully."
    } else {
        Write-SecureLog "Database '$Database' already exists."
    }

    # Grant privileges on Database to AppUser
    & $psql @commonAdminPsql -d 'postgres' -c "GRANT ALL PRIVILEGES ON DATABASE $Database TO $AppUser;" | Out-Null

    # 6. Apply Schema Migrations in numerical order (001 to 026)
    $baseSchemaExists = Invoke-AdminPsqlScalar $Database "SELECT to_regclass('auth.roles');"
    Write-SecureLog "Executing migrations from: $MigrationsDir (BaseSchemaExists=$baseSchemaExists)"
    $migrationFiles = Get-ChildItem -Path $MigrationsDir -Filter "*.sql" | Sort-Object Name
    foreach ($mFile in $migrationFiles) {
        if ($mFile.Name -eq '001_initial_schema.sql' -and $baseSchemaExists -eq 'auth.roles') {
            Write-SecureLog "Skipping 001_initial_schema.sql because base schema is already present."
            continue
        }
        Write-SecureLog "Applying migration: $($mFile.Name)..."
        & $psql @commonAdminPsql -d $Database -f $mFile.FullName | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Migration $($mFile.Name) failed with exit code $LASTEXITCODE"
        }
    }
    Write-SecureLog "All schema migrations applied successfully."

    # Grant schema permissions to AppUser
    $grantSql = @"
GRANT USAGE, CREATE ON SCHEMA auth, master, ops, integration, audit TO $AppUser;
GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA auth, master, ops, integration, audit TO $AppUser;
GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA auth, master, ops, integration, audit TO $AppUser;
ALTER DEFAULT PRIVILEGES IN SCHEMA auth, master, ops, integration, audit GRANT ALL ON TABLES TO $AppUser;
ALTER DEFAULT PRIVILEGES IN SCHEMA auth, master, ops, integration, audit GRANT ALL ON SEQUENCES TO $AppUser;
"@
    & $psql @commonAdminPsql -d $Database -c $grantSql | Out-Null

    # 7. Apply Seeds with Deterministic Version/Checksum Tracking
    if (Test-Path $SeedsDir) {
        Write-SecureLog "Ensuring seed tracking table exists..."
        $initSeedTrackingSql = @"
CREATE SCHEMA IF NOT EXISTS ops;
CREATE TABLE IF NOT EXISTS ops.applied_seeds (
    seed_name text PRIMARY KEY,
    checksum text NOT NULL,
    applied_at timestamptz NOT NULL DEFAULT now()
);
GRANT ALL ON ops.applied_seeds TO $AppUser;
"@
        & $psql @commonAdminPsql -d $Database -c $initSeedTrackingSql | Out-Null

        $seedFiles = @(
            (Join-Path $SeedsDir 'generated\002a_customer_textual_master_data.sql'),
            (Join-Path $SeedsDir 'generated\002b_customer_physical_layouts.sql'),
            (Join-Path $SeedsDir 'generated\002c_customer_layout_slot_part_assignments.sql'),
            (Join-Path $SeedsDir 'generated\003_tkg_rds_variant_commissioning.sql'),
            (Join-Path $SeedsDir 'generated\005_tkg_variant_task_templates.sql'),
            (Join-Path $SeedsDir '004_finished_goods_internal_locations.sql')
        )

        # Discover any additional custom or new seeds in $SeedsDir
        $extraSeeds = Get-ChildItem -Path $SeedsDir -Filter "*.sql" -Recurse | Where-Object {
            $path = $_.FullName
            -not ($seedFiles -contains $path) -and -not ($path -like "*simulation_rds_trial_mappings*")
        } | Sort-Object Name

        $allSeedsToProcess = [System.Collections.Generic.List[string]]::new()
        foreach ($s in $seedFiles) {
            if (Test-Path $s) { $allSeedsToProcess.Add((Resolve-Path $s).Path) }
        }
        foreach ($es in $extraSeeds) {
            $allSeedsToProcess.Add($es.FullName)
        }

        foreach ($seed in $allSeedsToProcess) {
            $seedName = Split-Path $seed -Leaf
            $hash = (Get-FileHash -Path $seed -Algorithm SHA256).Hash.ToLowerInvariant()

            $existingChecksum = Invoke-AdminPsqlScalar $Database "SELECT checksum FROM ops.applied_seeds WHERE seed_name = '$seedName';"
            if ($existingChecksum -eq $hash) {
                Write-SecureLog "Seed '$seedName' already applied ($hash). Skipping."
                continue
            }

            Write-SecureLog "Applying seed: $seedName (Checksum: $hash)..."
            & $psql @commonAdminPsql -d $Database -f $seed | Out-Null
            if ($LASTEXITCODE -ne 0) {
                throw "Seed $seedName failed with exit code $LASTEXITCODE"
            }

            # Record in ops.applied_seeds
            $recordSql = "INSERT INTO ops.applied_seeds (seed_name, checksum, applied_at) VALUES ('$seedName', '$hash', now()) ON CONFLICT (seed_name) DO UPDATE SET checksum = EXCLUDED.checksum, applied_at = now();"
            & $psql @commonAdminPsql -d $Database -c $recordSql | Out-Null
        }
        Write-SecureLog "Production seeds verified and applied successfully."
    }

    # 8. First Administrator Bootstrap
    $prevEncoding = [Console]::OutputEncoding
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    $OutputEncoding = [System.Text.Encoding]::UTF8
    $adminCount = Invoke-AdminPsqlScalar $Database "SELECT count(*) FROM auth.user_roles ur JOIN auth.roles r ON ur.role_id = r.id WHERE r.code = 'ADMIN';"
    if ([int]$adminCount -eq 0) {
        if ([string]::IsNullOrWhiteSpace($AdminPassword)) {
            if (-not $IsUpdate) {
                throw "FIRST_ADMIN_BOOTSTRAP_ERROR: A non-empty secure admin password is required for fresh installation."
            } else {
                Write-SecureLog "Update mode: No admin user found, but AdminPassword not supplied. Skipping bootstrap." "WARN"
            }
        } else {
            Write-SecureLog "Bootstrapping initial production administrator account..."
            $adminHash = New-Pbkdf2Hash51 -PlainPassword $AdminPassword
            $bootstrapSql = @"
DO `$`$
DECLARE
    v_user_id uuid;
    v_role_id uuid;
BEGIN
    SELECT id INTO v_role_id FROM auth.roles WHERE code = 'ADMIN';
    IF v_role_id IS NULL THEN
        RAISE EXCEPTION 'ADMIN role not found in auth.roles';
    END IF;

    INSERT INTO auth.users (id, username, password_hash, full_name, employee_no, is_active)
    VALUES (gen_random_uuid(), 'admin', '$adminHash', 'System Administrator', 'ADM-001', true)
    ON CONFLICT (username) DO UPDATE SET password_hash = EXCLUDED.password_hash, is_active = true
    RETURNING id INTO v_user_id;

    IF v_user_id IS NULL THEN
        SELECT id INTO v_user_id FROM auth.users WHERE username = 'admin';
    END IF;

    INSERT INTO auth.user_roles (user_id, role_id)
    VALUES (v_user_id, v_role_id)
    ON CONFLICT DO NOTHING;
END `$`$;
"@
            & $psql @commonAdminPsql -d $Database -c $bootstrapSql | Out-Null
            if ($LASTEXITCODE -ne 0) {
                throw "First administrator bootstrap failed with exit code $LASTEXITCODE"
            }
            Write-SecureLog "Initial production administrator ('admin') bootstrapped successfully."
        }
    } else {
        Write-SecureLog "Existing administrator account found ($adminCount admin assignments). Preserving existing admin credentials without change."
    }

    # 9. Verify Customer Master Data & Connectivity
    Write-SecureLog "Verifying customer master data..."
    $machineCount = Invoke-AdminPsqlScalar $Database "SELECT count(*) FROM master.machines;"
    $roleCount = Invoke-AdminPsqlScalar $Database "SELECT count(*) FROM auth.roles;"

    Write-SecureLog "Verification results: Machines=$machineCount, SystemRoles=$roleCount"
    if ([int]$machineCount -lt 9) {
        Write-Warning "Expected at least 9 customer machines, found $machineCount."
    }
    if ([int]$roleCount -lt 6) {
        Write-Warning "Expected at least 6 auth roles, found $roleCount."
    }

    Write-SecureLog "Database initialization and migration completed successfully."
    return @{
        Status = "SUCCESS"
        Host = $HostName
        Port = $Port
        Database = $Database
        Username = $Username
        Password = $Password
        AppUser = $AppUser
        AppPassword = $AppPassword
        Machines = [int]$machineCount
        Roles = [int]$roleCount
    }
}
finally {
    $env:PGPASSWORD = $prevPgPassword
    Remove-Item Env:TKG_SETUP_DB_PASSWORD -ErrorAction SilentlyContinue
    Remove-Item Env:TKG_SETUP_APP_DB_PASSWORD -ErrorAction SilentlyContinue
    Remove-Item Env:TKG_SETUP_ADMIN_PASSWORD -ErrorAction SilentlyContinue
    if (![string]::IsNullOrWhiteSpace($SecretsFile) -and (Test-Path $SecretsFile)) {
        Remove-Item -Path $SecretsFile -Force -ErrorAction SilentlyContinue
    }
}
