[CmdletBinding()]
param(
    [string]$ApiBaseUrl = 'http://127.0.0.1:5000',
    [string]$HostName = '127.0.0.1',
    [int]$Port = 55450,
    [string]$Database = 'tkg_pda_dev',
    [string]$User = 'postgres'
)

$ErrorActionPreference = 'Stop'
$psql = 'C:\Program Files\PostgreSQL\18\bin\psql.exe'
if (-not (Test-Path -LiteralPath $psql)) {
    $psql = (Get-Command psql.exe -ErrorAction SilentlyContinue)?.Source
}
$apiBase = $ApiBaseUrl

Write-Host "==================================================" -ForegroundColor Cyan
Write-Host "PHASE: REPEATED ALT DEMAND ACCEPTANCE" -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan

# 1. Fetch Machine SH-01-A, Variant 1, and real requestable ALT Part 67116BN050-40
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
JOIN master.navigation_profiles np ON np.id = vnpa.navigation_profile_id
WHERE m.code = 'SH-01-A';
"@

$row = (& $psql -h 127.0.0.1 -p 55450 -U postgres -d tkg_pda_dev -t -A -F '|' -c $sql).Trim().Split('|')
$machineId   = $row[0]
$variantId   = $row[1]
$groupId     = $row[2]
$mainPartId  = $row[3]
$altPartId   = $row[4]
$altPartCode = $row[5]
$containerId = $row[6]
$containerCode = $row[7]
$slotId      = $row[8]
$slotCode    = $row[9]
$navId       = $row[10]
$origNavStatus = $row[11]

Write-Host "Target Machine: SH-01-A ($machineId)"
Write-Host "Active Variant: 1 ($variantId)"
Write-Host "Real ALT Part:  $altPartCode ($altPartId) with Container: $containerCode ($containerId)"
Write-Host "Target Machine Slot: $slotCode ($slotId)"
Write-Host "Navigation Profile: $navId (Current Status: $origNavStatus)"

# Login as dev-line operator
$loginPayload = @{
    username   = 'dev-line'
    password   = 'DevPass123!'
} | ConvertTo-Json
$loginResp = Invoke-RestMethod -Method Post -Uri "$apiBase/api/v1/auth/login" -Headers @{ 'X-Device-Code' = 'DEV-LINE' } -ContentType 'application/json' -Body $loginPayload
$token = $loginResp.accessToken

$headers = @{
    'Authorization' = "Bearer $token"
    'X-Device-Code' = 'DEV-LINE'
}

$origNavId = (& $psql -h 127.0.0.1 -p 55450 -U postgres -d tkg_pda_dev -t -A -c "SELECT active_navigation_id::text FROM ops.machine_runtime_state WHERE machine_id = '$machineId';").Trim()

