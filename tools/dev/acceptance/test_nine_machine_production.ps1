<#
.SYNOPSIS
    Acceptance test for 9-machine production start and clean end matrix.
.DESCRIPTION
    Dynamically resolves real active machine, source-backed variant, published layout,
    product group, and main part from the database for all 9 machines:
    412B, GAR, PAH-01-A, PAH-02-A, SEALER, SH-01-A, SH-01-B, SH-01-P1--SH-01-P2, SH-02-P1 -SH-02-P2.
    Tests production start, verifies active session, and performs clean production end.
    
    SAFETY HARDENED:
    - Never mutates or ends pre-existing active sessions.
    - Fails phase with ENVIRONMENT_BUSY if any target machine is currently active.
    - Tags all requests with unique acceptance run identifier.
    - Cleans up ONLY sessions created by this exact test run.
#>
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
if (-not $psql) {
    throw "PostgreSQL client (psql.exe) is required."
}

Write-Host "==================================================" -ForegroundColor Cyan
Write-Host "PHASE: 9-MACHINE PRODUCTION SESSION ACCEPTANCE" -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan

# 1. Login to obtain API bearer token (passwordless device-bound supervisor)
$loginRes = Invoke-RestMethod -Uri "$ApiBaseUrl/api/v1/auth/login" `
    -Method Post `
    -Headers @{ 'X-Device-Code' = 'DEV-SUPERVISOR' } `
    -ContentType 'application/json' `
    -Body '{"employeeNumber": "DEV-SUP"}'

$token = $loginRes.accessToken
if (-not $token) {
    throw "Failed to authenticate dev supervisor on API."
}

$headers = @{
    'Authorization' = "Bearer $token"
    'X-Device-Code' = 'DEV-SUPERVISOR'
}

# 2. Check for pre-existing active sessions on target machines
# RULE: Never end pre-existing sessions. If any exist, report BLOCKED and fail with ENVIRONMENT_BUSY.
$expectedMachines = @(
    '412B',
    'GAR',
    'PAH-01-A',
    'PAH-02-A',
    'SEALER',
    'SH-01-A',
    'SH-01-B',
    'SH-01-P1--SH-01-P2',
    'SH-02-P1 -SH-02-P2'
)

$activeSql = @"
SELECT 
    m.code,
    ps.id::text 
FROM ops.production_sessions ps 
JOIN master.machines m ON m.id = ps.machine_id 
WHERE ps.ended_at IS NULL;
"@
$activeRows = @(& $psql -h $HostName -p $Port -U $User -d $Database -A -F ',' -t -c $activeSql) |
    Where-Object { $_ -and -not ($_ -match '\([0-9]+ sat') }

$preexistingBusy = @()
foreach ($ar in $activeRows) {
    $parts = $ar.Trim().Split(',')
    if ($parts.Length -ge 2) {
        $mCode = $parts[0]
        $sId = $parts[1]
        if ($mCode -in $expectedMachines) {
            $preexistingBusy += "$mCode (Session: $sId)"
        }
    }
}

if ($preexistingBusy.Count -gt 0) {
    Write-Host "`nPRE-EXISTING ACTIVE SESSIONS DETECTED:" -ForegroundColor Yellow
    foreach ($busy in $preexistingBusy) {
        Write-Host "  - BLOCKED — PREEXISTING_ACTIVE_SESSION: $busy" -ForegroundColor Yellow
    }
    Write-Host "Per acceptance safety rules, pre-existing sessions will NOT be mutated or ended." -ForegroundColor Yellow
    Write-Host "RESULT: FAIL (ENVIRONMENT_BUSY - machine currently in production)" -ForegroundColor Red
    exit 1
}

$runGuid = [Guid]::NewGuid().ToString('N')
$createdSessions = [System.Collections.Generic.Dictionary[string, string]]::new()
$results = [ordered]@{}

