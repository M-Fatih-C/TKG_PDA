param(
    [string]$PgBin = 'C:\Program Files\PostgreSQL\18\bin',
    [int]$PostgresPort = 55445,
    [int]$ApiPort = 5000,
    [string]$RdsBaseUrl = 'http://127.0.0.1:8080',
    [string]$CommissioningPhase = '08c',
    [string]$TaskLabel = 'TKG_SIM_MOVE',
    [string]$FromParameter = 'fromPoint',
    [string]$ToParameter = 'toPoint',
    [string]$FromValue = 'AP15',
    [string]$ToValue = 'AP20',
    [string]$SceneConfirmationVariable = 'TKG_PDA_PHASE08C_SIMULATOR_SCENE_CONFIRMED',
    [int]$TerminalTimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
if ($CommissioningPhase -notmatch '^[0-9a-z]+$') {
    throw 'CommissioningPhase may contain only lower-case letters and digits.'
}
$tempLeaf = ".codex-pg-phase$CommissioningPhase-live"
$tempRoot = Join-Path $projectRoot $tempLeaf
$dataDir = Join-Path $tempRoot 'data'
$database = "tkg_pda_phase$CommissioningPhase"
$apiProcess = $null
$postgresStarted = $false
$apiStdOut = Join-Path $tempRoot 'api.stdout.log'
$apiStdErr = Join-Path $tempRoot 'api.stderr.log'
$apiBaseUrl = "http://127.0.0.1:$ApiPort"

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

function Invoke-PsqlScalar {
    param([Parameter(Mandatory = $true)][string]$Sql)

    $value = (& $script:psql @script:common -Atc $Sql)
    if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL scalar query failed.' }
    return ($value -join "`n").Trim()
}

function ConvertTo-SqlLiteral {
    param([Parameter(Mandatory = $true)][string]$Value)
    return "'" + $Value.Replace("'", "''") + "'"
}

function Invoke-RdsPost {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][object]$Body
    )

    return Invoke-RestMethod -Method Post -Uri "$RdsBaseUrl$Path" -Headers @{ language = 'en' } `
        -ContentType 'application/json' -Body ($Body | ConvertTo-Json -Depth 12 -Compress) -TimeoutSec 15
}

function Get-RdsData {
    param([Parameter(Mandatory = $true)][object]$Envelope)
    $data = $Envelope.data
    if ($data -is [string]) { return $data | ConvertFrom-Json }
    return $data
}

function Get-WorkSiteStateDigest {
    $response = Invoke-RdsPost -Path '/api/work-sites/sites' -Body @{}
    if ([int]$response.code -ne 200) { throw "RDS WorkSite query failed with code $($response.code)." }
    $sites = @(Get-RdsData $response) | Sort-Object siteId | ForEach-Object {
        [ordered]@{
            siteId = $_.siteId
            filled = $_.filled
            content = $_.content
            locked = $_.locked
            lockedBy = $_.lockedBy
            disabled = $_.disabled
            syncFailed = $_.syncFailed
        }
    }
    $json = $sites | ConvertTo-Json -Depth 5 -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    $digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
    return [ordered]@{ count = $sites.Count; sha256 = $digest }
}

function New-Pbkdf2Hash {
    param([Parameter(Mandatory = $true)][string]$Password)
    $salt = [Security.Cryptography.RandomNumberGenerator]::GetBytes(16)
    $hash = [Security.Cryptography.Rfc2898DeriveBytes]::Pbkdf2(
        [Text.Encoding]::UTF8.GetBytes($Password),
        $salt,
        210000,
        [Security.Cryptography.HashAlgorithmName]::SHA512,
        32)
    return '$tkg-pbkdf2-sha512$v=1$i=210000$' + [Convert]::ToBase64String($salt) + '$' + [Convert]::ToBase64String($hash)
}

if ($env:Rds__Enabled -ne 'true' -or $env:Rds__DispatchEnabled -ne 'true') {
    throw 'Explicit live opt-in required: set Rds__Enabled=true and Rds__DispatchEnabled=true.'
}
if ([Environment]::GetEnvironmentVariable($SceneConfirmationVariable, 'Process') -ne 'true') {
    throw "Confirm in RDS UI that only sim_01/sim_02 are connected and set $SceneConfirmationVariable=true."
}
if ([string]::IsNullOrWhiteSpace($env:Rds__IntegrationKey) -or $env:Rds__IntegrationKey.Length -lt 32 `
    -or [string]::IsNullOrWhiteSpace($env:Rds__CallbackKey) -or $env:Rds__CallbackKey.Length -lt 32) {
    throw 'Live RDS integration and callback keys must be supplied through process environment.'
}
$rdsUri = [Uri]$RdsBaseUrl
if ($rdsUri.Scheme -ne 'http' -or -not $rdsUri.IsLoopback -or -not [string]::IsNullOrEmpty($rdsUri.UserInfo)) {
    throw 'Phase 08C accepts only the explicitly approved loopback HTTP RDS endpoint.'
}
if ($ApiPort -ne 5000) {
    throw 'The commissioned RDS callback currently targets loopback port 5000; use ApiPort 5000.'
}
if (Test-Path -LiteralPath $tempRoot) {
    throw "Temporary commissioning path already exists: $tempRoot"
}
if (Get-NetTCPConnection -State Listen -LocalPort $PostgresPort -ErrorAction SilentlyContinue) {
    throw "PostgreSQL commissioning port is already in use: $PostgresPort"
}
if (Get-NetTCPConnection -State Listen -LocalPort $ApiPort -ErrorAction SilentlyContinue) {
    throw "API commissioning port is already in use: $ApiPort"
}

