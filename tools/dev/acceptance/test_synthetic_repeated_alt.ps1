<#
.SYNOPSIS
    Synthetic Full-E2E Repeated ALT demand acceptance test for isolated acceptance database.
.DESCRIPTION
    Executes full multi-cycle lifecycle testing of Repeated ALT demands with synthetic commissioning:
    - SAFETY GATE: strictly enforces database is 'tkg_pda_acceptance' (refuses tkg_pda_dev).
    - Publishes synthetic NAV profile with verified geometry.
    - Installs synthetic shared endpoint bindings for EMPTY_RETURN and NON_FINISHED_RETURN.
    - Validates Cycle 1: OCCUPIED EMPTY -> REMOVE_EMPTY predecessor + BRING_FULL successor dependency chain.
    - Validates Cycle 2: Successive ALT demand creates second dependency chain.
    - Validates runtime rejection with CONTAINER_TYPE_MISSING when demanding an ALT part lacking container configuration.
#>
[CmdletBinding()]
param(
    [string]$ApiBaseUrl = 'http://127.0.0.1:5001',
    [string]$HostName = '127.0.0.1',
    [int]$Port = 55450,
    [string]$Database = 'tkg_pda_acceptance',
    [string]$User = 'postgres'
)

$ErrorActionPreference = 'Stop'

# ==============================================================================
# SAFETY GATE: Synthetic acceptance MUST NOT run against persistent dev database
# ==============================================================================
if ($Database -ne 'tkg_pda_acceptance') {
    Write-Host "`nCRITICAL SAFETY GATE VIOLATION:" -ForegroundColor Red
    Write-Host "Synthetic Repeated ALT test performs destructive setup and commissioning fabrication." -ForegroundColor Red
    Write-Host "It requires the isolated 'tkg_pda_acceptance' database, but was called with: '$Database'." -ForegroundColor Red
    throw "Synthetic acceptance requires isolated tkg_pda_acceptance database."
}
if ($ApiBaseUrl.TrimEnd('/') -ne 'http://127.0.0.1:5001') {
    throw 'Synthetic acceptance requires its dedicated isolated API on http://127.0.0.1:5001.'
}

$psql = 'C:\Program Files\PostgreSQL\18\bin\psql.exe'
if (-not (Test-Path -LiteralPath $psql)) {
    $psql = (Get-Command psql.exe -ErrorAction SilentlyContinue)?.Source
}
function Assert-AcceptanceDatabase {
    $actual = (& $psql -h $HostName -p $Port -U $User -d $Database -t -A -c "SELECT current_database();").Trim()
    if ($actual -ne 'tkg_pda_acceptance') {
        throw "Synthetic acceptance requires isolated tkg_pda_acceptance database. current_database() returned '$actual'."
    }
}
Assert-AcceptanceDatabase
$apiBase = $ApiBaseUrl

Write-Host "==================================================" -ForegroundColor Cyan
Write-Host "PHASE: SYNTHETIC REPEATED ALT DEMAND FULL E2E (ISOLATED DB)" -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan
Write-Host "Verified Database: $Database (ISOLATED)"
Write-Host "Target API URL:    $apiBase"

# 1. Fetch Machine SH-01-A, Variant 1, and requestable ALT Part 67116BN050-40 from isolated acceptance DB
$sql = @"
SELECT
    m.id::text as machine_id,
    v.id::text as variant_id,
    pg.id::text as group_id,
    p_main.id::text as main_part_id,
    p_alt.id::text as alt_part_id,
    p_alt.part_code as alt_part_code,
    ct.id::text as container_id,
    ct.code as container_code,
    s.id::text as slot_id,
    s.code as slot_code,
    np.id::text as nav_id,
    np.status as nav_status
