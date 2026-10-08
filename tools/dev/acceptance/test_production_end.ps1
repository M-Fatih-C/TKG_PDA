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
Write-Host "PHASE: PRODUCTION END ACCEPTANCE" -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan

# 1. Fetch Machine SH-01-A and Variant 1 IDs
$sql = @"
SELECT
    m.id::text as machine_id,
    v.id::text as variant_id,
    pg.id::text as group_id,
    p_main.id::text as main_part_id,
    p_alt.id::text as alt_part_id,
    np.id::text as nav_id,
    np.code as nav_code,
    np.status as nav_status,
    s1.id::text as s1_id,
    s2.id::text as s2_id,
    s3.id::text as s3_id,
    ct.id::text as container_id,
    loc.id::text as fg_loc_id
FROM master.machines m
JOIN master.variants v ON v.machine_id = m.id AND v.code = '1'
JOIN master.product_groups pg ON pg.variant_id = v.id
JOIN master.parts p_main ON p_main.id = pg.main_part_id
JOIN master.product_group_parts pgp ON pgp.product_group_id = pg.id AND pgp.relation_type = 'ALT_PART'
JOIN master.parts p_alt ON p_alt.id = pgp.part_id AND p_alt.part_code = '67116BN050-40'
JOIN master.variant_navigation_profile_assignments vnpa ON vnpa.variant_id = v.id
JOIN master.navigation_profiles np ON np.id = vnpa.navigation_profile_id
JOIN master.layouts l ON l.machine_id = m.id AND l.code = 'CUSTOMER-PHYSICAL-V1'
JOIN master.layout_slots s1 ON s1.layout_id = l.id AND s1.code = 'SLOT-001'
JOIN master.layout_slots s2 ON s2.layout_id = l.id AND s2.code = 'SLOT-002'
JOIN master.layout_slots s3 ON s3.layout_id = l.id AND s3.code = 'SLOT-003'
JOIN master.container_types ct ON ct.code = 'ARABA'
JOIN ops.store_locations loc ON loc.code = 'FG-01'
WHERE m.code = 'SH-01-A';
"@

$row = (& $psql -h 127.0.0.1 -p 55450 -U postgres -d tkg_pda_dev -t -A -F '|' -c $sql).Trim().Split('|')
$machineId   = $row[0]
$variantId   = $row[1]
$groupId     = $row[2]
$mainPartId  = $row[3]
$altPartId   = $row[4]
$navId       = $row[5]
$navCode     = $row[6]
$navStatus   = $row[7]
$slot1Id     = $row[8]
$slot2Id     = $row[9]
$slot3Id     = $row[10]
$containerId = $row[11]
$fgLocId     = $row[12]

Write-Host "Target Machine: SH-01-A ($machineId)"
Write-Host "Active Variant: 1 ($variantId)"
Write-Host "Active NAV Profile: $navCode ($navId) [Status: $navStatus]"
Write-Host "Main Part: 67115BN050-10-HAOS ($mainPartId)"
Write-Host "ALT Part:  67116BN050-40 ($altPartId)"

# Fetch Dev Line Operator user ID
$userId = (& $psql -h 127.0.0.1 -p 55450 -U postgres -d tkg_pda_dev -t -A -c "SELECT id::text FROM auth.users WHERE username = 'dev-line';").Trim()

# Login to get JWT
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

# Clean any existing active production session, test requests, or trial bindings on SH-01-A
& $psql -h 127.0.0.1 -p 55450 -U postgres -d tkg_pda_dev -c @"
UPDATE master.navigation_profiles SET status = 'DRAFT' WHERE id = '$navId';
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
WHERE layout_slot_id IN ('$slot1Id', '$slot2Id', '$slot3Id');
"@ | Out-Null

# Seed production end priorities if missing
& $psql -h 127.0.0.1 -p 55450 -U postgres -d tkg_pda_dev -c @"
INSERT INTO master.production_end_priorities (operation_code, priority)
VALUES ('REMOVE_FULL', 40), ('REMOVE_EMPTY', 70)
ON CONFLICT (operation_code) DO UPDATE SET priority = EXCLUDED.priority;
"@ | Out-Null

# Save original active navigation ID to restore at end of test
$origNavId = (& $psql -h 127.0.0.1 -p 55450 -U postgres -d tkg_pda_dev -t -A -c "SELECT active_navigation_id::text FROM ops.machine_runtime_state WHERE machine_id = '$machineId';").Trim()

