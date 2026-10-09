<#
.SYNOPSIS
    Synthetic Full-E2E Production End acceptance test for isolated acceptance database.
.DESCRIPTION
    Executes full lifecycle testing of Production End with synthetic commissioning:
    - SAFETY GATE: strictly enforces database is 'tkg_pda_acceptance' (refuses tkg_pda_dev).
    - Publishes synthetic NAV profile with verified geometry and commissioned state.
    - Installs synthetic shared endpoint bindings for FG-01, RETURN-01, and EMPTY-01.
    - Validates sequenced multi-slot cleanup request generation (REMOVE_FULL, REMOVE_EMPTY).
    - Validates deterministic dependency chaining across generated child requests.
    - Validates idempotent replay without duplicate request generation.
    - Validates atomic failure and zero surviving child requests on insufficient shared capacity.
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
    Write-Host "Synthetic Production End test performs destructive setup and commissioning fabrication." -ForegroundColor Red
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
Write-Host "PHASE: SYNTHETIC PRODUCTION END FULL E2E (ISOLATED DB)" -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan
Write-Host "Verified Database: $Database (ISOLATED)"
Write-Host "Target API URL:    $apiBase"

# 1. Fetch Machine SH-01-A and Variant 1 IDs from isolated acceptance DB
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
JOIN master.navigation_profiles np ON np.id = vnpa.navigation_profile_id AND np.config_json ->> 'commissioningRegistry' = 'tkg_rds_variant_map_naming_v1'
JOIN master.layouts l ON l.machine_id = m.id AND l.code = 'CUSTOMER-PHYSICAL-V1'
JOIN master.layout_slots s1 ON s1.layout_id = l.id AND s1.code = 'SLOT-001'
JOIN master.layout_slots s2 ON s2.layout_id = l.id AND s2.code = 'SLOT-002'
JOIN master.layout_slots s3 ON s3.layout_id = l.id AND s3.code = 'SLOT-003'
JOIN master.container_types ct ON ct.code = 'ARABA'
JOIN ops.store_locations loc ON loc.code = 'FG-01'
WHERE m.code = 'SH-01-A';
"@

$row = (& $psql -h $HostName -p $Port -U $User -d $Database -t -A -F '|' -c $sql).Trim().Split('|')
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
Write-Host "NAV Profile:    $navCode ($navId)"

# Login with passwordless dev-line operator
$loginPayload = @{ employeeNumber = 'DEV-LINE' } | ConvertTo-Json
$loginResp = Invoke-RestMethod -Method Post -Uri "$apiBase/api/v1/auth/login" -Headers @{ 'X-Device-Code' = 'DEV-LINE' } -ContentType 'application/json' -Body $loginPayload
$token = $loginResp.accessToken
$headers = @{
    'Authorization' = "Bearer $token"
    'X-Device-Code' = 'DEV-LINE'
}

$runGuid = [Guid]::NewGuid().ToString('N')

# Set profile to PUBLISHED with verified geometry in acceptance DB
& $psql -h $HostName -p $Port -U $User -d $Database -c @"
UPDATE master.navigation_profiles
SET status = 'PUBLISHED',
    published_at = COALESCE(published_at, now()),
    config_json = jsonb_set(
        jsonb_set(config_json, '{geometryVerified}', 'true'::jsonb),
        '{commissioningState}', '"COMMISSIONED"'::jsonb)
WHERE id = '$navId';
UPDATE audit.client_devices SET machine_id = '$machineId' WHERE device_code = 'DEV-LINE';
UPDATE ops.machine_runtime_state SET active_navigation_id = '$navId' WHERE machine_id = '$machineId';
"@ | Out-Null

# Install synthetic shared endpoint bindings in acceptance DB
& $psql -h $HostName -p $Port -U $User -d $Database -c @"
INSERT INTO integration.rds_worksites (navigation_profile_id, rds_site_id, rds_type, last_sync_at, last_disabled, last_sync_failed, raw_state_json)
SELECT '$navId', planned_rds_site_id, 1, now(), 0, 0, '{}'::jsonb
FROM integration.rds_worksite_plans
WHERE navigation_profile_id = '$navId' AND planned_rds_site_id IN ('TKG_SH01A_V01_WS_FG01', 'TKG_SH01A_V01_WS_RETURN01', 'TKG_SH01A_V01_WS_EMPTY01')
ON CONFLICT (navigation_profile_id, rds_site_id) DO UPDATE SET last_sync_at = now(), last_disabled = 0, last_sync_failed = 0;

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