FROM master.machines m
JOIN master.variants v ON v.machine_id = m.id AND v.code = '1'
JOIN master.product_groups pg ON pg.variant_id = v.id
JOIN master.parts p_main ON p_main.id = pg.main_part_id
JOIN master.product_group_parts pgp ON pgp.product_group_id = pg.id AND pgp.relation_type = 'ALT_PART'
JOIN master.parts p_alt ON p_alt.id = pgp.part_id AND p_alt.part_code = '67116BN050-40'
JOIN master.container_types ct ON ct.id = pgp.container_type_id
JOIN master.layouts l ON l.machine_id = m.id AND l.code = 'CUSTOMER-PHYSICAL-V1'
JOIN master.layout_slots s ON s.layout_id = l.id AND s.code = 'SLOT-003'
JOIN master.variant_navigation_profile_assignments vnpa ON vnpa.variant_id = v.id
JOIN master.navigation_profiles np ON np.id = vnpa.navigation_profile_id AND np.config_json ->> 'commissioningRegistry' = 'tkg_rds_variant_map_naming_v1'
WHERE m.code = 'SH-01-A';
"@

$row = (& $psql -h $HostName -p $Port -U $User -d $Database -t -A -F '|' -c $sql).Trim().Split('|')
$machineId     = $row[0]
$variantId     = $row[1]
$groupId       = $row[2]
$mainPartId    = $row[3]
$altPartId     = $row[4]
$altPartCode   = $row[5]
$containerId   = $row[6]
$containerCode = $row[7]
$slotId        = $row[8]
$slotCode      = $row[9]
$navId         = $row[10]

# Fetch missing-container machine SH-01-P1--SH-01-P2 from isolated DB
$mSql = @"
SELECT
    m.id::text,
    v.id::text,
    pg.id::text,
    p_main.id::text,
    p_alt.id::text,
    p_alt.part_code,
    np.id::text,
    np.status
FROM master.machines m
JOIN master.variants v ON v.machine_id = m.id AND v.code = '1'
JOIN master.product_groups pg ON pg.variant_id = v.id
JOIN master.parts p_main ON p_main.id = pg.main_part_id
JOIN master.product_group_parts pgp ON pgp.product_group_id = pg.id AND pgp.relation_type = 'ALT_PART' AND pgp.container_type_id IS NULL
JOIN master.parts p_alt ON p_alt.id = pgp.part_id
JOIN master.variant_navigation_profile_assignments vnpa ON vnpa.variant_id = v.id
JOIN master.navigation_profiles np ON np.id = vnpa.navigation_profile_id AND np.config_json ->> 'commissioningRegistry' = 'tkg_rds_variant_map_naming_v1'
WHERE m.code = 'SH-01-P1--SH-01-P2'
LIMIT 1;
"@
$mRow = (& $psql -h $HostName -p $Port -U $User -d $Database -t -A -F '|' -c $mSql).Trim().Split('|')
$mMachId  = $mRow[0]
$mVarId   = $mRow[1]
$mGroupId = $mRow[2]
$mMainId  = $mRow[3]
$mAltId   = $mRow[4]
$mAltCode = $mRow[5]
$mNavId   = $mRow[6]

Write-Host "Target Machine: SH-01-A ($machineId)"
Write-Host "Real ALT Part:  $altPartCode ($altPartId) with Container: $containerCode"

# Passwordless operator login
$loginPayload = @{ employeeNumber = 'DEV-LINE' } | ConvertTo-Json
$loginResp = Invoke-RestMethod -Method Post -Uri "$apiBase/api/v1/auth/login" -Headers @{ 'X-Device-Code' = 'DEV-LINE' } -ContentType 'application/json' -Body $loginPayload
$token = $loginResp.accessToken
$headers = @{
    'Authorization' = "Bearer $token"
    'X-Device-Code' = 'DEV-LINE'
}

$runGuid = [Guid]::NewGuid().ToString('N')

# In isolated acceptance DB: publish synthetic profile and install shared endpoints
& $psql -h $HostName -p $Port -U $User -d $Database -c @"
UPDATE master.navigation_profiles SET status = 'PUBLISHED' WHERE id = '$navId';
UPDATE audit.client_devices SET machine_id = '$machineId' WHERE device_code = 'DEV-LINE';
UPDATE ops.machine_runtime_state SET active_navigation_id = '$navId' WHERE machine_id = '$machineId';

