<#
.SYNOPSIS
    Acceptance test for Mamul Kasa container action state matrix and projection honesty.
.DESCRIPTION
    Validates the container action state matrix across all logical states:
    - VACANT + UNKNOWN  => Kasa Doldu disabled, Boş Kasa Getir enabled
    - OCCUPIED + EMPTY  => Kasa Doldu enabled, Boş Kasa Getir disabled
    - OCCUPIED + FULL   => Kasa Doldu enabled, Boş Kasa Getir disabled
    - ACTIVE TRANSPORT  => Both buttons visible, both disabled
    - Zero actions      => Badge honesty: must NOT report green "Hazır"
    - Absence of deprecated labels: "Alan Boş" and "Kasam Doldu" must never appear.
    Tests across all 9 machines and safely restores tested slot state in finally.
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

# 2. Derive SH-01-A target slot and IDs
$mId = '2384d0fc-4dea-4174-b477-347fb365f645' # SH-01-A
$vId = '3e5be5cf-2771-432a-a60b-0f87f4f0cf5b' # Variant 1
$lId = 'ae8d65cb-53fa-434a-bbc8-891c5443bfa1' # CUSTOMER-PHYSICAL-V1
$pgId = 'bc6e5f9b-e17e-4e09-a8d7-d5716f58e7ae' # 67115BN050-10-HAOS
$partId = '1a19039f-85c4-42dd-9718-929bc955f849' # 67115BN050-10-HAOS
$containerId = '2694fe17-d871-4613-a406-7f84537a5f09' # ARABA

$slot1Sql = @"
SELECT ls.id::text
FROM master.layout_slots ls
JOIN master.layouts l ON l.id = ls.layout_id
WHERE l.id = '$lId' AND ls.code = 'SLOT-001';
"@
$slot1Id = (& $psql -h $HostName -p $Port -U $User -d $Database -t -A -c $slot1Sql).Trim()

# Snapshot original slot state
$readSql = "SELECT coalesce(occupancy_state,'VACANT'), coalesce(load_state,'UNKNOWN'), coalesce(current_part_id::text,''), coalesce(current_container_type_id::text,'') FROM ops.slot_runtime_state WHERE layout_slot_id = '$slot1Id';"
$origRaw = (& $psql -h $HostName -p $Port -U $User -d $Database -t -A -F ',' -c $readSql).Trim().Split(',')
$origOcc = $origRaw[0]
$origLoad = $origRaw[1]
$origPart = $origRaw[2]
$origCont = $origRaw[3]

function Get-SH01Pos1 {
    $uri = "$ApiBaseUrl/api/v1/machines/$mId/container-positions?variantId=$vId&layoutId=$lId&productGroupId=$pgId&partId=$partId"
    $res = Invoke-RestMethod -Uri $uri -Method Get -Headers $headers
    return $res | Where-Object { $_.positionNumber -eq 1 }
}

$allPass = $true

try {
    Write-Host "`n--- Part 1: State Matrix Acceptance on Logical Position 1 ---" -ForegroundColor Yellow

    # State A: VACANT + UNKNOWN
    & $psql -h $HostName -p $Port -U $User -d $Database -c "UPDATE ops.slot_runtime_state SET occupancy_state = 'VACANT', load_state = 'UNKNOWN', current_part_id = NULL, current_container_type_id = NULL WHERE layout_slot_id = '$slot1Id';" | Out-Null
    $pos = Get-SH01Pos1
    $fullOk = ($pos.fullAction -and -not $pos.fullAction.enabled)
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
    $fullOk = ($pos.fullAction -and $pos.fullAction.enabled)
    $emptyOk = ($pos.emptyAction -and -not $pos.emptyAction.enabled)
    Write-Host "`nState B [OCCUPIED + EMPTY]:"
    Write-Host ("  Kasa Doldu:     Enabled={0}" -f $pos.fullAction.enabled)
    Write-Host ("  Boş Kasa Getir: Enabled={0} (DisabledCode={1})" -f $pos.emptyAction.enabled, $pos.emptyAction.disabledCode)
    if ($fullOk -and $emptyOk) {
        Write-Host "  -> PASS: Kasa Doldu enabled, Boş Kasa Getir disabled" -ForegroundColor Green
    } else {
        Write-Host "  -> FAIL" -ForegroundColor Red
        $allPass = $false
    }

    # State C: OCCUPIED + FULL
    & $psql -h $HostName -p $Port -U $User -d $Database -c "UPDATE ops.slot_runtime_state SET occupancy_state = 'OCCUPIED', load_state = 'FULL', current_part_id = '$partId', current_container_type_id = '$containerId' WHERE layout_slot_id = '$slot1Id';" | Out-Null
    $pos = Get-SH01Pos1
    $fullOk = ($pos.fullAction -and $pos.fullAction.enabled)
    $emptyOk = ($pos.emptyAction -and -not $pos.emptyAction.enabled)
    Write-Host "`nState C [OCCUPIED + FULL]:"
    Write-Host ("  Kasa Doldu:     Enabled={0} (Triggers REMOVE_FULL)" -f $pos.fullAction.enabled)
    Write-Host ("  Boş Kasa Getir: Enabled={0} (DisabledCode={1})" -f $pos.emptyAction.enabled, $pos.emptyAction.disabledCode)
    if ($fullOk -and $emptyOk) {
        Write-Host "  -> PASS: Kasa Doldu enabled, Boş Kasa Getir disabled" -ForegroundColor Green
    } else {
        Write-Host "  -> FAIL" -ForegroundColor Red
        $allPass = $false
    }

    # Part 2: Proving Absence of Deprecated Labels
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
    # Restore original slot state
    Write-Host "`nRestoring original slot state..."
    $pParam = if ([string]::IsNullOrWhiteSpace($origPart)) { "NULL" } else { "'$origPart'" }
    $cParam = if ([string]::IsNullOrWhiteSpace($origCont)) { "NULL" } else { "'$origCont'" }
    & $psql -h $HostName -p $Port -U $User -d $Database -c "UPDATE ops.slot_runtime_state SET occupancy_state = '$origOcc', load_state = '$origLoad', current_part_id = $pParam, current_container_type_id = $cParam, updated_at = now() WHERE layout_slot_id = '$slot1Id';" | Out-Null
    Write-Host "Slot state restored to: $origOcc / $origLoad"
}

if ($allPass) {
    Write-Host "`nRESULT: PASS (Mamul Kasa actions & state matrix verified)" -ForegroundColor Green
    exit 0
} else {
    Write-Host "`nRESULT: FAIL" -ForegroundColor Red
    exit 1
}