Write-Host "Synthetic commissioning bindings configured for FG-01, RETURN-01, and EMPTY-01."

# Start production session on SH-01-A
$startPayload = @{
    variantId      = $variantId
    productGroupId = $groupId
    mainPartId     = $mainPartId
} | ConvertTo-Json

$startHeaders = $headers.Clone()
$startHeaders['Idempotency-Key'] = "acceptance:${runGuid}:syn-start"
$sessionResp = Invoke-RestMethod -Method Post -Uri "$apiBase/api/v1/machines/$machineId/production-sessions" -Headers $startHeaders -ContentType 'application/json' -Body $startPayload
$sessionId = $sessionResp.session.id
Write-Host "Started production session: $sessionId"

# Set up slots for cleanup:
# SLOT-001 (display_order 1): OCCUPIED FULL (Main Part) -> requires FINISHED_GOODS
# SLOT-002 (display_order 2): OCCUPIED EMPTY (Container) -> requires EMPTY_RETURN
# SLOT-003 (display_order 3): OCCUPIED FULL (ALT Part)   -> requires NON_FINISHED_RETURN
& $psql -h $HostName -p $Port -U $User -d $Database -c @"
UPDATE ops.slot_runtime_state SET occupancy_state = 'OCCUPIED', load_state = 'FULL', current_part_id = '$mainPartId', current_container_type_id = '$containerId' WHERE layout_slot_id = '$slot1Id';
UPDATE ops.slot_runtime_state SET occupancy_state = 'OCCUPIED', load_state = 'EMPTY', current_part_id = '$mainPartId', current_container_type_id = '$containerId' WHERE layout_slot_id = '$slot2Id';
UPDATE ops.slot_runtime_state SET occupancy_state = 'OCCUPIED', load_state = 'FULL', current_part_id = '$altPartId', current_container_type_id = '$containerId' WHERE layout_slot_id = '$slot3Id';
"@ | Out-Null

# Execute Production End
$endHeaders = $headers.Clone()
$endHeaders['Idempotency-Key'] = "acceptance:${runGuid}:syn-end-valid"
$endResp = Invoke-RestMethod -Method Post -Uri "$apiBase/api/v1/machines/$machineId/production-sessions/$sessionId/end" -Headers $endHeaders -ContentType 'application/json'
Write-Host "Production End executed! Session status: $($endResp.session.status)"

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
    w.rds_site_id as resolved_worksite,
    r.id::text
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
$reqRows = (& $psql -h $HostName -p $Port -U $User -d $Database -t -A -F '|' -c $requestsSql).Trim().Split("`n")

Write-Host "`nGenerated Child Requests:"
foreach ($rLine in $reqRows) {
    $f = $rLine.Split('|')
    Write-Host ("  Req #{0}: Op={1}, Slot={2} (order {3}), Part={4}, DestCode={5}, Class={6}, WorkSite={7}, Status={8}" -f $f[0], $f[1], $f[2], $f[3], $f[6], $f[4], $f[9], $f[10], $f[7])
}

# Verify Child Request A: Finished product cleanup (SLOT-001)
$fgReq = $reqRows[0].Split('|')
if ($fgReq[1] -eq 'REMOVE_FULL' -and $fgReq[4] -eq 'FG-01' -and $fgReq[9] -eq 'FINISHED_GOODS' -and $fgReq[10] -eq 'TKG_SH01A_V01_WS_FG01') {
    Write-Host "PASS [A]: Finished product cleanup correctly resolved FG-01 -> FINISHED_GOODS -> TKG_SH01A_V01_WS_FG01." -ForegroundColor Green
} else {
    Write-Host "FAIL [A]: FG cleanup did not match expectations!" -ForegroundColor Red
    exit 1
}