INSERT INTO integration.rds_worksites (navigation_profile_id, rds_site_id, rds_type, last_sync_at, last_disabled, last_sync_failed, last_filled, last_locked, raw_state_json)
SELECT '$navId', planned_rds_site_id, 1, now(), 0, 0, 0, 0, '{"filled":0,"locked":0}'::jsonb
FROM integration.rds_worksite_plans
WHERE navigation_profile_id = '$navId' AND planned_rds_site_id IN ('TKG_SH01A_V01_WS_RETURN01', 'TKG_SH01A_V01_WS_EMPTY01')
ON CONFLICT (navigation_profile_id, rds_site_id) DO UPDATE SET last_sync_at = now(), last_disabled = 0, last_sync_failed = 0, last_filled = 0, last_locked = 0;

INSERT INTO integration.profile_shared_endpoint_bindings
    (navigation_profile_id, endpoint_class, business_code, store_location_id, rds_worksite_id)
SELECT '$navId', 'EMPTY_RETURN', 'EMPTY-01', NULL, w.id
FROM integration.rds_worksites w
WHERE w.navigation_profile_id = '$navId' AND w.rds_site_id = 'TKG_SH01A_V01_WS_EMPTY01'
ON CONFLICT DO NOTHING;

INSERT INTO integration.profile_shared_endpoint_bindings
    (navigation_profile_id, endpoint_class, business_code, store_location_id, rds_worksite_id)
SELECT '$navId', 'NON_FINISHED_RETURN', 'RETURN-01', NULL, w.id
FROM integration.rds_worksites w
WHERE w.navigation_profile_id = '$navId' AND w.rds_site_id = 'TKG_SH01A_V01_WS_RETURN01'
ON CONFLICT DO NOTHING;
"@ | Out-Null

# Start production session on SH-01-A
$startPayload = @{
    variantId      = $variantId
    productGroupId = $groupId
    mainPartId     = $mainPartId
} | ConvertTo-Json
$startHeaders = $headers.Clone()
$startHeaders['Idempotency-Key'] = "acceptance:${runGuid}:alt-start"
$sessionResp = Invoke-RestMethod -Method Post -Uri "$apiBase/api/v1/machines/$machineId/production-sessions" -Headers $startHeaders -ContentType 'application/json' -Body $startPayload
$sessionId = $sessionResp.session.id
Write-Host "Started production session: $sessionId"

Write-Host "`n--- Cycle 1: OCCUPIED EMPTY -> REMOVE_EMPTY Predecessor + BRING_FULL Successor ---" -ForegroundColor Yellow

# Set SLOT-003 to OCCUPIED EMPTY with ALT Part
& $psql -h $HostName -p $Port -U $User -d $Database -c @"
UPDATE ops.slot_runtime_state
SET occupancy_state = 'OCCUPIED',
    load_state = 'EMPTY',
    current_part_id = '$altPartId',
    current_container_type_id = '$containerId',
    row_version = row_version + 1,
    updated_at = now()
WHERE layout_slot_id = '$slotId';
"@ | Out-Null

# Submit ALT part demand
$demandPayload = @{ partId = $altPartId } | ConvertTo-Json
$demandHeaders = $headers.Clone()
$demandHeaders['Idempotency-Key'] = "acceptance:${runGuid}:alt-demand-c1"
$demandResp = Invoke-RestMethod -Method Post -Uri "$apiBase/api/v1/machines/$machineId/part-demands" -Headers $demandHeaders -ContentType 'application/json' -Body $demandPayload
Write-Host "Cycle 1 demand: $($demandResp.taskType) waits on $($demandResp.predecessorTaskType), readiness=$($demandResp.readiness)"

# The dependency table has no type column. A row means the successor cannot
# be dispatched until the predecessor status is COMPLETED.
$depSql = @"
SELECT
    pred.id::text,
    pred_tt.code,
    pred.status,
    succ.id::text,
    succ_tt.code,
    succ.status,
    (SELECT count(*)
     FROM ops.transport_requests queued
     WHERE queued.id = succ.id
       AND queued.status = 'QUEUED'
       AND NOT EXISTS (
           SELECT 1
           FROM ops.transport_request_dependencies dep
           JOIN ops.transport_requests pred2 ON pred2.id = dep.predecessor_request_id
           WHERE dep.successor_request_id = queued.id
             AND pred2.status <> 'COMPLETED'))::text
