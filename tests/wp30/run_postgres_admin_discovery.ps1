param(
    [string]$PgBin = 'C:\Program Files\PostgreSQL\18\bin',
    [int]$Port = 55460
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$tempRoot = Join-Path $projectRoot '.codex-pg-wp30'
$dataDir = Join-Path $tempRoot 'data'
$database = 'tkg_pda_wp30'
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
    foreach ($file in @(
        'db\migrations\001_initial_schema.sql',
        'db\migrations\002_operator_employee_identity.sql',
        'db\migrations\003_transport_batch.sql',
        'db\migrations\004_transport_request_readiness.sql',
        'db\migrations\005_layout_slot_part_mapping.sql',
        'db\migrations\006_production_sessions.sql',
        'db\migrations\007_bring_full_task_type.sql',
        'db\migrations\008_warehouse_preparation_areas.sql',
        'db\migrations\009_preparation_placement_key.sql',
        'db\migrations\010_finished_goods_store.sql',
        'db\migrations\011_warehouse_operator_roles.sql',
        'db\migrations\012_client_device_machine_binding.sql',
        'db\migrations\013_user_sessions.sql',
        'db\migrations\014_transport_event_context.sql',
        'db\migrations\015_part_demand_dependencies.sql',
        'db\migrations\016_finished_goods_areas.sql',
        'db\migrations\017_internal_device_notifications.sql',
        'db\migrations\018_production_end_priorities.sql',
        'db\migrations\019_robot_alarm_observations.sql',
        'db\migrations\020_robot_alarm_observation_state.sql',
        'db\seeds\generated\002a_customer_textual_master_data.sql',
        'db\seeds\generated\002b_customer_physical_layouts.sql',
        'db\seeds\generated\002c_customer_layout_slot_part_assignments.sql'
    )) {
        Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot $file)))
    }

    $connectionString = "Host=127.0.0.1;Port=$Port;Database=$database;Username=postgres;Pooling=true;Maximum Pool Size=20"
    $env:TKG_PDA_API_TEST_CONNECTION_STRING = $connectionString
    $env:TKG_PDA_TEST_CONNECTION_STRING = $connectionString
    $env:ConnectionStrings__TkgPda = $connectionString
    $env:Authentication__Jwt__SigningKey = 'wp30-integration-signing-key-with-more-than-thirty-two-bytes'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    Invoke-Checked 'dotnet' @(
        'test', (Join-Path $projectRoot 'tests\TkgPda.Api.Tests\TkgPda.Api.Tests.csproj'),
        '--configuration', 'Release', '--no-build',
        '--filter', 'FullyQualifiedName~RdsAdminApiTests',
        '--logger', 'console;verbosity=minimal'
    )
    Invoke-Checked 'dotnet' @(
        'test', (Join-Path $projectRoot 'tests\TkgPda.Infrastructure.Postgres.Tests\TkgPda.Infrastructure.Postgres.Tests.csproj'),
        '--configuration', 'Release', '--no-build',
        '--filter', 'FullyQualifiedName~PostgresRdsCommissioningConfigurationStoreTests',
        '--logger', 'console;verbosity=minimal'
    )

    $verification = (& $psql @common -Atc @"
SELECT json_build_object(
  'discovery_audit_rows', (SELECT count(*) FROM audit.audit_logs WHERE action = 'RDS_COMMISSIONING_DISCOVERY_CAPTURED'),
  'result', 'PASS'
);
"@) | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL verification query failed' }
    if ($verification.discovery_audit_rows -ne 0) { throw 'Test cleanup left unexpected discovery audit rows' }
    $verification | ConvertTo-Json
}
finally {
    Remove-Item Env:TKG_PDA_API_TEST_CONNECTION_STRING -ErrorAction SilentlyContinue
    Remove-Item Env:TKG_PDA_TEST_CONNECTION_STRING -ErrorAction SilentlyContinue
    Remove-Item Env:ConnectionStrings__TkgPda -ErrorAction SilentlyContinue
    Remove-Item Env:Authentication__Jwt__SigningKey -ErrorAction SilentlyContinue
    if ($started -and (Test-Path -LiteralPath $dataDir)) {
        & (Join-Path $PgBin 'pg_ctl.exe') -D $dataDir -m fast -w stop | Out-Host
    }
    if (Test-Path -LiteralPath $tempRoot) {
        $resolvedTemp = (Resolve-Path -LiteralPath $tempRoot).Path
        if (-not $resolvedTemp.StartsWith($projectRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
            (Split-Path -Leaf $resolvedTemp) -ne '.codex-pg-wp30') {
            throw "Refusing unsafe cleanup target: $resolvedTemp"
        }
        Remove-Item -LiteralPath $resolvedTemp -Recurse -Force
    }
}