# Verify Child Request B: Empty container cleanup (SLOT-002)
$emptyReq = $reqRows[1].Split('|')
if ($emptyReq[1] -eq 'REMOVE_EMPTY' -and $emptyReq[4] -eq 'EMPTY-01' -and $emptyReq[9] -eq 'EMPTY_RETURN' -and $emptyReq[10] -eq 'TKG_SH01A_V01_WS_EMPTY01') {
    Write-Host "PASS [B]: Empty container cleanup correctly resolved EMPTY-01 -> EMPTY_RETURN -> TKG_SH01A_V01_WS_EMPTY01." -ForegroundColor Green
} else {
    Write-Host "FAIL [B]: Empty cleanup did not match expectations!" -ForegroundColor Red
    exit 1
}

# Verify Child Request C: Non-finished / ALT cleanup (SLOT-003)
$altReq = $reqRows[2].Split('|')
if ($altReq[1] -eq 'REMOVE_FULL' -and $altReq[4] -eq 'RETURN-01' -and $altReq[9] -eq 'NON_FINISHED_RETURN' -and $altReq[10] -eq 'TKG_SH01A_V01_WS_RETURN01') {
    Write-Host "PASS [C]: Non-finished / ALT cleanup correctly resolved RETURN-01 -> NON_FINISHED_RETURN -> TKG_SH01A_V01_WS_RETURN01." -ForegroundColor Green
} else {
    Write-Host "FAIL [C]: ALT cleanup did not match expectations!" -ForegroundColor Red
    exit 1
}

# Verify Dependencies & Ordering Determinism
$depCount = (& $psql -h $HostName -p $Port -U $User -d $Database -t -A -c @"
SELECT count(*) FROM ops.transport_request_dependencies d
JOIN ops.transport_requests r ON r.id = d.predecessor_request_id
WHERE r.machine_id = '$machineId';
"@).Trim()

if ($depCount -eq '2') {
    Write-Host "PASS: Multiple cleanup requests chained deterministically (2 dependencies for 3 sequenced requests)." -ForegroundColor Green
} else {
    Write-Host "FAIL: Expected 2 dependencies, got $depCount" -ForegroundColor Red
    exit 1
}

# Verify Idempotent Replay
$replayResp = Invoke-RestMethod -Method Post -Uri "$apiBase/api/v1/machines/$machineId/production-sessions/$sessionId/end" -Headers $endHeaders -ContentType 'application/json'
$reqCountAfterReplay = (& $psql -h $HostName -p $Port -U $User -d $Database -t -A -c "SELECT count(*) FROM ops.transport_requests WHERE machine_id = '$machineId';").Trim()
if ($reqCountAfterReplay -eq '3' -and $replayResp.wasIdempotentReplay -eq $true) {
    Write-Host "PASS: Idempotent replay verified: wasIdempotentReplay=true, exactly 3 requests exist (0 duplicates created)." -ForegroundColor Green
} else {
    Write-Host "FAIL: Idempotency failed! Count=$reqCountAfterReplay, Replay=$($replayResp.wasIdempotentReplay)" -ForegroundColor Red
    exit 1
}

# --------------------------------------------------------------------------
# Part 2: Insufficient Capacity Failure & Atomic Rollback
# --------------------------------------------------------------------------
Write-Host "`n--- Part 2: Insufficient Capacity Failure & Atomic Rollback ---" -ForegroundColor Yellow

# Clean up only the requests and session created above. Re-check the database first.
Assert-AcceptanceDatabase
$ownedRequestIds = @($reqRows | Where-Object { $_.Trim() } | ForEach-Object { $_.Split('|')[-1] })
$idList = ($ownedRequestIds | ForEach-Object { "'$_'" }) -join ','
if ($idList) {
    & $psql -h $HostName -p $Port -U $User -d $Database -c @"
DELETE FROM ops.store_events WHERE store_unit_id IN (SELECT id FROM ops.store_units WHERE transport_request_id IN ($idList));
DELETE FROM ops.store_units WHERE transport_request_id IN ($idList);
DELETE FROM ops.production_end_run_items WHERE transport_request_id IN ($idList);
DELETE FROM ops.production_end_runs WHERE production_session_id = '$sessionId';
DELETE FROM ops.transport_request_dependencies WHERE predecessor_request_id IN ($idList) OR successor_request_id IN ($idList);
DELETE FROM ops.transport_requests WHERE id IN ($idList);
DELETE FROM ops.production_sessions WHERE id = '$sessionId';
"@ | Out-Null
}

# Start a new session
$startHeaders2 = $headers.Clone()
$startHeaders2['Idempotency-Key'] = "acceptance:${runGuid}:syn-start2"
$sessionResp2 = Invoke-RestMethod -Method Post -Uri "$apiBase/api/v1/machines/$machineId/production-sessions" -Headers $startHeaders2 -ContentType 'application/json' -Body $startPayload
$sessionId2 = $sessionResp2.session.id

# Set 2 slots to FULL with main part (requires 2 FINISHED_GOODS endpoints, but only FG-01 exists!)
& $psql -h $HostName -p $Port -U $User -d $Database -c @"
UPDATE ops.slot_runtime_state SET occupancy_state = 'OCCUPIED', load_state = 'FULL', current_part_id = '$mainPartId', current_container_type_id = '$containerId' WHERE layout_slot_id = '$slot1Id';
UPDATE ops.slot_runtime_state SET occupancy_state = 'OCCUPIED', load_state = 'FULL', current_part_id = '$mainPartId', current_container_type_id = '$containerId' WHERE layout_slot_id = '$slot2Id';
"@ | Out-Null

$endHeadersCapacity = $headers.Clone()
$endHeadersCapacity['Idempotency-Key'] = "acceptance:${runGuid}:syn-end-cap"

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
    if ([int]$_.Exception.Response.StatusCode -ne 422 -or $errObj.code -ne 'FINISHED_GOODS_CAPACITY_UNAVAILABLE') {
        throw "Expected HTTP 422 FINISHED_GOODS_CAPACITY_UNAVAILABLE, got $($errObj.code)."
    }
    Write-Host "PASS: Insufficient capacity correctly blocked: $($errObj.code) - $($errObj.detail)" -ForegroundColor Green
}

