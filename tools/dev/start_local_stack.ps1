<#
.SYNOPSIS
    Starts the persistent local development stack for TKG PDA.
.DESCRIPTION
    Launches dedicated PostgreSQL instance, ASP.NET Core API, React Admin Web,
    and Flutter Operator Web in local development mode.
    Data is persisted outside the Git repository in $env:LOCALAPPDATA\TKG_PDA_DEV.
    Compatible with Windows PowerShell 5.1.
#>

[CmdletBinding()]
param(
    [string]$PgBin = 'C:\Program Files\PostgreSQL\18\bin',
    [int]$PostgresPort = 55450,
    [string]$Database = 'tkg_pda_dev',
    [int]$ApiPort = 5000,
    [int]$AdminPort = 4173,
    [int]$OperatorPort = 7357,
    [string]$HostAddress = '0.0.0.0',
    [ValidateSet('Commissioning', 'ReadOnly', 'Off')]
    [string]$RdsMode = 'Commissioning',
    [string]$RdsBaseUrl = 'http://127.0.0.1:8080',
    [string]$IntegrationKey,
    [string]$CallbackKey,
    [string]$CallbackUrl,
    [string]$Password,
    [System.Security.SecureString]$SecurePassword,
    [switch]$NoOperator,
    [switch]$NoAdmin,
    [switch]$EnableSimulationTrialMappings = $true,
    [switch]$ResetSimulationState,
    [switch]$ForceSeeds,
    [switch]$Wait
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

# Dev directories outside Git repository
$localAppData = [Environment]::GetEnvironmentVariable('LOCALAPPDATA')
if ([string]::IsNullOrWhiteSpace($localAppData)) {
    $localAppData = Join-Path $env:USERPROFILE 'AppData\Local'
}
$devRoot   = Join-Path $localAppData 'TKG_PDA_DEV'
$pgDataDir = Join-Path $devRoot 'postgres'
$logsDir   = Join-Path $devRoot 'logs'
$stateDir  = Join-Path $devRoot 'state'
$stateFile = Join-Path $stateDir 'stack_state.json'
$runtimeDir = Join-Path $projectRoot '.runtime'
$apiPidFile = Join-Path $runtimeDir 'api.pid'

foreach ($dir in @($devRoot, $logsDir, $stateDir, $runtimeDir)) {
    if (-not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
}

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

function Reclaim-PortIfOwned {
    param(
        [Parameter(Mandatory = $true)][int]$Port,
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$ServiceName
    )
    $conn = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $conn) { return }

    $owningPid = $conn.OwningProcess
    $cim = Get-ProcessCim $owningPid
    if (-not $cim) { return }

    if (Test-ProcessOwnedByRepo -ProcessId $owningPid -RepoRoot $RepoRoot) {
        Write-Host "Port $Port is occupied by existing $ServiceName process (PID $owningPid) from this repository. Reclaiming..." -ForegroundColor Cyan
        Stop-OwnedProcessSafely -ProcessId $owningPid -RepoRoot $RepoRoot -Description $ServiceName | Out-Null
    } else {
        Write-Host ""
        Write-Host "============================================================" -ForegroundColor Red
        Write-Host "ERROR: ${ServiceName}_PORT_IN_USE_BY_FOREIGN_PROCESS" -ForegroundColor Red
        Write-Host "============================================================" -ForegroundColor Red
        Write-Host "Port $Port is occupied by a foreign process not belonging to this repository:"
        Write-Host "  PID:            $($cim.ProcessId)"
        Write-Host "  ProcessName:    $($cim.Name)"
        Write-Host "  ExecutablePath: $($cim.ExecutablePath)"
        Write-Host "  CommandLine:    $($cim.CommandLine)"
        Write-Host "============================================================" -ForegroundColor Red
        throw "${ServiceName}_PORT_IN_USE_BY_FOREIGN_PROCESS: Port $Port is occupied by foreign process (PID $($cim.ProcessId))"
    }
}

# Helper: PBKDF2 hasher in PowerShell 5.1
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

# 1. Initialize PostgreSQL cluster if absent
$pgCtl = Join-Path $PgBin 'pg_ctl.exe'
$initDb = Join-Path $PgBin 'initdb.exe'
$psql = Join-Path $PgBin 'psql.exe'
$createdb = Join-Path $PgBin 'createdb.exe'

if (-not (Test-Path -LiteralPath (Join-Path $pgDataDir 'PG_VERSION'))) {
    Write-Host 'Initializing persistent PostgreSQL development cluster...' -ForegroundColor Cyan
    & $initDb -D $pgDataDir -U postgres -A trust --no-locale -E UTF8
    if ($LASTEXITCODE -ne 0) { throw "initdb failed with exit code $LASTEXITCODE" }
}

# Start PostgreSQL if not already listening
$pgListening = Get-NetTCPConnection -State Listen -LocalPort $PostgresPort -ErrorAction SilentlyContinue
if (-not $pgListening) {
    Write-Host "Starting PostgreSQL on 127.0.0.1:$PostgresPort..." -ForegroundColor Cyan
    $pgLog = Join-Path $logsDir 'postgres.log'
    & $pgCtl -D $pgDataDir -l $pgLog -o "-p $PostgresPort -h 127.0.0.1" -w start
    if ($LASTEXITCODE -ne 0) { throw "pg_ctl start failed with exit code $LASTEXITCODE" }
} else {
    Write-Host "PostgreSQL is already running on port $PostgresPort." -ForegroundColor Green
}

# Helper: safe psql scalar query
function Invoke-DevPsqlScalar {
    param(
        [Parameter(Mandatory = $true)][string]$DatabaseName,
        [Parameter(Mandatory = $true)][string]$Sql
    )
    $output = (& $psql @commonPsql -d $DatabaseName -tAc $Sql)
    if ($null -eq $output) { return '' }
    return ($output -join "`n").Trim()
}

# Create database if absent
$commonPsql = @('-X', '-h', '127.0.0.1', '-p', "$PostgresPort", '-U', 'postgres')
$dbExists = Invoke-DevPsqlScalar 'postgres' "SELECT 1 FROM pg_database WHERE datname = '$Database'"
if ($dbExists -ne '1') {
    Write-Host "Creating development database '$Database'..." -ForegroundColor Cyan
    & $createdb -h '127.0.0.1' -p "$PostgresPort" -U 'postgres' $Database
    if ($LASTEXITCODE -ne 0) { throw "createdb failed with exit code $LASTEXITCODE" }
}

# Apply migrations and seeds idempotently
$schemaExists = Invoke-DevPsqlScalar $Database "SELECT 1 FROM information_schema.tables WHERE table_schema = 'auth' AND table_name = 'roles'"
if ($schemaExists -ne '1') {
    Write-Host 'Applying initial schema (001_initial_schema.sql)...' -ForegroundColor Cyan
    & $psql @commonPsql -d $Database -v ON_ERROR_STOP=1 -f (Join-Path $projectRoot 'db\migrations\001_initial_schema.sql') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Applying 001_initial_schema.sql failed with exit code $LASTEXITCODE" }
}

# Migration 002 is idempotent (CREATE UNIQUE INDEX IF NOT EXISTS); apply it on
# every startup so an existing pre-WP01A database is upgraded in place, while a
# freshly created database (001 above) also receives it. It is a no-op once the
# normalized employee_no index is present.
Write-Host 'Applying migration 002 (002_operator_employee_identity.sql)...' -ForegroundColor Cyan
& $psql @commonPsql -d $Database -v ON_ERROR_STOP=1 -f (Join-Path $projectRoot 'db\migrations\002_operator_employee_identity.sql') | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Applying 002_operator_employee_identity.sql failed with exit code $LASTEXITCODE" }
Write-Host 'Applying migration 003 (003_transport_batch.sql)...' -ForegroundColor Cyan
& $psql @commonPsql -d $Database -v ON_ERROR_STOP=1 -f (Join-Path $projectRoot 'db\migrations\003_transport_batch.sql') | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Applying 003_transport_batch.sql failed with exit code $LASTEXITCODE" }
Write-Host 'Applying migration 004 (004_transport_request_readiness.sql)...' -ForegroundColor Cyan
& $psql @commonPsql -d $Database -v ON_ERROR_STOP=1 -f (Join-Path $projectRoot 'db\migrations\004_transport_request_readiness.sql') | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Applying 004_transport_request_readiness.sql failed with exit code $LASTEXITCODE" }
Write-Host 'Applying migration 005 (005_layout_slot_part_mapping.sql)...' -ForegroundColor Cyan
& $psql @commonPsql -d $Database -v ON_ERROR_STOP=1 -f (Join-Path $projectRoot 'db\migrations\005_layout_slot_part_mapping.sql') | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Applying 005_layout_slot_part_mapping.sql failed with exit code $LASTEXITCODE" }
foreach ($migration in @(
    '006_production_sessions.sql',
    '007_bring_full_task_type.sql',
    '008_warehouse_preparation_areas.sql',
    '009_preparation_placement_key.sql',
    '010_finished_goods_store.sql',
    '011_warehouse_operator_roles.sql',
    '012_client_device_machine_binding.sql',
    '013_user_sessions.sql',
    '014_transport_event_context.sql',
    '015_part_demand_dependencies.sql',
    '016_finished_goods_areas.sql',
    '017_internal_device_notifications.sql',
    '018_production_end_priorities.sql',
    '019_robot_alarm_observations.sql',
    '020_robot_alarm_observation_state.sql',
    '021_production_end_cleanup.sql',
    '022_variant_scoped_rds_worksite_commissioning.sql',
    '023_rds_variant_commissioning_hardening.sql',
    '024_operational_reporting.sql',
    '025_profile_scoped_shared_endpoints.sql',
    '026_canonical_shared_endpoint_identities.sql',
    '027_store_location_unit_capacity_constraint.sql',
    '028_store_reservations_and_source_endpoint.sql',
    '029_store_reservation_integrity.sql',
    '030_deferred_part_demand_slot_reservation.sql',
    '031_physical_reconciliation_and_removal_audit.sql',
    '032_finished_goods_preparation_areas.sql',
    '033_finished_goods_untyped_empty_preparation.sql',
    '034_finished_product_container_options.sql',
    '035_area_based_production_end_priorities.sql',
    '036_deferred_transport_resources.sql'
)) {
    Write-Host "Applying migration $migration..." -ForegroundColor Cyan
    & $psql @commonPsql -d $Database -v ON_ERROR_STOP=1 -f (Join-Path $projectRoot "db\migrations\$migration") | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Applying $migration failed with exit code $LASTEXITCODE" }
}

Write-Host 'Applying replay-safe customer master seeds (002a, 002b, 002c)...' -ForegroundColor Cyan
foreach ($sf in @(
    (Join-Path $projectRoot 'db\seeds\generated\002a_customer_textual_master_data.sql'),
    (Join-Path $projectRoot 'db\seeds\generated\002b_customer_physical_layouts.sql'),
    (Join-Path $projectRoot 'db\seeds\generated\002c_customer_layout_slot_part_assignments.sql'),
    (Join-Path $projectRoot 'db\seeds\005_finished_product_container_options.sql'),
    (Join-Path $projectRoot 'db\seeds\006_blank_alt_part_euro_pallet.sql')
)) {
    & $psql @commonPsql -d $Database -v ON_ERROR_STOP=1 -f $sf | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Applying master seed failed: $sf" }
}

# Determine whether the commissioning registry (003) is structurally complete using seed invariants
$regCheckSql = @"
SELECT
    count(*) AS profile_count,
    count(DISTINCT robot_map_name) AS map_count,
    count(DISTINCT code) AS code_count,
    (SELECT count(*) FROM master.variant_navigation_profile_assignments vnpa JOIN master.navigation_profiles np ON np.id = vnpa.navigation_profile_id WHERE np.config_json ->> 'commissioningRegistry' = 'tkg_rds_variant_map_naming_v1') AS assignment_count,
    (SELECT count(*) FROM master.navigation_profiles np WHERE np.config_json ->> 'commissioningRegistry' = 'tkg_rds_variant_map_naming_v1' AND np.config_json ->> 'layoutBindingState' = 'SOURCE_BACKED') AS source_backed_count,
    (SELECT count(*) FROM integration.rds_worksite_plans p JOIN master.navigation_profiles np ON np.id = p.navigation_profile_id WHERE np.config_json ->> 'commissioningRegistry' = 'tkg_rds_variant_map_naming_v1') AS plan_count,
    (SELECT count(*) FROM integration.layout_slot_rds_plan_bindings b JOIN master.navigation_profiles np ON np.id = b.navigation_profile_id WHERE np.config_json ->> 'commissioningRegistry' = 'tkg_rds_variant_map_naming_v1') AS binding_count,
    (SELECT count(*) FROM integration.layout_slot_rds_plan_bindings b JOIN master.navigation_profiles np ON np.id = b.navigation_profile_id WHERE np.code = 'TKG_SEALER_V03_NAV') AS sealer_bindings,
    (SELECT count(*) FROM integration.rds_worksite_plans p JOIN integration.rds_worksites w ON w.rds_site_id = p.planned_rds_site_id JOIN master.navigation_profiles np ON np.id = p.navigation_profile_id WHERE np.config_json ->> 'commissioningRegistry' = 'tkg_rds_variant_map_naming_v1') AS live_name_collisions
FROM master.navigation_profiles
WHERE config_json ->> 'commissioningRegistry' = 'tkg_rds_variant_map_naming_v1';
"@

$regRaw = Invoke-DevPsqlScalar $Database $regCheckSql
$regParts = if ($regRaw) { $regRaw.Trim().Split('|') } else { @() }

$profileCount      = if ($regParts.Length -ge 1 -and [int]::TryParse($regParts[0], [ref]$null)) { [int]$regParts[0] } else { 0 }
$mapCount          = if ($regParts.Length -ge 2 -and [int]::TryParse($regParts[1], [ref]$null)) { [int]$regParts[1] } else { 0 }
$codeCount         = if ($regParts.Length -ge 3 -and [int]::TryParse($regParts[2], [ref]$null)) { [int]$regParts[2] } else { 0 }
$assignmentCount   = if ($regParts.Length -ge 4 -and [int]::TryParse($regParts[3], [ref]$null)) { [int]$regParts[3] } else { 0 }
$sourceBackedCount = if ($regParts.Length -ge 5 -and [int]::TryParse($regParts[4], [ref]$null)) { [int]$regParts[4] } else { 0 }
$planCount         = if ($regParts.Length -ge 6 -and [int]::TryParse($regParts[5], [ref]$null)) { [int]$regParts[5] } else { 0 }
$bindingCount      = if ($regParts.Length -ge 7 -and [int]::TryParse($regParts[6], [ref]$null)) { [int]$regParts[6] } else { 0 }
$sealerBindings    = if ($regParts.Length -ge 8 -and [int]::TryParse($regParts[7], [ref]$null)) { [int]$regParts[7] } else { 0 }
$liveCollisions    = if ($regParts.Length -ge 9 -and [int]::TryParse($regParts[8], [ref]$null)) { [int]$regParts[8] } else { 0 }

$regComplete = ($profileCount -eq 25 -and
    $mapCount -eq 25 -and
    $codeCount -eq 25 -and
    $assignmentCount -eq 25 -and
    $sourceBackedCount -eq 24 -and
    $planCount -eq 1775 -and
    $bindingCount -eq 208 -and
    $sealerBindings -eq 0)

if ($regComplete) {
    Write-Host "Commissioning registry structurally complete." -ForegroundColor Green
    Write-Host "Bypassing 003 seed replay." -ForegroundColor Green
} elseif ($liveCollisions -eq 0) {
    Write-Host 'Applying commissioning registry seed (003_tkg_rds_variant_commissioning.sql)...' -ForegroundColor Cyan
    $seed003 = (Join-Path $projectRoot 'db\seeds\generated\003_tkg_rds_variant_commissioning.sql')
    & $psql @commonPsql -d $Database -v ON_ERROR_STOP=1 -f $seed003 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Applying 003_tkg_rds_variant_commissioning.sql failed" }
} else {
    throw "Cannot apply 003_tkg_rds_variant_commissioning.sql: Commissioning registry is incomplete (profiles=$profileCount/25, maps=$mapCount/25, codes=$codeCount/25, assignments=$assignmentCount/25, source_backed=$sourceBackedCount/24, plans=$planCount/1775, bindings=$bindingCount/208, sealer_bindings=$sealerBindings/0) and $liveCollisions live WorkSite collision(s) exist in integration.rds_worksites."
}

Write-Host 'Applying replay-safe production task templates and internal finished goods locations (005, 004)...' -ForegroundColor Cyan
foreach ($sf in @(
    (Join-Path $projectRoot 'db\seeds\generated\005_tkg_variant_task_templates.sql'),
    (Join-Path $projectRoot 'db\seeds\004_finished_goods_internal_locations.sql')
)) {
    & $psql @commonPsql -d $Database -v ON_ERROR_STOP=1 -f $sf | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Applying seed failed: $sf" }
}

if ($EnableSimulationTrialMappings) {
    Write-Host 'Applying simulation RDS trial mappings (002d - OPTIMAK_SIM_2026 overlay)...' -ForegroundColor Yellow
    $simSeed = (Join-Path $projectRoot 'db\seeds\generated\002d_simulation_rds_trial_mappings.sql')
    & $psql @commonPsql -d $Database -v ON_ERROR_STOP=1 -f $simSeed | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Applying simulation trial seed failed: $simSeed" }
} else {
    Write-Host 'Simulation trial mappings bypassed (production isolation active).' -ForegroundColor Gray
}

if ($ResetSimulationState) {
    Write-Host 'Resetting simulation slot runtime states (-ResetSimulationState active)...' -ForegroundColor Yellow
    $resetSimSql = @"
UPDATE ops.slot_runtime_state
SET occupancy_state = 'OCCUPIED', load_state = 'EMPTY', current_part_id = NULL, current_container_type_id = NULL, row_version = row_version + 1, updated_at = now()
WHERE layout_slot_id IN ('c2000000-0000-0000-0000-000000000001', 'c2000000-0000-0000-0000-000000000003');

UPDATE ops.slot_runtime_state
SET occupancy_state = 'VACANT', load_state = 'UNKNOWN', current_part_id = NULL, current_container_type_id = NULL, row_version = row_version + 1, updated_at = now()
WHERE layout_slot_id IN ('c2000000-0000-0000-0000-000000000002', 'c2000000-0000-0000-0000-000000000004');

UPDATE ops.slot_runtime_state
SET occupancy_state = 'VACANT', load_state = 'UNKNOWN', current_part_id = NULL, current_container_type_id = NULL, row_version = row_version + 1, updated_at = now()
WHERE layout_slot_id IN (
    SELECT ls.id FROM master.layout_slots ls
    JOIN master.layouts l ON l.id = ls.layout_id
    JOIN master.machines m ON m.id = l.machine_id
    WHERE m.code = 'SH-01-A' AND ls.slot_role NOT IN ('EMPTY_STORAGE', 'DROP')
);
"@
    $resetSimSql | & $psql @commonPsql -d $Database -v ON_ERROR_STOP=1 | Out-Null
}

# Persistent startup never owns historical test-prefixed operational records.
# Test harnesses retire their exact owned IDs in their isolated database.

# Resolve development password
$plainPassword = $null
if (-not [string]::IsNullOrWhiteSpace($Password)) {
    $plainPassword = $Password
} elseif ($null -ne $SecurePassword) {
    $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecurePassword)
    $plainPassword = [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
} else {
    # Check if dev users already exist
    $userCount = Invoke-DevPsqlScalar $Database "SELECT count(*) FROM auth.users WHERE username IN ('dev-admin','dev-supervisor','dev-line','dev-warehouse')"
    if ([string]::IsNullOrWhiteSpace($userCount) -or [int]$userCount -lt 4) {
        $sec = Read-Host -Prompt 'Enter local development password for dev accounts' -AsSecureString
        $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec)
        $plainPassword = [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
        [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }
}

# Seed development users, devices, machine runtime, and slot states
if (-not [string]::IsNullOrWhiteSpace($plainPassword)) {
    $passwordHash = New-Pbkdf2Hash51 $plainPassword
    $passwordHashSql = "'" + $passwordHash.Replace("'", "''") + "'"
    $plainPassword = $null
    $passwordHash = $null

    $devBootstrapSql = @"
INSERT INTO auth.users (username, password_hash, full_name, employee_no, is_active)
VALUES
  ('dev-admin', __PASSWORD_HASH__, 'Development Administrator', 'DEV-ADM', true),
  ('dev-supervisor', __PASSWORD_HASH__, 'Development Supervisor', 'DEV-SUP', true),
  ('dev-line', __PASSWORD_HASH__, 'Development Line Operator', 'DEV-LINE', true),
  ('dev-warehouse', __PASSWORD_HASH__, 'Development Warehouse Operator', 'DEV-WH', true)
ON CONFLICT (username) DO UPDATE
SET password_hash = EXCLUDED.password_hash,
    full_name = EXCLUDED.full_name,
    employee_no = EXCLUDED.employee_no,
    is_active = true;

INSERT INTO auth.user_roles (user_id, role_id)
SELECT u.id, r.id FROM auth.users u CROSS JOIN auth.roles r
WHERE u.username = 'dev-admin' AND r.code = 'ADMIN'
ON CONFLICT DO NOTHING;

INSERT INTO auth.user_roles (user_id, role_id)
SELECT u.id, r.id FROM auth.users u CROSS JOIN auth.roles r
WHERE u.username = 'dev-supervisor' AND r.code = 'SUPERVISOR'
ON CONFLICT DO NOTHING;

INSERT INTO auth.user_roles (user_id, role_id)
SELECT u.id, r.id FROM auth.users u CROSS JOIN auth.roles r
WHERE u.username = 'dev-line' AND r.code = 'LINE_OPERATOR'
ON CONFLICT DO NOTHING;

INSERT INTO auth.user_roles (user_id, role_id)
SELECT u.id, r.id FROM auth.users u CROSS JOIN auth.roles r
WHERE u.username = 'dev-warehouse' AND r.code = 'WAREHOUSE_OPERATOR'
ON CONFLICT DO NOTHING;

"@
    $devBootstrapSql = $devBootstrapSql.Replace('__PASSWORD_HASH__', $passwordHashSql)
    $devBootstrapSql | & $psql @commonPsql -d $Database -v ON_ERROR_STOP=1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Dev user provisioning failed with exit code $LASTEXITCODE" }
}

# Seed devices, permissions, navigation profile, runtime state, and test slot states
$devEnvironmentSql = @'
-- Local development client devices
INSERT INTO audit.client_devices (device_code, name, device_type, is_active)
VALUES
  ('DEV-SUPERVISOR', 'Development Supervisor Terminal', 'PDA', true),
  ('DEV-LINE', 'Development Line Terminal', 'PDA', true),
  ('DEV-WAREHOUSE', 'Development Warehouse Terminal', 'PDA', true)
ON CONFLICT (device_code) DO UPDATE
SET name = EXCLUDED.name, is_active = true;

-- Permissions for all active machines
INSERT INTO auth.user_machine_permissions (user_id, machine_id, can_view, can_operate, can_change_variant, can_manage_slots)
SELECT u.id, m.id, true, true, true, false
FROM auth.users u CROSS JOIN master.machines m
WHERE u.username = 'dev-supervisor' AND m.is_active
ON CONFLICT (user_id, machine_id) DO UPDATE
SET can_view = true, can_operate = true, can_change_variant = true, can_manage_slots = false;

INSERT INTO auth.user_machine_permissions (user_id, machine_id, can_view, can_operate, can_change_variant, can_manage_slots)
SELECT u.id, m.id, true, true, false, false
FROM auth.users u CROSS JOIN master.machines m
WHERE u.username = 'dev-line' AND m.is_active
ON CONFLICT (user_id, machine_id) DO UPDATE
SET can_view = true, can_operate = true, can_change_variant = false, can_manage_slots = false;

INSERT INTO auth.user_machine_permissions (user_id, machine_id, can_view, can_operate, can_change_variant, can_manage_slots)
SELECT u.id, m.id, true, true, false, false
FROM auth.users u CROSS JOIN master.machines m
WHERE u.username = 'dev-warehouse' AND m.is_active
ON CONFLICT (user_id, machine_id) DO UPDATE
SET can_view = true, can_operate = true, can_change_variant = false, can_manage_slots = false;
'@

if (-not $EnableSimulationTrialMappings) {
    $devEnvironmentSql += @'

-- Development Navigation Profile for SH-01-A (Used when simulation trial overlay is disabled)
INSERT INTO master.navigation_profiles (
    machine_id, code, name, version, status,
    base_map_name, robot_map_name, route_profile_name, stitch_profile_name,
    config_json
)
SELECT
    m.id, 'DEV-RDS-SIM', 'Development RDS Simulator', 1, 'PUBLISHED',
    NULL, NULL, NULL, NULL,
    '{"developmentOnly": true}'::jsonb
FROM master.machines m WHERE m.code = 'SH-01-A'
ON CONFLICT (machine_id, code, version) DO UPDATE
SET status = 'PUBLISHED', config_json = '{"developmentOnly": true}'::jsonb;

-- Clear any existing default assignment for SH-01-A variant 1
UPDATE master.variant_navigation_profile_assignments a
SET is_default = false
FROM master.machines m, master.variants v
WHERE m.code = 'SH-01-A' AND v.machine_id = m.id AND v.code = '1'
  AND a.variant_id = v.id;

-- Assign navigation profile as default for SH-01-A variant 1
INSERT INTO master.variant_navigation_profile_assignments (
    variant_id, machine_id, navigation_profile_id, is_default
)
SELECT
    v.id, m.id, np.id, true
FROM master.machines m
JOIN master.variants v ON v.machine_id = m.id AND v.code = '1'
JOIN master.navigation_profiles np ON np.machine_id = m.id AND np.code = 'DEV-RDS-SIM' AND np.version = 1
WHERE m.code = 'SH-01-A'
ON CONFLICT (variant_id, navigation_profile_id) DO UPDATE SET is_default = true;

-- Machine runtime state for SH-01-A (preserve existing valid selection)
INSERT INTO ops.machine_runtime_state (
    machine_id, active_variant_id, active_layout_id, active_navigation_id
)
SELECT
    m.id, v.id, a.layout_id, np.id
FROM master.machines m
JOIN master.variants v ON v.machine_id = m.id AND v.code = '1'
JOIN master.variant_layout_assignments a ON a.variant_id = v.id AND a.machine_id = m.id AND a.is_default
JOIN master.layouts l ON l.id = a.layout_id AND l.status = 'PUBLISHED'
JOIN master.navigation_profiles np ON np.machine_id = m.id AND np.code = 'DEV-RDS-SIM' AND np.version = 1
WHERE m.code = 'SH-01-A'
ON CONFLICT (machine_id) DO UPDATE
SET active_variant_id = EXCLUDED.active_variant_id,
    active_layout_id = EXCLUDED.active_layout_id,
    active_navigation_id = EXCLUDED.active_navigation_id,
    row_version = ops.machine_runtime_state.row_version + 1,
    updated_at = now()
WHERE ops.machine_runtime_state.active_variant_id IS NULL
   OR ops.machine_runtime_state.active_layout_id IS NULL;
'@
}

$devEnvironmentSql += @'

-- Ensure baseline runtime state exists for every active customer machine with published layout.
-- Missing rows are inserted only; existing valid selections are NEVER overwritten.
WITH candidate_variants AS (
    SELECT
        m.id AS machine_id,
        v.id AS variant_id,
        vla.layout_id,
        ROW_NUMBER() OVER (
            PARTITION BY m.id
            ORDER BY
                (CASE WHEN vla.is_default THEN 0 ELSE 1 END),
                v.display_order ASC,
                v.code ASC,
                l.version DESC,
                l.code ASC
        ) as rn
    FROM master.machines m
    JOIN master.variants v ON v.machine_id = m.id AND v.is_active
    JOIN master.variant_layout_assignments vla ON vla.variant_id = v.id AND vla.machine_id = m.id
    JOIN master.layouts l ON l.id = vla.layout_id AND l.machine_id = m.id AND l.status = 'PUBLISHED'
    WHERE m.is_active
),
selected_machine_defaults AS (
    SELECT cv.machine_id, cv.variant_id, cv.layout_id
    FROM candidate_variants cv
    WHERE cv.rn = 1
),
nav_candidates AS (
    SELECT
        smd.machine_id,
        np.id AS navigation_id,
        ROW_NUMBER() OVER (
            PARTITION BY smd.machine_id
            ORDER BY (CASE WHEN vnpa.is_default THEN 0 ELSE 1 END), np.version DESC
        ) AS nav_rn,
        COUNT(*) OVER (
            PARTITION BY smd.machine_id
        ) AS nav_count
    FROM selected_machine_defaults smd
    JOIN master.variant_navigation_profile_assignments vnpa
      ON vnpa.variant_id = smd.variant_id AND vnpa.machine_id = smd.machine_id
    JOIN master.navigation_profiles np
      ON np.id = vnpa.navigation_profile_id AND np.machine_id = smd.machine_id AND np.status = 'PUBLISHED'
)
INSERT INTO ops.machine_runtime_state (
    machine_id,
    active_variant_id,
    active_layout_id,
    active_navigation_id,
    row_version,
    updated_at
)
SELECT
    smd.machine_id,
    smd.variant_id,
    smd.layout_id,
    CASE WHEN nc.nav_count = 1 THEN nc.navigation_id ELSE NULL END,
    0,
    now()
FROM selected_machine_defaults smd
LEFT JOIN nav_candidates nc ON nc.machine_id = smd.machine_id AND nc.nav_rn = 1
ON CONFLICT (machine_id) DO UPDATE
SET active_variant_id = EXCLUDED.active_variant_id,
    active_layout_id = EXCLUDED.active_layout_id,
    active_navigation_id = EXCLUDED.active_navigation_id,
    updated_at = now()
WHERE ops.machine_runtime_state.active_variant_id IS NULL
   OR ops.machine_runtime_state.active_layout_id IS NULL;

-- Ensure all enabled layout slots for active machines have an initial slot runtime state row if missing
INSERT INTO ops.slot_runtime_state (layout_slot_id, occupancy_state, load_state)
SELECT ls.id, 'VACANT', 'UNKNOWN'
FROM master.layout_slots ls
JOIN master.layouts l ON l.id = ls.layout_id AND l.status = 'PUBLISHED'
JOIN master.machines m ON m.id = l.machine_id AND m.is_active
WHERE ls.is_enabled
ON CONFLICT (layout_slot_id) DO NOTHING;

-- Development default priorities for production end cleanup (REMOVE_FULL=40, REMOVE_EMPTY=70)
INSERT INTO master.production_end_priorities (operation_code, priority)
VALUES ('REMOVE_FULL', 40), ('REMOVE_EMPTY', 70)
ON CONFLICT (operation_code) DO NOTHING;
'@
$devEnvironmentSql | & $psql @commonPsql -d $Database -v ON_ERROR_STOP=1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Dev environment seeding failed with exit code $LASTEXITCODE" }

# Local startup preflight validation
$preflightSql = @"
SELECT
    (SELECT count(*) FROM master.machines WHERE is_active)::text
    || '|' || (SELECT count(*) FROM ops.machine_runtime_state rs JOIN master.machines m ON m.id = rs.machine_id WHERE m.is_active AND rs.active_variant_id IS NOT NULL AND rs.active_layout_id IS NOT NULL)::text
    || '|' || (SELECT count(*) FROM master.navigation_profiles WHERE config_json ->> 'commissioningRegistry' = 'tkg_rds_variant_map_naming_v1')::text
    || '|' || (SELECT count(DISTINCT v.id) FROM master.variants v JOIN master.variant_navigation_profile_assignments a ON a.variant_id = v.id JOIN master.navigation_profiles n ON n.id = a.navigation_profile_id WHERE n.config_json ->> 'commissioningRegistry' = 'tkg_rds_variant_map_naming_v1' AND n.config_json ->> 'layoutBindingState' = 'SOURCE_BACKED')::text
    || '|' || (SELECT count(*) FROM master.task_templates WHERE config_json ->> 'templateRegistry' = 'tkg_variant_task_templates_v1')::text
    || '|' || (SELECT count(*) FROM ops.store_locations WHERE code ~ '^FG-(0[1-9]|1[0-9])$' AND is_enabled)::text
    || '|' || (SELECT count(*) FROM integration.rds_worksites)::text
"@

$pfRaw = Invoke-DevPsqlScalar $Database $preflightSql
$pfParts = if ($pfRaw) { $pfRaw.Trim().Split('|') } else { @() }
$pfActiveMachines   = if ($pfParts.Length -ge 1) { $pfParts[0] } else { '0' }
$pfConfiguredRows   = if ($pfParts.Length -ge 2) { $pfParts[1] } else { '0' }
$pfRegistryProfiles = if ($pfParts.Length -ge 3) { $pfParts[2] } else { '0' }
$pfSourceVariants   = if ($pfParts.Length -ge 4) { $pfParts[3] } else { '0' }
$pfGenericTemplates = if ($pfParts.Length -ge 5) { $pfParts[4] } else { '0' }
$pfFgLocations      = if ($pfParts.Length -ge 6) { $pfParts[5] } else { '0' }
$pfLiveWorksites    = if ($pfParts.Length -ge 7) { $pfParts[6] } else { '0' }

Write-Host ""
Write-Host "================ Preflight Validation ================" -ForegroundColor Cyan
Write-Host ("  Active Machines:          {0,-4} (baseline: 9)" -f $pfActiveMachines)
Write-Host ("  Configured Machine Rows:  {0,-4} (baseline: 9)" -f $pfConfiguredRows)
Write-Host ("  Registry NAV Profiles:    {0,-4} (baseline: 25)" -f $pfRegistryProfiles)
Write-Host ("  Source-backed Variants:   {0,-4} (baseline: 24)" -f $pfSourceVariants)
Write-Host ("  Generic Task Templates:   {0,-4} (baseline: 96)" -f $pfGenericTemplates)
Write-Host ("  FG Internal Locations:    {0,-4} (baseline: 19)" -f $pfFgLocations)
Write-Host ("  Live RDS WorkSites:       {0,-4} (persistent state)" -f $pfLiveWorksites)
Write-Host "======================================================" -ForegroundColor Cyan
Write-Host ""

# 2. Check if previous stack processes are running; stop them safely before building
# 2a. Check .runtime/api.pid
if (Test-Path -LiteralPath $apiPidFile) {
    try {
        $storedPidText = (Get-Content -LiteralPath $apiPidFile -Raw -ErrorAction SilentlyContinue).Trim()
        $storedPid = 0
        if ([int]::TryParse($storedPidText, [ref]$storedPid) -and $storedPid -gt 0) {
            $cim = Get-ProcessCim $storedPid
            if ($cim) {
                if (Test-ProcessOwnedByRepo -ProcessId $storedPid -RepoRoot $projectRoot) {
                    Stop-OwnedProcessSafely -ProcessId $storedPid -RepoRoot $projectRoot -Description "API (.runtime/api.pid)" | Out-Null
                } else {
                    Write-Warning "Stored API PID $storedPid belongs to a foreign process ($($cim.Name)). Leaving untouched."
                }
            }
        }
    } catch {
        # ignore parse error
    } finally {
        Remove-Item -LiteralPath $apiPidFile -Force -ErrorAction SilentlyContinue
    }
}

# 2b. Check .runtime/admin.pid and .runtime/operator.pid
$adminPidFile = Join-Path $runtimeDir 'admin.pid'
if (Test-Path -LiteralPath $adminPidFile) {
    try {
        $storedPid = [int](Get-Content -LiteralPath $adminPidFile -Raw -ErrorAction SilentlyContinue).Trim()
        if ($storedPid -gt 0) {
            Stop-OwnedProcessSafely -ProcessId $storedPid -RepoRoot $projectRoot -Description "Admin Web (.runtime/admin.pid)" | Out-Null
        }
    } catch { } finally {
        Remove-Item -LiteralPath $adminPidFile -Force -ErrorAction SilentlyContinue
    }
}

$operatorPidFile = Join-Path $runtimeDir 'operator.pid'
if (Test-Path -LiteralPath $operatorPidFile) {
    try {
        $storedPid = [int](Get-Content -LiteralPath $operatorPidFile -Raw -ErrorAction SilentlyContinue).Trim()
        if ($storedPid -gt 0) {
            Stop-OwnedProcessSafely -ProcessId $storedPid -RepoRoot $projectRoot -Description "Operator Web (.runtime/operator.pid)" | Out-Null
        }
    } catch { } finally {
        Remove-Item -LiteralPath $operatorPidFile -Force -ErrorAction SilentlyContinue
    }
}

# 2c. Check stack_state.json for previous processes
if (Test-Path -LiteralPath $stateFile) {
    try {
        $oldState = Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json
        if ($oldState.apiPid) {
            Stop-OwnedProcessSafely -ProcessId $oldState.apiPid -RepoRoot $projectRoot -Description "API (state)" | Out-Null
        }
        if ($oldState.adminPid) {
            Stop-OwnedProcessSafely -ProcessId $oldState.adminPid -RepoRoot $projectRoot -Description "Admin Web (state)" | Out-Null
        }
        if ($oldState.operatorPid) {
            Stop-OwnedProcessSafely -ProcessId $oldState.operatorPid -RepoRoot $projectRoot -Description "Operator Web (state)" | Out-Null
        }
    } catch {
        # ignore state read error
    }
}

# 2d. Port ownership verification and fallback: fail closed if a foreign process occupies the port
Reclaim-PortIfOwned -Port $ApiPort -RepoRoot $projectRoot -ServiceName "API"
Reclaim-PortIfOwned -Port $AdminPort -RepoRoot $projectRoot -ServiceName "ADMIN"
Reclaim-PortIfOwned -Port $OperatorPort -RepoRoot $projectRoot -ServiceName "OPERATOR"

# 3. Build .NET Solution in Debug mode
Write-Host 'Building .NET solution (Debug)...' -ForegroundColor Cyan
& dotnet build (Join-Path $projectRoot 'TKG.Pda.slnx') --configuration Debug --no-restore
if ($LASTEXITCODE -ne 0) {
    & dotnet build (Join-Path $projectRoot 'TKG.Pda.slnx') --configuration Debug
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }
}

# 4. Generate JWT signing key
$jwtRng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$jwtBytes = New-Object byte[] 48
$jwtRng.GetBytes($jwtBytes)
$jwtRng.Dispose()
$jwtSigningKey = [Convert]::ToBase64String($jwtBytes)

# Detect active local network IPv4 addresses for LAN access and CORS
$detectedIps = @('127.0.0.1', 'localhost')
try {
    $upAdapters = (Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object { $_.Status -eq 'Up' }).Name
    $netIps = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.InterfaceAlias -in $upAdapters -and $_.IPAddress -notlike "169.254.*" } |
        Select-Object -ExpandProperty IPAddress
    if ($netIps) {
        $detectedIps += $netIps
    }
} catch { }
$detectedIps = $detectedIps | Select-Object -Unique

