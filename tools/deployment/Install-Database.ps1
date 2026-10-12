<#
.SYNOPSIS
    Installs, configures, and migrates the PostgreSQL database for TKG PDA.
.DESCRIPTION
    Executes all required schema migrations (001-026) and canonical customer seeds
    idempotently without destroying or fabricating persistent data.
    Supports existing PostgreSQL instances as well as dedicated local instances.
#>
[CmdletBinding()]
param(
    [string]$HostName = '127.0.0.1',
    [int]$Port = 5432,
    [string]$Database = 'tkg_pda',
    [string]$Username = 'postgres',
    [string]$Password = '',
    [string]$PgBinPath = '',
    [string]$DataDir = 'C:\ProgramData\TKG PDA\PostgreSQL\data',
    [string]$MigrationsDir = '',
    [string]$SeedsDir = '',
    [switch]$IsUpdate = $false
)

$ErrorActionPreference = 'Stop'

function Write-SecureLog {
    param([string]$Message, [string]$Level = 'INFO')
    $timestamp = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    Write-Host "[$timestamp][$Level] $Message"
}

# 1. Resolve PostgreSQL Binaries
$candidatePaths = @(
    $PgBinPath,
    'C:\Program Files\PostgreSQL\18\bin',
    'C:\Program Files\PostgreSQL\17\bin',
    'C:\Program Files\PostgreSQL\16\bin',
    'C:\Program Files\PostgreSQL\15\bin'
)