# Verify atomic failure: 0 partial requests survived, session remains ACTIVE
$survivingCount = (& $psql -h $HostName -p $Port -U $User -d $Database -t -A -c "SELECT count(*) FROM ops.transport_requests WHERE machine_id = '$machineId';").Trim()
$statusAfterCapFail = (& $psql -h $HostName -p $Port -U $User -d $Database -t -A -c "SELECT status FROM ops.production_sessions WHERE id = '$sessionId2';").Trim()

if ($survivingCount -eq '0' -and $statusAfterCapFail -eq 'ACTIVE') {
    Write-Host "PASS: Atomic rollback on insufficient capacity confirmed: 0 partial child requests survive, session status is ACTIVE." -ForegroundColor Green
} else {
    Write-Host "FAIL: State corrupted during insufficient capacity failure! Count=$survivingCount, Status=$statusAfterCapFail" -ForegroundColor Red
    exit 1
}

Assert-AcceptanceDatabase
& $psql -h $HostName -p $Port -U $User -d $Database -c @"
DELETE FROM ops.production_end_run_items WHERE run_id IN (SELECT id FROM ops.production_end_runs WHERE production_session_id = '$sessionId2');
DELETE FROM ops.transport_request_dependencies WHERE predecessor_request_id IN (SELECT id FROM ops.transport_requests WHERE production_session_id = '$sessionId2') OR successor_request_id IN (SELECT id FROM ops.transport_requests WHERE production_session_id = '$sessionId2');
DELETE FROM ops.transport_requests WHERE production_session_id = '$sessionId2';
DELETE FROM ops.production_end_runs WHERE production_session_id = '$sessionId2';
DELETE FROM ops.production_sessions WHERE id = '$sessionId2';
"@ | Out-Null

Write-Host "`nRESULT: PASS (Synthetic Production End full workflow & capacity atomicity verified)" -ForegroundColor Green
exit 0
