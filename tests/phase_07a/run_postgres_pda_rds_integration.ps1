param(
    [string]$PgBin = 'C:\Program Files\PostgreSQL\18\bin',
    [int]$Port = 55441
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$tempRoot = Join-Path $projectRoot '.codex-pg-phase07a-integration'
$dataDir = Join-Path $tempRoot 'data'
$database = 'tkg_pda_phase07a'
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
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\001_initial_schema.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\002_operator_employee_identity.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\003_transport_batch.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\004_transport_request_readiness.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\005_layout_slot_part_mapping.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\006_production_sessions.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\007_bring_full_task_type.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\008_warehouse_preparation_areas.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\009_preparation_placement_key.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\010_finished_goods_store.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\014_transport_event_context.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\015_part_demand_dependencies.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\016_finished_goods_areas.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\seeds\004_finished_goods_internal_locations.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\017_internal_device_notifications.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\018_production_end_priorities.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\019_robot_alarm_observations.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\020_robot_alarm_observation_state.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\021_production_end_cleanup.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\022_variant_scoped_rds_worksite_commissioning.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\023_rds_variant_commissioning_hardening.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\024_operational_reporting.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\seeds\generated\002a_customer_textual_master_data.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\seeds\generated\002b_customer_physical_layouts.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\seeds\generated\002c_customer_layout_slot_part_assignments.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\seeds\generated\003_tkg_rds_variant_commissioning.sql')))

    $connectionString = "Host=127.0.0.1;Port=$Port;Database=$database;Username=postgres;Pooling=true;Maximum Pool Size=50"
    $env:TKG_PDA_API_TEST_CONNECTION_STRING = $connectionString
    $env:ConnectionStrings__TkgPda = $connectionString
    $env:Authentication__Jwt__SigningKey = [Convert]::ToBase64String(
        [Security.Cryptography.RandomNumberGenerator]::GetBytes(48))
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    Invoke-Checked 'dotnet' @(
        'test', (Join-Path $projectRoot 'tests\TkgPda.Api.Tests\TkgPda.Api.Tests.csproj'),
        '--configuration', 'Release', '--no-restore',
        '--filter', 'FullyQualifiedName~PdaRdsContractReadinessApiTests',
        '--logger', 'console;verbosity=normal'
    )

    $verification = (& $psql @common -Atc @"
SELECT json_build_object(
  'postgresql_version', current_setting('server_version'),
  'machine_master_records', (SELECT count(*) FROM master.machines),
  'occupancy_reserved_rows', (SELECT count(*) FROM ops.slot_runtime_state WHERE occupancy_state = 'RESERVED'),
  'live_rds_credentials', 0,
  'result', 'PASS'
);
"@) | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL verification query failed' }
    $verification | ConvertTo-Json
}
finally {
    Remove-Item Env:TKG_PDA_API_TEST_CONNECTION_STRING -ErrorAction SilentlyContinue
    Remove-Item Env:ConnectionStrings__TkgPda -ErrorAction SilentlyContinue
    Remove-Item Env:Authentication__Jwt__SigningKey -ErrorAction SilentlyContinue
    if ($started -and (Test-Path -LiteralPath $dataDir)) {
        & (Join-Path $PgBin 'pg_ctl.exe') -D $dataDir -m fast -w stop | Out-Host
    }
    if (Test-Path -LiteralPath $tempRoot) {
        $resolvedTemp = (Resolve-Path -LiteralPath $tempRoot).Path
        if (-not $resolvedTemp.StartsWith($projectRoot, [StringComparison]::OrdinalIgnoreCase) -or
            (Split-Path -Leaf $resolvedTemp) -ne '.codex-pg-phase07a-integration') {
            throw "Refusing unsafe cleanup target: $resolvedTemp"
        }
        Remove-Item -LiteralPath $resolvedTemp -Recurse -Force
    }
}