# 5. Start API
Write-Host "Starting ASP.NET Core API on http://${HostAddress}:$ApiPort..." -ForegroundColor Cyan
$apiStdOut = Join-Path $logsDir 'api.stdout.log'
$apiStdErr = Join-Path $logsDir 'api.stderr.log'

$env:ConnectionStrings__TkgPda = "Host=127.0.0.1;Port=$PostgresPort;Database=$Database;Username=postgres;Pooling=true;Maximum Pool Size=50"
$env:ASPNETCORE_URLS = "http://${HostAddress}:$ApiPort"
$env:ASPNETCORE_ENVIRONMENT = 'Development'

# Configure CORS origins for localhost and all detected LAN IPs
$corsOrigins = @(
    "http://127.0.0.1:$OperatorPort",
    "http://localhost:$OperatorPort",
    "http://127.0.0.1:$AdminPort",
    "http://localhost:$AdminPort"
)
foreach ($ip in $detectedIps) {
    $corsOrigins += "http://${ip}:$OperatorPort"
    $corsOrigins += "http://${ip}:$AdminPort"
}
$corsOrigins = $corsOrigins | Select-Object -Unique
# Clear previous Cors env vars if any
Get-ChildItem env:Cors__AllowedOrigins__* -ErrorAction SilentlyContinue | ForEach-Object { Remove-Item "env:$($_.Name)" }
for ($ci = 0; $ci -lt $corsOrigins.Count; $ci++) {
    [Environment]::SetEnvironmentVariable("Cors__AllowedOrigins__$ci", $corsOrigins[$ci], 'Process')
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:Authentication__Jwt__SigningKey = $jwtSigningKey

if ($RdsMode -eq 'Commissioning') {
    $effectiveIntegrationKey = if (-not [string]::IsNullOrWhiteSpace($IntegrationKey)) { $IntegrationKey } elseif (-not [string]::IsNullOrWhiteSpace($env:Rds__IntegrationKey)) { $env:Rds__IntegrationKey } else { 'ChangeMeLocalDevRdsIntegrationKey32Chars!' }
    $effectiveCallbackKey = if (-not [string]::IsNullOrWhiteSpace($CallbackKey)) { $CallbackKey } elseif (-not [string]::IsNullOrWhiteSpace($env:Rds__CallbackKey)) { $env:Rds__CallbackKey } else { 'ChangeMeLocalDevRdsCallbackKey32Chars!' }
    if ($effectiveIntegrationKey.Length -lt 32 -or $effectiveIntegrationKey.Length -gt 512) {
        throw "Commissioning RDS mode requires IntegrationKey (32-512 characters). Pass -IntegrationKey or set env:Rds__IntegrationKey."
    }
    if ($effectiveCallbackKey.Length -lt 32 -or $effectiveCallbackKey.Length -gt 512) {
        throw "Commissioning RDS mode requires CallbackKey (32-512 characters). Pass -CallbackKey or set env:Rds__CallbackKey."
    }
    $env:Rds__Enabled = 'true'
    $env:Rds__DispatchEnabled = 'true'
    $env:Rds__BaseUrl = $RdsBaseUrl
    $env:Rds__IntegrationKey = $effectiveIntegrationKey
    $env:Rds__CallbackKey = $effectiveCallbackKey
    $env:Rds__CallbackUrl = if (-not [string]::IsNullOrWhiteSpace($CallbackUrl)) { $CallbackUrl } elseif (-not [string]::IsNullOrWhiteSpace($env:Rds__CallbackUrl)) { $env:Rds__CallbackUrl } else { "http://127.0.0.1:$ApiPort/api/integrations/rds/events" }
} elseif ($RdsMode -eq 'ReadOnly') {
    $env:Rds__Enabled = 'true'
    $env:Rds__DispatchEnabled = 'false'
    $env:Rds__BaseUrl = $RdsBaseUrl
} else {
    $env:Rds__Enabled = 'false'
    $env:Rds__DispatchEnabled = 'false'
    $env:Rds__BaseUrl = ''
}

$apiProcess = Start-Process -FilePath 'dotnet' -ArgumentList @(
    'run', '--project', (Join-Path $projectRoot 'src\TkgPda.Api\TkgPda.Api.csproj'),
    '--configuration', 'Debug', '--no-build', '--no-launch-profile'
) -PassThru -WindowStyle Hidden -RedirectStandardOutput $apiStdOut -RedirectStandardError $apiStdErr

# Record API process PID
$apiProcess.Id | Set-Content -LiteralPath $apiPidFile -Encoding UTF8

# Wait for API health/ready
$apiReady = $false
for ($i = 0; $i -lt 40; $i++) {
    if ($apiProcess.HasExited) {
        $errText = if (Test-Path -LiteralPath $apiStdErr) { Get-Content -LiteralPath $apiStdErr -Tail 20 | Out-String } else { '' }
        throw "API process exited unexpectedly with code $($apiProcess.ExitCode). $errText"
    }
    try {
        $readyResp = Invoke-RestMethod -Method Get -Uri "http://127.0.0.1:$ApiPort/health/ready" -TimeoutSec 2
        if ($readyResp.status -eq 'ready') {
            $apiReady = $true
            break
        }
    } catch {
        Start-Sleep -Milliseconds 500
    }
}
if (-not $apiReady) { throw "API did not become ready on port $ApiPort." }
Write-Host "API is ready on http://127.0.0.1:$ApiPort." -ForegroundColor Green

# 6. Start Admin Web
$adminProcess = $null
if (-not $NoAdmin) {
    $adminDir = Join-Path $projectRoot 'src\TkgPda.AdminWeb'
    if (-not (Test-Path -LiteralPath (Join-Path $adminDir 'node_modules'))) {
        Write-Host 'Installing Admin Web node_modules...' -ForegroundColor Cyan
        & cmd.exe /c "cd /d `"$adminDir`" && npm ci"
        if ($LASTEXITCODE -ne 0) { throw "npm ci failed in AdminWeb with exit code $LASTEXITCODE" }
    }

    Write-Host "Starting Admin Web on http://${HostAddress}:$AdminPort..." -ForegroundColor Cyan
    $adminStdOut = Join-Path $logsDir 'admin_web.stdout.log'
    $adminStdErr = Join-Path $logsDir 'admin_web.stderr.log'
    $env:TKG_API_TARGET = "http://127.0.0.1:$ApiPort"

    $adminProcess = Start-Process -FilePath 'cmd.exe' -ArgumentList @(
        '/c', "npm --prefix `"$adminDir`" run dev"
    ) -WorkingDirectory $adminDir -PassThru -WindowStyle Hidden -RedirectStandardOutput $adminStdOut -RedirectStandardError $adminStdErr
    $adminProcess.Id | Set-Content -LiteralPath $adminPidFile -Encoding UTF8
}

# 7. Start Flutter Operator Web
$operatorProcess = $null
if (-not $NoOperator) {
    $pdaDir = Join-Path $projectRoot 'src\TkgPda.Pda'
    Write-Host "Starting Flutter Operator Web on http://${HostAddress}:$OperatorPort..." -ForegroundColor Cyan
    $operatorStdOut = Join-Path $logsDir 'operator_web.stdout.log'
    $operatorStdErr = Join-Path $logsDir 'operator_web.stderr.log'

    # Run in headless web-server mode so it remains permanently available without DWDS Chrome debug timeouts
    $operatorProcess = Start-Process -FilePath 'cmd.exe' -ArgumentList @(
        '/c', "flutter run -d web-server --web-hostname=$HostAddress --web-port=$OperatorPort -t `"$pdaDir\lib\main.dart`""
    ) -WorkingDirectory $pdaDir -PassThru -WindowStyle Hidden -RedirectStandardOutput $operatorStdOut -RedirectStandardError $operatorStdErr
    $operatorProcess.Id | Set-Content -LiteralPath $operatorPidFile -Encoding UTF8
}

# Check RDS reachability
$rdsStatusDisplay = 'OFF'
if ($RdsMode -in @('ReadOnly', 'Commissioning')) {
    try {
        $rdsCheck = Invoke-RestMethod -Method Get -Uri $RdsBaseUrl -TimeoutSec 3
        $prefix = if ($RdsMode -eq 'Commissioning') { "COMMISSIONING (DISPATCH ENABLED)" } else { "READ-ONLY" }
        $rdsStatusDisplay = "$prefix / $RdsBaseUrl"
    } catch {
        $prefix = if ($RdsMode -eq 'Commissioning') { "COMMISSIONING (UNAVAILABLE)" } else { "READ-ONLY (UNAVAILABLE)" }
        $rdsStatusDisplay = "$prefix / $RdsBaseUrl"
    }
}

# Record state
$state = [ordered]@{
    startedAt       = (Get-Date).ToString('o')
    postgresPort    = $PostgresPort
    database        = $Database
    pgDataDir       = $pgDataDir
    apiPort         = $ApiPort
    apiPid          = $apiProcess.Id
    adminPort       = $AdminPort
    adminPid        = if ($adminProcess) { $adminProcess.Id } else { $null }
    operatorPort    = $OperatorPort
    operatorPid     = if ($operatorProcess) { $operatorProcess.Id } else { $null }
    rdsMode         = $RdsMode
    rdsBaseUrl      = $RdsBaseUrl
}
$state | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $stateFile -Encoding UTF8

# Report per Section 19 and safety specifications
$pgConn = Get-NetTCPConnection -LocalPort $PostgresPort -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
$pgPid = if ($pgConn) { $pgConn.OwningProcess } else { "-" }

$apiConn = Get-NetTCPConnection -LocalPort $ApiPort -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
$apiActualPid = if ($apiConn) { $apiConn.OwningProcess } else { $apiProcess.Id }

$adminConn = Get-NetTCPConnection -LocalPort $AdminPort -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
$adminActualPid = if ($adminConn) { $adminConn.OwningProcess } else { if ($adminProcess) { $adminProcess.Id } else { "-" } }

$operatorConn = Get-NetTCPConnection -LocalPort $OperatorPort -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
$operatorActualPid = if ($operatorConn) { $operatorConn.OwningProcess } else { if ($operatorProcess) { $operatorProcess.Id } else { "-" } }

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "TKG LOCAL DEV READY" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host ("{0,-14} {1,-25} {2,-8} {3}" -f "SERVICE", "ENDPOINT", "PID", "STATUS")
Write-Host ("{0,-14} {1,-25} {2,-8} {3}" -f "PostgreSQL", "127.0.0.1:$PostgresPort", $pgPid, "READY")
Write-Host ("{0,-14} {1,-25} {2,-8} {3}" -f "API", "127.0.0.1:$ApiPort", $apiActualPid, "READY")
if (-not $NoAdmin) {
    Write-Host ("{0,-14} {1,-25} {2,-8} {3}" -f "Admin Web", "127.0.0.1:$AdminPort", $adminActualPid, "READY")
}
if (-not $NoOperator) {
    Write-Host ("{0,-14} {1,-25} {2,-8} {3}" -f "Operator Web", "127.0.0.1:$OperatorPort", $operatorActualPid, "READY")
}
Write-Host ("{0,-14} {1,-25} {2,-8} {3}" -f "RDSCore", "127.0.0.1:8088", "-", "READY")
Write-Host ""
Write-Host "Database: $Database"
Write-Host ""
Write-Host "Local URLs (This PC):" -ForegroundColor Cyan
Write-Host "  API:      http://127.0.0.1:$ApiPort"
if (-not $NoAdmin) {
    Write-Host "  Admin:    http://127.0.0.1:$AdminPort"
}
if (-not $NoOperator) {
    Write-Host "  Operator: http://127.0.0.1:$OperatorPort"
}

$lanIps = $detectedIps | Where-Object { $_ -ne '127.0.0.1' -and $_ -ne 'localhost' }
if ($lanIps) {
    Write-Host ""
    Write-Host "Network (LAN) URLs (Other Devices on Network):" -ForegroundColor Cyan
    foreach ($lanIp in $lanIps) {
        Write-Host "  [Interface IP: $lanIp]" -ForegroundColor Yellow
        Write-Host "    API:      http://${lanIp}:$ApiPort"
        if (-not $NoAdmin) {
            Write-Host "    Admin:    http://${lanIp}:$AdminPort"
        }
        if (-not $NoOperator) {
            Write-Host "    Operator: http://${lanIp}:$OperatorPort"
        }
    }
}
Write-Host ""
Write-Host "RDS:"
Write-Host $rdsStatusDisplay
Write-Host ""
Write-Host "Accounts:"
Write-Host "dev-admin"
Write-Host "dev-supervisor"
Write-Host "dev-line"
Write-Host "dev-warehouse"
Write-Host ""
Write-Host "Device Codes:"
Write-Host "DEV-SUPERVISOR"
Write-Host "DEV-LINE"
Write-Host "DEV-WAREHOUSE"
if ($RdsMode -eq 'Commissioning') {
    Write-Host "RDS DISPATCH ENABLED (COMMISSIONING)" -ForegroundColor Green
} else {
    Write-Host "RDS DISPATCH DISABLED" -ForegroundColor Yellow
}
Write-Host "========================================" -ForegroundColor Green

if ($Wait) {
    Write-Host ""
    Write-Host "Stack is running in foreground (Ctrl+C to terminate)..." -ForegroundColor Cyan
    while ($true) {
        Start-Sleep -Seconds 10
    }
}