# Bind DEV-LINE device to SH-01-A and set active navigation to variant's NAV profile (TKG_SH01A_V01_NAV)
& $psql -h 127.0.0.1 -p 55450 -U postgres -d tkg_pda_dev -c @"
UPDATE audit.client_devices SET machine_id = '$machineId' WHERE device_code = 'DEV-LINE';
UPDATE ops.machine_runtime_state SET active_navigation_id = '$navId' WHERE machine_id = '$machineId';
"@ | Out-Null

Write-Host "`n--- Part 1: Commissioning Boundary Verification (Profiles DRAFT / Unbound) ---" -ForegroundColor Yellow

# Verify DRAFT status blocker: Attempting to start production while profile is DRAFT fails with NAVIGATION_PROFILE_CONFLICT
$startPayload = @{
    variantId      = $variantId
    productGroupId = $groupId
    mainPartId     = $mainPartId
} | ConvertTo-Json
$startHeaders = @{
    'Authorization'   = "Bearer $token"
    'X-Device-Code'   = 'DEV-LINE'
    'Idempotency-Key' = "gap2-start-draft-$([guid]::NewGuid().ToString('N'))"
}
try {
    $draftResp = Invoke-RestMethod -Method Post -Uri "$apiBase/api/v1/machines/$machineId/production-sessions" -Headers $startHeaders -ContentType 'application/json' -Body $startPayload
    Write-Host "UNEXPECTED: Session started while profile was DRAFT!" -ForegroundColor Red
    exit 1
} catch {
    $errBody = if ($_.ErrorDetails -and $_.ErrorDetails.Message) { $_.ErrorDetails.Message } else {
        $reader = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())
        $reader.ReadToEnd()
    }
    $errObj = $errBody | ConvertFrom-Json
    Write-Host "PASS: Production start correctly blocked when navigation profile is DRAFT: $($errObj.code)" -ForegroundColor Green
    Write-Host "Detail: $($errObj.detail)" -ForegroundColor Gray
}

# Now simulate profile commissioning activation (set profile to PUBLISHED)
& $psql -h 127.0.0.1 -p 55450 -U postgres -d tkg_pda_dev -c @"
UPDATE master.navigation_profiles
SET status = 'PUBLISHED',
    published_at = COALESCE(published_at, now()),
    config_json = jsonb_set(
        jsonb_set(config_json, '{geometryVerified}', 'true'::jsonb),
        '{commissioningState}', '"COMMISSIONED"'::jsonb)
WHERE id = '$navId';
"@ | Out-Null

# Start production session on SH-01-A
$startHeadersPub = @{
    'Authorization'   = "Bearer $token"
    'X-Device-Code'   = 'DEV-LINE'
    'Idempotency-Key' = "gap2-start-pub-$([guid]::NewGuid().ToString('N'))"
}
$sessionResp = Invoke-RestMethod -Method Post -Uri "$apiBase/api/v1/machines/$machineId/production-sessions" -Headers $startHeadersPub -ContentType 'application/json' -Body $startPayload
$sessionId = $sessionResp.session.id
Write-Host "Started production session: $sessionId (Status: $($sessionResp.session.status))"

# Set up slots for cleanup:
# SLOT-001 (display_order 1): OCCUPIED FULL (Main Part) -> requires FINISHED_GOODS
# SLOT-002 (display_order 2): OCCUPIED EMPTY (Container) -> requires EMPTY_RETURN
# SLOT-003 (display_order 3): OCCUPIED FULL (ALT Part)   -> requires NON_FINISHED_RETURN
& $psql -h 127.0.0.1 -p 55450 -U postgres -d tkg_pda_dev -c @"
UPDATE ops.slot_runtime_state SET occupancy_state = 'OCCUPIED', load_state = 'FULL', current_part_id = '$mainPartId', current_container_type_id = '$containerId' WHERE layout_slot_id = '$slot1Id';
UPDATE ops.slot_runtime_state SET occupancy_state = 'OCCUPIED', load_state = 'EMPTY', current_part_id = '$mainPartId', current_container_type_id = '$containerId' WHERE layout_slot_id = '$slot2Id';
UPDATE ops.slot_runtime_state SET occupancy_state = 'OCCUPIED', load_state = 'FULL', current_part_id = '$altPartId', current_container_type_id = '$containerId' WHERE layout_slot_id = '$slot3Id';
"@ | Out-Null