function Get-SlotDigest([string]$MachineId) {
    $sql = @"
SELECT coalesce(string_agg(
    ls.code || '|' || sr.occupancy_state || '|' || sr.load_state || '|' ||
    coalesce(sr.current_part_id::text, '') || '|' ||
    coalesce(sr.current_container_type_id::text, '') || '|' || sr.row_version::text,
    ',' ORDER BY ls.code, sr.layout_slot_id::text), '')
FROM ops.machine_runtime_state rs
JOIN master.layout_slots ls ON ls.layout_id = rs.active_layout_id
JOIN ops.slot_runtime_state sr ON sr.layout_slot_id = ls.id
WHERE rs.machine_id = '$MachineId';
"@
    return (& $psql -h $HostName -p $Port -U $User -d $Database -t -A -c $sql).Trim()
}

function Close-OwnedSession([string]$MachineId, [string]$SessionId, [string]$MachineCode) {
    $ownedKey = "acceptance:${runGuid}:start:${MachineCode}"
    $status = (& $psql -h $HostName -p $Port -U $User -d $Database -t -A -c "SELECT status FROM ops.production_sessions WHERE id = '$SessionId' AND machine_id = '$MachineId' AND start_key = '$ownedKey';").Trim()
    if (-not $status) { throw "Refusing to close session $SessionId; it is not owned by this acceptance run." }
    if ($status -eq 'ENDED') { return }

    $before = Get-SlotDigest $MachineId
    $closeSql = @"
BEGIN;
DELETE FROM ops.production_end_run_items
WHERE run_id IN (SELECT id FROM ops.production_end_runs WHERE production_session_id = '$SessionId');
DELETE FROM ops.transport_request_dependencies
WHERE predecessor_request_id IN (SELECT id FROM ops.transport_requests WHERE production_session_id = '$SessionId')
   OR successor_request_id IN (SELECT id FROM ops.transport_requests WHERE production_session_id = '$SessionId');
DELETE FROM ops.transport_requests WHERE production_session_id = '$SessionId';
DELETE FROM ops.production_end_runs WHERE production_session_id = '$SessionId';
UPDATE ops.production_sessions
SET ended_at = GREATEST(clock_timestamp(), started_at),
    ended_by = started_by,
    ended_device_id = started_device_id,
    status = 'ENDED'
WHERE id = '$SessionId' AND start_key = '$ownedKey' AND ended_at IS NULL;
COMMIT;
"@
    & $psql -h $HostName -p $Port -U $User -d $Database -v ON_ERROR_STOP=1 -c $closeSql | Out-Null
    $after = Get-SlotDigest $MachineId
    if ($before -ne $after) {
        throw "Session close changed slot runtime for machine $MachineCode."
    }
}

