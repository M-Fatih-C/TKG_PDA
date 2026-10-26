param(
    [string]$PgBin = 'C:\Program Files\PostgreSQL\18\bin',
    [int]$Port = 55437
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$tempRoot = Join-Path $projectRoot '.codex-pg-phase03-integration'
$dataDir = Join-Path $tempRoot 'data'
$database = 'tkg_pda_phase03'
$started = $false

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)][string]$Executable,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Executable failed with exit code $LASTEXITCODE"
    }
}

if (Test-Path -LiteralPath $tempRoot) {
    throw "Temporary integration path already exists: $tempRoot"
}
if (Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue) {
    throw "Integration-test port is already in use: $Port"
}

try {
    Invoke-Checked (Join-Path $PgBin 'initdb.exe') @(
        '-D', $dataDir, '-U', 'postgres', '-A', 'trust', '--no-locale', '-E', 'UTF8'
    )
    Invoke-Checked (Join-Path $PgBin 'pg_ctl.exe') @(
        '-D', $dataDir,
        '-l', (Join-Path $tempRoot 'postgres.log'),
        '-o', "-p $Port -h 127.0.0.1",
        '-w', 'start'
    )
    $started = $true

    Invoke-Checked (Join-Path $PgBin 'createdb.exe') @(
        '-h', '127.0.0.1', '-p', "$Port", '-U', 'postgres', $database
    )

    $psql = Join-Path $PgBin 'psql.exe'
    $common = @('-X', '-h', '127.0.0.1', '-p', "$Port", '-U', 'postgres', '-d', $database, '-v', 'ON_ERROR_STOP=1')
    Get-ChildItem -LiteralPath (Join-Path $projectRoot 'db\migrations') -Filter '*.sql' |
        Sort-Object Name |
        ForEach-Object { Invoke-Checked $psql ($common + @('-f', $_.FullName)) }
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\seeds\generated\002a_customer_textual_master_data.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\seeds\generated\002b_customer_physical_layouts.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\seeds\generated\002c_customer_layout_slot_part_assignments.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\seeds\004_finished_goods_internal_locations.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\seeds\005_finished_product_container_options.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\seeds\generated\003_tkg_rds_variant_commissioning.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\seeds\generated\005_tkg_variant_task_templates.sql')))

    # Direct store-layer tests deliberately bypass the warehouse preparation API.
    # Give those legacy low-level requests isolated physical empty-container
    # inventory; production flows create request-bound preparation pallets instead.
    Invoke-Checked $psql ($common + @('-c', @"
INSERT INTO ops.store_locations (code, display_name, capacity, is_enabled, area_purpose, is_operator_visible)
SELECT format('PHASE03-EMPTY-%s', lpad(n::text, 2, '0')),
       format('Phase 03 Empty Container %s', n), 1, true, 'LEGACY', false
FROM generate_series(1, 20) AS n
ON CONFLICT (code) DO UPDATE SET is_enabled = true;

INSERT INTO ops.store_units (store_location_id, load_state)
SELECT location.id, 'EMPTY'
FROM ops.store_locations location
WHERE location.code LIKE 'PHASE03-EMPTY-%'
  AND NOT EXISTS (
      SELECT 1 FROM ops.store_units unit
      WHERE unit.store_location_id = location.id AND unit.removed_at IS NULL);
"@))

    $env:TKG_PDA_TEST_CONNECTION_STRING = "Host=127.0.0.1;Port=$Port;Database=$database;Username=postgres;Pooling=true;Maximum Pool Size=30"
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    Invoke-Checked 'dotnet' @(
        'test', (Join-Path $projectRoot 'tests\TkgPda.Infrastructure.Postgres.Tests\TkgPda.Infrastructure.Postgres.Tests.csproj'),
        '--configuration', 'Release', '--no-restore', '--logger', 'console;verbosity=minimal'
    )

    $verification = (& $psql @common -Atc @"
SELECT json_build_object(
  'postgresql_version', current_setting('server_version'),
  'machine_master_records', (SELECT count(*) FROM master.machines),
  'machine_regions', (SELECT count(*) FROM master.layout_slots WHERE slot_role = 'MACHINE'),
  'rds_tasks', (SELECT count(*) FROM integration.rds_tasks),
  'rds_worksites', (SELECT count(*) FROM integration.rds_worksites),
  'result', 'PASS'
);
"@) | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL verification query failed' }
    $verification | ConvertTo-Json
}
finally {
    Remove-Item Env:TKG_PDA_TEST_CONNECTION_STRING -ErrorAction SilentlyContinue
    if ($started -and (Test-Path -LiteralPath $dataDir)) {
        & (Join-Path $PgBin 'pg_ctl.exe') -D $dataDir -m fast -w stop | Out-Host
    }
    if (Test-Path -LiteralPath $tempRoot) {
        $resolvedTemp = (Resolve-Path -LiteralPath $tempRoot).Path
        if (-not $resolvedTemp.StartsWith($projectRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
            (Split-Path -Leaf $resolvedTemp) -ne '.codex-pg-phase03-integration') {
            throw "Refusing unsafe cleanup target: $resolvedTemp"
        }
        Remove-Item -LiteralPath $resolvedTemp -Recurse -Force
    }
}