try {
    # Pre-test cleanup on SH-01-A
    & $psql -h 127.0.0.1 -p 55450 -U postgres -d tkg_pda_dev -c @"
DELETE FROM integration.profile_shared_endpoint_bindings WHERE navigation_profile_id = '$navId';
DELETE FROM ops.store_events WHERE store_unit_id IN (SELECT id FROM ops.store_units WHERE transport_request_id IN (SELECT id FROM ops.transport_requests WHERE machine_id = '$machineId'));
DELETE FROM ops.store_units WHERE transport_request_id IN (SELECT id FROM ops.transport_requests WHERE machine_id = '$machineId');
DELETE FROM ops.preparation_events WHERE preparation_pallet_id IN (SELECT id FROM ops.preparation_pallets WHERE transport_request_id IN (SELECT id FROM ops.transport_requests WHERE machine_id = '$machineId'));
DELETE FROM ops.preparation_pallets WHERE transport_request_id IN (SELECT id FROM ops.transport_requests WHERE machine_id = '$machineId');
DELETE FROM ops.production_end_run_items WHERE run_id IN (SELECT id FROM ops.production_end_runs WHERE machine_id = '$machineId');
DELETE FROM ops.production_end_runs WHERE machine_id = '$machineId';
DELETE FROM ops.transport_request_dependencies WHERE predecessor_request_id IN (SELECT id FROM ops.transport_requests WHERE machine_id = '$machineId') OR successor_request_id IN (SELECT id FROM ops.transport_requests WHERE machine_id = '$machineId');
DELETE FROM ops.transport_requests WHERE machine_id = '$machineId';
DELETE FROM ops.production_sessions WHERE machine_id = '$machineId';
UPDATE ops.slot_runtime_state SET occupancy_state = 'VACANT', load_state = 'UNKNOWN', current_part_id = NULL, current_container_type_id = NULL
WHERE layout_slot_id = '$slotId';
UPDATE audit.client_devices SET machine_id = '$machineId' WHERE device_code = 'DEV-LINE';
UPDATE ops.machine_runtime_state SET active_navigation_id = '$navId' WHERE machine_id = '$machineId';
"@ | Out-Null

    # Temporarily set navigation profile to PUBLISHED and install trial shared endpoint bindings for EMPTY_RETURN and NON_FINISHED_RETURN
    & $psql -h 127.0.0.1 -p 55450 -U postgres -d tkg_pda_dev -c @"
UPDATE master.navigation_profiles SET status = 'PUBLISHED' WHERE id = '$navId';

INSERT INTO integration.rds_worksites (navigation_profile_id, rds_site_id, rds_type, last_sync_at, last_disabled, last_sync_failed, raw_state_json)
SELECT '$navId', planned_rds_site_id, 1, now(), 0, 0, '{}'::jsonb
FROM integration.rds_worksite_plans
WHERE navigation_profile_id = '$navId' AND planned_rds_site_id IN ('TKG_SH01A_V01_WS_RETURN01', 'TKG_SH01A_V01_WS_EMPTY01')
ON CONFLICT (navigation_profile_id, rds_site_id) DO UPDATE SET last_sync_at = now(), last_disabled = 0, last_sync_failed = 0;

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
    Write-Host "Temporarily set navigation profile $navId to PUBLISHED with trial shared endpoints (EMPTY-01, RETURN-01)."

    # Start production session on SH-01-A
    $startPayload = @{
        variantId      = $variantId
        productGroupId = $groupId
        mainPartId     = $mainPartId
    } | ConvertTo-Json
    $startHeaders = @{
        'Authorization'   = "Bearer $token"
        'X-Device-Code'   = 'DEV-LINE'
        'Idempotency-Key' = "gap3-start-$([guid]::NewGuid().ToString('N'))"
    }
    $sessionResp = Invoke-RestMethod -Method Post -Uri "$apiBase/api/v1/machines/$machineId/production-sessions" -Headers $startHeaders -ContentType 'application/json' -Body $startPayload
    $sessionId = $sessionResp.session.id
    Write-Host "Started production session: $sessionId"

    Write-Host "`n--- Cycle 1: OCCUPIED EMPTY Initial State -> REMOVE_EMPTY Predecessor + BRING_FULL Successor ---" -ForegroundColor Yellow

    # Set SLOT-003 to OCCUPIED EMPTY with ALT Part (operator emptied the container)
    & $psql -h 127.0.0.1 -p 55450 -U postgres -d tkg_pda_dev -c @"
UPDATE ops.slot_runtime_state
SET occupancy_state = 'OCCUPIED',
    load_state = 'EMPTY',
    current_part_id = '$altPartId',
    current_container_type_id = '$containerId',
    row_version = row_version + 1,
    updated_at = now()
WHERE layout_slot_id = '$slotId';
"@ | Out-Null

    Write-Host "Position $slotCode initialized to OCCUPIED EMPTY with part $altPartCode."

    # Request ALT Part demand
    $demandKey1 = "gap3-demand-empty-$([guid]::NewGuid().ToString('N'))"
    $demandPayload1 = @{
        partId = $altPartId
    } | ConvertTo-Json
    $demandHeaders1 = @{
        'Authorization'   = "Bearer $token"
        'X-Device-Code'   = 'DEV-LINE'
        'Idempotency-Key' = $demandKey1
    }

    $demandResp1 = Invoke-RestMethod -Method Post -Uri "$apiBase/api/v1/machines/$machineId/part-demands" -Headers $demandHeaders1 -ContentType 'application/json' -Body $demandPayload1
    Write-Host "ALT Demand Response 1: TaskType=$($demandResp1.taskType), PredecessorOp=$($demandResp1.predecessorTaskType), PredecessorReqNo=$($demandResp1.predecessorRequestNo), SuccessorReqNo=$($demandResp1.requestNo)"

    # Assertions for Cycle 1
    if ($demandResp1.taskType -eq 'BRING_FULL' -and $demandResp1.predecessorTaskType -eq 'REMOVE_EMPTY' -and $demandResp1.predecessorRequestNo -ne $null) {
        Write-Host "PASS: Predecessor operation is REMOVE_EMPTY, Successor operation is BRING_FULL." -ForegroundColor Green
    } else {
        Write-Host "FAIL: Predecessor / Successor operations incorrect!" -ForegroundColor Red
        exit 1
    }

    # Verify transport_request_dependencies row
    $depSql = @"
SELECT
    pred.request_no as pred_no,
    succ.request_no as succ_no,
    pred.id::text as pred_id,
    succ.id::text as succ_id
FROM ops.transport_request_dependencies d
JOIN ops.transport_requests pred ON pred.id = d.predecessor_request_id
JOIN ops.transport_requests succ ON succ.id = d.successor_request_id
WHERE succ.request_no = $($demandResp1.requestNo);
"@
    $depRow = (& $psql -h 127.0.0.1 -p 55450 -U postgres -d tkg_pda_dev -t -A -F '|' -c $depSql).Trim().Split('|')
    $predReqNo = $depRow[0]
    $succReqNo = $depRow[1]
    $predId    = $depRow[2]
    $succId    = $depRow[3]

    if ($predReqNo -eq "$($demandResp1.predecessorRequestNo)" -and $succReqNo -eq "$($demandResp1.requestNo)") {
        Write-Host "PASS: ops.transport_request_dependencies contains predecessor ($predReqNo) -> successor ($succReqNo) link." -ForegroundColor Green
    } else {
        Write-Host "FAIL: Dependency link missing or invalid in database!" -ForegroundColor Red
        exit 1
    }

    # Verify successor CANNOT advance before predecessor completes
    # In ListQueuedRequestIdsAsync: successor is filtered out if predecessor status <> 'COMPLETED'
    $queueBlockSql = @"
SELECT count(*)
FROM ops.transport_requests
WHERE id = '$succId'
  AND status = 'QUEUED'
  AND NOT EXISTS (
      SELECT 1
      FROM ops.transport_request_dependencies d
      JOIN ops.transport_requests pred ON pred.id = d.predecessor_request_id
      WHERE d.successor_request_id = '$succId'
        AND pred.status <> 'COMPLETED');
"@
    $eligibleBefore = (& $psql -h 127.0.0.1 -p 55450 -U postgres -d tkg_pda_dev -t -A -c $queueBlockSql).Trim()
    if ($eligibleBefore -eq '0') {
        Write-Host "PASS: Successor is blocked from dispatch queue progression while predecessor status <> 'COMPLETED'." -ForegroundColor Green
    } else {
        Write-Host "FAIL: Successor was eligible before predecessor completed!" -ForegroundColor Red
        exit 1
    }

    # Verify duplicate active demand is rejected (no duplicate uncontrolled tasks)
    $dupKey = "gap3-dup-$([guid]::NewGuid().ToString('N'))"
    $dupHeaders = @{
        'Authorization'   = "Bearer $token"
        'X-Device-Code'   = 'DEV-LINE'
        'Idempotency-Key' = $dupKey
    }
    try {
        $dupResp = Invoke-RestMethod -Method Post -Uri "$apiBase/api/v1/machines/$machineId/part-demands" -Headers $dupHeaders -ContentType 'application/json' -Body $demandPayload1
        Write-Host "UNEXPECTED: Duplicate active demand succeeded!" -ForegroundColor Red
        exit 1
    } catch {
        $errObj = $_.ErrorDetails.Message | ConvertFrom-Json
        Write-Host "PASS: Duplicate uncontrolled demand blocked: $($errObj.code) - $($errObj.detail)" -ForegroundColor Green
    }

    # Now complete the predecessor (simulating robot completing REMOVE_EMPTY)
    & $psql -h 127.0.0.1 -p 55450 -U postgres -d tkg_pda_dev -c @"
UPDATE ops.transport_requests
SET status = 'COMPLETED', completed_at = now()
WHERE id = '$predId';
"@ | Out-Null
    Write-Host "Predecessor request #$predReqNo marked COMPLETED."

    # Verify successor is now eligible in queue dependency check
    $eligibleAfter = (& $psql -h 127.0.0.1 -p 55450 -U postgres -d tkg_pda_dev -t -A -c $queueBlockSql).Trim()
    if ($eligibleAfter -eq '1') {
        Write-Host "PASS: Successor dependency satisfied after predecessor completion!" -ForegroundColor Green
    } else {
        Write-Host "FAIL: Successor not unblocked after predecessor completed!" -ForegroundColor Red
        exit 1
    }

    # Deliver successor (simulating material delivery: mark completed and slot becomes OCCUPIED FULL)
    & $psql -h 127.0.0.1 -p 55450 -U postgres -d tkg_pda_dev -c @"
UPDATE ops.transport_requests
SET status = 'COMPLETED', completed_at = now()
WHERE id = '$succId';

UPDATE ops.slot_runtime_state
SET occupancy_state = 'OCCUPIED',
    load_state = 'FULL',
    current_part_id = '$altPartId',
    current_container_type_id = '$containerId',
    row_version = row_version + 1,
    updated_at = now()
WHERE layout_slot_id = '$slotId';
"@ | Out-Null
    Write-Host "Successor completed and slot $slotCode is now OCCUPIED FULL."

    Write-Host "`n--- Cycle 2: Repeat ALT Demand on OCCUPIED FULL State -> REMOVE_FULL Predecessor + Propagation ---" -ForegroundColor Yellow

    # Issue repeat ALT demand when slot is OCCUPIED FULL
    $demandKey2 = "gap3-demand-full-$([guid]::NewGuid().ToString('N'))"
    $demandHeaders2 = @{
        'Authorization'   = "Bearer $token"
        'X-Device-Code'   = 'DEV-LINE'
        'Idempotency-Key' = $demandKey2
    }
    $demandResp2 = Invoke-RestMethod -Method Post -Uri "$apiBase/api/v1/machines/$machineId/part-demands" -Headers $demandHeaders2 -ContentType 'application/json' -Body $demandPayload1
    Write-Host "Repeat ALT Demand Response: TaskType=$($demandResp2.taskType), PredecessorOp=$($demandResp2.predecessorTaskType), PredecessorReqNo=$($demandResp2.predecessorRequestNo), SuccessorReqNo=$($demandResp2.requestNo)"

    if ($demandResp2.taskType -eq 'BRING_FULL' -and $demandResp2.predecessorTaskType -eq 'REMOVE_FULL') {
        Write-Host "PASS: Repeat demand on OCCUPIED FULL generated Predecessor=REMOVE_FULL, Successor=BRING_FULL." -ForegroundColor Green
    } else {
        Write-Host "FAIL: Repeat demand operations incorrect!" -ForegroundColor Red
        exit 1
    }

    # Fetch IDs for cycle 2
    $depRow2 = (& $psql -h 127.0.0.1 -p 55450 -U postgres -d tkg_pda_dev -t -A -F '|' -c @"
SELECT
    pred.request_no as pred_no,
    succ.request_no as succ_no,
    pred.id::text as pred_id,
    succ.id::text as succ_id
FROM ops.transport_request_dependencies d
JOIN ops.transport_requests pred ON pred.id = d.predecessor_request_id
JOIN ops.transport_requests succ ON succ.id = d.successor_request_id
WHERE succ.request_no = $($demandResp2.requestNo);
"@).Trim().Split('|')
    $predId2 = $depRow2[2]
    $succId2 = $depRow2[3]

    # Test Predecessor Cancellation Propagation
    Write-Host "Cancelling predecessor request #$($depRow2[0])..."
    $cancelHeaders = @{
        'Authorization' = "Bearer $token"
        'X-Device-Code' = 'DEV-LINE'
    }
    $cancelResp = Invoke-RestMethod -Method Post -Uri "$apiBase/api/v1/machines/$machineId/transport-requests/$predId2/cancel" -Headers $cancelHeaders -ContentType 'application/json' -Body '{}'
    Write-Host "Predecessor cancelled."

    # Assert that successor is automatically cancelled via domain dependency rule
    $succStatus = (& $psql -h 127.0.0.1 -p 55450 -U postgres -d tkg_pda_dev -t -A -c @"
SELECT status FROM ops.transport_requests WHERE id = '$succId2';
"@).Trim()

    $succMessage = (& $psql -h 127.0.0.1 -p 55450 -U postgres -d tkg_pda_dev -t -A -c @"
SELECT message FROM ops.transport_request_events WHERE transport_request_id = '$succId2' AND event_type = 'REQUEST_CANCELLED' ORDER BY created_at DESC LIMIT 1;
"@).Trim()

    if ($succStatus -eq 'CANCELLED' -and $succMessage -like "*nceki palet kald*") {
        Write-Host "PASS: Predecessor cancellation propagated: Successor automatically CANCELLED with domain reason: '$succMessage'." -ForegroundColor Green
    } else {
        Write-Host "FAIL: Successor was not cancelled properly! Status=$succStatus, Message=$succMessage" -ForegroundColor Red
        exit 1
    }

    # Verify slot state remained safe and unchanged (OCCUPIED FULL)
    $slotAfterCancel = (& $psql -h 127.0.0.1 -p 55450 -U postgres -d tkg_pda_dev -t -A -F '|' -c @"
SELECT occupancy_state, load_state, current_part_id::text FROM ops.slot_runtime_state WHERE layout_slot_id = '$slotId';
"@).Trim().Split('|')
    if ($slotAfterCancel[0] -eq 'OCCUPIED' -and $slotAfterCancel[1] -eq 'FULL' -and $slotAfterCancel[2] -eq $altPartId) {
        Write-Host "PASS: Slot runtime state safely preserved as OCCUPIED FULL following cancellation propagation." -ForegroundColor Green
    } else {
        Write-Host "FAIL: Slot state corrupted after cancellation!" -ForegroundColor Red
        exit 1
    }

    Write-Host "`n--- Part 3: Missing-Container Relations Remain Blocked (Customer Master Data Rule) ---" -ForegroundColor Yellow

    # Query total missing-container relations count
    $missingContainerCount = (& $psql -h 127.0.0.1 -p 55450 -U postgres -d tkg_pda_dev -t -A -c @"
SELECT count(*) FROM master.product_group_parts WHERE relation_type = 'ALT_PART' AND container_type_id IS NULL;
"@).Trim()

    Write-Host "Total missing-container ALT relations in customer master data: $missingContainerCount (baseline: 25)"
    if ($missingContainerCount -ne '25') {
        Write-Host "FAIL: Expected exactly 25 missing-container relations, found $missingContainerCount" -ForegroundColor Red
        exit 1
    }

    # Find machine SH-01-P1--SH-01-P2 active variant which has missing-container ALT part 81163-C7000
    $missingQuery = @"
SELECT
    m.id::text as m_id,
    v.id::text as v_id,
    pg.id::text as pg_id,
    pg.main_part_id::text as main_id,
    p_alt.id::text as alt_id,
    p_alt.part_code as alt_code,
    np.id::text as nav_id,
    np.status as nav_status
FROM master.machines m
JOIN ops.machine_runtime_state mrs ON mrs.machine_id = m.id
JOIN master.variants v ON v.id = mrs.active_variant_id
JOIN master.product_groups pg ON pg.variant_id = v.id
JOIN master.product_group_parts pgp ON pgp.product_group_id = pg.id AND pgp.relation_type = 'ALT_PART' AND pgp.container_type_id IS NULL
JOIN master.parts p_alt ON p_alt.id = pgp.part_id
JOIN master.variant_navigation_profile_assignments vnpa ON vnpa.variant_id = v.id
JOIN master.navigation_profiles np ON np.id = vnpa.navigation_profile_id
WHERE m.code = 'SH-01-P1--SH-01-P2'
LIMIT 1;
"@
    $mRow = (& $psql -h 127.0.0.1 -p 55450 -U postgres -d tkg_pda_dev -t -A -F '|' -c $missingQuery).Trim().Split('|')
    $mMachId  = $mRow[0]
    $mVarId   = $mRow[1]
    $mGroupId = $mRow[2]
    $mMainId  = $mRow[3]
    $mAltId   = $mRow[4]
    $mAltCode = $mRow[5]
    $mNavId   = $mRow[6]
    $mOrigNav = $mRow[7]

    Write-Host "Found machine SH-01-P1--SH-01-P2 with missing container ALT part: $mAltCode ($mAltId)"

    # Temporarily bind DEV-LINE to SH-01-P1--SH-01-P2 for machine scoped API checks
    & $psql -h 127.0.0.1 -p 55450 -U postgres -d tkg_pda_dev -c @"
UPDATE audit.client_devices SET machine_id = '$mMachId' WHERE device_code = 'DEV-LINE';
"@ | Out-Null

    # 1. Verify GET /machines/{id}/part-options reports requestable=false and CONTAINER_TYPE_MISSING in blockingReasons
    $mainPartsResp = Invoke-RestMethod -Method Get -Uri "$apiBase/api/v1/machines/$mMachId/part-options" -Headers $headers
    $foundAlt = $null
    foreach ($mp in $mainPartsResp) {
        foreach ($rp in $mp.relatedParts) {
            if ($rp.partCode -eq $mAltCode) {
                $foundAlt = $rp
                break
            }
        }
        if ($foundAlt) { break }
    }

    if ($foundAlt -and -not $foundAlt.requestable -and $foundAlt.blockers -contains 'CONTAINER_TYPE_MISSING') {
        Write-Host "PASS: Machine part-options API reports ALT part $mAltCode as non-requestable with blocker CONTAINER_TYPE_MISSING." -ForegroundColor Green
    } else {
        Write-Host "FAIL: Machine part-options API did not report CONTAINER_TYPE_MISSING for part $mAltCode! (foundAlt: $($foundAlt | ConvertTo-Json -Compress))" -ForegroundColor Red
        exit 1
    }

    # 2. Verify runtime demand rejection: publish nav profile, start session, demand ALT part
    & $psql -h 127.0.0.1 -p 55450 -U postgres -d tkg_pda_dev -c @"
UPDATE master.navigation_profiles SET status = 'PUBLISHED' WHERE id = '$mNavId';
DELETE FROM ops.production_end_run_items WHERE run_id IN (SELECT id FROM ops.production_end_runs WHERE machine_id = '$mMachId');
DELETE FROM ops.production_end_runs WHERE machine_id = '$mMachId';
DELETE FROM ops.production_sessions WHERE machine_id = '$mMachId';
UPDATE ops.machine_runtime_state SET active_navigation_id = '$mNavId' WHERE machine_id = '$mMachId';
"@ | Out-Null

    $mStartPayload = @{
        variantId      = $mVarId
        productGroupId = $mGroupId
        mainPartId     = $mMainId
    } | ConvertTo-Json
    $mStartHeaders = @{
        'Authorization'   = "Bearer $token"
        'X-Device-Code'   = 'DEV-LINE'
        'Idempotency-Key' = "gap3-missing-start-$([guid]::NewGuid().ToString('N'))"
    }
    $mSessionResp = Invoke-RestMethod -Method Post -Uri "$apiBase/api/v1/machines/$mMachId/production-sessions" -Headers $mStartHeaders -ContentType 'application/json' -Body $mStartPayload
    Write-Host "Started production session on SH-01-P1--SH-01-P2 for missing container test: $($mSessionResp.session.id)"

    $mDemandHeaders = @{
        'Authorization'   = "Bearer $token"
        'X-Device-Code'   = 'DEV-LINE'
        'Idempotency-Key' = "gap3-missing-demand-$([guid]::NewGuid().ToString('N'))"
    }
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

    # Cleanup SH-01-P1--SH-01-P2
    & $psql -h 127.0.0.1 -p 55450 -U postgres -d tkg_pda_dev -c @"
DELETE FROM ops.production_end_run_items WHERE run_id IN (SELECT id FROM ops.production_end_runs WHERE machine_id = '$mMachId');
DELETE FROM ops.production_end_runs WHERE machine_id = '$mMachId';
DELETE FROM ops.production_sessions WHERE machine_id = '$mMachId';
UPDATE ops.machine_runtime_state SET active_navigation_id = NULL WHERE machine_id = '$mMachId';
UPDATE master.navigation_profiles SET status = '$mOrigNav' WHERE id = '$mNavId';
UPDATE audit.client_devices SET machine_id = '$machineId' WHERE device_code = 'DEV-LINE';
"@ | Out-Null
    Write-Host "Cleaned up SH-01-P1--SH-01-P2 test session and restored navigation profile status."

} finally {
    # Restoration and cleanup
    Write-Host "`nRestoring navigation profile and test state..."
    & $psql -h 127.0.0.1 -p 55450 -U postgres -d tkg_pda_dev -c @"
UPDATE master.navigation_profiles SET status = '$origNavStatus' WHERE id = '$navId';
DELETE FROM integration.profile_shared_endpoint_bindings WHERE navigation_profile_id = '$navId';
DELETE FROM ops.store_events WHERE store_unit_id IN (SELECT id FROM ops.store_units WHERE transport_request_id IN (SELECT id FROM ops.transport_requests WHERE machine_id = '$machineId'));
DELETE FROM ops.store_units WHERE transport_request_id IN (SELECT id FROM ops.transport_requests WHERE machine_id = '$machineId');
DELETE FROM ops.preparation_events WHERE preparation_pallet_id IN (SELECT id FROM ops.preparation_pallets WHERE transport_request_id IN (SELECT id FROM ops.transport_requests WHERE machine_id = '$machineId'));
DELETE FROM ops.preparation_pallets WHERE transport_request_id IN (SELECT id FROM ops.transport_requests WHERE machine_id = '$machineId');
DELETE FROM ops.production_end_run_items WHERE run_id IN (SELECT id FROM ops.production_end_runs WHERE machine_id = '$machineId');
DELETE FROM ops.production_end_runs WHERE machine_id = '$machineId';
DELETE FROM ops.transport_request_dependencies WHERE predecessor_request_id IN (SELECT id FROM ops.transport_requests WHERE machine_id = '$machineId') OR successor_request_id IN (SELECT id FROM ops.transport_requests WHERE machine_id = '$machineId');
DELETE FROM ops.transport_requests WHERE machine_id = '$machineId';
DELETE FROM ops.production_sessions WHERE machine_id = '$machineId';
UPDATE ops.slot_runtime_state SET occupancy_state = 'VACANT', load_state = 'UNKNOWN', current_part_id = NULL, current_container_type_id = NULL
WHERE layout_slot_id = '$slotId';
UPDATE audit.client_devices SET machine_id = '$machineId' WHERE device_code = 'DEV-LINE';
UPDATE ops.machine_runtime_state SET active_navigation_id = CASE WHEN '$origNavId' = '' THEN NULL ELSE '$origNavId'::uuid END WHERE machine_id = '$machineId';
"@ | Out-Null
}

Write-Host "`nRESULT: PASS (Repeated ALT demand & dependency chain verified)" -ForegroundColor Green
exit 0
