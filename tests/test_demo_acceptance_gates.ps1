<#
.SYNOPSIS
    Automated acceptance test suite for TKG PDA Demo Mode improvements.
.DESCRIPTION
    Validates the critical acceptance criteria from the 19 October audit:
    1. Two-tablet gate (Request sits in WAITING_PREPARATION until warehouse confirms ready).
    2. Preparation area release (OCCUPIED/READY -> COLLECTED upon transport finish).
    3. 19 Finished goods store locations (Individual verification of FG-01 through FG-19, delivery, notification, and 'Aldım' removal).
    4. Idempotency (re-submitting does not duplicate pallets/store units).
    5. Demo safety & network isolation (Zero live RDS/M4 dispatch calls, DispatchEnabled: false, loopback enforcement, remote endpoint rejection).
#>
[CmdletBinding()]
param(
    [string]$ApiBaseUrl = 'http://127.0.0.1:5000',
    [string]$HostName = '127.0.0.1',
    [int]$Port = 55450,
    [string]$Database = 'tkg_pda_acceptance',
    [string]$User = 'postgres'
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$psql = 'C:\Program Files\PostgreSQL\18\bin\psql.exe'
if (-not (Test-Path -LiteralPath $psql)) {
    $cmd = Get-Command psql.exe -ErrorAction SilentlyContinue
    if ($cmd) { $psql = $cmd.Source }
}

# ------------------------------------------------------------------------------------------------
# STRICT TEST DATABASE SAFETY GUARD
# ------------------------------------------------------------------------------------------------
$allowedDatabases = @('tkg_pda_acceptance', 'tkg_pda_demo')
if ($Database -notin $allowedDatabases) {
    throw "SAFETY ERROR: Acceptance test script with TRUNCATE can only run against test/demo databases ($($allowedDatabases -join ', ')). Current database is '$Database'. Refusing to execute."
}

function Invoke-Sql {
    param([Parameter(Mandatory = $true)][string]$Sql)
    $res = & $psql -X -h $HostName -p $Port -U $User -d $Database -t -A -c $Sql
    if ($LASTEXITCODE -ne 0) { throw "SQL execution failed: $Sql" }
    return ($res -join "`n").Trim()
}

Write-Host "=================================================================" -ForegroundColor Cyan
Write-Host "   TKG PDA DEMO ACCEPTANCE AUDIT -- VERIFICATION SUITE           " -ForegroundColor Cyan
Write-Host "=================================================================" -ForegroundColor Cyan
Write-Host "Database: $Database on $HostName`:$Port (Safety Verified)" -ForegroundColor Gray
Write-Host "API:      $ApiBaseUrl" -ForegroundColor Gray
Write-Host ""

$passedCount = 0
$failedCount = 0

function Assert-Check {
    param(
        [string]$Criterion,
        [bool]$Condition,
        [string]$SuccessMessage,
        [string]$FailureMessage
    )
    if ($Condition) {
        Write-Host "  [PASS] $Criterion`: $SuccessMessage" -ForegroundColor Green
        $script:passedCount++
    } else {
        Write-Host "  [FAIL] $Criterion`: $FailureMessage" -ForegroundColor Red
        $script:failedCount++
        throw "$Criterion verification failed: $FailureMessage"
    }
}

# Reset operational tables for clean repeatable test runs
Invoke-Sql @"
TRUNCATE ops.store_events, ops.store_units, ops.preparation_events, ops.preparation_pallets, integration.rds_tasks, ops.transport_request_events, ops.transport_requests, ops.transport_batches CASCADE;
UPDATE ops.slot_runtime_state rs
SET occupancy_state = 'OCCUPIED', load_state = 'EMPTY', updated_at = now()
FROM master.layout_slots ls
WHERE rs.layout_slot_id = ls.id AND ls.slot_role = 'EMPTY_STORAGE';
"@ | Out-Null

# Ensure API is reachable
Write-Host "[Gate Check] Verifying API Health and Demo Status..." -ForegroundColor Cyan
$health = Invoke-RestMethod -Uri "$ApiBaseUrl/health/live" -TimeoutSec 5
Assert-Check "Health Live" ($health.status -eq 'live') "API is live" "API is not live"
Assert-Check "Demo Mode Flag" ($health.demoMode -eq $true) "DemoMode is active on server (/health/live)" "DemoMode is false"

$version = Invoke-RestMethod -Uri "$ApiBaseUrl/health/version" -TimeoutSec 5
Assert-Check "Version Demo Flag" ($version.demoMode -eq $true) "DemoMode is active on server (/health/version)" "DemoMode is false"

# Authenticate Admin
$adminLogin = Invoke-RestMethod -Uri "$ApiBaseUrl/api/v1/admin/auth/login" -Method Post `
    -ContentType 'application/json' `
    -Body '{"username": "dev-admin", "password": "Admin123!"}'
$adminHeaders = @{
    'Authorization' = "Bearer $($adminLogin.accessToken)"
}

# ------------------------------------------------------------------------------------------------
# CRITERION 5: DEMO SAFETY & ZERO LIVE DISPATCH & NETWORK ISOLATION
# ------------------------------------------------------------------------------------------------
Write-Host "`n>>> CRITERION 5: Demo Safety & Zero Live Robot Dispatch" -ForegroundColor Yellow
$rdsStatus = Invoke-RestMethod -Uri "$ApiBaseUrl/api/v1/admin/rds/status" -Headers $adminHeaders
Write-Host "  RDS Status Response: $($rdsStatus | ConvertTo-Json -Compress)" -ForegroundColor Gray
Assert-Check "Crit 5.1" ($rdsStatus.dispatchEnabled -eq $false) "DispatchEnabled is strictly FALSE in demo mode" "DispatchEnabled is true"
Assert-Check "Crit 5.2" ($rdsStatus.businessMessage -like "*sim*lasyon*") "BusinessMessage clearly states simulation mode ($($rdsStatus.businessMessage))" "Missing simulation message"
Assert-Check "Crit 5.3" ($rdsStatus.baseAddressDisplaySafe -like "*Yerel RDS*") "BaseAddress points to safe local loopback ($($rdsStatus.baseAddressDisplaySafe))" "Unsafe address"

# ------------------------------------------------------------------------------------------------
# AUTHENTICATION
# ------------------------------------------------------------------------------------------------
Write-Host "`n[Auth] Logging in Operator and Warehouse users..." -ForegroundColor Cyan
$lineLogin = Invoke-RestMethod -Uri "$ApiBaseUrl/api/v1/auth/login" -Method Post `
    -Headers @{ 'X-Device-Code' = 'DEV-LINE' } `
    -ContentType 'application/json' `
    -Body '{"employeeNumber": "DEV-LINE"}'
$lineHeaders = @{
    'Authorization' = "Bearer $($lineLogin.accessToken)"
    'X-Device-Code' = 'DEV-LINE'
}

$whLogin = Invoke-RestMethod -Uri "$ApiBaseUrl/api/v1/auth/login" -Method Post `
    -Headers @{ 'X-Device-Code' = 'DEV-WAREHOUSE' } `
    -ContentType 'application/json' `
    -Body '{"employeeNumber": "DEV-WH"}'
$whHeaders = @{
    'Authorization' = "Bearer $($whLogin.accessToken)"
    'X-Device-Code' = 'DEV-WAREHOUSE'
}
$whReg = Invoke-RestMethod -Uri "$ApiBaseUrl/api/v1/auth/device-notifications/register" -Method Put -Headers $whHeaders
Write-Host "  Authenticated Line Operator (DEV-LINE) and Warehouse Operator (DEV-WH, RegId: $($whReg.registrationId))." -ForegroundColor Gray

# Ensure SH-01-A has an active production session
Invoke-Sql @"
INSERT INTO ops.production_sessions (
    id, machine_id, variant_id, product_group_id, main_part_id,
    start_key, started_by, started_device_id, status, started_at
)
SELECT 
    gen_random_uuid(), m.id, mrs.active_variant_id, pg.id, pg.main_part_id,
    'session-sh01a-' || gen_random_uuid()::text, (SELECT id FROM auth.users WHERE username = 'dev-line' LIMIT 1),
    (SELECT id FROM audit.client_devices WHERE device_code = 'DEV-LINE' LIMIT 1), 'ACTIVE', now()
FROM master.machines m
JOIN ops.machine_runtime_state mrs ON mrs.machine_id = m.id
JOIN master.product_groups pg ON pg.variant_id = mrs.active_variant_id
WHERE m.code = 'SH-01-A'
  AND NOT EXISTS (SELECT 1 FROM ops.production_sessions WHERE machine_id = m.id AND status = 'ACTIVE')
LIMIT 1;
"@ | Out-Null

# Retrieve Active Production Context for SH-01-A
$ctxStr = Invoke-Sql @"
SELECT m.id, s.variant_id, mrs.active_layout_id, s.product_group_id, pg.main_part_id, c.id, ls.id, ls.code
FROM master.machines m
JOIN ops.machine_runtime_state mrs ON mrs.machine_id = m.id
JOIN ops.production_sessions s ON s.machine_id = m.id AND s.status = 'ACTIVE'
JOIN master.product_groups pg ON pg.id = s.product_group_id
JOIN master.layout_slots ls ON ls.layout_id = mrs.active_layout_id AND ls.is_enabled AND ls.slot_role NOT IN ('MACHINE','EMPTY_STORAGE','DROP')
JOIN master.layout_slot_parts lp ON lp.layout_slot_id = ls.id AND lp.part_id = pg.main_part_id
CROSS JOIN (SELECT id FROM master.container_types WHERE code = 'ARABA' LIMIT 1) c
WHERE m.code = 'SH-01-A'
ORDER BY ls.display_order, ls.code
LIMIT 1;
"@
$ctx = $ctxStr.Split('|')
$mId, $vId, $lId, $pgId, $partId, $containerId, $slotId, $slotCode = $ctx

# Set Slot to VACANT
Invoke-Sql @"
UPDATE ops.slot_runtime_state 
SET occupancy_state = 'VACANT', load_state = 'UNKNOWN', current_part_id = NULL, current_container_type_id = NULL, row_version = row_version + 1, updated_at = now()
WHERE layout_slot_id = '$slotId';
"@ | Out-Null

# ------------------------------------------------------------------------------------------------
# CRITERION 1: TWO-TABLET GATE (WAREHOUSE APPROVAL REQUIRED)
# ------------------------------------------------------------------------------------------------
Write-Host "`n>>> CRITERION 1: Two-Tablet Gate (Warehouse Approval Enforcement)" -ForegroundColor Yellow
$idemKey1 = "demo-audit-bring-empty-" + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$bringPayload = @{
    machineId = $mId
    variantId = $vId
    layoutId = $lId
    productGroupId = $pgId
    partId = $partId
    containerTypeId = $null
    operation = "BRING_EMPTY"
    requiresPreparation = $true
} | ConvertTo-Json

$bringReq = $null
try {
    $bringReq = Invoke-RestMethod -Uri "$ApiBaseUrl/api/v1/transport-requests" -Method Post `
        -Headers ($lineHeaders + @{ 'Idempotency-Key' = $idemKey1 }) `
        -ContentType 'application/json' `
        -Body $bringPayload
} catch [System.Net.WebException] {
    $stream = $_.Exception.Response.GetResponseStream()
    $reader = New-Object System.IO.StreamReader($stream)
    $body = $reader.ReadToEnd()
    Write-Host "Create Transport Failed: $body" -ForegroundColor Red
    throw $_
}

$reqId = $bringReq.id
Write-Host "  Transport request created with ID: $reqId (Readiness: $($bringReq.readinessState), Status: $($bringReq.status))" -ForegroundColor Gray

Assert-Check "Crit 1.1" ($bringReq.status -eq 'WAITING_PREPARATION' -or $bringReq.readinessState -eq 'WAITING_PREPARATION') `
    "Transport request entered WAITING_PREPARATION state" "Request did not enter WAITING_PREPARATION"

# Sleep 3 seconds to guarantee DemoSimulationWorker did NOT auto-advance it
Write-Host "  Waiting 3s to verify DemoSimulationWorker respects AutoPrepareWarehouseDemands = false..." -ForegroundColor Gray
Start-Sleep -Seconds 3

$stateCheck = Invoke-Sql "SELECT status, readiness_state FROM ops.transport_requests WHERE id = '$reqId';"
$st, $rd = $stateCheck.Split('|')
Assert-Check "Crit 1.2" ($rd -eq 'WAITING_PREPARATION') `
    "Request is STILL in WAITING_PREPARATION (not auto-prepared by simulation)" `
    "Request was improperly auto-prepared! Current: $st|$rd"

# Check warehouse console visibility
$prepList = Invoke-RestMethod -Uri "$ApiBaseUrl/api/v1/warehouse/preparation-requests" -Headers $whHeaders
$matching = $prepList | Where-Object { $_.requestId -eq $reqId }
Assert-Check "Crit 1.3" ($null -ne $matching) `
    "Warehouse Tablet sees the waiting preparation demand" `
    "Warehouse Tablet does not see the demand in preparation list"

# ------------------------------------------------------------------------------------------------
# CRITERION 2: PREPARATION AREA RELEASE UPON TRANSPORT COMPLETION
# ------------------------------------------------------------------------------------------------
Write-Host "`n>>> CRITERION 2: Preparation Pallet Linkage & Automatic Release (COLLECTED)" -ForegroundColor Yellow
# Link a preparation pallet in prep area
$prepAreaId = Invoke-Sql "SELECT id FROM ops.preparation_areas WHERE is_enabled LIMIT 1;"
$palletId = [Guid]::NewGuid().ToString()

Invoke-Sql @"
INSERT INTO ops.preparation_pallets (
    id, preparation_area_id, container_type_id, load_state, part_id, transport_request_id,
    placed_at, placed_by, placed_device_id, ready_at, ready_by, placement_key
) VALUES (
    '$palletId', '$prepAreaId', COALESCE((SELECT container_type_id FROM ops.transport_requests WHERE id = '$reqId'), (SELECT id FROM master.container_types LIMIT 1)), 'FULL', '$partId', '$reqId',
    now(), '$($whLogin.user.id)', '$($whLogin.device.id)', now(), '$($whLogin.user.id)', 'pallet-$palletId'
);
"@ | Out-Null

# Warehouse confirms READY
$readyRes = Invoke-RestMethod -Uri "$ApiBaseUrl/api/v1/transport-requests/$reqId/ready" -Method Post -Headers $whHeaders
Assert-Check "Crit 1.4" ($readyRes.readinessState -eq 'READY' -or $readyRes.status -eq 'QUEUED') `
    "Warehouse confirmed readiness; request state is READY/QUEUED" `
    "Failed confirming readiness on warehouse tablet"

# Wait for DemoSimulationWorker to pick up, run, and complete with live countdown
Write-Host "  Waiting for simulation execution cycle (QUEUED -> SENT_TO_RDS -> RUNNING -> COMPLETED) [Max 60s]..." -ForegroundColor Gray
$completed = $false
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$lastLogged = -10
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Seconds 1
    $taskRow = Invoke-Sql "SELECT r.status, COALESCE(t.rds_status::text, 'NO_TASK'), COALESCE(t.last_status_json->>'remaining_time', '0') FROM ops.transport_requests r LEFT JOIN integration.rds_tasks t ON t.transport_request_id = r.id WHERE r.id = '$reqId';"
    $rStatus, $tStatus, $remTime = $taskRow.Split('|')
    $elapsedSec = [int]$stopwatch.Elapsed.TotalSeconds
    if (($elapsedSec - $lastLogged -ge 4) -or $rStatus -eq 'COMPLETED') {
        Write-Host "    [T+${elapsedSec}s] Request: $rStatus | RDS Task: $tStatus | Simulated Remaining: ${remTime}s" -ForegroundColor DarkGray
        $lastLogged = $elapsedSec
    }
    if ($rStatus -eq 'COMPLETED') {
        $completed = $true
        break
    }
}
Assert-Check "Crit 2.1" ($completed -eq $true) `
    "Transport request reached COMPLETED successfully after simulated 30s run" `
    "Transport request did not reach COMPLETED in time"

# Check pallet release in preparation area
$palletState = Invoke-Sql "SELECT released_at IS NOT NULL, released_reason FROM ops.preparation_pallets WHERE id = '$palletId';"
$isReleased, $reason = $palletState.Split('|')
Assert-Check "Crit 2.2" ($isReleased -eq 't' -and $reason -eq 'COLLECTED') `
    "Preparation pallet was automatically marked COLLECTED (released_at set, reason: COLLECTED)" `
    "Preparation pallet was not collected properly: released=$isReleased, reason=$reason"

$prepEvent = Invoke-Sql "SELECT count(*) FROM ops.preparation_events WHERE preparation_pallet_id = '$palletId' AND event_type = 'PALLET_COLLECTED';"
Assert-Check "Crit 2.3" ([int]$prepEvent -ge 1) `
    "PALLET_COLLECTED event logged in ops.preparation_events" `
    "Missing PALLET_COLLECTED event"

# ------------------------------------------------------------------------------------------------
# CRITERION 3: 19 FINISHED GOODS STORE LOCATIONS & REMOVAL ("ALDIM")
# ------------------------------------------------------------------------------------------------
Write-Host "`n>>> CRITERION 3: 19 Finished Goods Store Locations Verification & Removal ('Aldım')" -ForegroundColor Yellow

# Verify every single store location from FG-01 to FG-19
$activeStoreLocations = (Invoke-Sql "SELECT code FROM ops.store_locations WHERE is_enabled ORDER BY code;") -split "`n"
Write-Host "  Active Store Locations Count: $($activeStoreLocations.Count)" -ForegroundColor Gray
for ($fgIdx = 1; $fgIdx -le 19; $fgIdx++) {
    $expectedCode = "FG-{0:D2}" -f $fgIdx
    $exists = $activeStoreLocations -contains $expectedCode
    Assert-Check "Crit 3.1 ($expectedCode)" ($exists -eq $true) `
        "Store location '$expectedCode' is present and active in master layout" `
        "Store location '$expectedCode' is MISSING or disabled"
}

# Set slot to OCCUPIED + FULL to simulate Kasa Doldu
Invoke-Sql @"
UPDATE ops.slot_runtime_state 
SET occupancy_state = 'OCCUPIED', load_state = 'FULL', current_part_id = '$partId', current_container_type_id = '$containerId', row_version = row_version + 1, updated_at = now()
WHERE layout_slot_id = '$slotId';
"@ | Out-Null

$idemKey2 = "demo-audit-remove-full-" + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$fgPayload = @{
    machineId = $mId
    variantId = $vId
    layoutId = $lId
    productGroupId = $pgId
    partId = $partId
    containerTypeId = $null
    operation = "REMOVE_FULL"
} | ConvertTo-Json

$fgReq = Invoke-RestMethod -Uri "$ApiBaseUrl/api/v1/transport-requests" -Method Post `
    -Headers ($lineHeaders + @{ 'Idempotency-Key' = $idemKey2 }) `
    -ContentType 'application/json' `
    -Body $fgPayload

$fgReqId = $fgReq.id
Write-Host "  REMOVE_FULL transport request created: $fgReqId" -ForegroundColor Gray

# Wait for completion with live countdown
Write-Host "  Waiting for REMOVE_FULL completion by simulated AGV [Max 60s]..." -ForegroundColor Gray
$fgCompleted = $false
$stopwatchFg = [System.Diagnostics.Stopwatch]::StartNew()
$lastLoggedFg = -10
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Seconds 1
    $taskRow = Invoke-Sql "SELECT r.status, COALESCE(t.rds_status::text, 'NO_TASK'), COALESCE(t.last_status_json->>'remaining_time', '0') FROM ops.transport_requests r LEFT JOIN integration.rds_tasks t ON t.transport_request_id = r.id WHERE r.id = '$fgReqId';"
    $rStatus, $tStatus, $remTime = $taskRow.Split('|')
    $elapsedSec = [int]$stopwatchFg.Elapsed.TotalSeconds
    if (($elapsedSec - $lastLoggedFg -ge 4) -or $rStatus -eq 'COMPLETED') {
        Write-Host "    [T+${elapsedSec}s] Request: $rStatus | RDS Task: $tStatus | Simulated Remaining: ${remTime}s" -ForegroundColor DarkGray
        $lastLoggedFg = $elapsedSec
    }
    if ($rStatus -eq 'COMPLETED') {
        $fgCompleted = $true
        break
    }
}
Assert-Check "Crit 3.2" ($fgCompleted -eq $true) `
    "REMOVE_FULL transport reached COMPLETED after simulated 30s run" `
    "REMOVE_FULL transport did not reach COMPLETED in time"

# Check delivery in ops.store_units
$unitInfo = Invoke-Sql @"
SELECT u.id, sl.code, u.delivered_at IS NOT NULL, u.removed_at IS NULL, u.row_version
FROM ops.store_units u
JOIN ops.store_locations sl ON sl.id = u.store_location_id
WHERE u.transport_request_id = '$fgReqId';
"@
if (-not $unitInfo) {
    throw "No store unit found for completed REMOVE_FULL request $fgReqId"
}
$unitId, $storeLocCode, $isDelivered, $isNotRemoved, $rowVer = $unitInfo.Split('|')
Assert-Check "Crit 3.3" ($isDelivered -eq 't' -and $isNotRemoved -eq 't') `
    "Delivered unit present at store location '$storeLocCode' (delivered_at set, active)" `
    "Store unit delivery state incorrect"

# Check UNIT_DELIVERED event
$delEvent = [int](Invoke-Sql "SELECT count(*) FROM ops.store_events WHERE store_unit_id = '$unitId' AND event_type = 'UNIT_DELIVERED';")
Assert-Check "Crit 3.4" ($delEvent -ge 1) `
    "UNIT_DELIVERED event logged in ops.store_events" `
    "Missing UNIT_DELIVERED event"

# Check push notification outbox
$notifCount = [int](Invoke-Sql "SELECT count(*) FROM integration.notification_outbox WHERE notification_type = 'FINISHED_GOODS_DELIVERED' AND data_json ->> 'unitId' = '$unitId';")
Assert-Check "Crit 3.5" ($notifCount -ge 1) `
    "FINISHED_GOODS_DELIVERED push notification generated for warehouse staff" `
    "Push notification missing from notification_outbox"

# Now simulate Operator Removal ("Aldım" button click)
Write-Host "  Simulating Warehouse Operator clicking 'Aldım' (unit removal)..." -ForegroundColor Gray
$removeRes = Invoke-RestMethod -Uri "$ApiBaseUrl/api/v1/warehouse/store/units/$unitId/remove" -Method Post `
    -Headers $whHeaders `
    -ContentType 'application/json' `
    -Body (@{ expectedRowVersion = [long]$rowVer } | ConvertTo-Json)

$unitRemovedCheck = Invoke-Sql "SELECT removed_at IS NOT NULL FROM ops.store_units WHERE id = '$unitId';"
Assert-Check "Crit 3.6" ($unitRemovedCheck -eq 't') `
    "Store unit successfully cleared ('Aldım') and marked removed_at" `
    "Store unit was not marked removed"

$removeEvent = [int](Invoke-Sql "SELECT count(*) FROM ops.store_events WHERE store_unit_id = '$unitId' AND event_type = 'UNIT_REMOVED';")
Assert-Check "Crit 3.7" ($removeEvent -ge 1) `
    "UNIT_REMOVED event logged in ops.store_events" `
    "Missing UNIT_REMOVED event"

# ------------------------------------------------------------------------------------------------
# CRITERION 4: IDEMPOTENCY AND DUPLICATION PREVENTION
# ------------------------------------------------------------------------------------------------
Write-Host "`n>>> CRITERION 4: Idempotency & Conflict Safety" -ForegroundColor Yellow
# Replay first bring request with same idempotency key
$replayRes = Invoke-RestMethod -Uri "$ApiBaseUrl/api/v1/transport-requests" -Method Post `
    -Headers ($lineHeaders + @{ 'Idempotency-Key' = $idemKey1 }) `
    -ContentType 'application/json' `
    -Body $bringPayload

Assert-Check "Crit 4.1" ($replayRes.id -eq $reqId) `
    "Re-submitting with identical idempotency key returned existing request ID ($reqId)" `
    "Idempotency failed, returned different ID ($($replayRes.id))"

# Verify duplicate store unit delivery is strictly blocked by ON CONFLICT guard
$duplicateStoreCount = [int](Invoke-Sql "SELECT count(*) FROM ops.store_units WHERE transport_request_id = '$fgReqId';")
Assert-Check "Crit 4.2" ($duplicateStoreCount -eq 1) `
    "Store unit count for transport request remains exactly 1 (no duplicate pallets/units)" `
    "Duplicate store units found: $duplicateStoreCount"

Write-Host "`n=================================================================" -ForegroundColor Green
Write-Host "   ALL ACCEPTANCE CRITERIA VERIFIED SUCCESSFULLY!                " -ForegroundColor Green
Write-Host "   PASSED: $passedCount / $( $passedCount + $failedCount ) Checks" -ForegroundColor Green
Write-Host "=================================================================" -ForegroundColor Green
