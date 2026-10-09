<#
.SYNOPSIS
    Initializes or cleans up an isolated acceptance database for synthetic full-E2E tests.
.DESCRIPTION
    Creates or recreates a completely isolated database (default: tkg_pda_acceptance),
    applies all SQL migrations and seeds, and provisions test users/devices.
    Never operates against tkg_pda_dev or production databases.
.EXAMPLE
    pwsh -File tools/dev/acceptance/init_acceptance_db.ps1
.EXAMPLE
    pwsh -File tools/dev/acceptance/init_acceptance_db.ps1 -Drop
#>
[CmdletBinding()]
param(
    [string]$Database = 'tkg_pda_acceptance',
    [string]$HostName = '127.0.0.1',
    [int]$Port = 55450,
    [string]$User = 'postgres',
    [string]$PgBin = 'C:\Program Files\PostgreSQL\18\bin',
    [switch]$Drop
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path

# STRICT SAFETY GATE. This script drops and recreates its target.
if ($Database -ne 'tkg_pda_acceptance') {
    throw "Synthetic acceptance requires isolated tkg_pda_acceptance database."
}

$psql = Join-Path $PgBin 'psql.exe'
if (-not (Test-Path -LiteralPath $psql)) {
    $psql = (Get-Command psql.exe -ErrorAction SilentlyContinue)?.Source
}
if (-not $psql) {
    throw "PostgreSQL client (psql.exe) is required."
}

# 1. Drop database if requested
if ($Drop) {
    Write-Host "Dropping isolated acceptance database '$Database'..." -ForegroundColor Yellow
    & $psql -X -h $HostName -p $Port -U $User -d postgres -c "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$Database' AND pid <> pg_backend_pid();" | Out-Null
    & $psql -X -h $HostName -p $Port -U $User -d postgres -c "DROP DATABASE IF EXISTS `"$Database`" WITH (FORCE);" | Out-Null
    Write-Host "Acceptance database '$Database' dropped successfully." -ForegroundColor Green
    return
}

Write-Host "Initializing isolated acceptance database '$Database' on $HostName`:$Port..." -ForegroundColor Cyan

# 2. Recreate clean database
& $psql -X -h $HostName -p $Port -U $User -d postgres -c "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$Database' AND pid <> pg_backend_pid();" | Out-Null
& $psql -X -h $HostName -p $Port -U $User -d postgres -c "DROP DATABASE IF EXISTS `"$Database`" WITH (FORCE);" | Out-Null
& $psql -X -h $HostName -p $Port -U $User -d postgres -c "CREATE DATABASE `"$Database`";" | Out-Null

$commonPsql = @('-X', '-h', $HostName, '-p', "$Port", '-U', $User, '-d', $Database, '-v', 'ON_ERROR_STOP=1')
$actualDatabase = (& $psql @commonPsql -t -A -c "SELECT current_database();").Trim()
if ($actualDatabase -ne 'tkg_pda_acceptance') {
    throw "Synthetic acceptance requires isolated tkg_pda_acceptance database. current_database() returned '$actualDatabase'."
}

# 3. Apply all SQL migrations (001 to 026) in numerical order
$migrations = Get-ChildItem -Path (Join-Path $projectRoot 'db\migrations\*.sql') | Sort-Object Name
foreach ($m in $migrations) {
    & $psql @commonPsql -f $m.FullName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Migration $($m.Name) failed on $Database" }
}

# 4. Apply replay-safe customer master seeds
$seeds = @(
    'db\seeds\generated\002a_customer_textual_master_data.sql',
    'db\seeds\generated\002b_customer_physical_layouts.sql',
    'db\seeds\generated\002c_customer_layout_slot_part_assignments.sql',
    'db\seeds\generated\003_tkg_rds_variant_commissioning.sql',
    'db\seeds\generated\005_tkg_variant_task_templates.sql',
    'db\seeds\004_finished_goods_internal_locations.sql',
    'db\seeds\generated\002d_simulation_rds_trial_mappings.sql'
)
foreach ($s in $seeds) {
    $sf = Join-Path $projectRoot $s
    & $psql @commonPsql -f $sf | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Seed $s failed on $Database" }
}

# 5. Seed development users, roles, devices and default permissions.
# The hash is random and is not a known password. Acceptance logins use employee number.
$nonce = [Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(18))
$acceptanceHash = '$tkg-pbkdf2-sha512$v=1$i=210000$' + $nonce + '$unused'
$devBootstrapSql = @"
INSERT INTO auth.users (username, password_hash, full_name, employee_no, is_active)
VALUES
  ('dev-admin', '$acceptanceHash', 'Development Administrator', 'DEV-ADM', true),
  ('dev-supervisor', '$acceptanceHash', 'Development Supervisor', 'DEV-SUP', true),
  ('dev-line', '$acceptanceHash', 'Development Line Operator', 'DEV-LINE', true),
  ('dev-warehouse', '$acceptanceHash', 'Development Warehouse Operator', 'DEV-WH', true)
ON CONFLICT (username) DO UPDATE
SET full_name = EXCLUDED.full_name, employee_no = EXCLUDED.employee_no, is_active = true;

INSERT INTO auth.user_roles (user_id, role_id)
SELECT u.id, r.id FROM auth.users u CROSS JOIN auth.roles r
WHERE u.username = 'dev-admin' AND r.code = 'ADMIN' ON CONFLICT DO NOTHING;

INSERT INTO auth.user_roles (user_id, role_id)
SELECT u.id, r.id FROM auth.users u CROSS JOIN auth.roles r
WHERE u.username = 'dev-supervisor' AND r.code = 'SUPERVISOR' ON CONFLICT DO NOTHING;

INSERT INTO auth.user_roles (user_id, role_id)
SELECT u.id, r.id FROM auth.users u CROSS JOIN auth.roles r
WHERE u.username = 'dev-line' AND r.code = 'LINE_OPERATOR' ON CONFLICT DO NOTHING;

INSERT INTO auth.user_roles (user_id, role_id)
SELECT u.id, r.id FROM auth.users u CROSS JOIN auth.roles r
WHERE u.username = 'dev-warehouse' AND r.code = 'WAREHOUSE_OPERATOR' ON CONFLICT DO NOTHING;

INSERT INTO audit.client_devices (device_code, name, device_type, is_active)
VALUES
  ('DEV-SUPERVISOR', 'Development Supervisor Terminal', 'PDA', true),
  ('DEV-LINE', 'Development Line Terminal', 'PDA', true),
  ('DEV-WAREHOUSE', 'Development Warehouse Terminal', 'PDA', true)
ON CONFLICT (device_code) DO UPDATE SET name = EXCLUDED.name, is_active = true;

-- The synthetic line terminal obeys the same terminal -> machine contract.
UPDATE audit.client_devices SET machine_id = (SELECT id FROM master.machines WHERE code = 'SH-01-A')
WHERE device_code = 'DEV-LINE';

INSERT INTO auth.user_machine_permissions (user_id, machine_id, can_view, can_operate, can_change_variant, can_manage_slots)
SELECT u.id, m.id, true, true, true, false
FROM auth.users u CROSS JOIN master.machines m
WHERE u.username = 'dev-supervisor' AND m.is_active
ON CONFLICT (user_id, machine_id) DO UPDATE SET can_view = true, can_operate = true, can_change_variant = true, can_manage_slots = false;

INSERT INTO auth.user_machine_permissions (user_id, machine_id, can_view, can_operate, can_change_variant, can_manage_slots)
SELECT u.id, m.id, true, true, false, false
FROM auth.users u CROSS JOIN master.machines m
WHERE u.username = 'dev-line' AND m.is_active
ON CONFLICT (user_id, machine_id) DO UPDATE SET can_view = true, can_operate = true, can_change_variant = false, can_manage_slots = false;

INSERT INTO auth.user_machine_permissions (user_id, machine_id, can_view, can_operate, can_change_variant, can_manage_slots)
SELECT u.id, m.id, true, true, false, false
FROM auth.users u CROSS JOIN master.machines m
WHERE u.username = 'dev-warehouse' AND m.is_active
ON CONFLICT (user_id, machine_id) DO UPDATE SET can_view = true, can_operate = true, can_change_variant = false, can_manage_slots = false;

-- Production End Priorities
INSERT INTO master.production_end_priorities (operation_code, priority)
VALUES ('REMOVE_FULL', 40), ('REMOVE_EMPTY', 70)
ON CONFLICT (operation_code) DO UPDATE SET priority = EXCLUDED.priority;

-- Baseline machine runtime states
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
            ORDER BY
                (CASE WHEN vnpa.is_default THEN 0 ELSE 1 END),
                (CASE WHEN np.config_json ->> 'layoutBindingState' = 'SOURCE_BACKED' THEN 0 ELSE 1 END),
                np.version DESC,
                np.code ASC
        ) as rn
    FROM selected_machine_defaults smd
    JOIN master.variant_navigation_profile_assignments vnpa ON vnpa.variant_id = smd.variant_id AND vnpa.machine_id = smd.machine_id
    JOIN master.navigation_profiles np ON np.id = vnpa.navigation_profile_id AND np.status = 'PUBLISHED'
)
INSERT INTO ops.machine_runtime_state (machine_id, active_variant_id, active_layout_id, active_navigation_id)
SELECT smd.machine_id, smd.variant_id, smd.layout_id, nc.navigation_id
FROM selected_machine_defaults smd
LEFT JOIN nav_candidates nc ON nc.machine_id = smd.machine_id AND nc.rn = 1
ON CONFLICT (machine_id) DO UPDATE
SET active_variant_id = EXCLUDED.active_variant_id,
    active_layout_id = EXCLUDED.active_layout_id,
    active_navigation_id = EXCLUDED.active_navigation_id;

-- Initial slot runtime states
INSERT INTO ops.slot_runtime_state (layout_slot_id, occupancy_state, load_state, current_part_id, current_container_type_id)
SELECT ls.id, 'VACANT', 'UNKNOWN', NULL, NULL
FROM master.layout_slots ls
ON CONFLICT (layout_slot_id) DO NOTHING;
"@

$devBootstrapSql | & $psql @commonPsql | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Dev bootstrap failed on $Database" }

Write-Host "Acceptance DB initialized: tkg_pda_acceptance" -ForegroundColor Green
