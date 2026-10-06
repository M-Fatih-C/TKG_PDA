param(
    [string]$PgBin = 'C:\Program Files\PostgreSQL\18\bin',
    [int]$Port = 55440
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$tempRoot = Join-Path $projectRoot '.codex-pg-phase05-integration'
$dataDir = Join-Path $tempRoot 'data'
$database = 'tkg_pda_phase05'
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
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\seeds\generated\002a_customer_textual_master_data.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\seeds\generated\002b_customer_physical_layouts.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\005_layout_slot_part_mapping.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\006_production_sessions.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\007_bring_full_task_type.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\008_warehouse_preparation_areas.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\009_preparation_placement_key.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\010_finished_goods_store.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\014_transport_event_context.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\015_part_demand_dependencies.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\seeds\generated\002c_customer_layout_slot_part_assignments.sql')))

    $connectionString = "Host=127.0.0.1;Port=$Port;Database=$database;Username=postgres;Pooling=true;Maximum Pool Size=50"
    $env:TKG_PDA_API_TEST_CONNECTION_STRING = $connectionString
    $env:ConnectionStrings__TkgPda = $connectionString
    $env:Authentication__Jwt__SigningKey = 'phase-05-integration-signing-key-with-more-than-thirty-two-bytes'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    Invoke-Checked 'dotnet' @(
        'test', (Join-Path $projectRoot 'tests\TkgPda.Api.Tests\TkgPda.Api.Tests.csproj'),
        '--configuration', 'Release', '--no-restore', '--filter', 'FullyQualifiedName~AdminConfigurationApiTests',
        '--logger', 'console;verbosity=normal'
    )

    $verification = (& $psql @common -Atc @"
SELECT json_build_object(
  'postgresql_version', current_setting('server_version'),
  'roles', (SELECT json_agg(code ORDER BY code) FROM auth.roles),
  'machine_master_records', (SELECT count(*) FROM master.machines),
  'admin_audit_records', (SELECT count(*) FROM audit.audit_logs WHERE action LIKE 'ADMIN_%'),
  'rds_tasks', (SELECT count(*) FROM integration.rds_tasks),
  'rds_worksites', (SELECT count(*) FROM integration.rds_worksites),
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
        if (-not $resolvedTemp.StartsWith($projectRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
            (Split-Path -Leaf $resolvedTemp) -ne '.codex-pg-phase05-integration') {
            throw "Refusing unsafe cleanup target: $resolvedTemp"
        }
        Remove-Item -LiteralPath $resolvedTemp -Recurse -Force
    }
}
