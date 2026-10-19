param(
    [string]$PgBin = 'C:\Program Files\PostgreSQL\18\bin',
    [int]$Port = 55442
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$tempRoot = Join-Path $projectRoot '.codex-pg-m4-migration'
$dataDir = Join-Path $tempRoot 'data'
$database = 'tkg_pda_m4_migration'
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
    $migrations = Get-ChildItem -LiteralPath (Join-Path $projectRoot 'db\migrations') -Filter '*.sql' |
        Where-Object Name -LT '027_fleet_provider_compatibility.sql' |
        Sort-Object Name
    foreach ($migration in $migrations) {
        Invoke-Checked $psql ($common + @('-f', $migration.FullName))
    }

    Invoke-Checked $psql ($common + @('-f', (Join-Path $PSScriptRoot '027_fleet_provider_compatibility_pre_migration.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\027_fleet_provider_compatibility.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $PSScriptRoot '027_fleet_provider_compatibility_test.sql')))

    # Replay after both RDS and M4 records exist must be harmless.
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\027_fleet_provider_compatibility.sql')))
    $fleetCount = & $psql @common -Atc "SELECT count(*) FROM integration.fleet_orders"
    if ($LASTEXITCODE -ne 0 -or [int]$fleetCount -ne 2) {
        throw "Migration replay changed fleet order cardinality: $fleetCount"
    }

    # Rollback is expected to fail closed while M4-owned rows exist.
    & $psql @common -f (Join-Path $projectRoot 'db\rollback\027_fleet_provider_compatibility.sql') 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) {
        throw 'Rollback unexpectedly succeeded while M4 data existed'
    }

    Invoke-Checked $psql ($common + @('-c', @"
DELETE FROM integration.fleet_callback_inbox WHERE provider = 'M4';
DELETE FROM integration.fleet_orders WHERE provider = 'M4';
DELETE FROM ops.transport_requests WHERE execution_provider = 'M4';
DELETE FROM integration.external_endpoints WHERE provider = 'M4';
DELETE FROM master.navigation_profiles WHERE execution_provider = 'M4';
"@))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\rollback\027_fleet_provider_compatibility.sql')))

    $verification = & $psql @common -Atc @"
SELECT json_build_object(
  'rds_task_preserved', EXISTS (
      SELECT 1 FROM integration.rds_tasks
      WHERE transport_request_id = '02700000-0000-0000-0000-000000000003'
  ),
  'provider_column_removed', NOT EXISTS (
      SELECT 1 FROM information_schema.columns
      WHERE table_schema = 'ops'
        AND table_name = 'transport_requests'
        AND column_name = 'execution_provider'
  ),
  'fleet_table_removed', to_regclass('integration.fleet_orders') IS NULL,
  'result', 'PASS'
);
"@
    if ($LASTEXITCODE -ne 0) {
        throw 'Post-rollback verification failed'
    }
    $verification | ConvertFrom-Json | ConvertTo-Json
}
finally {
    if ($started -and (Test-Path -LiteralPath $dataDir)) {
        & (Join-Path $PgBin 'pg_ctl.exe') -D $dataDir -m fast -w stop | Out-Host
    }
    if (Test-Path -LiteralPath $tempRoot) {
        $resolvedTemp = (Resolve-Path -LiteralPath $tempRoot).Path
        if (-not $resolvedTemp.StartsWith($projectRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
            (Split-Path -Leaf $resolvedTemp) -ne '.codex-pg-m4-migration') {
            throw "Refusing unsafe cleanup target: $resolvedTemp"
        }
        Remove-Item -LiteralPath $resolvedTemp -Recurse -Force
    }
}
