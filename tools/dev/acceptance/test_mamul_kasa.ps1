<#
.SYNOPSIS
    Acceptance test for Mamul Kasa container action state matrix and projection honesty.
.DESCRIPTION
    Validates the container action state matrix across all logical states:
    - VACANT + UNKNOWN  => Kasa Doldu disabled, Boş Kasa Getir enabled
    - OCCUPIED + EMPTY/FULL => Kasa Doldu requires configured FG; Boş Kasa Getir disabled
    - Active-transport and rendered badge honesty are covered by API/widget/browser tests.
    - Absence of deprecated labels: "Alan Boş" and "Kasam Doldu" must never appear.
    Reads action presence across 9 machines; tests the state matrix on SH-01-A only.
    Busy targets are rejected before mutation; full original runtime row is restored.
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

Write-Host "==================================================" -ForegroundColor Cyan
Write-Host "PHASE: MAMUL KASA BUSINESS ACTION ACCEPTANCE" -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan

# 1. Login
$loginRes = Invoke-RestMethod -Uri "$ApiBaseUrl/api/v1/auth/login" `
    -Method Post `
    -Headers @{ 'X-Device-Code' = 'DEV-SUPERVISOR' } `
    -ContentType 'application/json' `
    -Body '{"employeeNumber": "DEV-SUP"}'

$headers = @{
    'Authorization' = "Bearer $($loginRes.accessToken)"
    'X-Device-Code' = 'DEV-SUPERVISOR'
}