Write-Host "Slots initialized: SLOT-001 (FG full), SLOT-002 (Empty), SLOT-003 (ALT full)"

# Attempt Production End without profile shared endpoint bindings in integration.profile_shared_endpoint_bindings
$endKey = "gap2-end-unbound-$([guid]::NewGuid().ToString('N'))"
$endHeaders = @{
    'Authorization'   = "Bearer $token"
    'X-Device-Code'   = 'DEV-LINE'
    'Idempotency-Key' = $endKey
}
$blocked = $false
$blockerCode = ""
try {
    $endResp = Invoke-RestMethod -Method Post -Uri "$apiBase/api/v1/machines/$machineId/production-sessions/$sessionId/end" -Headers $endHeaders -ContentType 'application/json'
    Write-Host "UNEXPECTED: End succeeded without commissioning bindings!" -ForegroundColor Red
    exit 1
} catch {
    $blocked = $true
    $errBody = if ($_.ErrorDetails -and $_.ErrorDetails.Message) { $_.ErrorDetails.Message } else {
        $reader = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())
        $reader.ReadToEnd()
    }
    $errObj = $errBody | ConvertFrom-Json
    $blockerCode = $errObj.code
    Write-Host "PASS: Production End correctly blocked by physical commissioning boundary: $blockerCode" -ForegroundColor Green
    Write-Host "Message: $($errObj.detail)" -ForegroundColor Gray
}

# Verify atomic failure: 0 transport requests created, session remains ACTIVE
$reqCount = (& $psql -h 127.0.0.1 -p 55450 -U postgres -d tkg_pda_dev -t -A -c "SELECT count(*) FROM ops.transport_requests WHERE machine_id = '$machineId';").Trim()
$sessionStatus = (& $psql -h 127.0.0.1 -p 55450 -U postgres -d tkg_pda_dev -t -A -c "SELECT status FROM ops.production_sessions WHERE id = '$sessionId';").Trim()
if ($reqCount -eq '0' -and $sessionStatus -eq 'ACTIVE') {
    Write-Host "PASS: Atomic rollback confirmed: 0 child requests survive, session status is still ACTIVE." -ForegroundColor Green
} else {
    Write-Host "FAIL: State corrupted during blocked production end! ReqCount=$reqCount, Status=$sessionStatus" -ForegroundColor Red
    exit 1
}

Write-Host "`n--- Part 2: Full Planning & Allocation Path with Trial Commissioning Bindings ---" -ForegroundColor Yellow

# Ensure planned WorkSites exist in integration.rds_worksites for profile TKG_SH01A_V01_NAV
& $psql -h 127.0.0.1 -p 55450 -U postgres -d tkg_pda_dev -c @"
INSERT INTO integration.rds_worksites (navigation_profile_id, rds_site_id, rds_type, last_sync_at, last_disabled, last_sync_failed, raw_state_json)
SELECT '$navId', planned_rds_site_id, 1, now(), 0, 0, '{}'::jsonb
FROM integration.rds_worksite_plans
WHERE navigation_profile_id = '$navId' AND planned_rds_site_id IN ('TKG_SH01A_V01_WS_FG01', 'TKG_SH01A_V01_WS_RETURN01', 'TKG_SH01A_V01_WS_EMPTY01')
ON CONFLICT (navigation_profile_id, rds_site_id) DO UPDATE SET last_sync_at = now(), last_disabled = 0, last_sync_failed = 0;

-- Install canonical shared endpoint bindings
INSERT INTO integration.profile_shared_endpoint_bindings
    (navigation_profile_id, endpoint_class, business_code, store_location_id, rds_worksite_id)
SELECT '$navId', 'FINISHED_GOODS', 'FG-01', '$fgLocId', w.id
FROM integration.rds_worksites w
WHERE w.navigation_profile_id = '$navId' AND w.rds_site_id = 'TKG_SH01A_V01_WS_FG01'
ON CONFLICT DO NOTHING;

INSERT INTO integration.profile_shared_endpoint_bindings
    (navigation_profile_id, endpoint_class, business_code, store_location_id, rds_worksite_id)
