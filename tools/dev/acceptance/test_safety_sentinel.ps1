<#
.SYNOPSIS
    Safety regression acceptance test verifying non-destructive preservation of pre-existing state.
.DESCRIPTION
    Proves requirement 12:
    Given:
    - a pre-existing active production session;
    - a pre-existing transport request;
    - a pre-existing shared endpoint binding;
    - a DRAFT navigation profile;
    - existing slot state;
    running persistent acceptance suites does NOT:
    - end the pre-existing session;
    - delete the request;
    - delete or change the binding;
    - publish the profile;
    - mark geometry verified;
    - modify the slot except test-owned state;
    - alter production priorities.
    Compares exact before and after snapshots.
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
if ($Database -ne 'tkg_pda_acceptance') {
    throw 'Synthetic sentinel setup requires isolated tkg_pda_acceptance database.'
}
if ($ApiBaseUrl.TrimEnd('/') -ne 'http://127.0.0.1:5001') {
    throw 'Synthetic sentinel requires its dedicated isolated API on http://127.0.0.1:5001.'
}
$psql = 'C:\Program Files\PostgreSQL\18\bin\psql.exe'
if (-not (Test-Path -LiteralPath $psql)) {
    $psql = (Get-Command psql.exe -ErrorAction SilentlyContinue)?.Source
}
$scriptDir = $PSScriptRoot

Write-Host "==================================================" -ForegroundColor Cyan
Write-Host "PHASE: ACCEPTANCE SUITE SAFETY REGRESSION SENTINEL TEST" -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan
Write-Host "Database: $Database"

# 1. Fetch prerequisite foreign keys for sentinel creation
$machSql = @"
SELECT 
    m.id::text,
    v.id::text,
    pg.id::text,
    p.id::text,
    l.id::text,
    s.id::text,
    tt.id::text,
    np.id::text
FROM master.machines m
JOIN master.variants v ON v.machine_id = m.id AND v.code = '1'
JOIN master.product_groups pg ON pg.variant_id = v.id
JOIN master.parts p ON p.id = pg.main_part_id
JOIN master.layouts l ON l.machine_id = m.id AND l.status = 'PUBLISHED'
JOIN master.layout_slots s ON s.layout_id = l.id
JOIN master.task_types tt ON tt.code = 'REMOVE_FULL'
CROSS JOIN (SELECT id FROM master.navigation_profiles WHERE code = 'DEV-RDS-SIM-MAP-A' LIMIT 1) np
WHERE m.code = '412B'
LIMIT 1;
"@
$machRow = (& $psql -h $HostName -p $Port -U $User -d $Database -t -A -F '|' -c $machSql).Trim().Split('|')
$machId   = $machRow[0]
$vId      = $machRow[1]
$pgId     = $machRow[2]
$partId   = $machRow[3]
$layoutId = $machRow[4]
$slotId   = $machRow[5]
$ttId     = $machRow[6]
$simNavId = $machRow[7]

$userId = (& $psql -h $HostName -p $Port -U $User -d $Database -t -A -c "SELECT id::text FROM auth.users WHERE username = 'dev-line' LIMIT 1;").Trim()
$devId  = (& $psql -h $HostName -p $Port -U $User -d $Database -t -A -c "SELECT id::text FROM audit.client_devices WHERE device_code = 'DEV-LINE' LIMIT 1;").Trim()

$wsRow = (& $psql -h $HostName -p $Port -U $User -d $Database -t -A -F '|' -c "SELECT id::text, navigation_profile_id::text FROM integration.rds_worksites LIMIT 1;").Trim().Split('|')
$worksiteId = $wsRow[0]
$wsNavId    = $wsRow[1]

# Generate unique sentinel IDs
$sentinelSessionId = [Guid]::NewGuid().ToString()
$sentinelRequestId = [Guid]::NewGuid().ToString()
$sentinelBindingId = [Guid]::NewGuid().ToString()
$sentinelNavId     = [Guid]::NewGuid().ToString()
$sentinelReqNo     = 888888

Write-Host "`nSeeding sentinel records in $Database..."
Write-Host "  Sentinel Session ID: $sentinelSessionId (Machine 412B)"
Write-Host "  Sentinel Request ID: $sentinelRequestId"
Write-Host "  Sentinel Binding ID: $sentinelBindingId"
Write-Host "  Sentinel NAV ID:     $sentinelNavId (DRAFT profile)"