FROM ops.transport_request_dependencies d
JOIN ops.transport_requests pred ON pred.id = d.predecessor_request_id
JOIN master.task_types pred_tt ON pred_tt.id = pred.task_type_id
JOIN ops.transport_requests succ ON succ.id = d.successor_request_id
JOIN master.task_types succ_tt ON succ_tt.id = succ.task_type_id
WHERE pred.production_session_id = '$sessionId';
"@
$depRows = @((& $psql -h $HostName -p $Port -U $User -d $Database -t -A -F '|' -c $depSql) | Where-Object { $_.Trim() })
$dParts = $depRows[0].Split('|')
$predId = $dParts[0]
$predOp = $dParts[1]
$succId = $dParts[3]
$succOp = $dParts[4]
$eligible = $dParts[6]

Write-Host "Cycle 1 Dependency Chain:"
Write-Host ("  Predecessor: {0} ({1})" -f $predOp, $predId)
Write-Host ("  Successor:   {0} ({1})" -f $succOp, $succId)
Write-Host ("  Dispatch-eligible while predecessor is open: {0}" -f $eligible)

if ($predOp -eq 'REMOVE_EMPTY' -and $succOp -eq 'BRING_FULL' -and $demandResp.predecessorTaskType -eq 'REMOVE_EMPTY' -and $eligible -eq '0') {
    Write-Host "PASS: Cycle 1 verified REMOVE_EMPTY predecessor + BRING_FULL successor. Successor cannot advance before predecessor success." -ForegroundColor Green
} else {
    Write-Host "FAIL: Cycle 1 dependency mismatch!" -ForegroundColor Red
    exit 1
}

# Simulate fulfillment of Cycle 1 in acceptance DB
& $psql -h $HostName -p $Port -U $User -d $Database -c @"
UPDATE ops.transport_requests SET status = 'COMPLETED', completed_at = now() WHERE id IN ('$predId', '$succId');
UPDATE ops.slot_runtime_state
SET occupancy_state = 'OCCUPIED',
    load_state = 'EMPTY',
    current_part_id = '$altPartId',
    current_container_type_id = '$containerId',
    row_version = row_version + 1,
    updated_at = now()
WHERE layout_slot_id = '$slotId';
"@ | Out-Null

Write-Host "`n--- Cycle 2: OCCUPIED FULL -> REMOVE_FULL Predecessor + BRING_FULL Successor ---" -ForegroundColor Yellow

& $psql -h $HostName -p $Port -U $User -d $Database -c @"
UPDATE ops.slot_runtime_state
SET occupancy_state = 'OCCUPIED',
    load_state = 'FULL',
    current_part_id = '$altPartId',
    current_container_type_id = '$containerId',
    row_version = row_version + 1,
    updated_at = now()
WHERE layout_slot_id = '$slotId';
"@ | Out-Null

$demandHeaders2 = $headers.Clone()
$demandHeaders2['Idempotency-Key'] = "acceptance:${runGuid}:alt-demand-c2"
$demandResp2 = Invoke-RestMethod -Method Post -Uri "$apiBase/api/v1/machines/$machineId/part-demands" -Headers $demandHeaders2 -ContentType 'application/json' -Body $demandPayload
Write-Host "Cycle 2 demand: $($demandResp2.taskType) waits on $($demandResp2.predecessorTaskType)"