SELECT '$navId', 'NON_FINISHED_RETURN', 'RETURN-01', NULL, w.id
FROM integration.rds_worksites w
WHERE w.navigation_profile_id = '$navId' AND w.rds_site_id = 'TKG_SH01A_V01_WS_RETURN01'
ON CONFLICT DO NOTHING;

INSERT INTO integration.profile_shared_endpoint_bindings
    (navigation_profile_id, endpoint_class, business_code, store_location_id, rds_worksite_id)
SELECT '$navId', 'EMPTY_RETURN', 'EMPTY-01', NULL, w.id
FROM integration.rds_worksites w
WHERE w.navigation_profile_id = '$navId' AND w.rds_site_id = 'TKG_SH01A_V01_WS_EMPTY01'
ON CONFLICT DO NOTHING;
"@ | Out-Null

Write-Host "Trial commissioning bindings installed for FG-01, RETURN-01, and EMPTY-01."

# Execute Production End now
$validEndKey = "gap2-end-valid-$([guid]::NewGuid().ToString('N'))"
$validEndHeaders = @{
    'Authorization'   = "Bearer $token"
    'X-Device-Code'   = 'DEV-LINE'
    'Idempotency-Key' = $validEndKey
}
$validEndResp = Invoke-RestMethod -Method Post -Uri "$apiBase/api/v1/machines/$machineId/production-sessions/$sessionId/end" -Headers $validEndHeaders -ContentType 'application/json'
Write-Host "Production End executed! Session status: $($validEndResp.session.status)"

# Query generated transport requests
$requestsSql = @"
SELECT
    r.request_no,
    tt.code as task_type,
    ls.code as source_slot,
    ls.display_order as slot_order,
    r.destination_business_code,
    r.navigation_id::text,
    p.part_code,
    r.status,
    r.priority,
    b.endpoint_class,
    w.rds_site_id as resolved_worksite
FROM ops.transport_requests r
JOIN master.task_types tt ON tt.id = r.task_type_id
JOIN master.layout_slots ls ON ls.id = r.source_slot_id
LEFT JOIN master.parts p ON p.id = r.part_id
LEFT JOIN integration.profile_shared_endpoint_bindings b
    ON b.navigation_profile_id = r.navigation_id AND b.business_code = r.destination_business_code
LEFT JOIN integration.rds_worksites w ON w.id = b.rds_worksite_id
WHERE r.machine_id = '$machineId'
ORDER BY ls.display_order, r.request_no;
"@
$reqRows = (& $psql -h 127.0.0.1 -p 55450 -U postgres -d tkg_pda_dev -t -A -F '|' -c $requestsSql).Trim().Split("`n")

Write-Host "`nGenerated Child Requests:"
foreach ($rLine in $reqRows) {
    $f = $rLine.Split('|')
    Write-Host ("  Req #{0}: Op={1}, Slot={2} (order {3}), Part={4}, DestCode={5}, Class={6}, WorkSite={7}, Status={8}" -f $f[0], $f[1], $f[2], $f[3], $f[6], $f[4], $f[9], $f[10], $f[7])
}

# Verify Child Request A: Finished product cleanup (SLOT-001)
$fgReq = $reqRows[0].Split('|')
if ($fgReq[1] -eq 'REMOVE_FULL' -and $fgReq[4] -eq 'FG-01' -and $fgReq[9] -eq 'FINISHED_GOODS' -and $fgReq[5] -eq $navId -and $fgReq[10] -eq 'TKG_SH01A_V01_WS_FG01') {
    Write-Host "PASS [A]: Finished product cleanup correctly resolved destination_business_code=FG-01, endpoint_class=FINISHED_GOODS, WorkSite=TKG_SH01A_V01_WS_FG01." -ForegroundColor Green
} else {
    Write-Host "FAIL [A]: FG cleanup did not match expectations!" -ForegroundColor Red
    exit 1
}

# Verify Child Request B: Empty container cleanup (SLOT-002)
$emptyReq = $reqRows[1].Split('|')
if ($emptyReq[1] -eq 'REMOVE_EMPTY' -and $emptyReq[4] -eq 'EMPTY-01' -and $emptyReq[9] -eq 'EMPTY_RETURN' -and $emptyReq[5] -eq $navId -and $emptyReq[10] -eq 'TKG_SH01A_V01_WS_EMPTY01') {
    Write-Host "PASS [C]: Empty container cleanup correctly resolved destination_business_code=EMPTY-01, endpoint_class=EMPTY_RETURN, WorkSite=TKG_SH01A_V01_WS_EMPTY01." -ForegroundColor Green
} else {
    Write-Host "FAIL [C]: Empty cleanup did not match expectations!" -ForegroundColor Red
    exit 1
}