# 2. Seed sentinel artifacts into persistent DB
try {
& $psql -h $HostName -p $Port -U $User -d $Database -v ON_ERROR_STOP=1 -c @"
-- 1. Pre-existing active production session
INSERT INTO ops.production_sessions (id, machine_id, variant_id, product_group_id, main_part_id, start_key, started_by, started_device_id, status, started_at, ended_at)
VALUES ('$sentinelSessionId', '$machId', '$vId', '$pgId', '$partId', 'sentinel:$sentinelSessionId', '$userId', '$devId', 'ACTIVE', now(), NULL);

-- 2. Pre-existing transport request
INSERT INTO ops.transport_requests (id, request_origin, requested_by, machine_id, variant_id, layout_id, source_slot_id, task_type_id, status, priority, correlation_id, idempotency_key)
VALUES ('$sentinelRequestId', 'LINE_OPERATOR', '$userId', '$machId', '$vId', '$layoutId', '$slotId', '$ttId', 'QUEUED', 45, gen_random_uuid(), 'sentinel:$sentinelRequestId');

-- 3. Pre-existing shared endpoint binding
INSERT INTO integration.profile_shared_endpoint_bindings (id, navigation_profile_id, endpoint_class, business_code, store_location_id, rds_worksite_id)
VALUES ('$sentinelBindingId', '$wsNavId', 'FINISHED_GOODS', 'SENTINEL-FG', NULL, '$worksiteId');

-- 4. Pre-existing DRAFT navigation profile with unverified geometry
INSERT INTO master.navigation_profiles (id, machine_id, code, name, version, status, config_json)
VALUES ('$sentinelNavId', '$machId', 'SENTINEL-DRAFT-NAV', 'Sentinel Nav', 99, 'DRAFT', '{"geometryVerified": false, "commissioningState": "PLANNED"}'::jsonb);
"@ | Out-Null

function Get-SentinelSnapshot {
    $sessionSnap = (& $psql -h $HostName -p $Port -U $User -d $Database -t -A -F '|' -c "SELECT status, coalesce(ended_at::text, 'NULL') FROM ops.production_sessions WHERE id = '$sentinelSessionId';").Trim()
    $reqSnap     = (& $psql -h $HostName -p $Port -U $User -d $Database -t -A -F '|' -c "SELECT status, priority, request_no FROM ops.transport_requests WHERE id = '$sentinelRequestId';").Trim()
    $bindingSnap = (& $psql -h $HostName -p $Port -U $User -d $Database -t -A -F '|' -c "SELECT endpoint_class, business_code FROM integration.profile_shared_endpoint_bindings WHERE id = '$sentinelBindingId';").Trim()
    $navSnap     = (& $psql -h $HostName -p $Port -U $User -d $Database -t -A -F '|' -c "SELECT status, config_json->>'geometryVerified', config_json->>'commissioningState' FROM master.navigation_profiles WHERE id = '$sentinelNavId';").Trim()
    $slotSnap    = (& $psql -h $HostName -p $Port -U $User -d $Database -t -A -F '|' -c "SELECT occupancy_state, load_state, coalesce(current_part_id::text,'NULL'), coalesce(current_container_type_id::text,'NULL'), row_version::text FROM ops.slot_runtime_state WHERE layout_slot_id = '$slotId';").Trim()
    $priSnap     = (& $psql -h $HostName -p $Port -U $User -d $Database -t -A -c "SELECT md5(coalesce(string_agg(operation_code || ':' || priority::text, ',' ORDER BY operation_code), '')) FROM master.production_end_priorities;").Trim()
    $customerNav = (& $psql -h $HostName -p $Port -U $User -d $Database -t -A -c "SELECT md5(coalesce(string_agg(code || '|' || status || '|' || coalesce(published_at::text,'') || '|' || config_json::text, ',' ORDER BY code), '')) FROM master.navigation_profiles WHERE code <> 'SENTINEL-DRAFT-NAV';").Trim()
    $runtimeNav  = (& $psql -h $HostName -p $Port -U $User -d $Database -t -A -c "SELECT md5(coalesce(string_agg(machine_id::text || '|' || coalesce(active_navigation_id::text,''), ',' ORDER BY machine_id), '')) FROM ops.machine_runtime_state;").Trim()
    $deviceSnap  = (& $psql -h $HostName -p $Port -U $User -d $Database -t -A -c "SELECT md5(coalesce(string_agg(id::text || '|' || device_code || '|' || coalesce(updated_at::text,''), ',' ORDER BY id), '')) FROM audit.client_devices;").Trim()

    return [PSCustomObject]@{
        Session = $sessionSnap
        Request = $reqSnap
        Binding = $bindingSnap
        Nav     = $navSnap
        Slot    = $slotSnap
        PriorityDigest = $priSnap
        CustomerNav = $customerNav
        RuntimeNav = $runtimeNav
        Devices = $deviceSnap
    }
}

    # 3. Capture exact BEFORE snapshot
    $snapBefore = Get-SentinelSnapshot
    Write-Host "`nCaptured BEFORE snapshot of all sentinel entities:"
    Write-Host "  Session:         $($snapBefore.Session)"
    Write-Host "  Request:         $($snapBefore.Request)"
    Write-Host "  Binding:         $($snapBefore.Binding)"
    Write-Host "  Profile:         $($snapBefore.Nav)"
    Write-Host "  Slot:            $($snapBefore.Slot)"
    Write-Host "  Priority Digest: $($snapBefore.PriorityDigest)"

    # 4. Execute Persistent Acceptance Suites against the environment
    Write-Host "`n--- Running Persistent Acceptance Harnesses Against DB with Active Sentinels ---" -ForegroundColor Yellow

    # 4a. Nine-Machine production runner:
    # Must detect pre-existing active session on machine 412B and exit with ENVIRONMENT_BUSY without touching it!
    Write-Host "`n1. Testing 9-Machine Pre-existing Active Session Safety Guard..."
    $nmScript = Join-Path $scriptDir 'test_nine_machine_production.ps1'
    & pwsh -NoProfile -File $nmScript -ApiBaseUrl $ApiBaseUrl -HostName $HostName -Port $Port -Database $Database -User $User
    $nmExit = $LASTEXITCODE

    if ($nmExit -eq 2) {
        Write-Host "PASS: 9-Machine runner safely rejected execution due to active sentinel session (exited with ENVIRONMENT_BUSY code $nmExit)." -ForegroundColor Green
    } else {
        Write-Host "FAIL: 9-Machine runner did not reject active session on machine 412B!" -ForegroundColor Red
        exit 1
    }

    # 4b. Persistent Production End commissioning blocker test
    Write-Host "`n2. Running Persistent Production End Commissioning Blocker Test..."
    $peScript = Join-Path $scriptDir 'test_production_end.ps1'
    & pwsh -NoProfile -File $peScript -ApiBaseUrl $ApiBaseUrl -HostName $HostName -Port $Port -Database $Database -User $User
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAIL: Persistent Production End failed!" -ForegroundColor Red
        exit 1
    }

    # 4c. Persistent Repeated ALT demand test
    Write-Host "`n3. Running Persistent Repeated ALT Invariant Test..."
    $altScript = Join-Path $scriptDir 'test_repeated_alt_demand.ps1'
    & pwsh -NoProfile -File $altScript -ApiBaseUrl $ApiBaseUrl -HostName $HostName -Port $Port -Database $Database -User $User
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAIL: Persistent Repeated ALT failed!" -ForegroundColor Red
        exit 1
    }

    # 4d. Mamul Kasa state matrix test
    Write-Host "`n4. Running Mamul Kasa State Matrix Test..."
    $mkScript = Join-Path $scriptDir 'test_mamul_kasa.ps1'
    & pwsh -NoProfile -File $mkScript -ApiBaseUrl $ApiBaseUrl -HostName $HostName -Port $Port -Database $Database -User $User
    if ($LASTEXITCODE -notin @(0, 2)) {
        Write-Host "FAIL: Mamul Kasa failed!" -ForegroundColor Red
        exit 1
    }

    # 5. Capture exact AFTER snapshot
    $snapAfter = Get-SentinelSnapshot
    Write-Host "`nCaptured AFTER snapshot of all sentinel entities:"
    Write-Host "  Session:         $($snapAfter.Session)"
    Write-Host "  Request:         $($snapAfter.Request)"
    Write-Host "  Binding:         $($snapAfter.Binding)"
    Write-Host "  Profile:         $($snapAfter.Nav)"
    Write-Host "  Slot:            $($snapAfter.Slot)"
    Write-Host "  Priority Digest: $($snapAfter.PriorityDigest)"

    # 6. Verify Byte-for-Business-State Equality
    Write-Host "`n--- Verifying Exact Sentinel Preservation ---" -ForegroundColor Yellow

    $passed = $true

    if ($snapBefore.Session -eq $snapAfter.Session -and $snapAfter.Session.StartsWith("ACTIVE|NULL")) {
        Write-Host "PASS [1/6]: Pre-existing active session was NOT ended (status is ACTIVE, ended_at is NULL)." -ForegroundColor Green
    } else {
        Write-Host "FAIL [1/6]: Pre-existing active session was mutated or ended! Before: '$($snapBefore.Session)', After: '$($snapAfter.Session)'" -ForegroundColor Red
        $passed = $false
    }

    if ($snapBefore.Request -eq $snapAfter.Request -and $snapAfter.Request.Length -gt 0) {
        Write-Host "PASS [2/6]: Pre-existing transport request was NOT deleted or mutated." -ForegroundColor Green
    } else {
        Write-Host "FAIL [2/6]: Pre-existing transport request was deleted or modified! Before: '$($snapBefore.Request)', After: '$($snapAfter.Request)'" -ForegroundColor Red
        $passed = $false
    }

    if ($snapBefore.Binding -eq $snapAfter.Binding -and $snapAfter.Binding.Length -gt 0) {
        Write-Host "PASS [3/6]: Pre-existing shared endpoint binding was NOT deleted or altered." -ForegroundColor Green
    } else {
        Write-Host "FAIL [3/6]: Pre-existing shared endpoint binding was deleted or modified! Before: '$($snapBefore.Binding)', After: '$($snapAfter.Binding)'" -ForegroundColor Red
        $passed = $false
    }

    if ($snapBefore.Nav -eq $snapAfter.Nav -and $snapAfter.Nav -eq "DRAFT|false|PLANNED") {
        Write-Host "PASS [4/6]: DRAFT navigation profile was NOT published and geometry was NOT marked verified." -ForegroundColor Green
    } else {
        Write-Host "FAIL [4/6]: Navigation profile was published or mutated! Before: '$($snapBefore.Nav)', After: '$($snapAfter.Nav)'" -ForegroundColor Red
        $passed = $false
    }

    if ($snapBefore.Slot -eq $snapAfter.Slot) {
        Write-Host "PASS [5/6]: Existing machine slot state was completely preserved." -ForegroundColor Green
    } else {
        Write-Host "FAIL [5/6]: Machine slot state was modified! Before: '$($snapBefore.Slot)', After: '$($snapAfter.Slot)'" -ForegroundColor Red
        $passed = $false
    }

    if ($snapBefore.PriorityDigest -eq $snapAfter.PriorityDigest) {
        Write-Host "PASS [6/6]: Production end priorities were completely preserved." -ForegroundColor Green
    } else {
        Write-Host "FAIL [6/6]: Production end priorities were altered! Before: '$($snapBefore.PriorityDigest)', After: '$($snapAfter.PriorityDigest)'" -ForegroundColor Red
        $passed = $false
    }

    if ($snapBefore.CustomerNav -eq $snapAfter.CustomerNav -and $snapBefore.RuntimeNav -eq $snapAfter.RuntimeNav) {
        Write-Host "PASS: Customer navigation profiles and machine active navigation ids are unchanged." -ForegroundColor Green
    } else {
        Write-Host "FAIL: Customer navigation profile or active navigation changed." -ForegroundColor Red
        $passed = $false
    }

    if ($snapBefore.Devices -eq $snapAfter.Devices -and $snapBefore.Devices.Length -gt 0) {
        Write-Host "PASS: Device bindings are unchanged." -ForegroundColor Green
    } else {
        Write-Host "FAIL: Device bindings changed. Before: '$($snapBefore.Devices)', After: '$($snapAfter.Devices)'" -ForegroundColor Red
        $passed = $false
    }

    if (-not $passed) {
        Write-Host "`nRESULT: FAIL (Sentinel safety regression check failed)" -ForegroundColor Red
        exit 1
    }

    Write-Host "`nRESULT: PASS (All pre-existing sentinel entities verified byte-for-business-state identical)" -ForegroundColor Green
    exit 0

} finally {
    # 7. Cleanup ONLY the exact sentinel records by ID
    Write-Host "`nCleaning up ONLY exact sentinel IDs..."
    & $psql -h $HostName -p $Port -U $User -d $Database -c @"
DELETE FROM ops.transport_requests WHERE id = '$sentinelRequestId';
DELETE FROM ops.production_sessions WHERE id = '$sentinelSessionId';
DELETE FROM integration.profile_shared_endpoint_bindings WHERE id = '$sentinelBindingId';
DELETE FROM master.navigation_profiles WHERE id = '$sentinelNavId';
"@ | Out-Null
    Write-Host "Cleaned up exact sentinel records: $sentinelRequestId, $sentinelSessionId, $sentinelBindingId, $sentinelNavId."
}