$totalDeps = (& $psql -h $HostName -p $Port -U $User -d $Database -t -A -c @"
SELECT count(*) FROM ops.transport_request_dependencies d
JOIN ops.transport_requests r ON r.id = d.predecessor_request_id
WHERE r.machine_id = '$machineId';
"@).Trim()

$cycle2Op = (& $psql -h $HostName -p $Port -U $User -d $Database -t -A -c @"
SELECT pred_tt.code || '|' || pred.id::text || '|' || succ.id::text
FROM ops.transport_request_dependencies d
JOIN ops.transport_requests pred ON pred.id = d.predecessor_request_id
JOIN master.task_types pred_tt ON pred_tt.id = pred.task_type_id
JOIN ops.transport_requests succ ON succ.id = d.successor_request_id
WHERE succ.idempotency_key = 'acceptance:${runGuid}:alt-demand-c2';
"@).Trim()
$cycle2Parts = $cycle2Op.Split('|')
if ([int]$totalDeps -eq 2 -and $demandResp2.predecessorTaskType -eq 'REMOVE_FULL' -and $cycle2Parts[0] -eq 'REMOVE_FULL') {
    Write-Host "PASS: Cycle 2 verified REMOVE_FULL predecessor + BRING_FULL successor. Dependency count is exactly 2." -ForegroundColor Green
} else {
    Write-Host "FAIL: Cycle 2 dependency mismatch. Count=$totalDeps, predecessor=$($demandResp2.predecessorTaskType), row=$cycle2Op" -ForegroundColor Red
    exit 1
}

$cancelHeaders = $headers.Clone()
$cancelHeaders['Idempotency-Key'] = "acceptance:${runGuid}:alt-cancel"
Invoke-RestMethod -Method Post -Uri "$apiBase/api/v1/machines/$machineId/transport-requests/$($cycle2Parts[1])/cancel" -Headers $cancelHeaders | Out-Null
$succStatus = (& $psql -h $HostName -p $Port -U $User -d $Database -t -A -c "SELECT status FROM ops.transport_requests WHERE id = '$($cycle2Parts[2])';").Trim()
if ($succStatus -eq 'CANCELLED') {
    Write-Host "PASS: Predecessor cancellation propagated to the BRING_FULL successor." -ForegroundColor Green
} else {
    Write-Host "FAIL: Successor status after predecessor cancel is $succStatus" -ForegroundColor Red
    exit 1
}

Write-Host "`n--- Part 3: Runtime Missing-Container Demand Rejection ---" -ForegroundColor Yellow

# On machine SH-01-P1--SH-01-P2, publish profile in isolated DB and start session
& $psql -h $HostName -p $Port -U $User -d $Database -c @"
UPDATE master.navigation_profiles SET status = 'PUBLISHED' WHERE id = '$mNavId';
UPDATE ops.machine_runtime_state SET active_navigation_id = '$mNavId' WHERE machine_id = '$mMachId';
UPDATE audit.client_devices SET machine_id = '$mMachId' WHERE device_code = 'DEV-LINE';
"@ | Out-Null

$mStartPayload = @{
    variantId      = $mVarId
    productGroupId = $mGroupId
    mainPartId     = $mMainId
} | ConvertTo-Json
$mStartHeaders = $headers.Clone()
$mStartHeaders['Idempotency-Key'] = "acceptance:${runGuid}:m-start"
$mSessionResp = Invoke-RestMethod -Method Post -Uri "$apiBase/api/v1/machines/$mMachId/production-sessions" -Headers $mStartHeaders -ContentType 'application/json' -Body $mStartPayload
Write-Host "Started production session on ${mMachId}: $($mSessionResp.session.id)"

$mDemandHeaders = $headers.Clone()
$mDemandHeaders['Idempotency-Key'] = "acceptance:${runGuid}:m-demand"
$mDemandPayload = @{ partId = $mAltId } | ConvertTo-Json

try {
    $mDemandResp = Invoke-RestMethod -Method Post -Uri "$apiBase/api/v1/machines/$mMachId/part-demands" -Headers $mDemandHeaders -ContentType 'application/json' -Body $mDemandPayload
    Write-Host "UNEXPECTED: Missing-container demand succeeded on SH-01-P1--SH-01-P2!" -ForegroundColor Red
    exit 1
} catch {
    $errBody = if ($_.ErrorDetails -and $_.ErrorDetails.Message) { $_.ErrorDetails.Message } else {
        $reader = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())
        $reader.ReadToEnd()
    }
    $errObj = $errBody | ConvertFrom-Json
    if ($errObj.code -eq 'CONTAINER_TYPE_MISSING') {
        Write-Host "PASS: Demand rejected with CONTAINER_TYPE_MISSING: '$($errObj.detail)'" -ForegroundColor Green
    } else {
        Write-Host "FAIL: Unexpected error code for missing container: $($errObj.code)" -ForegroundColor Red
        exit 1
    }
}

Write-Host "`nRESULT: PASS (Synthetic Repeated ALT multi-cycle execution & rejection verified)" -ForegroundColor Green
exit 0