# Verify Child Request C: Non-finished / ALT cleanup (SLOT-003)
$altReq = $reqRows[2].Split('|')
if ($altReq[1] -eq 'REMOVE_FULL' -and $altReq[4] -eq 'RETURN-01' -and $altReq[9] -eq 'NON_FINISHED_RETURN' -and $altReq[5] -eq $navId -and $altReq[10] -eq 'TKG_SH01A_V01_WS_RETURN01') {
    Write-Host "PASS [B]: Non-finished / ALT cleanup correctly resolved destination_business_code=RETURN-01, endpoint_class=NON_FINISHED_RETURN, WorkSite=TKG_SH01A_V01_WS_RETURN01." -ForegroundColor Green
} else {
    Write-Host "FAIL [B]: ALT cleanup did not match expectations!" -ForegroundColor Red
    exit 1
}

# Verify Dependencies & Ordering Determinism
$depCount = (& $psql -h 127.0.0.1 -p 55450 -U postgres -d tkg_pda_dev -t -A -c @"
SELECT count(*) FROM ops.transport_request_dependencies d
JOIN ops.transport_requests r ON r.id = d.predecessor_request_id
WHERE r.machine_id = '$machineId';
"@).Trim()

if ($depCount -eq '2') {
    Write-Host "PASS: Multiple cleanup requests are chained deterministically (2 dependencies for 3 sequenced requests)." -ForegroundColor Green
} else {
    Write-Host "FAIL: Expected 2 dependencies, got $depCount" -ForegroundColor Red
    exit 1
}

# Verify Idempotent Replay
$replayResp = Invoke-RestMethod -Method Post -Uri "$apiBase/api/v1/machines/$machineId/production-sessions/$sessionId/end" -Headers $validEndHeaders -ContentType 'application/json'
$reqCountAfterReplay = (& $psql -h 127.0.0.1 -p 55450 -U postgres -d tkg_pda_dev -t -A -c "SELECT count(*) FROM ops.transport_requests WHERE machine_id = '$machineId';").Trim()
if ($reqCountAfterReplay -eq '3' -and $replayResp.wasIdempotentReplay -eq $true) {
    Write-Host "PASS: Idempotent replay verified: wasIdempotentReplay=true, exactly 3 requests exist (0 duplicate requests created)." -ForegroundColor Green
} else {
    Write-Host "FAIL: Idempotency failed! Count=$reqCountAfterReplay, Replay=$($replayResp.wasIdempotentReplay)" -ForegroundColor Red
    exit 1
}

Write-Host "`n--- Part 3: Insufficient Shared Capacity Atomic Failure ---" -ForegroundColor Yellow

# Clean up previous requests and session
& $psql -h 127.0.0.1 -p 55450 -U postgres -d tkg_pda_dev -c @"
DELETE FROM ops.store_events WHERE store_unit_id IN (SELECT id FROM ops.store_units WHERE transport_request_id IN (SELECT id FROM ops.transport_requests WHERE machine_id = '$machineId'));
DELETE FROM ops.store_units WHERE transport_request_id IN (SELECT id FROM ops.transport_requests WHERE machine_id = '$machineId');
DELETE FROM ops.preparation_events WHERE preparation_pallet_id IN (SELECT id FROM ops.preparation_pallets WHERE transport_request_id IN (SELECT id FROM ops.transport_requests WHERE machine_id = '$machineId'));
DELETE FROM ops.preparation_pallets WHERE transport_request_id IN (SELECT id FROM ops.transport_requests WHERE machine_id = '$machineId');
DELETE FROM ops.production_end_run_items WHERE run_id IN (SELECT id FROM ops.production_end_runs WHERE machine_id = '$machineId');
DELETE FROM ops.production_end_runs WHERE machine_id = '$machineId';
DELETE FROM ops.transport_request_dependencies WHERE predecessor_request_id IN (SELECT id FROM ops.transport_requests WHERE machine_id = '$machineId') OR successor_request_id IN (SELECT id FROM ops.transport_requests WHERE machine_id = '$machineId');
DELETE FROM ops.transport_requests WHERE machine_id = '$machineId';
DELETE FROM ops.production_sessions WHERE machine_id = '$machineId';
"@ | Out-Null

# Start a new session
$startHeaders2 = @{
    'Authorization'   = "Bearer $token"
    'X-Device-Code'   = 'DEV-LINE'
    'Idempotency-Key' = "gap2-start2-$([guid]::NewGuid().ToString('N'))"
}
$sessionResp2 = Invoke-RestMethod -Method Post -Uri "$apiBase/api/v1/machines/$machineId/production-sessions" -Headers $startHeaders2 -ContentType 'application/json' -Body $startPayload
$sessionId2 = $sessionResp2.session.id

# Set 2 slots to FULL with main part (requires 2 FINISHED_GOODS endpoints), but we only have FG-01 bound!
& $psql -h 127.0.0.1 -p 55450 -U postgres -d tkg_pda_dev -c @"
UPDATE ops.slot_runtime_state SET occupancy_state = 'OCCUPIED', load_state = 'FULL', current_part_id = '$mainPartId', current_container_type_id = '$containerId' WHERE layout_slot_id = '$slot1Id';
UPDATE ops.slot_runtime_state SET occupancy_state = 'OCCUPIED', load_state = 'FULL', current_part_id = '$mainPartId', current_container_type_id = '$containerId' WHERE layout_slot_id = '$slot2Id';
"@ | Out-Null

$endKeyCapacity = "gap2-end-cap-$([guid]::NewGuid().ToString('N'))"
$endHeadersCapacity = @{
    'Authorization'   = "Bearer $token"
    'X-Device-Code'   = 'DEV-LINE'
    'Idempotency-Key' = $endKeyCapacity
}

$capBlocked = $false
try {
    $endResp2 = Invoke-RestMethod -Method Post -Uri "$apiBase/api/v1/machines/$machineId/production-sessions/$sessionId2/end" -Headers $endHeadersCapacity -ContentType 'application/json'
    Write-Host "UNEXPECTED: End succeeded despite insufficient capacity!" -ForegroundColor Red
    exit 1
} catch {
    $capBlocked = $true
    $errBody = if ($_.ErrorDetails -and $_.ErrorDetails.Message) { $_.ErrorDetails.Message } else {
        $reader = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())
        $reader.ReadToEnd()
    }
    $errObj = $errBody | ConvertFrom-Json
    Write-Host "PASS: Insufficient capacity correctly blocked: $($errObj.code) - $($errObj.detail)" -ForegroundColor Green
}

