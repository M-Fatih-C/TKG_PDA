<#
.SYNOPSIS
    Acceptance test for runtime slot state persistence across stack restarts.
.DESCRIPTION
    Proves that normal development stack restarts do not overwrite or reset
    existing slot runtime states, verifying that the 002d overlay is strictly
    insert-missing / self-heal and that -ResetSimulationState is opt-in only.
    Snapshots original states and restores them in finally.
#>
[CmdletBinding()]
param(
    [string]$HostName = '127.0.0.1',
    [int]$Port = 55450,
    [string]$Database = 'tkg_pda_dev',
    [string]$User = 'postgres'
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$psql = 'C:\Program Files\PostgreSQL\18\bin\psql.exe'
if (-not (Test-Path -LiteralPath $psql)) {
    $psql = (Get-Command psql.exe -ErrorAction SilentlyContinue)?.Source
}

Write-Host "==================================================" -ForegroundColor Cyan
Write-Host "PHASE: RUNTIME PERSISTENCE ACCEPTANCE" -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan

# 1. Locate SH-01-A SLOT-001 ID
$slotSql = @"
SELECT ls.id::text
FROM master.layout_slots ls
JOIN master.layouts l ON l.id = ls.layout_id
JOIN master.machines m ON m.id = l.machine_id
WHERE m.code = 'SH-01-A' AND l.code = 'CUSTOMER-PHYSICAL-V1' AND ls.code = 'SLOT-001';
"@
$slotId = (& $psql -h $HostName -p $Port -U $User -d $Database -t -A -c $slotSql).Trim()
if (-not $slotId) {
    throw "Could not locate SLOT-001 on machine SH-01-A."
}
$bufferId = 'c2000000-0000-0000-0000-000000000001'

Write-Host "Target slots: SLOT-001 ($slotId), Simulation Buffer ($bufferId)"

# 2. Snapshot original state
$readSql = "SELECT coalesce(occupancy_state,'VACANT'), coalesce(load_state,'UNKNOWN'), coalesce(current_part_id::text,''), coalesce(current_container_type_id::text,''), row_version FROM ops.slot_runtime_state WHERE layout_slot_id = '$slotId';"
$snapRaw = (& $psql -h $HostName -p $Port -U $User -d $Database -t -A -F ',' -c $readSql).Trim().Split(',')
$origOccupancy = $snapRaw[0]
$origLoad = $snapRaw[1]
$origPart = $snapRaw[2]
$origContainer = $snapRaw[3]

$readBufSql = "SELECT coalesce(occupancy_state,'OCCUPIED'), coalesce(load_state,'EMPTY'), coalesce(current_container_type_id::text,''), row_version FROM ops.slot_runtime_state WHERE layout_slot_id = '$bufferId';"
$bufSnapRaw = (& $psql -h $HostName -p $Port -U $User -d $Database -t -A -F ',' -c $readBufSql).Trim().Split(',')
$origBufOccupancy = $bufSnapRaw[0]
$origBufLoad = $bufSnapRaw[1]
$origBufContainer = $bufSnapRaw[2]

Write-Host ("Original snapshot: SLOT-001 [{0}/{1}] | BUFFER-001 [{2}/{3}]" -f $origOccupancy, $origLoad, $origBufOccupancy, $origBufLoad)

$testPartId = '1a19039f-85c4-42dd-9718-929bc955f849'
$testContainerId = '2694fe17-d871-4613-a406-7f84537a5f09'
$pass = $false

try {
    # 3. Set deterministic test state
    Write-Host "`nSetting deterministic state (SLOT-001 OCCUPIED FULL, BUFFER row_version=42)..."
    $setSql = @"
UPDATE ops.slot_runtime_state
SET occupancy_state = 'OCCUPIED',
    load_state = 'FULL',
    current_part_id = '$testPartId',
    current_container_type_id = '$testContainerId',
    row_version = row_version + 1,
    updated_at = now()
WHERE layout_slot_id = '$slotId';

UPDATE ops.slot_runtime_state
SET current_container_type_id = '$testContainerId',
    row_version = 42,
    updated_at = now()
WHERE layout_slot_id = '$bufferId';
"@
    & $psql -h $HostName -p $Port -U $User -d $Database -c $setSql | Out-Null

    $before = (& $psql -h $HostName -p $Port -U $User -d $Database -t -A -F ',' -c $readSql).Trim().Split(',')
    $bufBefore = (& $psql -h $HostName -p $Port -U $User -d $Database -t -A -F ',' -c $readBufSql).Trim().Split(',')
    Write-Host ("State before restart: SLOT-001 Occupancy={0}, Load={1}, Part={2}, Container={3}" -f $before[0], $before[1], $before[2], $before[3])

    # 4. Stop stack
    Write-Host "`nStopping local stack via stop_local_stack.ps1..."
    pwsh -File (Join-Path $projectRoot 'tools\dev\stop_local_stack.ps1')

    # 5. Normal start stack
    Write-Host "`nStarting local stack in normal mode (ResetSimulationState=$false)..."
    pwsh -File (Join-Path $projectRoot 'tools\dev\start_local_stack.ps1') -Password 'DevPass123!'

    # 6. Assert state after restart
    Write-Host "`nVerifying state after restart..."
    $after = (& $psql -h $HostName -p $Port -U $User -d $Database -t -A -F ',' -c $readSql).Trim().Split(',')
    $bufAfter = (& $psql -h $HostName -p $Port -U $User -d $Database -t -A -F ',' -c $readBufSql).Trim().Split(',')

    Write-Host ("State after restart:  SLOT-001 Occupancy={0}, Load={1}, Part={2}, Container={3}" -f $after[0], $after[1], $after[2], $after[3])
    Write-Host ("Buffer after restart: BUFFER-001 Occupancy={0}, Load={1}, Container={2}, RowVersion={3}" -f $bufAfter[0], $bufAfter[1], $bufAfter[2], $bufAfter[3])

    $slotOk = ($after[0] -eq 'OCCUPIED' -and $after[1] -eq 'FULL' -and $after[2] -eq $testPartId -and $after[3] -eq $testContainerId)
    $bufferOk = ($bufAfter[2] -eq $testContainerId -and $bufAfter[3] -eq '42')

    if ($slotOk -and $bufferOk) {
        Write-Host "`nPASS: Slot runtime state and simulation buffer fully survived normal restart!" -ForegroundColor Green
        $pass = $true
    } else {
        Write-Host "`nFAIL: Runtime state was overwritten during normal startup!" -ForegroundColor Red
        $pass = $false
    }
}
finally {
    # 7. Safe restore to original state
    Write-Host "`nRestoring original snapshotted states..."
    $partParam = if ([string]::IsNullOrWhiteSpace($origPart)) { "NULL" } else { "'$origPart'" }
    $contParam = if ([string]::IsNullOrWhiteSpace($origContainer)) { "NULL" } else { "'$origContainer'" }
    $bufContParam = if ([string]::IsNullOrWhiteSpace($origBufContainer)) { "NULL" } else { "'$origBufContainer'" }

    $restoreSql = @"
UPDATE ops.slot_runtime_state
SET occupancy_state = '$origOccupancy',
    load_state = '$origLoad',
    current_part_id = $partParam,
    current_container_type_id = $contParam,
    updated_at = now()
WHERE layout_slot_id = '$slotId';

UPDATE ops.slot_runtime_state
SET occupancy_state = '$origBufOccupancy',
    load_state = '$origBufLoad',
    current_container_type_id = $bufContParam,
    row_version = 0,
    updated_at = now()
WHERE layout_slot_id = '$bufferId';
"@
    & $psql -h $HostName -p $Port -U $User -d $Database -c $restoreSql | Out-Null
    Write-Host "Snapshot restoration completed."
}

if ($pass) {
    Write-Host "`nRESULT: PASS (Runtime persistence confirmed)" -ForegroundColor Green
    exit 0
} else {
    Write-Host "`nRESULT: FAIL" -ForegroundColor Red
    exit 1
}