$previousEnvironment = @{}
$managedEnvironment = @(
    'ConnectionStrings__TkgPda', 'Authentication__Jwt__SigningKey', 'ASPNETCORE_URLS',
    'ASPNETCORE_ENVIRONMENT', 'Rds__BaseUrl', 'Rds__CallbackUrl', 'Rds__WorkerIntervalSeconds',
    'Rds__TimeoutSeconds', 'DOTNET_CLI_TELEMETRY_OPTOUT', 'PGCLIENTENCODING',
    'Rds__Enabled', 'Rds__DispatchEnabled', 'Rds__IntegrationKey', 'Rds__CallbackKey',
    $SceneConfirmationVariable
)
foreach ($name in $managedEnvironment) {
    $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

try {
    $env:PGCLIENTENCODING = 'UTF8'
    New-Item -ItemType Directory -Path $tempRoot | Out-Null
    Invoke-Checked (Join-Path $PgBin 'initdb.exe') @(
        '-D', $dataDir, '-U', 'postgres', '-A', 'trust', '--no-locale', '-E', 'UTF8'
    )
    Invoke-Checked (Join-Path $PgBin 'pg_ctl.exe') @(
        '-D', $dataDir,
        '-l', (Join-Path $tempRoot 'postgres.log'),
        '-o', "-p $PostgresPort -h 127.0.0.1",
        '-w', 'start'
    )
    $postgresStarted = $true

    Invoke-Checked (Join-Path $PgBin 'createdb.exe') @(
        '-h', '127.0.0.1', '-p', "$PostgresPort", '-U', 'postgres', $database
    )

    $script:psql = Join-Path $PgBin 'psql.exe'
    $script:common = @('-X', '-h', '127.0.0.1', '-p', "$PostgresPort", '-U', 'postgres', '-d', $database, '-v', 'ON_ERROR_STOP=1')
    Invoke-Checked $script:psql ($script:common + @('-f', (Join-Path $projectRoot 'db\migrations\001_initial_schema.sql')))
    Invoke-Checked $script:psql ($script:common + @('-f', (Join-Path $projectRoot 'db\migrations\002_operator_employee_identity.sql')))
    Invoke-Checked $script:psql ($script:common + @('-f', (Join-Path $projectRoot 'db\migrations\003_transport_batch.sql')))
    Invoke-Checked $script:psql ($script:common + @('-f', (Join-Path $projectRoot 'db\migrations\004_transport_request_readiness.sql')))
    Invoke-Checked $script:psql ($script:common + @('-f', (Join-Path $projectRoot 'db\migrations\005_layout_slot_part_mapping.sql')))
    Invoke-Checked $script:psql ($script:common + @('-f', (Join-Path $projectRoot 'db\migrations\006_production_sessions.sql')))
    Invoke-Checked $script:psql ($script:common + @('-f', (Join-Path $projectRoot 'db\migrations\007_bring_full_task_type.sql')))
    Invoke-Checked $script:psql ($script:common + @('-f', (Join-Path $projectRoot 'db\migrations\008_warehouse_preparation_areas.sql')))
    Invoke-Checked $script:psql ($script:common + @('-f', (Join-Path $projectRoot 'db\migrations\009_preparation_placement_key.sql')))
    Invoke-Checked $script:psql ($script:common + @('-f', (Join-Path $projectRoot 'db\migrations\010_finished_goods_store.sql')))
    Invoke-Checked $script:psql ($script:common + @('-f', (Join-Path $projectRoot 'db\migrations\014_transport_event_context.sql')))
    Invoke-Checked $script:psql ($script:common + @('-f', (Join-Path $projectRoot 'db\migrations\015_part_demand_dependencies.sql')))
    Invoke-Checked $script:psql ($script:common + @('-f', (Join-Path $projectRoot 'db\migrations\016_finished_goods_areas.sql')))
    Invoke-Checked $script:psql ($script:common + @('-f', (Join-Path $projectRoot 'db\migrations\017_internal_device_notifications.sql')))
    Invoke-Checked $script:psql ($script:common + @('-f', (Join-Path $projectRoot 'db\seeds\generated\002a_customer_textual_master_data.sql')))
    Invoke-Checked $script:psql ($script:common + @('-f', (Join-Path $projectRoot 'db\seeds\generated\002b_customer_physical_layouts.sql')))
    Invoke-Checked $script:psql ($script:common + @('-f', (Join-Path $projectRoot 'db\seeds\generated\002c_customer_layout_slot_part_assignments.sql')))

    $context = (Invoke-PsqlScalar @"
SELECT json_build_object(
  'machineId', m.id,
  'variantId', v.id,
  'layoutId', a.layout_id,
  'slotId', (
    SELECT ls.id FROM master.layout_slots ls
    WHERE ls.layout_id = a.layout_id AND ls.slot_role <> 'MACHINE' AND ls.is_enabled
    ORDER BY ls.display_order, ls.code LIMIT 1))
FROM master.machines m
JOIN master.variants v ON v.machine_id = m.id AND v.code = '1'
JOIN master.variant_layout_assignments a ON a.variant_id = v.id AND a.machine_id = m.id AND a.is_default
JOIN master.layouts l ON l.id = a.layout_id AND l.status = 'PUBLISHED'
WHERE m.code = 'SH-01-A'
LIMIT 1;
"@) | ConvertFrom-Json
    if (-not $context.machineId -or -not $context.variantId -or -not $context.layoutId -or -not $context.slotId) {
        throw "The Phase $CommissioningPhase customer context SH-01-A / variant 1 / logistics slot was not found."
    }

    $operatorPassword = 'Phase08C-simulator-only-42!'
    $passwordHash = New-Pbkdf2Hash $operatorPassword
    $passwordHashSql = ConvertTo-SqlLiteral $passwordHash
    $taskConfigJson = [ordered]@{
        testScope = "P$($CommissioningPhase.ToUpperInvariant())"
        commissioningOnly = $true
        rdsInputBindings = @(
            [ordered]@{ param = $FromParameter; source = 'LITERAL'; value = $FromValue; required = $true }
            [ordered]@{ param = $ToParameter; source = 'LITERAL'; value = $ToValue; required = $true }
        )
    } | ConvertTo-Json -Depth 8 -Compress
    $taskLabelSql = ConvertTo-SqlLiteral $TaskLabel
    $taskConfigJsonSql = ConvertTo-SqlLiteral $taskConfigJson
    $setupSql = @'
INSERT INTO auth.users (username, password_hash, full_name, employee_no, is_active)
VALUES ('phase08c-operator', __PASSWORD_HASH__, 'Phase 08C Simulator Operator', 'P08C-SIM', true)
ON CONFLICT (username) DO UPDATE
SET password_hash = EXCLUDED.password_hash, full_name = EXCLUDED.full_name, is_active = true;

INSERT INTO auth.user_roles (user_id, role_id)
SELECT u.id, r.id FROM auth.users u CROSS JOIN auth.roles r
WHERE u.username = 'phase08c-operator' AND r.code = 'LINE_OPERATOR'
ON CONFLICT DO NOTHING;

INSERT INTO audit.client_devices (device_code, name, device_type, is_active)
VALUES ('PDA-P08C-SIM', 'Phase 08C Simulator PDA', 'PDA', true)
ON CONFLICT (device_code) DO UPDATE SET is_active = true, name = EXCLUDED.name;

INSERT INTO auth.user_machine_permissions (
    user_id, machine_id, can_view, can_operate, can_change_variant, can_manage_slots)
SELECT u.id, '__MACHINE_ID__'::uuid, true, true, false, false
FROM auth.users u WHERE u.username = 'phase08c-operator'
ON CONFLICT (user_id, machine_id) DO UPDATE
SET can_view = true, can_operate = true;

INSERT INTO ops.machine_runtime_state (machine_id, active_variant_id, active_layout_id, active_navigation_id)
VALUES ('__MACHINE_ID__'::uuid, '__VARIANT_ID__'::uuid, '__LAYOUT_ID__'::uuid, NULL)
ON CONFLICT (machine_id) DO UPDATE
SET active_variant_id = EXCLUDED.active_variant_id,
    active_layout_id = EXCLUDED.active_layout_id,
    active_navigation_id = NULL,
    row_version = ops.machine_runtime_state.row_version + 1,
    updated_at = now();

INSERT INTO ops.slot_runtime_state (layout_slot_id, occupancy_state, load_state)
VALUES ('__SLOT_ID__'::uuid, 'VACANT', 'UNKNOWN')
ON CONFLICT (layout_slot_id) DO UPDATE
SET occupancy_state = 'VACANT', load_state = 'UNKNOWN',
    current_part_id = NULL, current_container_type_id = NULL,
    row_version = ops.slot_runtime_state.row_version + 1, updated_at = now();

UPDATE master.task_templates
SET is_active = false
WHERE variant_id = '__VARIANT_ID__'::uuid
  AND task_type_id = (SELECT id FROM master.task_types WHERE code = 'BRING_EMPTY')
  AND is_active;

INSERT INTO master.task_templates (
    variant_id, task_type_id, rds_task_label, priority, config_json, is_active)
SELECT '__VARIANT_ID__'::uuid, id, __TASK_LABEL__, 1,
       __TASK_CONFIG_JSON__::jsonb,
       true
FROM master.task_types WHERE code = 'BRING_EMPTY';
'@
    $setupSql = $setupSql.Replace('__PASSWORD_HASH__', $passwordHashSql)
    $setupSql = $setupSql.Replace('__MACHINE_ID__', [string]$context.machineId)
    $setupSql = $setupSql.Replace('__VARIANT_ID__', [string]$context.variantId)
    $setupSql = $setupSql.Replace('__LAYOUT_ID__', [string]$context.layoutId)
    $setupSql = $setupSql.Replace('__SLOT_ID__', [string]$context.slotId)
    $setupSql = $setupSql.Replace('__TASK_LABEL__', $taskLabelSql)
    $setupSql = $setupSql.Replace('__TASK_CONFIG_JSON__', $taskConfigJsonSql)
    [void](Invoke-PsqlScalar $setupSql)

    $workSitesBefore = Get-WorkSiteStateDigest

    $connectionString = "Host=127.0.0.1;Port=$PostgresPort;Database=$database;Username=postgres;Pooling=true;Maximum Pool Size=50"
    $jwtBytes = [Security.Cryptography.RandomNumberGenerator]::GetBytes(48)
    $env:ConnectionStrings__TkgPda = $connectionString
    $env:Authentication__Jwt__SigningKey = [Convert]::ToBase64String($jwtBytes)
    $env:ASPNETCORE_URLS = $apiBaseUrl
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:Rds__BaseUrl = $RdsBaseUrl
    $env:Rds__CallbackUrl = "$apiBaseUrl/api/integrations/rds/events"
    $env:Rds__WorkerIntervalSeconds = '1'
    $env:Rds__TimeoutSeconds = '10'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

    Invoke-Checked 'dotnet' @('build', (Join-Path $projectRoot 'TKG.Pda.slnx'), '--configuration', 'Release', '--no-restore')
    $apiProcess = Start-Process -FilePath 'dotnet' -ArgumentList @(
        'run', '--project', (Join-Path $projectRoot 'src\TkgPda.Api\TkgPda.Api.csproj'),
        '--configuration', 'Release', '--no-build', '--no-restore', '--no-launch-profile'
    ) -PassThru -WindowStyle Hidden -RedirectStandardOutput $apiStdOut -RedirectStandardError $apiStdErr

    $ready = $false
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        if ($apiProcess.HasExited) { throw "TKG API exited before readiness with code $($apiProcess.ExitCode)." }
        try {
            $health = Invoke-RestMethod -Method Get -Uri "$apiBaseUrl/health/ready" -TimeoutSec 2
            if ($health.status -eq 'ready') { $ready = $true; break }
        }
        catch { Start-Sleep -Milliseconds 500 }
    }
    if (-not $ready) { throw 'TKG API did not become ready for the controlled simulator test.' }

    $deviceHeaders = @{ 'X-Device-Code' = 'PDA-P08C-SIM' }
    $login = Invoke-RestMethod -Method Post -Uri "$apiBaseUrl/api/v1/auth/login" -Headers $deviceHeaders `
        -ContentType 'application/json' -Body (@{ username = 'phase08c-operator'; password = $operatorPassword } | ConvertTo-Json -Compress)
    $operatorPassword = $null
    $passwordHash = $null
    $authHeaders = @{
        Authorization = "Bearer $($login.accessToken)"
        'X-Device-Code' = 'PDA-P08C-SIM'
        'Idempotency-Key' = "phase$CommissioningPhase-single-simulator-request"
    }
    $transportBody = [ordered]@{
        machineId = [string]$context.machineId
        variantId = [string]$context.variantId
        layoutId = [string]$context.layoutId
        navigationProfileId = $null
        productGroupId = $null
        partId = $null
        containerTypeId = $null
        operation = 'BRING_EMPTY'
        sourceSlotId = $null
        destinationSlotId = [string]$context.slotId
        priority = 1
    }
    $transportJson = $transportBody | ConvertTo-Json -Depth 5 -Compress
    $createdResponse = Invoke-WebRequest -Method Post -Uri "$apiBaseUrl/api/v1/transport-requests" `
        -Headers $authHeaders -ContentType 'application/json' -Body $transportJson
    if ([int]$createdResponse.StatusCode -ne 201) { throw 'The single controlled TransportRequest was not created.' }
    $created = $createdResponse.Content | ConvertFrom-Json
    $requestId = [string]$created.id
    Write-Host "PHASE_HARNESS TransportRequestId=$requestId"

    $replayResponse = Invoke-WebRequest -Method Post -Uri "$apiBaseUrl/api/v1/transport-requests" `
        -Headers $authHeaders -ContentType 'application/json' -Body $transportJson
    $replay = $replayResponse.Content | ConvertFrom-Json
    if ([int]$replayResponse.StatusCode -ne 200 -or [string]$replay.id -ne $requestId `
        -or $replayResponse.Headers['Idempotency-Replayed'] -ne 'true') {
        throw 'TKG HTTP idempotency replay did not return the same TransportRequest.'
    }

    $tkgStatusSequence = [Collections.Generic.List[string]]::new()
    $rdsStatusSequence = [Collections.Generic.List[int]]::new()
    $terminal = $null
    for ($poll = 0; $poll -lt $TerminalTimeoutSeconds; $poll++) {
        $stateText = Invoke-PsqlScalar @"
SELECT json_build_object(
  'requestStatus', tr.status,
  'rdsTaskRecordId', rt.rds_task_record_id,
  'rdsTaskLabel', rt.rds_task_label,
  'rdsStatus', rt.rds_status,
  'agvId', rt.agv_id,
  'endedReason', rt.ended_reason,
  'requestPayload', rt.request_payload_json,
  'responsePayload', rt.response_payload_json,
  'lastStatus', rt.last_status_json)
FROM ops.transport_requests tr
LEFT JOIN integration.rds_tasks rt ON rt.transport_request_id = tr.id
WHERE tr.id = '$requestId'::uuid;
"@
        $state = $stateText | ConvertFrom-Json
        if ($tkgStatusSequence.Count -eq 0 -or $tkgStatusSequence[$tkgStatusSequence.Count - 1] -ne [string]$state.requestStatus) {
            $tkgStatusSequence.Add([string]$state.requestStatus)
        }
        if ($null -ne $state.rdsStatus) {
            $statusNumber = [int]$state.rdsStatus
            if ($rdsStatusSequence.Count -eq 0 -or $rdsStatusSequence[$rdsStatusSequence.Count - 1] -ne $statusNumber) {
                $rdsStatusSequence.Add($statusNumber)
            }
        }
        if ($state.requestStatus -in @('COMPLETED', 'FAILED', 'CANCELLED')) { $terminal = $state; break }
        Start-Sleep -Seconds 1
    }
    if ($null -eq $terminal) { throw "The controlled TKG/RDS simulator request did not reach a terminal state in $TerminalTimeoutSeconds seconds." }
    if ($terminal.requestStatus -ne 'COMPLETED' -or [int]$terminal.rdsStatus -ne 1003) {
        throw "The controlled simulator request ended unexpectedly: TKG=$($terminal.requestStatus), RDS=$($terminal.rdsStatus)."
    }
    if ([string]$terminal.rdsTaskRecordId -ne $requestId) {
        throw 'RDS taskRecordId does not equal the immutable TKG TransportRequest UUID.'
    }

    $queryEnvelope = Invoke-RdsPost -Path '/api/queryWindTask' -Body @{
        currentPage = 1
        pageSize = 100
        queryParam = @{ taskRecordId = $requestId }
    }
    if ([int]$queryEnvelope.code -ne 200) { throw 'RDS task query failed after simulator completion.' }
    $queryData = Get-RdsData $queryEnvelope
    $rdsTasks = @($queryData.pageList)
    if ([int]$queryData.totalCount -ne 1 -or $rdsTasks.Count -ne 1) {
        throw "Expected one RDS task for the TKG UUID, found $($queryData.totalCount)."
    }
    $rdsTask = $rdsTasks[0]

    $blockEnvelope = Invoke-RdsPost -Path '/api/queryBlocksByTaskId' -Body @{ taskRecordId = $requestId }
    if ([int]$blockEnvelope.code -ne 200) { throw 'RDS block query failed after simulator completion.' }
    $blockData = Get-RdsData $blockEnvelope
    $blocks = @($blockData.blockList) | ForEach-Object {
        [ordered]@{
            blockName = $_.blockName
            status = $_.status
            endedReason = $_.endedReason
            blockLabel = $_.blockLabel
        }
    }

    $retryInputParams = @{}
    $retryInputParams[$FromParameter] = $FromValue
    $retryInputParams[$ToParameter] = $ToValue
    $retryEnvelope = Invoke-RdsPost -Path '/script-api/tkg/create-transport' -Body @{
        integrationKey = $env:Rds__IntegrationKey
        taskRecordId = $requestId
        taskLabel = $TaskLabel
        priority = 1
        inputParams = $retryInputParams
    }
    if ([int]$retryEnvelope.code -ne 200 -or $retryEnvelope.message -ne 'IDEMPOTENT_REPLAY') {
        throw 'The same RDS taskRecordId was not accepted as an idempotent replay.'
    }
    $retryData = Get-RdsData $retryEnvelope
    if (-not $retryData.idempotentReplay) { throw 'RDS replay response did not mark idempotentReplay=true.' }

    $countAfterRetryEnvelope = Invoke-RdsPost -Path '/api/queryWindTask' -Body @{
        currentPage = 1
        pageSize = 100
        queryParam = @{ taskRecordId = $requestId }
    }
    $countAfterRetry = Get-RdsData $countAfterRetryEnvelope
    if ([int]$countAfterRetry.totalCount -ne 1) { throw 'The same taskRecordId retry created a duplicate RDS Wind Task.' }

    $workSitesAfter = Get-WorkSiteStateDigest
    $workSitesUnchanged = $workSitesBefore.count -eq $workSitesAfter.count -and $workSitesBefore.sha256 -eq $workSitesAfter.sha256
    if (-not $workSitesUnchanged) { throw 'Commissioning task unexpectedly changed observable RDS WorkSite state.' }

    $callbackAuditCount = [int](Invoke-PsqlScalar "SELECT count(*) FROM audit.audit_logs WHERE entity_id = '$requestId' AND action = 'RDS_CALLBACK_APPLIED';")
    $events = (Invoke-PsqlScalar @"
SELECT COALESCE(json_agg(json_build_object(
  'eventType', event_type,
  'oldStatus', old_status,
  'newStatus', new_status,
  'message', message)
  ORDER BY created_at), '[]'::json)
FROM ops.transport_request_events
WHERE transport_request_id = '$requestId'::uuid;
"@) | ConvertFrom-Json
    $requestCount = [int](Invoke-PsqlScalar "SELECT count(*) FROM ops.transport_requests;")
    $rdsProjectionCount = [int](Invoke-PsqlScalar "SELECT count(*) FROM integration.rds_tasks WHERE rds_task_record_id = '$requestId';")
    if ($requestCount -ne 1 -or $rdsProjectionCount -ne 1) {
        throw 'TKG persistence contains an unexpected duplicate commissioning request/task projection.'
    }

    $reportedInputParams = [ordered]@{}
    $reportedInputParams[$FromParameter] = $FromValue
    $reportedInputParams[$ToParameter] = $ToValue
    [ordered]@{
        result = 'PASS'
        simulatorAvailable = $true
        simulatorIds = @('sim_01', 'sim_02')
        physicalRobotConnected = $false
        taskDefinition = [ordered]@{
            label = $TaskLabel
            fixedRobot = if ($TaskLabel -eq 'TKG_SIM_MOVE') { 'sim_01' } else { $null }
            fixedRoute = if ($TaskLabel -eq 'TKG_SIM_MOVE') { @($FromValue, $ToValue) } else { $null }
            actions = @('Wait', 'Wait')
            productionWorkflow = $false
        }
        transportRequestId = $requestId
        rdsTaskRecordId = [string]$terminal.rdsTaskRecordId
        identityMatches = ([string]$terminal.rdsTaskRecordId -eq $requestId)
        tkgStatusSequence = @($tkgStatusSequence)
        rdsStatusSequence = @($rdsStatusSequence)
        finalTkgStatus = [string]$terminal.requestStatus
        finalRdsStatus = [int]$terminal.rdsStatus
        rdsTaskLabel = [string]$rdsTask.defLabel
        assignedAgv = [string]$rdsTask.agvId
        inputParams = $reportedInputParams
        rdsReportedInputParams = $rdsTask.inputParams
        endedReason = [string]$rdsTask.endedReason
        blocks = $blocks
        callbackAuditCount = $callbackAuditCount
        callbackObserved = ($callbackAuditCount -gt 0)
        reconciliationResult = if ($callbackAuditCount -gt 0) { 'CALLBACK_COMPLETED' } else { 'TERMINAL_STATE_OBSERVED_BY_RECONCILIATION' }
        transportEvents = $events
        tkgHttpReplaySameRequest = $true
        rdsRetryMessage = [string]$retryEnvelope.message
        rdsTaskCountForRecordId = [int]$countAfterRetry.totalCount
        tkgRequestCount = $requestCount
        tkgRdsProjectionCount = $rdsProjectionCount
        workSitesBefore = $workSitesBefore
        workSitesAfter = $workSitesAfter
        workSitesUnchanged = $workSitesUnchanged
        cancellationTestExecuted = $false
        cancellationReason = 'Optional second task omitted after the required successful create/replay proof.'
        postgresVersion = Invoke-PsqlScalar 'SHOW server_version;'
    } | ConvertTo-Json -Depth 12
}
catch {
    if (Test-Path -LiteralPath $apiStdErr) {
        Get-Content -LiteralPath $apiStdErr -Tail 80 | Write-Warning
    }
    if (Test-Path -LiteralPath $apiStdOut) {
        Get-Content -LiteralPath $apiStdOut -Tail 80 | Write-Warning
    }
    throw
}
finally {
    if ($null -ne $apiProcess -and -not $apiProcess.HasExited) {
        Stop-Process -Id $apiProcess.Id -Force -ErrorAction SilentlyContinue
        $apiProcess.WaitForExit(5000) | Out-Null
    }
    foreach ($name in $managedEnvironment) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process')
    }
    if ($postgresStarted -and (Test-Path -LiteralPath $dataDir)) {
        & (Join-Path $PgBin 'pg_ctl.exe') -D $dataDir -m fast -w stop | Out-Host
    }
    if (Test-Path -LiteralPath $tempRoot) {
        $resolvedTemp = (Resolve-Path -LiteralPath $tempRoot).Path
        if (-not $resolvedTemp.StartsWith($projectRoot, [StringComparison]::OrdinalIgnoreCase) `
            -or (Split-Path -Leaf $resolvedTemp) -ne $tempLeaf) {
            throw "Refusing unsafe cleanup target: $resolvedTemp"
        }
        Remove-Item -LiteralPath $resolvedTemp -Recurse -Force
    }
}