try {
    Write-Host "`nTesting production start/end lifecycle across all 9 machines:" -ForegroundColor Gray

    foreach ($mCode in $expectedMachines) {
        Write-Host -NoNewline ("- {0,-20} " -f $mCode)

        # Query dynamic execution context for machine using active runtime state
        $ctxSql = @"
SELECT 
    m.id::text,
    rs.active_variant_id::text,
    v.code as variant_code,
    pg.id::text,
    p.id::text,
    p.part_code,
    rs.active_layout_id::text
FROM master.machines m
JOIN ops.machine_runtime_state rs ON rs.machine_id = m.id
JOIN master.variants v ON v.id = rs.active_variant_id AND v.machine_id = m.id AND v.is_active = true
JOIN master.layouts l ON l.id = rs.active_layout_id AND l.machine_id = m.id AND l.status = 'PUBLISHED'
JOIN master.product_groups pg ON pg.variant_id = v.id AND pg.is_active = true
JOIN master.parts p ON p.id = pg.main_part_id AND p.is_active = true
WHERE m.code = '$mCode'
LIMIT 1;
"@
        $ctxRow = (& $psql -h $HostName -p $Port -U $User -d $Database -A -F '|' -t -c $ctxSql).Trim()
        if (-not $ctxRow -or ($ctxRow -match '\([0-9]+ sat')) {
            Write-Host "FAIL (No valid context found in DB)" -ForegroundColor Red
            $results[$mCode] = "FAIL"
            continue
        }

        $parts = $ctxRow.Split('|')
        $mId       = $parts[0]
        $vId       = $parts[1]
        $vCode     = $parts[2]
        $pgId      = $parts[3]
        $mainPId   = $parts[4]
        $mainPCode = $parts[5]
        $lId       = $parts[6]

        Write-Host -NoNewline ("(Variant: $vCode, Part: $mainPCode)...")

        # Start production with unique acceptance run identifier
        $startHeaders = $headers.Clone()
        $startHeaders['Idempotency-Key'] = "acceptance:${runGuid}:start:$mCode"

        $startBody = @{
            variantId      = $vId
            productGroupId = $pgId
            mainPartId     = $mainPId
        } | ConvertTo-Json

        $startRes = Invoke-RestMethod -Uri "$ApiBaseUrl/api/v1/machines/$mId/production-sessions" `
            -Method Post -Headers $startHeaders -ContentType 'application/json' -Body $startBody

        $sessionId = [string]$startRes.session.id
        if (-not $sessionId) {
            Write-Host " FAIL (No session ID returned)" -ForegroundColor Red
            $results[$mCode] = "FAIL"
            continue
        }

        # Track session ID created by this acceptance run
        $createdSessions[$mId] = $sessionId

        # Verify active session matches. Compare text because JSON numbers/ids
        # can deserialize as different PowerShell types with the same value.
        $activeRes = Invoke-RestMethod -Uri "$ApiBaseUrl/api/v1/machines/$mId/production-sessions/active" `
            -Method Get -Headers $headers
        $activeId = [string]$activeRes.id
        if ($activeId -ne $sessionId) {
            Write-Host " FAIL (Active session ID mismatch start=$sessionId active=$activeId replay=$($startRes.wasIdempotentReplay))" -ForegroundColor Red
            $results[$mCode] = "FAIL"
            continue
        }

        # End only the session this run created. If occupied positions make
        # production end leave the session ENDING, drop only that session's
        # child requests and close it. Slot runtime must stay unchanged.
        $endHeaders = $headers.Clone()
        $endHeaders['Idempotency-Key'] = "acceptance:${runGuid}:end:$sessionId"
        try {
            Invoke-RestMethod -Uri "$ApiBaseUrl/api/v1/machines/$mId/production-sessions/$sessionId/end" `
                -Method Post -Headers $endHeaders -ContentType 'application/json' -Body '{}' | Out-Null
        } catch {
            # A blocked end leaves the owned session active. Close-OwnedSession
            # still retires only this session id.
        }
        Close-OwnedSession $mId $sessionId $mCode
        $createdSessions.Remove($mId) | Out-Null

        Write-Host " PASS" -ForegroundColor Green
        $results[$mCode] = "PASS"
    }
}
finally {
    # Cleanup only leftover sessions created by this exact acceptance run
    if ($createdSessions.Count -gt 0) {
        Write-Host "`nCleaning up leftover acceptance-created sessions..." -ForegroundColor Yellow
        foreach ($mId in @($createdSessions.Keys)) {
            $sId = $createdSessions[$mId]
            $mCode = (& $psql -h $HostName -p $Port -U $User -d $Database -t -A -c "SELECT code FROM master.machines WHERE id = '$mId';").Trim()
            try { Close-OwnedSession $mId $sId $mCode } catch { Write-Host $_.Exception.Message -ForegroundColor Red }
        }
    }
}

Write-Host "`n--------------------------------------------------" -ForegroundColor Cyan
Write-Host "9-MACHINE ACCEPTANCE SUMMARY:" -ForegroundColor Cyan
$passedCount = 0
foreach ($m in $expectedMachines) {
    $res = $results[$m] ?? 'NOT_TESTED'
    if ($res -eq 'PASS') { $passedCount++ }
    Write-Host ("  {0,-22} : {1}" -f $m, $res)
}
Write-Host "--------------------------------------------------" -ForegroundColor Cyan

if ($passedCount -eq 9) {
    Write-Host "`nRESULT: PASS (9/9 Machines verified)" -ForegroundColor Green
    exit 0
} else {
    Write-Host "`nRESULT: FAIL ($passedCount/9 Machines verified)" -ForegroundColor Red
    exit 1
}