$resolvedPgBin = $null
foreach ($cand in $candidatePaths) {
    if (![string]::IsNullOrWhiteSpace($cand) -and (Test-Path (Join-Path $cand 'psql.exe'))) {
        $resolvedPgBin = $cand
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

if (-not $resolvedPgBin) {
    throw "PostgreSQL client utilities (psql.exe) could not be found. Please install PostgreSQL or specify -PgBinPath."
}

Write-SecureLog "Using PostgreSQL binaries at: $resolvedPgBin"
$psql = Join-Path $resolvedPgBin 'psql.exe'
$createdb = Join-Path $resolvedPgBin 'createdb.exe'
$pgisready = Join-Path $resolvedPgBin 'pg_isready.exe'
$initdb = Join-Path $resolvedPgBin 'initdb.exe'

# 2. Resolve Migrations and Seeds directories
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Resolve-Path (Join-Path $scriptDir '..\..') -ErrorAction SilentlyContinue

if ([string]::IsNullOrWhiteSpace($MigrationsDir)) {
    $candMigrations = @(
        (Join-Path $scriptDir 'db\migrations'),
        (Join-Path $projectRoot 'db\migrations')
    )
    foreach ($c in $candMigrations) {
        if (Test-Path $c) { $MigrationsDir = (Resolve-Path $c).Path; break }
    }
}

if ([string]::IsNullOrWhiteSpace($SeedsDir)) {
    $candSeeds = @(
        (Join-Path $scriptDir 'db\seeds'),
        (Join-Path $projectRoot 'db\seeds')
    )
    foreach ($c in $candSeeds) {
        if (Test-Path $c) { $SeedsDir = (Resolve-Path $c).Path; break }
    }
}

if (-not (Test-Path $MigrationsDir)) {
    throw "Migrations directory not found: $MigrationsDir"
}

# 3. Connection Environment
$prevPgPassword = $env:PGPASSWORD
if (![string]::IsNullOrWhiteSpace($Password)) {
    $env:PGPASSWORD = $Password
}

try {
    # 4. Check PostgreSQL Availability
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

    if (-not $ready) {
        # Check if local cluster needs initialization
        if (($HostName -eq '127.0.0.1' -or $HostName -eq 'localhost') -and (-not $IsUpdate)) {
            Write-SecureLog "PostgreSQL is not listening. Checking local data directory: $DataDir"
            if (-not (Test-Path (Join-Path $DataDir 'PG_VERSION'))) {
                Write-SecureLog "Initializing dedicated local PostgreSQL cluster..."
                if (-not (Test-Path $DataDir)) {
                    New-Item -ItemType Directory -Path $DataDir -Force | Out-Null
                }
                & $initdb -D $DataDir -U $Username -A trust -E UTF8 --no-locale
                if ($LASTEXITCODE -ne 0) { throw "initdb failed with exit code $LASTEXITCODE" }
            }
            # Start local cluster
            $pgctl = Join-Path $resolvedPgBin 'pg_ctl.exe'
            $pgLog = Join-Path (Split-Path $DataDir -Parent) 'postgres_service.log'
            Write-SecureLog "Starting PostgreSQL cluster..."
            & $pgctl -D $DataDir -l $pgLog -o "-p $Port -h 127.0.0.1" -w start
            if ($LASTEXITCODE -ne 0) { throw "pg_ctl start failed with exit code $LASTEXITCODE" }
        } else {
            throw "PostgreSQL server at $HostName`:$Port is not accessible."
        }
    }

    Write-SecureLog "PostgreSQL instance is online."

    # 5. Create database if absent
    $commonPsql = @('-X', '-h', $HostName, '-p', "$Port", '-U', $Username, '-w', '-v', 'ON_ERROR_STOP=1')

    function Invoke-PsqlScalar {
        param([string]$TargetDb, [string]$Sql)
        $out = (& $psql @commonPsql -d $TargetDb -t -A -c $Sql 2>$null)
        if ($null -eq $out) { return '' }
        return ($out -join "`n").Trim()
    }

    $dbCheck = Invoke-PsqlScalar 'postgres' "SELECT 1 FROM pg_database WHERE datname = '$Database';"
    if ($dbCheck -ne '1') {
        Write-SecureLog "Database '$Database' does not exist. Creating..."
        & $psql @commonPsql -d 'postgres' -c "CREATE DATABASE $Database ENCODING 'UTF8';"
        if ($LASTEXITCODE -ne 0) { throw "Failed to create database $Database" }
        Write-SecureLog "Database '$Database' created successfully."
    } else {
        Write-SecureLog "Database '$Database' already exists."
    }

    # 6. Apply Schema Migrations in numerical order (001 to 026)
    $baseSchemaExists = Invoke-PsqlScalar $Database "SELECT to_regclass('auth.roles');"
    Write-SecureLog "Executing migrations from: $MigrationsDir (BaseSchemaExists=$baseSchemaExists)"
    $migrationFiles = Get-ChildItem -Path $MigrationsDir -Filter "*.sql" | Sort-Object Name
    foreach ($mFile in $migrationFiles) {
        if ($mFile.Name -eq '001_initial_schema.sql' -and $baseSchemaExists -eq 'auth.roles') {
            Write-SecureLog "Skipping 001_initial_schema.sql because base schema is already present."
            continue
        }
        Write-SecureLog "Applying migration: $($mFile.Name)..."
        & $psql @commonPsql -d $Database -f $mFile.FullName | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Migration $($mFile.Name) failed with exit code $LASTEXITCODE"
        }
    }
    Write-SecureLog "All schema migrations applied successfully."

    # 7. Apply Seeds
    if (Test-Path $SeedsDir) {
        $masterCount = Invoke-PsqlScalar $Database "SELECT count(*) FROM master.machines;"

        if ($IsUpdate -and [int]$masterCount -ge 9) {
            Write-SecureLog "Customer master data already present ($masterCount machines). Preserving existing operational records and skipping fresh seeds."
        } else {
            Write-SecureLog "Applying required production seeds from: $SeedsDir"
            $seedFiles = @(
                (Join-Path $SeedsDir 'generated\002a_customer_textual_master_data.sql'),
                (Join-Path $SeedsDir 'generated\002b_customer_physical_layouts.sql'),
                (Join-Path $SeedsDir 'generated\002c_customer_layout_slot_part_assignments.sql'),
                (Join-Path $SeedsDir 'generated\003_tkg_rds_variant_commissioning.sql'),
                (Join-Path $SeedsDir 'generated\005_tkg_variant_task_templates.sql'),
                (Join-Path $SeedsDir '004_finished_goods_internal_locations.sql')
            )

            foreach ($seed in $seedFiles) {
                if (Test-Path $seed) {
                    $seedName = Split-Path $seed -Leaf
                    Write-SecureLog "Applying seed: $seedName..."
                    & $psql @commonPsql -d $Database -f $seed | Out-Null
                    if ($LASTEXITCODE -ne 0) {
                        throw "Seed $seedName failed with exit code $LASTEXITCODE"
                    }
                }
            }
            Write-SecureLog "All required production seeds applied successfully."
        }
    }

    # 8. Replay Safety Validation
    Write-SecureLog "Validating replay safety of migrations..."
    # Re-running migration 006 and 026 to verify idempotency
    $testMigrations = @(
        (Join-Path $MigrationsDir '006_production_sessions.sql'),
        (Join-Path $MigrationsDir '026_canonical_shared_endpoint_identities.sql')
    )
    foreach ($tm in $testMigrations) {
        if (Test-Path $tm) {
            & $psql @commonPsql -d $Database -f $tm | Out-Null
            if ($LASTEXITCODE -ne 0) {
                throw "Replay safety validation failed on $(Split-Path $tm -Leaf)"
            }
        }
    }
    Write-SecureLog "Replay safety verified: PASS"

    # 9. Verify Expected Customer Master Data
    Write-SecureLog "Verifying customer master data..."
    $machineCount = Invoke-PsqlScalar $Database "SELECT count(*) FROM master.machines;"
    $roleCount = Invoke-PsqlScalar $Database "SELECT count(*) FROM auth.roles;"

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
        Machines = [int]$machineCount
        Roles = [int]$roleCount
    }
}
finally {
    $env:PGPASSWORD = $prevPgPassword
}
