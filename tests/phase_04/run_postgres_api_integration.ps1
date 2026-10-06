param(
    [string]$PgBin = 'C:\Program Files\PostgreSQL\18\bin',
    [int]$Port = 55439,
    [string]$TestFilter = 'FullyQualifiedName~AuthenticatedApiTests|FullyQualifiedName~CompatibleVariantsApiTests|FullyQualifiedName~MachinePartOptionsApiTests|FullyQualifiedName~TransportBatchApiTests|FullyQualifiedName~TransportReadinessApiTests|FullyQualifiedName~WarehousePreparationApiTests|FullyQualifiedName~ContainerActionsApiTests|FullyQualifiedName~ProductionSessionApiTests|FullyQualifiedName~WarehouseRoleAuthorizationApiTests|FullyQualifiedName~TerminalMachineBindingApiTests|FullyQualifiedName~SingleActiveSessionApiTests|FullyQualifiedName~GlobalTaskVisibilityAndCancellationScopeApiTests|FullyQualifiedName~OperationalAuditEventsApiTests|FullyQualifiedName~OperationalAuditContainerActionEventsApiTests|FullyQualifiedName~ActiveTransportProjectionApiTests|FullyQualifiedName~FinishedContainerFlowApiTests|FullyQualifiedName~PartDemandApiTests|FullyQualifiedName~DeviceNotificationApiTests|FullyQualifiedName~FinishedGoodsAreasApiTests|FullyQualifiedName~OperationalEvidenceApiTests|FullyQualifiedName~RobotAlarmObservationApiTests|FullyQualifiedName~RdsAlarmObservationWorkerTests'
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$tempRoot = Join-Path $projectRoot '.codex-pg-phase04-integration'
$dataDir = Join-Path $tempRoot 'data'
$database = 'tkg_pda_phase04'
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
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\011_warehouse_operator_roles.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\012_client_device_machine_binding.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\013_user_sessions.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\014_transport_event_context.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\015_part_demand_dependencies.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\016_finished_goods_areas.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\017_internal_device_notifications.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\018_production_end_priorities.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\019_robot_alarm_observations.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\020_robot_alarm_observation_state.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\seeds\generated\002a_customer_textual_master_data.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\seeds\generated\002b_customer_physical_layouts.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\seeds\generated\002c_customer_layout_slot_part_assignments.sql')))
    # Prove the additive migration can safely be replayed after customer seeds.
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\006_production_sessions.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\007_bring_full_task_type.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\008_warehouse_preparation_areas.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\009_preparation_placement_key.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\010_finished_goods_store.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\011_warehouse_operator_roles.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\012_client_device_machine_binding.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\013_user_sessions.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\014_transport_event_context.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\015_part_demand_dependencies.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\016_finished_goods_areas.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\017_internal_device_notifications.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\018_production_end_priorities.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\019_robot_alarm_observations.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\020_robot_alarm_observation_state.sql')))

    $connectionString = "Host=127.0.0.1;Port=$Port;Database=$database;Username=postgres;Pooling=true;Maximum Pool Size=50"
    $env:TKG_PDA_API_TEST_CONNECTION_STRING = $connectionString
    $env:ConnectionStrings__TkgPda = $connectionString
    $env:Authentication__Jwt__SigningKey = 'phase-04-integration-signing-key-with-more-than-thirty-two-bytes'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    # This isolated HTTP/DB suite never reaches the live simulator or RDS.
    $env:Rds__Enabled = 'false'
    $env:Rds__DispatchEnabled = 'false'
    $env:RdsCoreExecution__Enabled = 'false'
    Invoke-Checked 'dotnet' @(
        'test', (Join-Path $projectRoot 'tests\TkgPda.Api.Tests\TkgPda.Api.Tests.csproj'),
        '--configuration', 'Release', '--no-restore', '--filter', $TestFilter,
        '--logger', 'console;verbosity=normal'
    )

    $productionDigestSql = @'
SELECT md5(jsonb_build_array(
  (SELECT jsonb_agg(to_jsonb(s) ORDER BY id) FROM ops.production_sessions s),
  (SELECT jsonb_agg(to_jsonb(r) ORDER BY id) FROM ops.transport_requests r),
  (SELECT jsonb_agg(to_jsonb(a) ORDER BY id) FROM audit.audit_logs a)
)::text);
'@
    $productionBefore = & $psql @common -Atc $productionDigestSql
    if ($LASTEXITCODE -ne 0) { throw 'Production pre-replay digest failed' }
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\006_production_sessions.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\007_bring_full_task_type.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\008_warehouse_preparation_areas.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\009_preparation_placement_key.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\010_finished_goods_store.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\011_warehouse_operator_roles.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\012_client_device_machine_binding.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\013_user_sessions.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\014_transport_event_context.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\015_part_demand_dependencies.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\016_finished_goods_areas.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\017_internal_device_notifications.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\018_production_end_priorities.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\019_robot_alarm_observations.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\020_robot_alarm_observation_state.sql')))
    $productionAfter = & $psql @common -Atc $productionDigestSql
    if ($LASTEXITCODE -ne 0 -or $productionBefore -ne $productionAfter) { throw 'Migration replay changed production/transport/audit records' }

    $verification = (& $psql @common -Atc @"
SELECT json_build_object(
  'postgresql_version', current_setting('server_version'),
  'roles', (SELECT json_agg(code ORDER BY code) FROM auth.roles),
  'machine_master_records', (SELECT count(*) FROM master.machines),
  'rds_tasks', (SELECT count(*) FROM integration.rds_tasks),
  'rds_worksites', (SELECT count(*) FROM integration.rds_worksites),
  'production_migration_replay', 'UNCHANGED',
  'production_orphans', (SELECT count(*) FROM ops.transport_requests r
      LEFT JOIN ops.production_sessions s ON s.id = r.production_session_id AND s.machine_id = r.machine_id
      WHERE r.production_session_id IS NOT NULL AND s.id IS NULL),
  'result', 'PASS'
);
"@) | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL verification query failed' }
    if ($verification.production_orphans -ne 0) { throw 'Production/transport orphan detected' }
    $verification | ConvertTo-Json
}
finally {
    Remove-Item Env:TKG_PDA_API_TEST_CONNECTION_STRING -ErrorAction SilentlyContinue
    Remove-Item Env:ConnectionStrings__TkgPda -ErrorAction SilentlyContinue
    Remove-Item Env:Authentication__Jwt__SigningKey -ErrorAction SilentlyContinue
    Remove-Item Env:Rds__Enabled -ErrorAction SilentlyContinue
    Remove-Item Env:Rds__DispatchEnabled -ErrorAction SilentlyContinue
    Remove-Item Env:RdsCoreExecution__Enabled -ErrorAction SilentlyContinue
    if ($started -and (Test-Path -LiteralPath $dataDir)) {
        & (Join-Path $PgBin 'pg_ctl.exe') -D $dataDir -m fast -w stop | Out-Host
    }
    if (Test-Path -LiteralPath $tempRoot) {
        $resolvedTemp = (Resolve-Path -LiteralPath $tempRoot).Path
        if (-not $resolvedTemp.StartsWith($projectRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
            (Split-Path -Leaf $resolvedTemp) -ne '.codex-pg-phase04-integration') {
            throw "Refusing unsafe cleanup target: $resolvedTemp"
        }
        Remove-Item -LiteralPath $resolvedTemp -Recurse -Force
    }
}