. (Join-Path $PSScriptRoot 'persistent_safety.ps1')
$context = (Invoke-AcceptanceSql @"
SELECT m.id, v.id, l.id, g.id, g.main_part_id, c.id, ls.id
FROM master.machines m
JOIN master.variants v ON v.machine_id = m.id AND v.code = '1'
JOIN master.layouts l ON l.machine_id = m.id AND l.code = 'CUSTOMER-PHYSICAL-V1'
JOIN master.product_groups g ON g.variant_id = v.id AND g.is_active
JOIN master.layout_slots ls ON ls.layout_id = l.id AND ls.is_enabled AND ls.slot_role NOT IN ('MACHINE','EMPTY_STORAGE')
JOIN master.layout_slot_parts lp ON lp.layout_slot_id = ls.id AND lp.part_id = g.main_part_id
JOIN master.container_types c ON c.code = 'ARABA'
WHERE m.code = 'SH-01-A' ORDER BY ls.display_order, ls.code, g.id LIMIT 1;
"@).Split('|')
if ($context.Count -ne 7) { throw 'Mamul Kasa context unavailable.' }
$mId, $vId, $lId, $pgId, $partId, $containerId, $slot1Id = $context
Assert-PersistentTargetIdle @($mId)
$originalSlot = Invoke-AcceptanceSql "SELECT row_to_json(s)::text FROM ops.slot_runtime_state s WHERE layout_slot_id = '$slot1Id';"

function Get-SH01Pos1 {
    $uri = "$ApiBaseUrl/api/v1/machines/$mId/container-positions?variantId=$vId&layoutId=$lId&productGroupId=$pgId&partId=$partId"
    $res = Invoke-RestMethod -Uri $uri -Method Get -Headers $headers
    return $res | Where-Object { $_.positionNumber -eq 1 }
}

# A persistent matrix cannot invent three physical WorkSite observations.
# Keep mandatory matrix assertions in isolated DB/API tests; report a blocker,
# never PASS, before modifying any business row on a mirrored physical target.
$operational = Invoke-RestMethod -Uri "$ApiBaseUrl/api/v1/machines/$mId/operational-context" -Method Get -Headers $headers
$physicalSlot = @($operational.slots | Where-Object { $_.id -eq $slot1Id })
if ($physicalSlot.Count -ne 1) { throw 'Physical slot preflight could not be verified.' }
if ($physicalSlot[0].executionReadiness.rdsWorkSiteMapped) {
    $reason = $physicalSlot[0].executionReadiness.readinessCode
    if (-not $reason) { $reason = 'PHYSICAL_MATRIX_REQUIRES_ISOLATION' }
    Write-Host "BLOCKED: PHYSICAL_STATE_UNAVAILABLE ($reason) — persistent WorkSite evidence cannot be fabricated for a state matrix; no runtime mutation performed." -ForegroundColor Yellow
    exit 2
}

$allPass = $true
$fieldBlockedStates = 0
function Test-FullAvailability($Position) {
    if ($Position.fullAction.enabled) { return $true }
    if ($Position.fullAction.disabledCode -eq 'FINISHED_GOODS_DESTINATION_NOT_CONFIGURED' -and
        -not [string]::IsNullOrWhiteSpace($Position.fullAction.disabledMessage)) {
        $script:fieldBlockedStates++
        Write-Host '  FIELD BLOCKED: finished-goods destination is absent; full action correctly disabled.' -ForegroundColor Yellow
        return $true
    }
    return $false
}

try {
    Write-Host "`n--- Part 1: State Matrix Acceptance on Logical Position 1 ---" -ForegroundColor Yellow

    # State A: VACANT + UNKNOWN
    & $psql -h $HostName -p $Port -U $User -d $Database -c "UPDATE ops.slot_runtime_state SET occupancy_state = 'VACANT', load_state = 'UNKNOWN', current_part_id = NULL, current_container_type_id = NULL WHERE layout_slot_id = '$slot1Id';" | Out-Null
    $pos = Get-SH01Pos1
    $fullOk = ($pos.fullAction -and -not $pos.fullAction.enabled -and $pos.fullAction.disabledCode -eq 'OCCUPIED_CONTAINER_REQUIRED')
    $emptyOk = ($pos.emptyAction -and $pos.emptyAction.enabled)
    Write-Host "State A [VACANT]:"
    Write-Host ("  Kasa Doldu:     Enabled={0} (DisabledCode={1})" -f $pos.fullAction.enabled, $pos.fullAction.disabledCode)
    Write-Host ("  Boş Kasa Getir: Enabled={0}" -f $pos.emptyAction.enabled)
    if ($fullOk -and $emptyOk) {
        Write-Host "  -> PASS: Kasa Doldu disabled, Boş Kasa Getir enabled" -ForegroundColor Green
    } else {
        Write-Host "  -> FAIL" -ForegroundColor Red
        $allPass = $false
    }

    # State B: OCCUPIED + EMPTY
    & $psql -h $HostName -p $Port -U $User -d $Database -c "UPDATE ops.slot_runtime_state SET occupancy_state = 'OCCUPIED', load_state = 'EMPTY', current_part_id = '$partId', current_container_type_id = '$containerId' WHERE layout_slot_id = '$slot1Id';" | Out-Null
    $pos = Get-SH01Pos1
    $fullOk = Test-FullAvailability $pos
    $emptyOk = ($pos.emptyAction -and -not $pos.emptyAction.enabled -and $pos.emptyAction.disabledCode -eq 'CONTAINER_POSITION_OCCUPIED')
    Write-Host "`nState B [OCCUPIED + EMPTY]:"
    Write-Host ("  Kasa Doldu:     Enabled={0}" -f $pos.fullAction.enabled)
    Write-Host ("  Boş Kasa Getir: Enabled={0} (DisabledCode={1})" -f $pos.emptyAction.enabled, $pos.emptyAction.disabledCode)
    if ($fullOk -and $emptyOk) {
        Write-Host '  -> PASS: FULL availability respects destination configuration; EMPTY rejects occupied position.' -ForegroundColor Green
    } else {
        Write-Host "  -> FAIL" -ForegroundColor Red
        $allPass = $false
    }

    # State C: OCCUPIED + FULL
    & $psql -h $HostName -p $Port -U $User -d $Database -c "UPDATE ops.slot_runtime_state SET occupancy_state = 'OCCUPIED', load_state = 'FULL', current_part_id = '$partId', current_container_type_id = '$containerId' WHERE layout_slot_id = '$slot1Id';" | Out-Null
    $pos = Get-SH01Pos1
    $fullOk = Test-FullAvailability $pos
    $emptyOk = ($pos.emptyAction -and -not $pos.emptyAction.enabled -and $pos.emptyAction.disabledCode -eq 'CONTAINER_POSITION_OCCUPIED')
    Write-Host "`nState C [OCCUPIED + FULL]:"
    Write-Host ("  Kasa Doldu:     Enabled={0} (Triggers REMOVE_FULL)" -f $pos.fullAction.enabled)
    Write-Host ("  Boş Kasa Getir: Enabled={0} (DisabledCode={1})" -f $pos.emptyAction.enabled, $pos.emptyAction.disabledCode)
    if ($fullOk -and $emptyOk) {
        Write-Host '  -> PASS: FULL availability respects destination configuration; EMPTY rejects occupied position.' -ForegroundColor Green
    } else {
        Write-Host "  -> FAIL" -ForegroundColor Red
        $allPass = $false
    }

    # Part 2: API copy check (rendered labels are independently tested in Flutter/browser).
    Write-Host "`n--- Part 2: Forbidden Labels Absence Audit ---" -ForegroundColor Yellow
    $allJson = (Invoke-RestMethod -Uri "$ApiBaseUrl/api/v1/machines/$mId/container-positions?variantId=$vId&layoutId=$lId&productGroupId=$pgId&partId=$partId" -Headers $headers) | ConvertTo-Json -Depth 5
    $forbiddenFound = ($allJson -match 'Alan Boş' -or $allJson -match 'Kasam Doldu')
    if (-not $forbiddenFound) {
        Write-Host "PASS: 'Alan Boş' and 'Kasam Doldu' labels are completely absent." -ForegroundColor Green
    } else {
        Write-Host "FAIL: Deprecated label detected in API output!" -ForegroundColor Red
        $allPass = $false
    }

    # Part 3: Multi-Machine Position Action Integrity
    Write-Host "`n--- Part 3: Multi-Machine Container Position Action Coverage ---" -ForegroundColor Yellow
    $machinesSql = @"
SELECT 
    m.id::text, 
    m.code, 
    rs.active_variant_id::text, 
    rs.active_layout_id::text, 
    pg.id::text, 
    p.id::text
FROM master.machines m
JOIN ops.machine_runtime_state rs ON rs.machine_id = m.id
JOIN master.variants v ON v.id = rs.active_variant_id
JOIN master.product_groups pg ON pg.variant_id = v.id AND pg.is_active
JOIN master.parts p ON p.id = pg.main_part_id AND p.is_active
WHERE m.is_active
ORDER BY m.code;
"@
    $mRows = @(& $psql -h $HostName -p $Port -U $User -d $Database -A -F ',' -t -c $machinesSql) |
        Where-Object { $_ -and -not ($_ -match '\([0-9]+ sat') }
    
    $seen = @{}
    foreach ($line in $mRows) {
        $c = $line.Trim().Split(',')
        $currCode = $c[1]
        if ($seen.ContainsKey($currCode)) { continue }
        $seen[$currCode] = $true

        $posUri = "$ApiBaseUrl/api/v1/machines/$($c[0])/container-positions?variantId=$($c[2])&layoutId=$($c[3])&productGroupId=$($c[4])&partId=$($c[5])"
        try {
            $positions = Invoke-RestMethod -Uri $posUri -Method Get -Headers $headers
            $allPositionsHaveBothActions = $true
            foreach ($p in $positions) {
                if ($null -eq $p.fullAction -or $null -eq $p.emptyAction) {
                    $allPositionsHaveBothActions = $false
                }
            }
            if ($positions.Count -gt 0) {
                if ($allPositionsHaveBothActions) {
                    Write-Host ("- {0,-20} : {1} position(s) verified (both actions present)" -f $currCode, $positions.Count) -ForegroundColor Green
                } else {
                    Write-Host ("- {0,-20} : Missing action object on returned positions" -f $currCode) -ForegroundColor Red
                    $allPass = $false
                }
            } else {
                Write-Host ("- {0,-20} : 0 DROP positions configured (expected in current customer master)" -f $currCode) -ForegroundColor Gray
            }
        } catch {
            Write-Host ("- {0,-20} : Error: $($_.Exception.Message)" -f $currCode) -ForegroundColor Red
            $allPass = $false
        }
    }
}
finally {
    Restore-RuntimeRow $originalSlot

}

if ($allPass) {
    Write-Host "`nRESULT: PASS (projection semantics verified; $fieldBlockedStates states FIELD BLOCKED, executable actions covered by DB API tests)" -ForegroundColor Green
    exit 0
} else {
    Write-Host "`nRESULT: FAIL" -ForegroundColor Red
    exit 1
}