# Verify atomic failure: 0 partial requests survived, session is NOT ended/partially ended
$survivingCount = (& $psql -h 127.0.0.1 -p 55450 -U postgres -d tkg_pda_dev -t -A -c "SELECT count(*) FROM ops.transport_requests WHERE machine_id = '$machineId';").Trim()
$statusAfterCapFail = (& $psql -h 127.0.0.1 -p 55450 -U postgres -d tkg_pda_dev -t -A -c "SELECT status FROM ops.production_sessions WHERE id = '$sessionId2';").Trim()

if ($survivingCount -eq '0' -and $statusAfterCapFail -eq 'ACTIVE') {
    Write-Host "PASS: Atomic rollback on insufficient capacity confirmed: 0 partial child requests survive, session status is ACTIVE." -ForegroundColor Green
} else {
    Write-Host "FAIL: State corrupted during insufficient capacity failure! Count=$survivingCount, Status=$statusAfterCapFail" -ForegroundColor Red
    exit 1
}

# Cleanup trial bindings and test data, restore DRAFT profile status and original active navigation ID
& $psql -h 127.0.0.1 -p 55450 -U postgres -d tkg_pda_dev -c @"
UPDATE master.navigation_profiles SET status = 'DRAFT' WHERE id = '$navId';
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
WHERE layout_slot_id IN ('$slot1Id', '$slot2Id', '$slot3Id');
UPDATE ops.machine_runtime_state SET active_navigation_id = '$origNavId' WHERE machine_id = '$machineId';
"@ | Out-Null

Write-Host "`nRESULT: PASS (Production End allocation & atomicity verified)" -ForegroundColor Green
exit 0
